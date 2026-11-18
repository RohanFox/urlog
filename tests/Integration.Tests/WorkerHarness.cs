using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using ClickHouse.Driver.Tcp;
using Urlog.Core.Audit;
using Urlog.Core.Ingest;
using Urlog.Core.Jobs;
using Urlog.Core.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Integration.Tests;

/// <summary>
/// What the job runner's integration tests share: a fresh metadata file, the event store reset to an empty migrated
/// schema, an intake record for a file on disk, the store read back as the query account, and <c>urlog worker</c> as
/// a child process whose output is collected line by line.
/// </summary>
internal sealed class WorkerHarness : IAsyncDisposable
{
    public const string IngestUser = "urlog_ingest";
    public const string IngestPassword = "urlog_ingest";

    private readonly ClickHouseFixture _ch;
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("urlog-worker-");
    private readonly List<WorkerProcess> _workers = [];
    private long _ownerId;

    private WorkerHarness(ClickHouseFixture ch)
    {
        _ch = ch;
        Store = new MetadataStore(Path.Combine(_directory.FullName, "metadata.db"));
    }

    public MetadataStore Store { get; }

    /// <summary>The identity the harness gave its metadata file and the event store.</summary>
    public string StoreId { get; } = StoreIdentity.NewId();

    public string Folder => _directory.FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<WorkerHarness> CreateAsync(ClickHouseFixture ch)
    {
        var harness = new WorkerHarness(ch);
        await using (var admin = harness.Admin())
        {
            await new ClickHouseMigrator(admin).ApplyAsync(MigrationSet.Embedded(), Ct);
            // Every test starts its own metadata file at id 1, so the store starts empty too.
            await admin.ExecuteAsync("TRUNCATE TABLE events", cancellationToken: Ct);
            await admin.ExecuteAsync("TRUNCATE TABLE file_generation", cancellationToken: Ct);
            await admin.ExecuteAsync("TRUNCATE TABLE field_stats", cancellationToken: Ct);
            // And a new pair: the store takes the identity of this test's metadata file.
            await admin.ExecuteAsync("TRUNCATE TABLE store_identity", cancellationToken: Ct);
            await new EventStoreAdmin(admin).WriteStoreIdentityAsync(harness.StoreId, ClickHouseFixture.AdminUser, Ct);
        }

        await harness.Store.MigrateAsync(Ct);
        await using var db = harness.Store.CreateDbContext();
        db.StoreIdentity.Add(new StoreIdentityRow { StoreId = harness.StoreId, CreatedAt = DateTime.UtcNow, Origin = StoreIdentityOrigin.Created });
        var user = new User { Name = "alice", PasswordHash = "argon2id$placeholder", CreatedAt = DateTime.UtcNow };
        db.Users.Add(user);
        await db.SaveChangesAsync(Ct);
        harness._ownerId = user.Id;
        return harness;
    }

    public ClickHouseTcpClient Admin() => new(_ch.NativeConnectionString(ClickHouseFixture.AdminUser, ClickHouseFixture.AdminPassword));

    /// <summary>The event store as the runner reaches it in production: the ingest account, nothing else.</summary>
    public ClickHouseJobEventStore EventStore(int writers = 4) =>
        ClickHouseJobEventStore.Create(_ch.Host, _ch.NativeHostPort, IngestUser, IngestPassword, writers);

    public async Task<CaseRecord> AddCaseAsync(string name)
    {
        await using var db = Store.CreateDbContext();
        return await CaseOperations.CreateAsync(db, Store.Audit, new AuditActor("alice"), name, _ownerId, DateTime.UtcNow, Ct);
    }

    /// <summary>The intake record of a file already on disk (its SHA-256 known) and a queued parse.</summary>
    public Task<(EvidenceFile File, Job Job)> AddFileAsync(long caseId, string path, string sha256, string source = "nginx") =>
        AddFileAsync(Store, caseId, path, sha256, source);

