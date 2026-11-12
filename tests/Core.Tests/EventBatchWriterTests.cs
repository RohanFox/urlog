using System.Text;
using Urlog.Core.Pipeline;
using Urlog.Core.Sources;

namespace Urlog.Core.Tests;

public sealed class EventBatchWriterTests
{
    private static PipelineOptions Options(int batchRows) => new()
    {
        BatchRows = batchRows,
        Parallelism = 1,
        InFlightChunks = 3,
        InFlightBatches = 8,
    };

    [Fact]
    public void ExtrasOutgrowBatchRowsWithoutLosingPairs()
    {
        // The extras columns hold one row per pair: a source writing 20 pairs per event fills 20× BatchRows of them.
        var buffers = new PipelineBuffers(Options(100));
        var sink = new CollectingBatchSink();
        var writer = new EventBatchWriter(buffers.Batches, sink);
        writer.Bind(new ParseContext { CaseId = 1 });

        for (var row = 0; row < 100; row++)
        {
            for (var k = 0; k < 20; k++)
            {
                writer.AppendExtra("k"u8, "v"u8);
            }

            writer.AppendString(EventColumn.Raw, "x"u8);
            writer.CommitRow();
        }

        var batch = Assert.Single(sink.Batches);
        Assert.Equal(100, batch.RowCount);
        Assert.Equal(2000, batch.ExtrasCount);
        Assert.Equal(2000, batch.ExtrasKeys.RowCount);
        Assert.Equal("v", batch.ExtrasValues.RowString(1999));
        Assert.Equal(1980, batch.ExtrasOffsets[98]);
        sink.ReturnAll();

        // The grown batch comes back from the pool and takes the same load again.
        for (var row = 0; row < 100; row++)
        {
            writer.AppendExtra("a"u8, "b"u8);
            writer.AppendExtra("c"u8, "d"u8);
            writer.CommitRow();
        }

        var second = Assert.Single(sink.Batches); // ReturnAll emptied the sink
        Assert.Equal(200, second.ExtrasCount);
        sink.ReturnAll();
        writer.Close();
    }

    [Fact]
    public void FlushesAtBatchRowsAndOnDemand()
    {
        var buffers = new PipelineBuffers(Options(1000));
        var sink = new CountingBatchSink();
        var writer = new EventBatchWriter(buffers.Batches, sink);
        writer.Bind(new ParseContext { CaseId = 1 });

        for (var i = 0; i < 2500; i++)
        {
            writer.AppendString(EventColumn.Raw, "line"u8);
            writer.CommitRow();
        }

        Assert.Equal(2, sink.Batches);
        Assert.Equal(2000, sink.Rows);
        Assert.Equal(500, writer.RowCount);

        writer.Flush();
        Assert.Equal(3, sink.Batches);
        Assert.Equal(2500, sink.Rows);
        Assert.Equal(2500 * 4, sink.RawBytes);
        Assert.Equal(0, writer.RowCount);

        writer.Flush(); // nothing pending: no empty batch
        Assert.Equal(3, sink.Batches);
        writer.Close();
        Assert.Equal(0, buffers.Batches.Outstanding);
    }

