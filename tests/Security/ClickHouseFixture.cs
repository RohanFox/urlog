using System.Globalization;
using ClickHouse.Driver.Tcp;
using DotNet.Testcontainers.Builders;
using Urlog.Core;
using Urlog.Core.Ingest;
using Urlog.Core.Pipeline;
using Urlog.Core.Sources;
using Urlog.Query;
using Testcontainers.ClickHouse;
using Urlog.Tests.Shared;

namespace Urlog.Security.Tests;

/// <summary>
/// A ClickHouse container for a test class, or for the classes of <see cref="SeedReaders"/> together: the pinned
/// image, the same config.d/users.d overrides the dev stack mounts (so the grants and constraints under test are
/// the deployed ones), the migrations applied as the admin account, and a small seed written as
/// <c>urlog_ingest</c>: cases 1, 2 and 3 with a known number of rows each, across two months so partition pruning
/// is visible.
/// <para>
/// At most <see cref="StartsAtOnce"/> of them start at a time, and one counts as ready only when the server has
/// answered a native handshake and each of the three accounts has run a statement the way the tests reach it. The
/// container library calls a container started when its HTTP port answers, and that says nothing about the native
/// port the insert path and the migrations use.
/// </para>
/// </summary>
public sealed class ClickHouseFixture : IAsyncLifetime
{
    /// <summary>
    /// Containers that may be between their start and their seed at one time. Measured on the Linux test VM (8
    /// cores, 12 GB): one container answers 4.5 s after its start, four at once 5.1 s, eight 5.9 s, twelve 7.0 s.
    /// Four costs a start a tenth of its time and leaves half of the cores to the tests already running.
    /// </summary>
    public const int StartsAtOnce = 4;

    private static readonly SemaphoreSlim Starting = new(StartsAtOnce, StartsAtOnce);

    public const string Image = "clickhouse/clickhouse-server:26.8.36.3";
    public const string AdminUser = "default";
    public const string AdminPassword = "clickhouse";
    public const string QueryUser = "urlog_query";
    public const string QueryPassword = "urlog_query";
    public const string IngestUser = "urlog_ingest";
    public const string IngestPassword = "urlog_ingest";

    /// <summary>Rows per (case, month) in the seed; the cases and months seeded.</summary>
    public const int RowsPerPartition = 100;
    public static readonly ulong[] Cases = [1, 2, 3];
    public static readonly int[] Months = [1, 2];

    private readonly ClickHouseContainer _container;

    public ClickHouseFixture()
    {
        var deploy = Path.Combine(RepoRoot, "deploy", "clickhouse");
        _container = new ClickHouseBuilder(Image)
            .WithUsername(AdminUser)
            .WithPassword(AdminPassword)
            .WithEnvironment("CLICKHOUSE_DEFAULT_ACCESS_MANAGEMENT", "1") // as compose.dev.yml: the admin may create the probe users the fallback tests need
            .WithPortBinding(8123, assignRandomHostPort: true)
            .WithPortBinding(9000, assignRandomHostPort: true)
            .WithResourceMapping(new FileInfo(Path.Combine(deploy, "config.d", "urlog.xml")), "/etc/clickhouse-server/config.d/")
            .WithResourceMapping(new FileInfo(Path.Combine(deploy, "users.d", "urlog.xml")), "/etc/clickhouse-server/users.d/")
            // A ceiling of its own, so the server's memory limit does not move with what else the box holds.
            .WithResourceMapping(TestServerMemory.ConfigBytes, TestServerMemory.ContainerPath)
            .Build();
    }

