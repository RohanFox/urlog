using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Parameters;
using ClickHouse.Driver;
using ClickHouse.Driver.Compression;
using ClickHouse.Driver.Tcp;
using Urlog.Bench.Reporting;
using Urlog.Bench.Stub;

namespace Urlog.Bench.Benches;

/// <summary>
/// Insert path alone: one pre-built block per op into the pinned events schema. Both transports on the same
/// block (ADR 0002/0003): <see cref="Tcp"/> is columnar blocks over native TCP with LZ4; <see cref="Http"/> is
/// RowBinary rows over HTTP with LZ4. The table is truncated before every iteration so part counts stay flat.
/// Needs ClickHouse (compose.dev.yml); see <see cref="BenchEnv"/>.
/// </summary>
[SimpleJob(RunStrategy.Throughput, warmupCount: 2, iterationCount: 8, invocationCount: 4)]
public class Insert : IDisposable
{
    public const string Table = "bench_events";

    private static readonly ClickHouseTcpInsertOptions TcpOptions = new();

    private IColumn[] _columns = [];
    private object[][] _rows = [];
    private ClickHouseTcpClient _tcp = null!;
    private ClickHouseClient _http = null!;
    private InsertOptions _httpOptions = null!;

    [Params(100_000, 250_000, 500_000)]
    public int BlockSize { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _tcp = new ClickHouseTcpClient(BenchEnv.TcpConnectionString);
        _http = new ClickHouseClient(BenchEnv.HttpConnectionString);
        await _tcp.ExecuteAsync($"DROP TABLE IF EXISTS {Table}");
        await _tcp.ExecuteAsync(EventBlock.Ddl(Table));

        var block = EventBlockBuilder.Build(BlockSize);
        _columns = block.ToTcpColumns();
        _rows = block.ToHttpRows();
        _httpOptions = new InsertOptions
        {
            BatchSize = BlockSize,
            MaxDegreeOfParallelism = 1,
            Compressor = Lz4Compressor.Default,
            ColumnTypes = EventBlock.ColumnTypes,
            UseSchemaCache = true,
        };
    }

    [IterationSetup]
    public void Truncate() => _tcp.ExecuteAsync($"TRUNCATE TABLE {Table}").AsTask().GetAwaiter().GetResult();

    [Benchmark(Baseline = true)]
    public Task Tcp() => _tcp.InsertAsync($"INSERT INTO {Table} VALUES", _columns, TcpOptions).AsTask();

    [Benchmark]
    public Task<long> Http() => _http.InsertBinaryAsync($"default.{Table}", EventBlock.ColumnNames, _rows, _httpOptions);

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _tcp.ExecuteAsync($"DROP TABLE IF EXISTS {Table}");
        await _tcp.DisposeAsync();
        _http.Dispose();
    }

    public void Dispose()
    {
        _tcp?.Dispose();
        _http?.Dispose();
        GC.SuppressFinalize(this);
    }

    public static Workload WorkloadFor(ParameterInstances parameters)
    {
        var rows = (int)parameters.Items.Single(p => p.Name == nameof(BlockSize)).Value;
        return new Workload(EventBlockBuilder.RawBytes(rows), rows);
    }
}