    [Fact]
    public void ColumnsStayAlignedAndDefaultsApply()
    {
        var buffers = new PipelineBuffers(Options(100));
        var sink = new CollectingBatchSink();
        var writer = new EventBatchWriter(buffers.Batches, sink);
        writer.Bind(new ParseContext { CaseId = 7, HostId = 8, FileId = 9, ParseGeneration = 3, DefaultHost = "WEB-01"u8.ToArray() });

        // Row 0: everything set. Row 1: nothing set. Row 2: a column written twice, IPv4, extras.
        writer.AppendUInt64(EventColumn.FileOffset, 42);
        writer.AppendTimestamp(1_772_323_200_000);
        writer.AppendString(EventColumn.Host, "Mail-02"u8);
        writer.AppendString(EventColumn.Program, "Sshd"u8);
        writer.AppendString(EventColumn.Severity, "INFO"u8);
        writer.AppendUInt32(EventColumn.EventCode, 4625);
        writer.AppendString(EventColumn.EventAction, "Logon"u8);
        writer.AppendString(EventColumn.EventOutcome, "FAILURE"u8);
        writer.AppendString(EventColumn.UserName, "Administrator"u8);
        writer.AppendIp(EventColumn.SourceIp, [0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1]);
        writer.AppendString(EventColumn.ProcessName, "LSASS.EXE"u8);
        writer.AppendString(EventColumn.UrlPath, "/Admin"u8);
        writer.AppendUInt16(EventColumn.HttpStatus, 401);
        writer.AppendMessage(0, 3);
        writer.AppendString(EventColumn.Raw, "raw 0"u8);
        writer.CommitRow();

        writer.CommitRow();

        writer.AppendMessage(0, 1);
        writer.AppendMessage(1, 2); // the second write wins
        writer.AppendIp(EventColumn.DestinationIp, [10, 0, 0, 5]);
        writer.AppendExtra("k1"u8, "v1"u8);
        writer.AppendExtra("k2"u8, "v2"u8);
        writer.AppendString(EventColumn.Raw, "raw 2"u8);
        writer.CommitRow();
        writer.Flush();

        var batch = Assert.Single(sink.Batches);
        Assert.Equal(3, batch.RowCount);
        Assert.Equal([7UL, 7UL, 7UL], batch.CaseId[..3]);
        Assert.Equal([8UL, 8UL, 8UL], batch.HostId[..3]);
        Assert.Equal([9UL, 9UL, 9UL], batch.FileId[..3]);
        Assert.Equal([3U, 3U, 3U], batch.ParseGeneration[..3]);
        Assert.Equal([42UL, 0UL, 0UL], batch.FileOffset[..3]);
        Assert.Equal([1_772_323_200_000L, 0L, 0L], batch.Ts[..3]);
        Assert.Equal([4625U, 0U, 0U], batch.EventCode[..3]);
        Assert.Equal([(ushort)401, (ushort)0, (ushort)0], batch.HttpStatus[..3]);

        var host = batch.Strings(EventColumn.Host);
        Assert.Equal("mail-02", host.RowString(0));
        Assert.Equal("web-01", host.RowString(1)); // default host, lowercased
        Assert.Equal("web-01", host.RowString(2));
        Assert.Equal("sshd", batch.Strings(EventColumn.Program).RowString(0));
        Assert.Equal("info", batch.Strings(EventColumn.Severity).RowString(0));
        Assert.Equal("logon", batch.Strings(EventColumn.EventAction).RowString(0));
        Assert.Equal("failure", batch.Strings(EventColumn.EventOutcome).RowString(0));
        Assert.Equal("lsass.exe", batch.Strings(EventColumn.ProcessName).RowString(0));
        Assert.Equal("Administrator", batch.Strings(EventColumn.UserName).RowString(0)); // String, not LowCardinality: case kept
        Assert.Equal("/Admin", batch.Strings(EventColumn.UrlPath).RowString(0));
        Assert.Equal("", batch.Strings(EventColumn.Program).RowString(1));
        Assert.Equal([0U, 0U, 1U], batch.MsgOff[..3]);
        Assert.Equal([3U, 0U, 2U], batch.MsgLen[..3]);
        Assert.All(Enumerable.Range(0, 3), i => Assert.Equal("", batch.Strings(EventColumn.MessageText).RowString(i)));
        Assert.Equal(["raw 0", "", "raw 2"], Enumerable.Range(0, 3).Select(batch.Strings(EventColumn.Raw).RowString));

        Assert.Equal([0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1], batch.SourceIp[..16]);
        Assert.All(batch.SourceIp[16..48], b => Assert.Equal(0, b));
        Assert.Equal([0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 10, 0, 0, 5], batch.DestinationIp[32..48]);

        Assert.Equal([0, 0, 2], batch.ExtrasOffsets[..3]);
        Assert.Equal(2, batch.ExtrasCount);
        Assert.Equal("k1", batch.ExtrasKeys.RowString(0));
        Assert.Equal("v2", batch.ExtrasValues.RowString(1));
        Assert.Equal(10, batch.RawBytes);

        sink.ReturnAll();
        writer.Close();
        Assert.Equal(0, buffers.Batches.Outstanding);
    }

