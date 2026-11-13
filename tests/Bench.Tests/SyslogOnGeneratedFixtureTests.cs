using System.Buffers;
using Urlog.Bench.Fixtures;
using Urlog.Bench.Stub;
using Urlog.Core.Pipeline;
using Urlog.Core.Sources;
using Urlog.Sources.Syslog;

namespace Urlog.Bench.Tests;

/// <summary>
/// The real syslog tokeniser against the fixed-seed generator: every generated line parses (the generator's
/// oddities are all valid RFC 3164 / 5424 lines), nothing allocates per row, and the sniffer tells the two fixtures apart.
/// </summary>
public sealed class SyslogOnGeneratedFixtureTests : IDisposable
{
    private const long SampleBytes = 1 << 20;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "urlog-bench-tests-" + Guid.NewGuid().ToString("N"));

    public SyslogOnGeneratedFixtureTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void EveryGeneratedLineParsesWithoutAllocating()
    {
        var meta = FixtureStore.Generate(Path.Combine(_dir, "syslog.log"), new SyslogGen(FixtureStore.SyslogSeed), SampleBytes);
        var data = File.ReadAllBytes(meta.Path);
        var source = new SyslogSource();
        var writer = new NullBatchWriter();
        var ctx = InMemoryChunks.Context("db-01", writer);

        InMemoryChunks.Parse(source, data, ctx, writer, maxChunk: 100_000); // warm: JIT, first chunk
        Assert.Equal(meta.Lines, writer.Rows);
        Assert.Equal(0, ctx.Rejects);
        var crlf = data.AsSpan().Count("\r\n"u8);
        Assert.Equal(meta.Bytes - meta.Lines - crlf, writer.RawBytes); // every byte but the line terminators went through raw

        var allocated = AllocationProbe.Least(_ => InMemoryChunks.Parse(source, data, ctx, writer, maxChunk: 100_000));

        Assert.Equal(meta.Lines * (1 + AllocationProbe.Passes), writer.Rows);
        Assert.Equal(0, ctx.Rejects);
        Assert.Equal(0, allocated);
        Assert.True(writer.StringBytes > writer.RawBytes + (meta.Lines * (1 + AllocationProbe.Passes) * 8), "a generated line yields host, program, severity and facility as typed columns besides raw");
    }

    [Fact]
    public void GeneratedFixtureRowsCarryTheTypedColumns()
    {
        var meta = FixtureStore.Generate(Path.Combine(_dir, "syslog.log"), new SyslogGen(FixtureStore.SyslogSeed), 64 << 10);
        var data = File.ReadAllBytes(meta.Path);
        var options = new PipelineOptions { ChunkBytes = 1 << 20, BatchRows = 100_000, Parallelism = 1, InFlightChunks = 3, InFlightBatches = 4 };
        var buffers = new PipelineBuffers(options);
        var sink = new CollectingBatchSink();
        var writer = new EventBatchWriter(buffers.Batches, sink);
        var ctx = new ParseContext { CaseId = 1, HostId = 1, FileId = 1, DefaultHost = "db-01"u8.ToArray(), YearHint = 2026 };
        writer.Bind(ctx);
        new SyslogSource().Parse(new ReadOnlySequence<byte>(data), ctx, writer);
        writer.Flush();

        var batch = Assert.Single(sink.Batches);
        Assert.Equal(meta.Lines, batch.RowCount);
        var rfc5424 = 0;
        var postfix = 0;
        var noPri = 0;
        var longLines = 0;
        for (var i = 0; i < batch.RowCount; i++)
        {
            Assert.InRange(batch.Ts[i], 1_772_323_200_000L, 1_772_323_200_000L + (86_400_000L * 30)); // March 2026, as generated
            Assert.NotEqual("", batch.Strings(EventColumn.Host).RowString(i)); // every generated line carries a host
            var raw = batch.Strings(EventColumn.Raw).Row(i);
            if (raw.IndexOf(">1 2026-"u8) >= 0)
            {
                rfc5424++;
            }

            if (batch.Strings(EventColumn.Program).RowString(i) == "postfix")
            {
                postfix++;
            }

            if (raw[0] != (byte)'<')
            {
                noPri++;
                Assert.Equal("", batch.Strings(EventColumn.Severity).RowString(i));
            }
            else
            {
                Assert.NotEqual("", batch.Strings(EventColumn.Severity).RowString(i));
            }

            if (raw.Length > 1024)
            {
                longLines++;
            }
        }

        Assert.True(rfc5424 > 0, "the generator emits RFC 5424 lines");
        Assert.True(postfix > 0, "the generator emits postfix/smtpd lines");
        Assert.True(noPri > 0, "the generator emits lines without a PRI");
        Assert.True(longLines > 0, "the generator emits 1-2 KB messages");
        sink.ReturnAll();
        writer.Close();
    }

    [Fact]
    public void SniffSeparatesTheGeneratedFixtures()
    {
        var nginx = File.ReadAllBytes(FixtureStore.Generate(Path.Combine(_dir, "nginx.log"), new NginxGen(FixtureStore.NginxSeed), SampleBytes).Path);
        var syslog = File.ReadAllBytes(FixtureStore.Generate(Path.Combine(_dir, "syslog.log"), new SyslogGen(FixtureStore.SyslogSeed), SampleBytes).Path);
        var source = new SyslogSource();

        Assert.Equal(1.0f, Sniff(source, syslog));
        Assert.Equal(0f, Sniff(source, nginx));
    }

    private static float Sniff(SyslogSource source, byte[] file)
    {
        var head = file.AsSpan(0, Math.Min(file.Length, 64 << 10));
        var tail = file.AsSpan(Math.Max(0, file.Length - (4 << 10)));
        return source.Sniff(head, tail, new SniffContext("x.log", file.Length));
    }
}
