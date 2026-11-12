using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Parameters;
using Urlog.Bench.Fixtures;
using Urlog.Bench.Reporting;
using Urlog.Bench.Stub;
using Urlog.Core.Pipeline;
using Urlog.Core.Sources;
using Urlog.Sources.Generic;

namespace Urlog.Bench.Benches;

/// <summary>
/// Same shape as <see cref="TokeniseSyslog"/> on the mixed application-log fixture, through the real
/// <see cref="GenericSource"/>. The generic source is the one place a regex is allowed and it is outside the
/// throughput SLO: this entry is reported and listed as ungated in the baseline, never compared.
/// </summary>
public class TokeniseGeneric
{
    private byte[] _data = [];
    private GenericSource _source = new();
    private NullBatchWriter _writer = new();
    private ParseContext _ctx = new();

    [GlobalSetup]
    public void Setup()
    {
        _data = File.ReadAllBytes(FixtureStore.Generic1G().Path);
        _source = new GenericSource();
        _writer = new NullBatchWriter();
    }

    [Benchmark]
    public long Parse()
    {
        // A fresh context per op: the source keeps the last timestamp of the file in it.
        _ctx = InMemoryChunks.Context("app-01", _writer);
        var before = _writer.Rows;
        InMemoryChunks.Parse(_source, _data, _ctx, _writer);
        _writer.Flush();
        return _writer.Rows - before;
    }

    public static Workload WorkloadFor(ParameterInstances parameters)
    {
        var meta = FixtureStore.Generic1G();
        return new Workload(meta.Bytes, meta.Lines - meta.Counts![GenericGen.BlankLines]);
    }
}
