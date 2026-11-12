using Urlog.Core.Pipeline;
using Urlog.Core.Sources;

namespace Urlog.Core.Tests;

/// <summary>
/// The logical <c>message</c> is a slice of <c>raw</c> (<c>msg_off</c>, <c>msg_len</c>) or, for a source whose
/// message is not a byte slice of its record, the stored <c>message_text</c>. The writer enforces that a row has one
/// or the other and that the slice lies inside the row's raw bytes: a violation is a plugin bug and throws.
/// </summary>
public sealed class MessageSliceTests
{
    private static PipelineOptions Options => new() { ChunkBytes = 4096, BatchRows = 8, Parallelism = 1, InFlightChunks = 3, InFlightBatches = 1 };

    [Fact]
    public void SliceAndTextAreBothAccepted()
    {
        var buffers = new PipelineBuffers(Options);
        var sink = new CollectingBatchSink();
        var writer = new EventBatchWriter(buffers.Batches, sink);
        writer.Bind(new ParseContext { CaseId = 1 });

        writer.AppendString(EventColumn.Raw, "<34>Dec 31 23:59:58 h sshd[1]: Failed password"u8);
        writer.AppendMessage(31, 15);
        writer.CommitRow();

        writer.AppendString(EventColumn.Raw, "<Event><EventID>4625</EventID></Event>"u8);
        writer.AppendString(EventColumn.MessageText, "An account failed to log on."u8);
        writer.CommitRow();

        writer.AppendString(EventColumn.Raw, ""u8); // an empty record: an empty slice, which reads as the empty message_text
        writer.AppendMessage(0, 0);
        writer.CommitRow();
        writer.Flush();

        var batch = Assert.Single(sink.Batches);
        Assert.Equal([31U, 0U, 0U], batch.MsgOff[..3]);
        Assert.Equal([15U, 0U, 0U], batch.MsgLen[..3]);
        Assert.Equal("Failed password", batch.Strings(EventColumn.Raw).RowString(0)[31..]);
        Assert.Equal("", batch.Strings(EventColumn.MessageText).RowString(0));
        Assert.Equal("An account failed to log on.", batch.Strings(EventColumn.MessageText).RowString(1));
        sink.ReturnAll();
        writer.Close();
    }

    [Fact]
    public void ASliceOutsideTheRawBytesIsAPluginBug()
    {
        var buffers = new PipelineBuffers(Options);
        var writer = new EventBatchWriter(buffers.Batches, new CountingBatchSink());
        writer.Bind(new ParseContext { CaseId = 1 });
        writer.AppendString(EventColumn.Raw, "short"u8);
        writer.AppendMessage(2, 10);
        var ex = Assert.Throws<InvalidOperationException>(writer.CommitRow);
        Assert.Contains("outside", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASliceAndATextOnTheSameRowIsAPluginBug()
    {
        var buffers = new PipelineBuffers(Options);
        var writer = new EventBatchWriter(buffers.Batches, new CountingBatchSink());
        writer.Bind(new ParseContext { CaseId = 1 });
        writer.AppendString(EventColumn.Raw, "a line"u8);
        writer.AppendMessage(0, 6);
        writer.AppendString(EventColumn.MessageText, "a line"u8);
        Assert.Throws<InvalidOperationException>(writer.CommitRow);
    }

    [Fact]
    public void NegativeSlicesAreRejectedAtOnce()
    {
        var buffers = new PipelineBuffers(Options);
        var writer = new EventBatchWriter(buffers.Batches, new CountingBatchSink());
        writer.Bind(new ParseContext { CaseId = 1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.AppendMessage(-1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.AppendMessage(0, -1));
    }

    [Fact]
    public void TheScratchTableDdlListsEveryColumnAndTheAlias()
    {
        var ddl = EventSchema.CreateTable("t", index: true);
        foreach (var column in EventColumns.All)
        {
            Assert.Contains($"  {EventColumns.Name(column)} {EventColumns.ClickHouseType(column)}", ddl, StringComparison.Ordinal);
        }

        Assert.Contains("message String ALIAS if(msg_len = 0, message_text, substring(raw, msg_off + 1, msg_len))", ddl, StringComparison.Ordinal);
        Assert.Contains(EventSchema.TextIndex, ddl, StringComparison.Ordinal);
        Assert.DoesNotContain(EventSchema.TextIndex, EventSchema.CreateTable("t", index: false), StringComparison.Ordinal);
        Assert.Equal(EventColumns.Count, EventColumns.All.Length);
        Assert.Equal(EventColumns.StringColumnCount, EventColumns.All.ToArray().Count(EventColumns.IsString));
    }
}
