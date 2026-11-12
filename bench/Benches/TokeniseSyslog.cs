using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Parameters;
using Urlog.Bench.Fixtures;
using Urlog.Bench.Reporting;
using Urlog.Bench.Stub;
using Urlog.Sources.Syslog;
using Urlog.Core.Pipeline;
using Urlog.Core.Sources;

namespace Urlog.Bench.Benches;

/// <summary>
/// Same shape as <see cref="TokeniseNginx"/> on the syslog fixture: the whole 1 GiB in memory, 64 MB newline-aligned
/// chunks, single thread, through the real <c>IBatchWriter</c> contract into a <see cref="NullBatchWriter"/>. The real
/// <see cref="SyslogSource"/> (a stub before it existed): the 50 MB/s/core gate for syslog. Must stay at 0 B/row.
/// </summary>
public class TokeniseSyslog
{
    private byte[] _data = [];
    private SyslogSource _source = new();
    private NullBatchWriter _writer = new();
    private ParseContext _ctx = new();

    [GlobalSetup]
    public void Setup()
    {
        _data = File.ReadAllBytes(FixtureStore.Syslog1G().Path);
        _source = new SyslogSource();
        _writer = new NullBatchWriter();
        _ctx = InMemoryChunks.Context("db-01", _writer);
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
        var meta = FixtureStore.Syslog1G();
        return new Workload(meta.Bytes, meta.Lines);
    }
}
