using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Parameters;
using Urlog.Bench.Fixtures;
using Urlog.Bench.Reporting;
using Urlog.Bench.Stub;
using Urlog.Sources.Nginx;
using Urlog.Core.Pipeline;
using Urlog.Core.Sources;

namespace Urlog.Bench.Benches;

/// <summary>
/// Tokenise only, single thread, whole 1 GiB fixture in memory, 64 MB newline-aligned chunks through the real
/// <c>IBatchWriter</c> contract into a <see cref="NullBatchWriter"/>. Runs the real <see cref="NginxSource"/>:
/// this is the 50 MB/s/core gate for nginx (bench/baseline.json). Must stay at 0 B/row.
/// </summary>
public class TokeniseNginx
{
    private byte[] _data = [];
    private NginxSource _source = new();
    private NullBatchWriter _writer = new();
    private ParseContext _ctx = new();

    [GlobalSetup]
    public void Setup()
    {
        _data = File.ReadAllBytes(FixtureStore.Nginx1G().Path);
        _source = new NginxSource();
        _writer = new NullBatchWriter();
        _ctx = InMemoryChunks.Context("web-01", _writer);
    }

    [Benchmark]
    public long Parse()
    {
        var before = _writer.Rows;
        InMemoryChunks.Parse(_source, _data, _ctx, _writer);
        _writer.Flush();
        return _writer.Rows - before;
    }

    public static Workload WorkloadFor(ParameterInstances parameters)
    {
        var meta = FixtureStore.Nginx1G();
        return new Workload(meta.Bytes, meta.Lines);
    }
}