    public async Task<Job> JobAsync(long id)
    {
        await using var db = Store.CreateDbContext();
        return await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == id, Ct);
    }

    public async Task<EvidenceFile> FileAsync(long id)
    {
        await using var db = Store.CreateDbContext();
        return await db.Files.AsNoTracking().SingleAsync(f => f.Id == id, Ct);
    }

    /// <summary>
    /// What a user of the case reads: rows, distinct generations and the highest generation through <c>events_v</c>
    /// as <c>urlog_query</c> with the case in <c>SQL_allowed_cases</c>.
    /// </summary>
    public async Task<(long Rows, long Generations, long MaxGeneration)> VisibleAsync(long caseId)
    {
        await using var query = new ClickHouseTcpClient(_ch.NativeConnectionString(ClickHouseFixture.QueryUser, ClickHouseFixture.QueryPassword));
        var c = caseId.ToString(CultureInfo.InvariantCulture);
        await foreach (var row in query.QueryAsync($"SELECT count(), uniqExact(parse_generation), max(parse_generation) FROM events_v WHERE case_id = {c} SETTINGS SQL_allowed_cases = '[{c}]'", cancellationToken: Ct))
        {
            return (Convert.ToInt64(row[0], CultureInfo.InvariantCulture), Convert.ToInt64(row[1], CultureInfo.InvariantCulture), Convert.ToInt64(row[2], CultureInfo.InvariantCulture));
        }

        throw new InvalidOperationException("the count returned no row");
    }

    /// <summary>Every row of the case in <c>field_stats</c>, as the admin account reads the table: what is stored, not what the view shows.</summary>
    public async Task<List<(string Field, string Value, long Count, long Distinct, DateTime ComputedAt)>> FieldStatsAsync(long caseId)
    {
        await using var admin = Admin();
        var rows = new List<(string, string, long, long, DateTime)>();
        await foreach (var row in admin.QueryAsync($"SELECT toString(field), value, count, distinct_estimate, toUnixTimestamp64Milli(computed_at) FROM field_stats WHERE case_id = {caseId.ToString(CultureInfo.InvariantCulture)} ORDER BY field, count DESC, value", cancellationToken: Ct))
        {
            rows.Add(((string)row[0]!, (string)row[1]!, Convert.ToInt64(row[2], CultureInfo.InvariantCulture), Convert.ToInt64(row[3], CultureInfo.InvariantCulture), DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(row[4], CultureInfo.InvariantCulture)).UtcDateTime));
        }

        return rows;
    }

    /// <summary>Rows in the case's active parts: what the table holds, visible or not, without reading the table.</summary>
    public async Task<long> StoredRowsAsync(long caseId)
    {
        await using var admin = Admin();
        var c = caseId.ToString(CultureInfo.InvariantCulture);
        return Convert.ToInt64(await admin.ExecuteScalarAsync($"SELECT sum(rows) FROM system.parts WHERE database = currentDatabase() AND table = 'events' AND active AND partition LIKE '({c},%'", cancellationToken: Ct), CultureInfo.InvariantCulture);
    }

    /// <summary>Rows of a file per generation that the lightweight delete has not masked, read as the admin account.</summary>
    public async Task<Dictionary<uint, long>> StoredGenerationsAsync(long fileId)
    {
        await using var admin = Admin();
        var result = new Dictionary<uint, long>();
        await foreach (var row in admin.QueryAsync($"SELECT parse_generation, count() FROM events WHERE file_id = {fileId.ToString(CultureInfo.InvariantCulture)} GROUP BY parse_generation", cancellationToken: Ct))
        {
            result[Convert.ToUInt32(row[0], CultureInfo.InvariantCulture)] = Convert.ToInt64(row[1], CultureInfo.InvariantCulture);
        }

        return result;
    }

    /// <summary>
    /// The stored histogram of a case against its events, read as the admin account: how many (minute, severity)
    /// buckets differ between the two over the committed generations, how many events the committed
    /// buckets hold, and the generations of the given file the table has rows of that no delete has masked.
    /// </summary>
    public async Task<(long Differing, long Events, uint[] FileGenerations)> HistogramAsync(long caseId, long fileId = 0)
    {
        await using var admin = Admin();
        var id = caseId.ToString(CultureInfo.InvariantCulture);
        var committed = Urlog.Core.Ingest.CaseScopedEventsQuery.CommittedGenerations;
        var differing = Convert.ToInt64(
            await admin.ExecuteScalarAsync(
                "SELECT count() FROM "
                + $"(SELECT {Urlog.Core.Ingest.CaseScopedEventsQuery.HistogramBucket} AS b, severity, count() AS n FROM events WHERE case_id = {id} AND {committed} GROUP BY b, severity) AS e "
                + $"FULL JOIN (SELECT bucket AS b, severity, sum(events) AS n FROM case_histogram WHERE case_id = {id} AND {committed} GROUP BY b, severity) AS h USING (b, severity) "
                + "WHERE e.n != h.n",
                cancellationToken: Ct),
            CultureInfo.InvariantCulture);
        var events = Convert.ToInt64(await admin.ExecuteScalarAsync($"SELECT sum(events) FROM case_histogram WHERE case_id = {id} AND {committed}", cancellationToken: Ct), CultureInfo.InvariantCulture);
        var generations = new List<uint>();
        await foreach (var row in admin.QueryAsync($"SELECT DISTINCT parse_generation FROM case_histogram WHERE case_id = {id} AND file_id = {fileId.ToString(CultureInfo.InvariantCulture)} ORDER BY parse_generation", cancellationToken: Ct))
        {
            generations.Add(Convert.ToUInt32(row[0], CultureInfo.InvariantCulture));
        }

        return (differing, events, [.. generations]);
    }

    /// <summary>Rows of every generation the case has in the stored histogram.</summary>
    public async Task<long> HistogramRowsAsync(long caseId)
    {
        await using var admin = Admin();
        return Convert.ToInt64(await admin.ExecuteScalarAsync($"SELECT count() FROM case_histogram WHERE case_id = {caseId.ToString(CultureInfo.InvariantCulture)}", cancellationToken: Ct), CultureInfo.InvariantCulture);
    }

    public async Task<uint> MirroredGenerationAsync(long fileId)
    {
        await using var ingest = new ClickHouseTcpClient(_ch.NativeConnectionString(IngestUser, IngestPassword));
        return (await new EventStoreAdmin(ingest).ReadFileGenerationsAsync(Ct)).GetValueOrDefault((ulong)fileId);
    }

    /// <summary>Starts <c>urlog worker</c> on this metadata file and this event store, as the ingest account.</summary>
    public WorkerProcess StartWorker(params string[] extra)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        string[] arguments =
        [
            CliPath(), "worker", "--db", Store.Path, "--clickhouse", _ch.Host, "--native-port", _ch.NativeHostPort.ToString(CultureInfo.InvariantCulture),
            "--user", IngestUser, "--password", IngestPassword, "--min-free-gb", "0", "--min-free-percent", "0", .. extra,
        ];
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        var worker = new WorkerProcess(Process.Start(start)!);
        _workers.Add(worker);
        return worker;
    }

    /// <summary>Starts any <c>urlog</c> command against this event store and returns while it runs, so a test can stop it part-way.</summary>
    public WorkerProcess StartCli(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        string[] all = [CliPath(), .. arguments, "--clickhouse", _ch.Host, "--native-port", _ch.NativeHostPort.ToString(CultureInfo.InvariantCulture), "--user", IngestUser, "--password", IngestPassword];
        foreach (var argument in all)
        {
            start.ArgumentList.Add(argument);
        }

        var process = new WorkerProcess(Process.Start(start)!);
        _workers.Add(process);
        return process;
    }

    /// <summary>
    /// Runs one <c>urlog</c> command to its end against this event store (not against this metadata file unless
    /// the arguments say so). Standard input is <paramref name="stdin"/> and then closed, so the command never sees
    /// a terminal.
    /// </summary>
    public async Task<CliResult> RunCliAsync(string? stdin, params string[] arguments) =>
        await RunCliAsync(_ch.NativeHostPort, stdin, arguments);

    /// <summary>As <see cref="RunCliAsync(string?, string[])"/>, at a native port of the caller's choosing (1: nothing listens there).</summary>
    public async Task<CliResult> RunCliAsync(int nativePort, string? stdin, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        foreach (var variable in new[] { "URLOG_DB", "URLOG_CH_HOST", "URLOG_CH_TCP_PORT", "URLOG_CH_USER", "URLOG_CH_PASSWORD", "URLOG_CH_ADMIN_USER", "URLOG_CH_ADMIN_PASSWORD", "URLOG_PASSWORD", "URLOG_BACKUP_DIR" })
        {
            start.Environment.Remove(variable);
        }

        string[] all = [CliPath(), .. arguments, "--clickhouse", _ch.Host, "--native-port", nativePort.ToString(CultureInfo.InvariantCulture)];
        foreach (var argument in all)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin);
        }

        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var error = process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(120), Ct);
        return new CliResult(process.ExitCode, await output, await error);
    }

    /// <summary>
    /// Everything in the event store a wrong pairing could change, as text: every <c>file_generation</c> row with
    /// its time, the rows of <c>events</c> per file and generation, the mutations queued on <c>events</c> (a
    /// lightweight delete is one), and the store's identity. Read as the admin account.
    /// </summary>
    public async Task<string> StoreSnapshotAsync()
    {
        await using var admin = Admin();
        var text = new System.Text.StringBuilder();
        foreach (var sql in new[]
                 {
                     "SELECT 'file_generation', toString(file_id), toString(current), toString(updated_at) FROM file_generation ORDER BY file_id, updated_at, current",
                     "SELECT 'events', toString(file_id), toString(parse_generation), toString(count()) FROM events GROUP BY file_id, parse_generation ORDER BY file_id, parse_generation",
                     "SELECT 'parts', toString(sum(rows)), toString(count()), '' FROM system.parts WHERE database = currentDatabase() AND table = 'events' AND active",
                     "SELECT 'mutations', toString(count()), '', '' FROM system.mutations WHERE database = currentDatabase() AND table = 'events'",
                     "SELECT 'store_identity', store_id, toString(created_at), created_by FROM store_identity ORDER BY created_at",
                 })
        {
            await foreach (var row in admin.QueryAsync(sql, cancellationToken: Ct))
            {
                text.Append(row[0]).Append(' ').Append(row[1]).Append(' ').Append(row[2]).Append(' ').Append(row[3]).Append('\n');
            }
        }

        return text.ToString();
    }

    /// <summary>The visible generation per file id, as the ingest account reads <c>file_generation</c>.</summary>
    public async Task<Dictionary<ulong, uint>> EventStoreGenerationsAsync()
    {
        await using var ingest = new ClickHouseTcpClient(_ch.NativeConnectionString(IngestUser, IngestPassword));
        return await new EventStoreAdmin(ingest).ReadFileGenerationsAsync(Ct);
    }

    /// <summary>The store's identity as the admin account reads it; null for none.</summary>
    public async Task<string?> StoreIdentityAsync()
    {
        await using var admin = Admin();
        return await new EventStoreAdmin(admin).ReadStoreIdentityAsync(Ct);
    }

    /// <summary>Takes the identity away from the event store: the state of a store from before the identity existed.</summary>
    public async Task ClearStoreIdentityAsync()
    {
        await using var admin = Admin();
        await admin.ExecuteAsync("TRUNCATE TABLE store_identity", cancellationToken: Ct);
    }

    /// <summary>
    /// A second metadata file that has nothing to do with this event store, in a folder of its own: migrated, with
    /// a user and a case, and with an identity of its own unless <paramref name="identity"/> is false. Its ids
    /// start at 1 like every new file's.
    /// </summary>
    public async Task<(MetadataStore Store, long CaseId, string? StoreId)> AddForeignMetadataAsync(string name, bool identity = true)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_directory.FullName, name));
        var store = new MetadataStore(Path.Combine(folder.FullName, "metadata.db"));
        await store.MigrateAsync(Ct);
        await using var db = store.CreateDbContext();
        var user = new User { Name = "mallory", PasswordHash = "argon2id$placeholder", CreatedAt = DateTime.UtcNow };
        db.Users.Add(user);
        string? id = null;
        if (identity)
        {
            id = Urlog.Core.Metadata.StoreIdentity.NewId();
            db.StoreIdentity.Add(new StoreIdentityRow { StoreId = id, CreatedAt = DateTime.UtcNow, Origin = StoreIdentityOrigin.Created });
        }

        await db.SaveChangesAsync(Ct);
        var @case = await CaseOperations.CreateAsync(db, store.Audit, new AuditActor("mallory"), "scratch", user.Id, DateTime.UtcNow, Ct);
        return (store, @case.Id, id);
    }

    /// <summary>An intake record with a queued parse in <paramref name="store"/> (this harness's own file, or a foreign one).</summary>
    public static async Task<(EvidenceFile File, Job Job)> AddFileAsync(MetadataStore store, long caseId, string path, string sha256, string source = "nginx")
    {
        await using var db = store.CreateDbContext();
        var host = await HostOperations.GetOrCreateAsync(db, caseId, "web-01", DateTime.UtcNow, Ct);
        var file = new EvidenceFile
        {
            CaseId = caseId,
            HostId = host.Id,
            Sha256 = sha256,
            Size = new FileInfo(path).Length,
            OriginalName = Path.GetFileName(path),
            StoredPath = path,
            Source = FileSource.Cli,
            Uploader = "alice",
            ReceivedAt = DateTime.UtcNow,
            Encoding = "utf8",
            DetectedSource = source,
        };
        var added = (await FileOperations.AddAsync(db, store.Audit, new AuditActor("alice"), file, Ct)).File;
        var job = await JobOperations.EnqueueAsync(db, added.Id, DateTime.UtcNow, Ct);
        return (added, job);
    }

    public static async Task EventuallyAsync(Func<Task<bool>> condition, string what, int seconds = 120, int pollMilliseconds = 20)
    {
        var deadline = Stopwatch.StartNew();
        while (!await condition())
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(seconds), $"timed out waiting for: {what}");
            await Task.Delay(pollMilliseconds, Ct);
        }
    }

    public ValueTask DisposeAsync()
    {
        foreach (var worker in _workers)
        {
            worker.Dispose();
        }

        SqliteConnection.ClearAllPools();
        try
        {
            // Evidence stored under a root is read-only on disk, which a recursive delete does not get past.
            foreach (var file in _directory.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                file.Attributes = FileAttributes.Normal;
            }

            _directory.Delete(recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>The built <c>urlog</c> of the same configuration as this test binary.</summary>
    private static string CliPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = dir.Parent!.Name;
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "urlog.sln")))
        {
            dir = dir.Parent;
        }

        var path = Path.Combine(dir!.FullName, "src", "Cli", "bin", configuration, "net10.0", "urlog.dll");
        Assert.True(File.Exists(path), $"urlog.dll is not built at {path}");
        return path;
    }
}