    public static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "urlog.sln")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? throw new InvalidOperationException("urlog.sln not found above the test binary");
        }
    }

    public string Host => _container.Hostname;

    public int HttpPort => _container.GetMappedPublicPort(8123);

    public int NativePort => _container.GetMappedPublicPort(9000);

    public static int RowsPerCase => RowsPerPartition * Months.Length;

    /// <summary>The query path exactly as the CLI and the backend build it: HTTP, the read-only user, gated.</summary>
    public ClickHouseQueryClient QueryClient() => new(Host, HttpPort, QueryUser, QueryPassword);

    /// <summary>The driver's HTTP connection string for a user: the harness side, where the gate is bypassed on purpose.</summary>
    public string HttpConnectionString(string user, string password) =>
        $"Host={Host};Port={HttpPort.ToString(CultureInfo.InvariantCulture)};Username={user};Password={password};Database=default";

    /// <summary>The write path's control plane as the backend builds it: the driver's TCP client as <c>urlog_ingest</c>.</summary>
    public ClickHouseTcpClient Ingest() => new($"Host={Host};Port={NativePort.ToString(CultureInfo.InvariantCulture)};Username={IngestUser};Password={IngestPassword};Database=default;Compression=lz4");

    public ClickHouseTcpClient Admin() => new($"Host={Host};Port={NativePort.ToString(CultureInfo.InvariantCulture)};Username={AdminUser};Password={AdminPassword};Database=default;Compression=lz4");

    public async ValueTask InitializeAsync()
    {
        await Starting.WaitAsync();
        try
        {
            await StartAndSeedAsync();
        }
        finally
        {
            Starting.Release();
        }
    }

    private async Task StartAndSeedAsync()
    {
        await _container.StartAsync();
        await WaitForNativeAsync();
        await using var admin = Admin();
        await new ClickHouseMigrator(admin).ApplyAsync(MigrationSet.Embedded());
        foreach (var caseId in Cases)
        {
            foreach (var month in Months)
            {
                await InsertAsync(caseId, fileId: caseId, generation: 1, month, RowsPerPartition);
            }
        }

        // A file shows nothing until its generation is committed (migration 0006), so the seed is committed like a parse.
        await using var ingest = Ingest();
        var store = new EventStoreAdmin(ingest);
        foreach (var caseId in Cases)
        {
            await store.CommitFileGenerationAsync(caseId, 1);
        }

        // Ready is what the tests will do, done once: the ingest account over the native port, as the write path
        // connects, and the query account through the gated HTTP client, reading the seed through the view.
        await ingest.ExecuteScalarAsync("SELECT 1");
        var seeded = await QueryClient().RunSqlAsync("SELECT count() FROM events_v", Cases);
        if (seeded.Rows[0][0] != (Cases.Length * RowsPerCase).ToString(CultureInfo.InvariantCulture))
        {
            throw new InvalidOperationException($"the seed is {seeded.Rows[0][0]} rows through events_v, not {Cases.Length * RowsPerCase}");
        }
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>
    /// A native handshake as the admin account, which the migrations need next. The container library has seen the
    /// HTTP port answer by now; the native port is another published port and has been seen refusing connections
    /// after that, for a moment and once for the whole minute. Each try has its own five seconds, sixty seconds in
    /// all, and a start that fails says what the container looked like (<see cref="DescribeAsync"/>).
    /// </summary>
    private async Task WaitForNativeAsync()
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            try
            {
                using var attempt = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await using var probe = Admin();
                await probe.ExecuteScalarAsync("SELECT 1", cancellationToken: attempt.Token);
                return;
            }
            catch (Exception e) when (e is not OutOfMemoryException && DateTime.UtcNow < until)
            {
                await Task.Delay(250);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                throw new InvalidOperationException(await DescribeAsync(e), e);
            }
        }
    }

    /// <summary>
    /// What the container looked like when its native port did not answer: its state, whether the server answers
    /// a client inside the container, the ports the engine says it published, and the end of its log. A start that
    /// fails says which side failed, the server or the way to it.
    /// </summary>
    private async Task<string> DescribeAsync(Exception failure)
    {
        var text = new System.Text.StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"the native port of the ClickHouse container did not answer within 60 s: {failure.Message}. ");
        try
        {
            text.Append(CultureInfo.InvariantCulture, $"State {_container.State}, health {_container.Health}; published native {Host}:{NativePort}, http {Host}:{HttpPort}. ");
            var inside = await _container.ExecAsync(["clickhouse-client", "--password", AdminPassword, "--query", "SELECT 'answers inside', version(), uptime()"]);
            text.Append(CultureInfo.InvariantCulture, $"A client inside the container: exit {inside.ExitCode}, '{inside.Stdout.Trim()}' '{inside.Stderr.Trim()}'. ");
            var listening = await _container.ExecAsync(["sh", "-c", "(ss -ltn 2>/dev/null || netstat -ltn 2>/dev/null) | grep -E ':(9000|8123) ' ; ps -eo pid,etimes,args | grep -v grep | grep -E 'clickhouse|entrypoint'"]);
            text.Append("Listening and processes: ").Append(listening.Stdout.Trim()).Append(". ");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            try
            {
                text.Append(CultureInfo.InvariantCulture, $"The published HTTP port now: {(int)(await http.GetAsync(new Uri($"http://{Host}:{HttpPort}/ping"))).StatusCode}. ");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                text.Append(CultureInfo.InvariantCulture, $"The published HTTP port now: {e.Message}. ");
            }

            var (stdout, stderr) = await _container.GetLogsAsync();
            var log = (stdout + stderr).Split('\n');
            text.Append("Last log lines: ").Append(string.Join(" | ", log.Skip(Math.Max(0, log.Length - 25)).Select(l => l.Trim())));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            text.Append(CultureInfo.InvariantCulture, $"(the container could not be asked: {e.Message})");
        }

        return text.ToString();
    }

    /// <summary>
    /// Writes <paramref name="rows"/> rows into one (case, month) partition through the real insert path
    /// (<see cref="NativeInserter"/>, the only writer of <c>events</c>) as <c>urlog_ingest</c>, the account the
    /// backend inserts with: hosts <c>web-01</c>/<c>web-02</c>, one row a minute. The seed is file id = case id,
    /// generation 1.
    /// </summary>
    public Task InsertAsync(ulong caseId, ulong fileId, uint generation, int month, int rows)
    {
        var start = new DateTimeOffset(2026, month, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        return InsertRowsAsync(caseId, fileId, generation, [.. Enumerable.Range(0, rows).Select(i => new SeedRow(start + (i * 60_000L), i % 2 == 0 ? "web-01" : "web-02", string.Empty))]);
    }

    /// <summary>
    /// Writes the given rows of one file and generation through the real insert path, as <see cref="InsertAsync"/>
    /// does: each with its own time, host and severity, its offset the row's position.
    /// </summary>
    public async Task InsertRowsAsync(ulong caseId, ulong fileId, uint generation, IReadOnlyList<SeedRow> seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        var rows = seed.Count;
        var buffers = new PipelineBuffers(new PipelineOptions { BatchRows = Math.Max(1, rows), Parallelism = 1, InFlightChunks = 3, InFlightBatches = 5 });
        var sink = new CollectingBatchSink();
        var writer = new EventBatchWriter(buffers.Batches, sink);
        writer.Bind(new ParseContext { CaseId = caseId, HostId = caseId, FileId = fileId, ParseGeneration = generation, DefaultHost = "web-01"u8.ToArray() });
        for (var i = 0; i < rows; i++)
        {
            writer.AppendUInt64(EventColumn.FileOffset, (ulong)i);
            writer.AppendTimestamp(seed[i].Milliseconds);
            writer.AppendString(EventColumn.Host, System.Text.Encoding.UTF8.GetBytes(seed[i].Host));
            if (seed[i].Severity.Length > 0)
            {
                writer.AppendString(EventColumn.Severity, System.Text.Encoding.UTF8.GetBytes(seed[i].Severity));
            }

            writer.AppendString(EventColumn.Program, "nginx"u8);
            writer.AppendString(EventColumn.Raw, "GET /index.html 200"u8);
            writer.AppendMessage(0, 19);
            writer.CommitRow();
        }

        writer.Flush();
        try
        {
            var options = new NativeInserterOptions
            {
                Host = Host,
                Port = NativePort,
                User = IngestUser,
                Password = IngestPassword,
                Table = "events",
                Writers = 1,
                ReadTimeout = TimeSpan.FromSeconds(20),
            };
            await using var inserter = new NativeInserter(options, returnBatches: false);
            foreach (var batch in sink.Batches)
            {
                inserter.Submit(batch);
            }

            await inserter.CompleteAsync(CancellationToken.None);
        }
        finally
        {
            sink.ReturnAll();
            buffers.Batches.Dispose();
        }
    }
}

/// <summary>
/// The test classes that only read the seed, as the query account, and so can share one server: none of them
/// inserts a row, creates an account or changes a setting, and the row counts they assert are the seed's. Classes
/// of one collection run one after another, which these do in a few seconds; every other class of the project
/// writes to its server and keeps a server of its own.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SeedReaders : ICollectionFixture<ClickHouseFixture>
{
    public const string Name = "seed readers";
}

/// <summary>One seeded event: its time in milliseconds, its host, and its severity (empty for none).</summary>
public sealed record SeedRow(long Milliseconds, string Host, string Severity);
