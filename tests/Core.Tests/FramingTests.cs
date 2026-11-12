using System.Buffers;
using System.Text;
using Urlog.Core.Sources;

namespace Urlog.Core.Tests;

public sealed class FramingTests
{
    [Theory]
    [InlineData("a\nb\nc", 4)]
    [InlineData("a\nb\nc\n", 6)]
    [InlineData("no newline at all", 0)]
    [InlineData("", 0)]
    [InlineData("\n", 1)]
    [InlineData("a\r\nb\r\npartial", 6)]
    public void LineBoundaryIsAfterTheLastNewline(string text, int expected)
    {
        Assert.Equal(expected, Framing.Line.LastRecordEnd(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void LineRecordsStripLfAndCrLfAndKeepEmptyLines()
    {
        var records = Records("first\r\nsecond\n\nfourth-no-terminator", Framing.Line);
        Assert.Equal(["first", "second", "", "fourth-no-terminator"], records.Select(r => r.Text));
        Assert.Equal([0, 7, 14, 15], records.Select(r => r.Offset));
    }

    [Fact]
    public void MultiLineBoundaryIsTheStartOfTheLastRecord()
    {
        var framing = Framing.MultiLine("2026-"u8);
        var text = "2026-03-01 first\n  continuation\n2026-03-02 second\n  more\n";
        var end = framing.LastRecordEnd(Encoding.UTF8.GetBytes(text));
        Assert.Equal(text.IndexOf("2026-03-02", StringComparison.Ordinal), end);

        // A single record (or a leading continuation only) has no boundary yet.
        Assert.Equal(0, framing.LastRecordEnd("2026-03-01 only\n  cont\n"u8));
        Assert.Equal(0, framing.LastRecordEnd("  orphan continuation\n"u8));
    }

    [Fact]
    public void MultiLineRecordsRunUntilTheNextStart()
    {
        var framing = Framing.MultiLine("2026-"u8);
        var records = Records("2026-03-01 first\n  continuation\r\n2026-03-02 second\n2026-03-03 third", framing);
        Assert.Equal(["2026-03-01 first\n  continuation", "2026-03-02 second", "2026-03-03 third"], records.Select(r => r.Text));
        Assert.Equal([0, 33, 51], records.Select(r => r.Offset));
    }

    [Fact]
    public void BlankLineRecordsSplitOnBlankLinesOfEitherEnding()
    {
        var framing = Framing.BlankLineRecord;
        var text = "k=1\nv=a\n\nk=2\r\nv=b\r\n\r\n\nk=3\nv=c";
        Assert.Equal(text.IndexOf("k=3", StringComparison.Ordinal), framing.LastRecordEnd(Encoding.UTF8.GetBytes(text)));
        var records = Records(text, framing);
        Assert.Equal(["k=1\nv=a", "k=2\r\nv=b", "k=3\nv=c"], records.Select(r => r.Text));
    }

    [Fact]
    public void BinaryFramingDelegatesToTheSplitter()
    {
        var framing = Framing.Binary(new FixedSizeSplitter(4));
        var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        Assert.Equal(8, framing.LastRecordEnd(data));
        var records = Records(data[..8], framing);
        Assert.Equal(2, records.Count);
        Assert.Equal([1, 2, 3, 4], records[0].Bytes);
        Assert.Equal(4, records[1].Offset);
    }

    [Fact]
    public void MultiLineRejectsAnEmptyPattern()
    {
        Assert.Throws<ArgumentException>(() => Framing.MultiLine([]));
    }

    [Fact]
    public void MultiSegmentSequencesAreRejected()
    {
        var first = new Segment(new byte[] { 1, 2 });
        var second = first.Append(new byte[] { 3 });
        var seq = new ReadOnlySequence<byte>(first, 0, second, 1);
        var ctx = new ParseContext();
        Assert.Throws<ArgumentException>(() => seq.Records(Framing.Line, ctx));
    }

    private static List<(string Text, byte[] Bytes, long Offset)> Records(string text, Framing framing) => Records(Encoding.UTF8.GetBytes(text), framing);

    private static List<(string Text, byte[] Bytes, long Offset)> Records(byte[] data, Framing framing)
    {
        var list = new List<(string, byte[], long)>();
        foreach (var r in new RecordEnumerator(data, framing, 0))
        {
            list.Add((Encoding.UTF8.GetString(r.Bytes), r.Bytes.ToArray(), r.FileOffset));
        }

        return list;
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