/// <summary>How one <c>urlog</c> command ended.</summary>
internal sealed record CliResult(int ExitCode, string Output, string Error)
{
    public override string ToString() => $"exit {ExitCode}\n--- stdout\n{Output}\n--- stderr\n{Error}";
}

/// <summary>A <c>urlog worker</c> child process. Its output is read as it comes, so the pipe never fills and a test can wait for a line.</summary>
internal sealed class WorkerProcess : IDisposable
{
    private readonly ConcurrentQueue<string> _lines = new();
    private readonly Task _pump;

    public WorkerProcess(Process process)
    {
        Process = process;
        _pump = Task.WhenAll(PumpAsync(process.StandardOutput), PumpAsync(process.StandardError));
    }

    public Process Process { get; }

    public string Output => string.Join(Environment.NewLine, _lines);

    public bool Printed(string text) => _lines.Any(l => l.Contains(text, StringComparison.Ordinal));

    /// <summary>Kills the process from outside: no handler, finaliser or flush of the worker gets to run.</summary>
    public async Task KillAsync()
    {
        Process.Kill(entireProcessTree: true);
        await Process.WaitForExitAsync(TestContext.Current.CancellationToken);
        await _pump;
    }

    public async Task<int> WaitForExitAsync(int seconds)
    {
        await Process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(seconds), TestContext.Current.CancellationToken);
        await _pump;
        return Process.ExitCode;
    }

    public void Dispose()
    {
        try
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }

        Process.Dispose();
    }

    private async Task PumpAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            _lines.Enqueue(line);
        }
    }
}
