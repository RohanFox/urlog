using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Parameters;
using Urlog.Bench.Fixtures;
using Urlog.Bench.Reporting;
using Urlog.Core.Detection;
using Urlog.Core.Sources;

namespace Urlog.Bench.Benches;

/// <summary>
/// The detector on one file's sample, the first 64 KB and the last 4 KB, with every registered source: what
/// every upload and every dropped file pays before it is queued. One case per format, plus the text no source
/// claims (each source reads its 200 head lines and 50 tail lines and refuses them), UTF-16 (transcoded first) and
/// a file decided by its signature. Reported and listed as ungated in the baseline: the budget, 50 ms for the
/// slowest case, is asserted by a test over the fixture corpus.
/// </summary>
public class DetectFormat
{
    private IReadOnlyList<ILogSource> _sources = [];
    private byte[] _head = [];
    private byte[] _tail = [];
    private DetectionInput _input = new("x", 0);

    [Params("nginx", "syslog", "unclaimed-text", "utf16-syslog", "signature")]
    public string Case { get; set; } = "nginx";

    [GlobalSetup]
    public void Setup()
    {
        var catalog = new SourceCatalog();
        catalog.Add(Urlog.Sources.LogSourceRegistry.Registrations);
        _sources = FormatDetector.Instantiate(catalog);

        var meta = Case switch
        {
            "nginx" => FixtureStore.Nginx1G(),
            "unclaimed-text" => FixtureStore.Generic1G(),
            _ => FixtureStore.Syslog1G(),
        };
        using var file = new FileStream(meta.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _head = new byte[DetectionSample.HeadBytes];
        file.ReadExactly(_head);
        _tail = new byte[DetectionSample.TailBytes];
        file.Seek(-_tail.Length, SeekOrigin.End);
        file.ReadExactly(_tail);
        var name = "sample.log";
        var length = meta.Bytes;

        if (Case == "utf16-syslog")
        {
            // 64 KB of UTF-16 holds half the text; cut on a character so the sample is what a real file would give.
            var text = Encoding.UTF8.GetString(_head.AsSpan(0, _head.Length / 2));
            byte[] encoded = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)];
            _head = encoded[..(Math.Min(encoded.Length, DetectionSample.HeadBytes) & ~1)];
            _tail = Encoding.Unicode.GetBytes(Encoding.UTF8.GetString(_tail.AsSpan(_tail.AsSpan().IndexOf((byte)'\n') + 1)));
            length = 2L * meta.Bytes;
        }
        else if (Case == "signature")
        {
            "%PDF-1.7\n"u8.CopyTo(_head);
            name = "report.pdf";
        }

        _input = new DetectionInput(name, length);
    }

    [Benchmark]
    public int Detect() => (int)FormatDetector.Detect(_head, _tail, _input, _sources).Kind;

    public static Workload WorkloadFor(ParameterInstances parameters) => new(DetectionSample.HeadBytes + DetectionSample.TailBytes, 1);
}
