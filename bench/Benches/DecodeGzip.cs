using System.IO.Compression;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Parameters;
using Urlog.Bench.Fixtures;
using Urlog.Bench.Reporting;
using Urlog.Core.Pipeline;

namespace Urlog.Bench.Benches;

/// <summary>
/// What decoding costs before any parsing: the 1 GiB nginx fixture as gzip, read to the end and discarded. Rates
/// are in decoded bytes, the unit the throughput SLO uses. <see cref="Runtime"/> is the runtime's own stream and
/// <see cref="Pipeline"/> is the decoder the parse pipeline opens, so the difference between the two is the price
/// of whatever the pipeline's decoder does on top. Reported and listed as ungated: it measures a library.
/// </summary>
public class DecodeGzip
{
    private readonly byte[] _buffer = new byte[1 << 20];
    private string _path = "";

    [GlobalSetup]
    public void Setup()
    {
        _path = FixtureStore.Nginx1GGzip();
        Runtime();
    }

    [Benchmark(Baseline = true)]
    public long Runtime()
    {
        using var raw = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        using var decoded = new GZipStream(raw, CompressionMode.Decompress);
        return Drain(decoded);
    }

    [Benchmark]
    public long Pipeline()
    {
        using var raw = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        using var decoded = InputDecoding.Decompress(raw, CompressionKind.Gzip);
        return Drain(decoded);
    }

    public static Workload WorkloadFor(ParameterInstances parameters)
    {
        var meta = FixtureStore.Nginx1G();
        return new Workload(meta.Bytes, meta.Lines);
    }

    private long Drain(Stream decoded)
    {
        long total = 0;
        int n;
        while ((n = decoded.Read(_buffer, 0, _buffer.Length)) > 0)
        {
            total += n;
        }

        return total;
    }
}
