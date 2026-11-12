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
        }

        await harness.Store.MigrateAsync(Ct);
        await using var db = harness.Store.CreateDbContext();
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
    public async Task<(EvidenceFile File, Job Job)> AddFileAsync(long caseId, string path, string sha256, string source = "nginx")
    {
        await using var db = Store.CreateDbContext();
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
        var added = (await FileOperations.AddAsync(db, Store.Audit, new AuditActor("alice"), file, Ct)).File;
        var job = await JobOperations.EnqueueAsync(db, added.Id, DateTime.UtcNow, Ct);
        return (added, job);
    }

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
            _directory.Delete(recursive: true);
        }
        catch (IOException)
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
