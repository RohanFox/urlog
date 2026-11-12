using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Parameters;
using Urlog.Bench.Fixtures;
using Urlog.Bench.Reporting;

namespace Urlog.Bench.Benches;

/// <summary>
/// Hardware baseline: sequential read of the 1 GiB nginx fixture, no parsing. GlobalSetup reads the file once
/// so every measured op is a warm page-cache read and runs are comparable; a cold number needs a fresh boot.
/// </summary>
public class DiskRead
{
    private readonly byte[] _buffer = new byte[1 << 20];
    private string _path = "";

    [GlobalSetup]
    public void Setup()
    {
        _path = FixtureStore.Nginx1G().Path;
        ReadNginx1G();
    }

    [Benchmark]
    public long ReadNginx1G()
    {
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0, FileOptions.SequentialScan);
        long total = 0;
        int n;
        while ((n = fs.Read(_buffer, 0, _buffer.Length)) > 0)
        {
            total += n;
        }

        return total;
    }

    public static Workload WorkloadFor(ParameterInstances parameters)
    {
        var meta = FixtureStore.Nginx1G();
        return new Workload(meta.Bytes, meta.Lines);
    }
}
