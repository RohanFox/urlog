using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Parameters;
using System.Globalization;
using ClickHouse.Driver.Tcp;
using Urlog.Bench.Fixtures;
using Urlog.Bench.Reporting;
using Urlog.Bench.Stub;
using Urlog.Core.Ingest;
using Urlog.Core.Pipeline;
using Urlog.Sources.Nginx;

namespace Urlog.Bench.Benches;

/// <summary>
/// The real chunk pipeline on the 1 GiB nginx fixture with the stub source: pooled 64 MB chunks from disk, line framing,
/// up to 8 parser threads, 100k-row <c>EventBatch</c>es. Two sinks, reported side by side (ungated; the numbers go in the description of the change):
/// <list type="bullet">
/// <item><see cref="PipelineNginx1G"/>: batches land in a <see cref="CountingBatchSink"/>. The pipeline's own cost and
/// allocation; the pipeline's bound is ≤ 50 B/row here.</item>
/// <item><see cref="StubPipelineNginx1G"/>: batches go through the real <see cref="NativeInserter"/> (four writers,
/// Native blocks straight from the batch arrays, LZ4, 500k rows per INSERT) into the pinned schema with the text index.
/// Same name as the first stub pipeline so the numbers compare over time; it measured 275 B/row here through the
/// driver's column API, the floor the Native writer removes.</item>
/// </list>
/// </summary>
[SimpleJob(RunStrategy.Monitoring, warmupCount: 1, iterationCount: 3, invocationCount: 1)]
public class EndToEnd : IDisposable
{
    public const string Table = "bench_events_e2e";
    public const int Writers = 4;

    private string _path = "";
    private ClickHouseTcpClient _tcp = null!;
    private PipelineBuffers _buffers = null!;
    private ParsePipeline _pipeline = null!;
    private NginxSource _source = null!;
    private FileIdentity _file = null!;
    private NativeInserterOptions _inserter = null!;

    /// <summary>SLO sizing: 8 parsers (fewer on a smaller box), pools per <see cref="PipelineOptions"/> defaults, 4 writers.</summary>
    public static PipelineOptions Options { get; } = new()
    {
        Parallelism = Math.Min(Environment.ProcessorCount, 8),
        InFlightChunks = Math.Min(Environment.ProcessorCount, 8) + 2,
        Writers = Writers,
        InFlightBatches = Writers,
    };

    [GlobalSetup]
    public async Task Setup()
    {
        _path = FixtureStore.Nginx1G().Path;
        _tcp = new ClickHouseTcpClient(BenchEnv.TcpConnectionString);
        await _tcp.ExecuteAsync($"DROP TABLE IF EXISTS {Table}");
        await _tcp.ExecuteAsync(EventBlock.Ddl(Table));
        _buffers = new PipelineBuffers(Options);
        _pipeline = new ParsePipeline(_buffers);
        _source = new NginxSource();
        _file = new FileIdentity(CaseId: 1, HostId: 1, FileId: 1, ParseGeneration: 1, DefaultHost: "web-01");
        _inserter = new NativeInserterOptions
        {
            Host = BenchEnv.Host,
            Port = int.Parse(BenchEnv.TcpPort, CultureInfo.InvariantCulture),
            User = BenchEnv.User,
            Password = BenchEnv.Password,
            Table = Table,
            Writers = Writers,
        };
        Console.WriteLine($"EndToEnd: {Options.Parallelism} parsers, {Options.InFlightChunks} chunk buffers, {Options.BatchPoolSize} batches; pooled memory ~{Options.EstimatePoolBytes(223) / 1e6:N0} MB");
    }

    [IterationSetup]
    public void Truncate() => _tcp.ExecuteAsync($"TRUNCATE TABLE {Table}").AsTask().GetAwaiter().GetResult();

    [Benchmark]
    public async Task<long> PipelineNginx1G()
    {
        var sink = new CountingBatchSink();
        using var file = OpenFixture();
        var result = await _pipeline.RunAsync(file, _source, _file, sink);
        return result.Rows;
    }

    /// <summary>
    /// <see cref="PipelineNginx1G"/> with the job runner's <c>TimeRangeSink</c> in front of the counting sink: the
    /// difference between the two is what the scan of each batch's timestamp column for the file's least and
    /// greatest event time costs.
    /// </summary>
    [Benchmark]
    public async Task<long> PipelineNginx1GTimeRange()
    {
        var sink = new Urlog.Core.Jobs.TimeRangeSink(new CountingBatchSink());
        using var file = OpenFixture();
        var result = await _pipeline.RunAsync(file, _source, _file, sink);
        return sink.Max is null ? -1 : result.Rows;
    }

    [Benchmark]
    public async Task<long> StubPipelineNginx1G()
    {
        await using var sink = new NativeInserter(_inserter, Options);
        using var file = OpenFixture();
        var result = await _pipeline.RunAsync(file, _source, _file, sink);
        return result.Rows;
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _tcp.ExecuteAsync($"DROP TABLE IF EXISTS {Table}");
        await _tcp.DisposeAsync();
        _buffers.Batches.Dispose();
    }

    public void Dispose()
    {
        _tcp?.Dispose();
        _buffers?.Batches.Dispose();
        GC.SuppressFinalize(this);
    }

    public static Workload WorkloadFor(ParameterInstances parameters)
    {
        var meta = FixtureStore.Nginx1G();
        return new Workload(meta.Bytes, meta.Lines);
    }

    private FileStream OpenFixture() => new(_path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0, FileOptions.SequentialScan);
}
