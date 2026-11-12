using System.Globalization;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Parameters;
using ClickHouse.Driver.Tcp;
using Urlog.Bench.Fixtures;
using Urlog.Bench.Reporting;
using Urlog.Bench.Stub;
using Urlog.Core.Ingest;

namespace Urlog.Bench.Benches;

/// <summary>
/// The real insert path: <see cref="NativeInserter"/> with <see cref="Writers"/> writers streaming pre-built
/// <c>EventBatch</c>es of <see cref="BatchRows"/> rows (parsed once from the nginx fixture by the real source) into
/// the pinned schema with the text index on, LZ4 on the wire, up to 500k rows per INSERT (the defaults). One op is
/// <see cref="RowsPerOp"/> rows and ends when the server has acknowledged every INSERT. The table is truncated
/// before every op. Writers = 4 is the SLO configuration and gated; 6 is reported for the curve
/// (design record "Throughput SLO and benchmark harness"). B/row includes the writers' connection buffers (about 6 MB per connection
/// per op). Needs ClickHouse (compose.dev.yml); see <see cref="BenchEnv"/>.
/// </summary>
[SimpleJob(RunStrategy.Throughput, warmupCount: 1, iterationCount: 6, invocationCount: 1)]
public class InsertNative : IDisposable
{
    public const string Table = "bench_events_native";
    public const int RowsPerOp = 2_000_000;

    private EventBatchSet _blocks = null!;
    private ClickHouseTcpClient _admin = null!;
    private NativeInserterOptions _options = null!;

    [Params(100_000, 250_000, 500_000)]
    public int BatchRows { get; set; }

    [Params(4, 6)]
    public int Writers { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _admin = new ClickHouseTcpClient(BenchEnv.TcpConnectionString);
        await _admin.ExecuteAsync($"DROP TABLE IF EXISTS {Table}");
        await _admin.ExecuteAsync(EventBlock.Ddl(Table));
        _blocks = EventBatchSet.Nginx(BatchRows, RowsPerOp / BatchRows);
        _options = new NativeInserterOptions
        {
            Host = BenchEnv.Host,
            Port = int.Parse(BenchEnv.TcpPort, CultureInfo.InvariantCulture),
            User = BenchEnv.User,
            Password = BenchEnv.Password,
            Table = Table,
            Writers = Writers,
        };
        Console.WriteLine($"InsertNative: {_blocks.Batches.Count} x {BatchRows:N0} rows, {_blocks.RawBytes / 1e6:N0} MB raw, {Writers} writers, {_options.MaxRowsPerInsert:N0} rows/INSERT");
    }

    [IterationSetup]
    public void Truncate() => _admin.ExecuteAsync($"TRUNCATE TABLE {Table}").AsTask().GetAwaiter().GetResult();

    [Benchmark]
    public async Task<long> Native()
    {
        await using var inserter = new NativeInserter(_options, queueCapacity: 1, returnBatches: false);
        foreach (var batch in _blocks.Batches)
        {
            inserter.Submit(batch);
        }

        await inserter.CompleteAsync(CancellationToken.None);
        return inserter.Rows;
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _admin.ExecuteAsync($"DROP TABLE IF EXISTS {Table}");
        await _admin.DisposeAsync();
        _blocks.Dispose();
    }

    public void Dispose()
    {
        _admin?.Dispose();
        _blocks?.Dispose();
        GC.SuppressFinalize(this);
    }

    public static Workload WorkloadFor(ParameterInstances parameters)
    {
        // Raw bytes per row of the nginx fixture (223 B mean) × rows per op; exact bytes depend on which lines the
        // blocks hold, so the MB/s column is nominal here and rows/s is the number that matters.
        var meta = FixtureStore.Nginx1G();
        return new Workload((long)(RowsPerOp * meta.MeanLineBytes), RowsPerOp);
    }
}