    [Fact]
    public void RejectWritesARawRowInTheSameBatch()
    {
        var buffers = new PipelineBuffers(Options(100));
        var sink = new CollectingBatchSink();
        var writer = new EventBatchWriter(buffers.Batches, sink);
        var ctx = new ParseContext { CaseId = 1, DefaultHost = "h"u8.ToArray() };
        writer.Bind(ctx);

        writer.AppendString(EventColumn.Raw, "good"u8);
        writer.CommitRow();
        ctx.Reject("gar\xbage"u8, 1234, "not syslog"u8);
        ctx.Reject("x"u8, 99, "a string reason");
        writer.Flush();

        var batch = Assert.Single(sink.Batches);
        Assert.Equal(3, batch.RowCount);
        Assert.Equal(2, ctx.Rejects);
        Assert.Equal(1234UL, batch.FileOffset[1]);
        Assert.Equal("gar\xbage", batch.Strings(EventColumn.Raw).RowString(1));
        Assert.Equal((0U, (uint)batch.Strings(EventColumn.Raw).Row(1).Length), (batch.MsgOff[1], batch.MsgLen[1])); // message = the whole raw
        Assert.Equal("h", batch.Strings(EventColumn.Host).RowString(1));
        Assert.Equal([0, 1, 2], batch.ExtrasOffsets[..3]);
        Assert.Equal("reject_reason", batch.ExtrasKeys.RowString(0));
        Assert.Equal("not syslog", batch.ExtrasValues.RowString(0));
        Assert.Equal("a string reason", batch.ExtrasValues.RowString(1));
        sink.ReturnAll();
        writer.Close();
    }

    [Fact]
    public void WrongColumnKindsThrow()
    {
        var buffers = new PipelineBuffers(Options(10));
        var writer = new EventBatchWriter(buffers.Batches, new CountingBatchSink());
        Assert.Throws<ArgumentException>(() => writer.AppendUInt64(EventColumn.MessageText, 1));
        Assert.Throws<ArgumentException>(() => writer.AppendUInt32(EventColumn.MsgOff, 1)); // the slice goes through AppendMessage
        Assert.Throws<ArgumentException>(() => writer.AppendUInt32(EventColumn.Ts, 1));
        Assert.Throws<ArgumentException>(() => writer.AppendUInt16(EventColumn.EventCode, 1));
        Assert.Throws<ArgumentException>(() => writer.AppendString(EventColumn.Ts, "x"u8));
        Assert.Throws<ArgumentException>(() => writer.AppendIp(EventColumn.Host, [1, 2, 3, 4]));
        Assert.Throws<ArgumentException>(() => writer.AppendIp(EventColumn.SourceIp, [1, 2, 3]));
        writer.Close();
    }

    [Fact]
    public void SteadyStateAppendsAllocateNothing()
    {
        var buffers = new PipelineBuffers(Options(10_000));
        var sink = new CountingBatchSink();
        var writer = new EventBatchWriter(buffers.Batches, sink);
        writer.Bind(new ParseContext { CaseId = 1, DefaultHost = "web-01"u8.ToArray() });
        var line = Encoding.UTF8.GetBytes("203.0.113.9 - - [01/Mar/2026:00:00:00 +0000] \"GET /index.html HTTP/1.1\" 200 5120 \"-\" \"curl/8.11.0\"");

        // Warm-up: every batch in the pool grows its string buffers once; afterwards nothing grows.
        for (var pass = 0; pass < 2; pass++)
        {
            Fill(writer, line, 100_000);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        Fill(writer, line, 100_000);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(300_000, sink.Rows);
        Assert.Equal(0, allocated);
        writer.Close();
    }

    private static void Fill(EventBatchWriter writer, byte[] line, int rows)
    {
        for (var i = 0; i < rows; i++)
        {
            writer.AppendUInt64(EventColumn.FileOffset, (ulong)i * 100);
            writer.AppendTimestamp(1_772_323_200_000 + i);
            writer.AppendString(EventColumn.Program, "nginx"u8);
            writer.AppendString(EventColumn.Severity, "info"u8);
            writer.AppendString(EventColumn.EventAction, "access"u8);
            writer.AppendString(EventColumn.EventOutcome, "success"u8);
            writer.AppendIp(EventColumn.SourceIp, [203, 0, 113, 9]);
            writer.AppendString(EventColumn.UrlPath, "/index.html"u8);
            writer.AppendUInt16(EventColumn.HttpStatus, 200);
            writer.AppendMessage(0, line.Length);
            writer.AppendString(EventColumn.Raw, line);
            writer.AppendExtra("http.user_agent"u8, "curl/8.11.0"u8);
            writer.CommitRow();
        }

        writer.Flush();
    }
}
