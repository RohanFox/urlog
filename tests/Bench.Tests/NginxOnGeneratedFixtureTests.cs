using System.Buffers;
using Urlog.Bench.Fixtures;
using Urlog.Bench.Stub;
using Urlog.Core.Pipeline;
using Urlog.Core.Sources;
using Urlog.Sources.Nginx;

namespace Urlog.Bench.Tests;

/// <summary>
/// The real nginx tokeniser against the fixed-seed generators: every generated line parses (the generator's
/// oddities are all valid combined-log lines), nothing allocates per row, and the sniffer tells the two fixtures apart.
/// </summary>
public sealed class NginxOnGeneratedFixtureTests : IDisposable
{
    private const long SampleBytes = 1 << 20;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "urlog-bench-tests-" + Guid.NewGuid().ToString("N"));

    public NginxOnGeneratedFixtureTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void EveryGeneratedLineParsesWithoutAllocating()
    {
        var meta = FixtureStore.Generate(Path.Combine(_dir, "nginx.log"), new NginxGen(FixtureStore.NginxSeed), SampleBytes);
        var data = File.ReadAllBytes(meta.Path);
        var source = new NginxSource();
        var writer = new NullBatchWriter();
        var ctx = InMemoryChunks.Context("web-01", writer);

        InMemoryChunks.Parse(source, data, ctx, writer, maxChunk: 100_000); // warm: JIT, first chunk
        Assert.Equal(meta.Lines, writer.Rows);
        Assert.Equal(0, ctx.Rejects);
        Assert.Equal(meta.Bytes - meta.Lines, writer.RawBytes); // every byte but the newlines went through raw

        var before = GC.GetAllocatedBytesForCurrentThread();
        InMemoryChunks.Parse(source, data, ctx, writer, maxChunk: 100_000);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(meta.Lines * 2, writer.Rows);
        Assert.Equal(0, ctx.Rejects);
        Assert.Equal(0, allocated);
        Assert.True(writer.StringBytes > writer.RawBytes + (meta.Lines * 2 * 20), "a combined line yields method, version, user agent and path as typed columns besides raw");
    }

    [Fact]
    public void GeneratedFixtureRowsCarryTheTypedColumns()
    {
        var meta = FixtureStore.Generate(Path.Combine(_dir, "nginx.log"), new NginxGen(FixtureStore.NginxSeed), 64 << 10);
        var data = File.ReadAllBytes(meta.Path);
        var options = new PipelineOptions { ChunkBytes = 1 << 20, BatchRows = 100_000, Parallelism = 1, InFlightChunks = 3, InFlightBatches = 4 };
        var buffers = new PipelineBuffers(options);
        var sink = new CollectingBatchSink();
        var writer = new EventBatchWriter(buffers.Batches, sink);
        var ctx = InMemoryChunks.Context("web-01", writer);
        writer.Bind(ctx);
        new NginxSource().Parse(new ReadOnlySequence<byte>(data), ctx, writer);
        writer.Flush();

        var batch = Assert.Single(sink.Batches);
        Assert.Equal(meta.Lines, batch.RowCount);
        var ipv6 = 0;
        var long1 = 0;
        for (var i = 0; i < batch.RowCount; i++)
        {
            Assert.InRange(batch.Ts[i], 1_772_323_200_000L, 1_772_323_200_000L + (86_400_000L * 30));
            Assert.InRange(batch.HttpStatus[i], (ushort)200, (ushort)503);
            Assert.Equal(batch.HttpStatus[i], batch.EventCode[i]);
            Assert.Equal("nginx", batch.Strings(EventColumn.Program).RowString(i));
            var ip = batch.SourceIp.AsSpan(i * 16, 16);
            Assert.False(ip.SequenceEqual(new byte[16]), "every generated client is an address");
            if (!(ip[10] == 0xFF && ip[11] == 0xFF))
            {
                ipv6++;
            }

            if (batch.Strings(EventColumn.Raw).Row(i).Length > 1024)
            {
                long1++;
            }
        }

        Assert.True(ipv6 > 0, "the generator emits IPv6 clients");
        Assert.True(long1 > 0, "the generator emits 1-4 KB user agents");
        sink.ReturnAll();
        writer.Close();
    }

    [Fact]
    public void FastScanAgreesWithTheGenericWalkOnTheGeneratedSample()
    {
        var meta = FixtureStore.Generate(Path.Combine(_dir, "nginx.log"), new NginxGen(FixtureStore.NginxSeed), SampleBytes);
        var data = File.ReadAllBytes(meta.Path);
        var fastStarts = new int[NginxLayout.MaxFields];
        var fastLengths = new int[NginxLayout.MaxFields];
        var slowStarts = new int[NginxLayout.MaxFields];
        var slowLengths = new int[NginxLayout.MaxFields];
        var lines = 0;
        foreach (var record in new RecordEnumerator(data, Framing.Line, 0))
        {
            lines++;
            Assert.True(NginxSource.TryScanCombined(record.Bytes, fastStarts, fastLengths, out var fast));
            Assert.Equal(NginxSource.Status.Ok, NginxSource.TokeniseGeneric(record.Bytes, NginxLayout.Combined, slowStarts, slowLengths, out var slow));
            Assert.Equal(slowStarts.AsSpan(0, 9).ToArray(), fastStarts.AsSpan(0, 9).ToArray());
            Assert.Equal(slowLengths.AsSpan(0, 9).ToArray(), fastLengths.AsSpan(0, 9).ToArray());
            Assert.Equal(slow.UnixMilliseconds, fast.UnixMilliseconds);
            Assert.Equal(slow.HttpStatus, fast.HttpStatus);
            Assert.Equal(slow.End, fast.End);
        }

        Assert.Equal(meta.Lines, lines);
    }

    [Fact]
    public void SniffSeparatesTheGeneratedFixtures()
    {
        var nginx = File.ReadAllBytes(FixtureStore.Generate(Path.Combine(_dir, "nginx.log"), new NginxGen(FixtureStore.NginxSeed), SampleBytes).Path);
        var syslog = File.ReadAllBytes(FixtureStore.Generate(Path.Combine(_dir, "syslog.log"), new SyslogGen(FixtureStore.SyslogSeed), SampleBytes).Path);
        var source = new NginxSource();

        Assert.InRange(Sniff(source, nginx), 0.99f, 1.0f);
        Assert.InRange(Sniff(source, syslog), 0f, 0.05f);
    }

    private static float Sniff(NginxSource source, byte[] file)
    {
        var head = file.AsSpan(0, Math.Min(file.Length, 64 << 10));
        var tail = file.AsSpan(Math.Max(0, file.Length - (4 << 10)));
        return source.Sniff(head, tail, new SniffContext("x.log", file.Length));
    }
}
