using System.Text;
using Urlog.Bench.Fixtures;
using Urlog.Bench.Stub;
using Urlog.Core.Pipeline;

namespace Urlog.Bench.Tests;

public sealed class FixtureGeneratorTests : IDisposable
{
    private const long SampleBytes = 8L << 20; // 8 MiB: ~35k nginx lines, ~70k syslog lines

    // Pinned on 2026-11-04 from generator version 1. A change here means every baseline number is void.
    private const string NginxSampleSha256 = "4fbbfd8896e8a90cdf36ec2a71a618aa158be2960a4d7b5f76967d10f370218e";
    private const string SyslogSampleSha256 = "ba875f959977ac5efd92ebd588c775d157574de2feeb48ff9ae513f92d296418"; // generator v2

    private const string GenericSampleSha256 = "14b7ac57ea22ed6a4f3f29d56736f2a8734a3f47401a2cb77714e1f447ca1ed5"; // generator v1

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "urlog-bench-tests-" + Guid.NewGuid().ToString("N"));

    public FixtureGeneratorTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void NginxIsDeterministicAndPinned()
    {
        var a = FixtureStore.Generate(Path.Combine(_dir, "a.log"), new NginxGen(FixtureStore.NginxSeed), SampleBytes);
        var b = FixtureStore.Generate(Path.Combine(_dir, "b.log"), new NginxGen(FixtureStore.NginxSeed), SampleBytes);
        Assert.Equal(a.Sha256, b.Sha256);
        Assert.Equal(a.Bytes, b.Bytes);
        Assert.Equal(a.Lines, b.Lines);
        AssertPinned(NginxSampleSha256, a.Sha256, "nginx");
    }

    [Fact]
    public void SyslogIsDeterministicAndPinned()
    {
        var a = FixtureStore.Generate(Path.Combine(_dir, "a.log"), new SyslogGen(FixtureStore.SyslogSeed), SampleBytes);
        var b = FixtureStore.Generate(Path.Combine(_dir, "b.log"), new SyslogGen(FixtureStore.SyslogSeed), SampleBytes);
        Assert.Equal(a.Sha256, b.Sha256);
        AssertPinned(SyslogSampleSha256, a.Sha256, "syslog");
    }

    [Fact]
    public void GenericIsDeterministicAndPinned()
    {
        var a = FixtureStore.Generate(Path.Combine(_dir, "a.log"), new GenericGen(FixtureStore.GenericSeed), SampleBytes);
        var b = FixtureStore.Generate(Path.Combine(_dir, "b.log"), new GenericGen(FixtureStore.GenericSeed), SampleBytes);
        Assert.Equal(a.Sha256, b.Sha256);
        Assert.Equal(a.Counts, b.Counts);
        AssertPinned(GenericSampleSha256, a.Sha256, "generic");
    }

    [Fact]
    public void DifferentSeedsDiffer()
    {
        var a = FixtureStore.Generate(Path.Combine(_dir, "a.log"), new NginxGen(1), 256 << 10);
        var b = FixtureStore.Generate(Path.Combine(_dir, "b.log"), new NginxGen(2), 256 << 10);
        Assert.NotEqual(a.Sha256, b.Sha256);
    }

    [Fact]
    public void NginxDistributionMatchesTicket()
    {
        var meta = FixtureStore.Generate(Path.Combine(_dir, "nginx.log"), new NginxGen(FixtureStore.NginxSeed), SampleBytes);
        var lines = File.ReadAllLines(meta.Path);

        Assert.Equal(meta.Lines, lines.Length);
        Assert.InRange(meta.MeanLineBytes, 180, 260);
        Assert.InRange(meta.OddityLines / (double)meta.Lines, 0.04, 0.065);

        var wellFormed = lines.Count(l => l.Contains("] \"", StringComparison.Ordinal) && l.EndsWith('"'));
        Assert.Equal(lines.Length, wellFormed);
        Assert.Contains(lines, l => l.Contains("2001:db8:", StringComparison.Ordinal) || l.StartsWith("fe80::", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Length > 1024);
        Assert.Contains(lines, l => l.Contains("\\x22", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("\"-\" 400 0 \"-\" \"-\"", StringComparison.Ordinal));
    }

    [Fact]
    public void SyslogDistributionMatchesTicket()
    {
        var meta = FixtureStore.Generate(Path.Combine(_dir, "syslog.log"), new SyslogGen(FixtureStore.SyslogSeed), SampleBytes);
        var bytes = File.ReadAllBytes(meta.Path);
        var text = Encoding.UTF8.GetString(bytes);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(meta.Lines, lines.Length);
        Assert.InRange(meta.MeanLineBytes, 100, 150);
        Assert.InRange(meta.OddityLines / (double)meta.Lines, 0.04, 0.065);

        var withPri = lines.Count(l => l.StartsWith('<'));
        Assert.InRange(withPri / (double)lines.Length, 0.98, 1.0);
        var rfc5424 = lines.Count(l => l.Contains(">1 2026-", StringComparison.Ordinal));
        Assert.InRange(rfc5424 / (double)lines.Length, 0.10, 0.16);
        Assert.Contains(lines, l => l.EndsWith('\r'));
        Assert.Contains(lines, l => l.Contains("Ünïcödé", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Length > 1024);
        Assert.Contains(lines, l => l.Contains("eventSource=\"App\\\"lication\"", StringComparison.Ordinal));
    }

    [Fact]
    public void StubSourceCountsLinesWithoutAllocating()
    {
        var meta = FixtureStore.Generate(Path.Combine(_dir, "nginx.log"), new NginxGen(FixtureStore.NginxSeed), 1 << 20);
        var data = File.ReadAllBytes(meta.Path);
        var source = new StubSource("nginx");
        var writer = new NullBatchWriter();
        var ctx = InMemoryChunks.Context("web-01", writer);

        InMemoryChunks.Parse(source, data, ctx, writer, maxChunk: 100_000); // warm: JIT, first chunk
        var warmRows = writer.Rows;
        var warmFields = source.Fields;
        var before = GC.GetAllocatedBytesForCurrentThread();
        InMemoryChunks.Parse(source, data, ctx, writer, maxChunk: 100_000);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(meta.Lines, warmRows);
        Assert.Equal(meta.Lines * 2, writer.Rows);
        Assert.Equal(warmFields * 2, source.Fields);
        Assert.True(warmFields > meta.Lines * 8, "a combined log line has more than 8 delimiters");
        Assert.Equal(meta.Bytes - meta.Lines, writer.RawBytes / 2); // every byte but the newlines went through raw, twice
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void InMemoryChunksNeverSplitALine()
    {
        var meta = FixtureStore.Generate(Path.Combine(_dir, "nginx.log"), new NginxGen(FixtureStore.NginxSeed), 1 << 20);
        var data = File.ReadAllBytes(meta.Path);
        var chunks = 0;
        var rest = data.AsSpan();
        while (!rest.IsEmpty)
        {
            var len = InMemoryChunks.NextChunkLength(rest, Urlog.Core.Sources.Framing.Line, 100_000);
            Assert.Equal((byte)'\n', rest[len - 1]);
            Assert.InRange(len, 1, 100_000);
            chunks++;
            rest = rest[len..];
        }

        Assert.True(chunks >= 10, "1 MiB in 100 KB chunks should take at least 10 chunks");
    }

    private static void AssertPinned(string pinned, string actual, string which)
    {
        // Full hash in the message: a generator change must be pinned deliberately, and xunit truncates Assert.Equal diffs.
        Assert.True(pinned == actual, $"{which} sample sha256 is {actual}, pinned {pinned}. If the generator changed on purpose, bump its Version, re-pin here, and re-measure every baseline.");
    }
}
