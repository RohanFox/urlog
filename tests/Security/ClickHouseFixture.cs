using System.Globalization;
using ClickHouse.Driver.Tcp;
using DotNet.Testcontainers.Builders;
using Urlog.Core;
using Urlog.Core.Ingest;
using Urlog.Core.Pipeline;
using Urlog.Core.Sources;
using Urlog.Query;
using Testcontainers.ClickHouse;

namespace Urlog.Security.Tests;

/// <summary>
/// One ClickHouse container per test class: the pinned image, the same config.d/users.d overrides the dev stack
/// mounts (so the grants and constraints under test are the deployed ones), the migrations applied as the admin
/// account, and a small seed written as <c>urlog_ingest</c>: cases 1, 2 and 3 with a known number of rows each, across two months so partition pruning is visible.
/// </summary>
public sealed class ClickHouseFixture : IAsyncLifetime
{
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
        await _container.StartAsync();
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
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>
    /// Writes <paramref name="rows"/> rows into one (case, month) partition through the real insert path
    /// (<see cref="NativeInserter"/>, the only writer of <c>events</c>) as <c>urlog_ingest</c>, the account the
    /// backend inserts with: hosts <c>web-01</c>/<c>web-02</c>, one row a minute. The seed is file id = case id,
    /// generation 1.
    /// </summary>
    public async Task InsertAsync(ulong caseId, ulong fileId, uint generation, int month, int rows)
    {
        var buffers = new PipelineBuffers(new PipelineOptions { BatchRows = rows, Parallelism = 1, InFlightChunks = 3, InFlightBatches = 5 });
        var sink = new CollectingBatchSink();
        var writer = new EventBatchWriter(buffers.Batches, sink);
        writer.Bind(new ParseContext { CaseId = caseId, HostId = caseId, FileId = fileId, ParseGeneration = generation, DefaultHost = "web-01"u8.ToArray() });
        var start = new DateTimeOffset(2026, month, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        for (var i = 0; i < rows; i++)
        {
            writer.AppendUInt64(EventColumn.FileOffset, (ulong)i);
            writer.AppendTimestamp(start + (i * 60_000L));
            writer.AppendString(EventColumn.Host, i % 2 == 0 ? "web-01"u8 : "web-02"u8);
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
