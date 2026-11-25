using System.Buffers;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using Urlog.Core.Detection;
using Urlog.Core.Metadata;
using Urlog.Core.Pipeline;
using Urlog.Core.Sources;

namespace Urlog.Core.Tests.Detection;

/// <summary>
/// Control bytes alone never make a text file "not a log" (design record, "Format detection"). A log is written
/// by whoever was attacked or attacking, and by machines that crash: one NUL, a NUL in every line, a block of
/// NULs where the power went, junk before, after or inside the lines. Each of these stays a log for the generic
/// source and says in its flags that it holds control bytes. What is not text stays "not a log".
/// </summary>
public sealed class ControlByteDetectionTests
{
    private const int Window = DetectionSample.WindowBytes;

    private static readonly byte[] Log = Lines(3000);

    /// <summary>Lines in the shape of an application log, about 107 bytes each.</summary>
    private static byte[] Lines(int count, int start = 0)
    {
        var text = new StringBuilder();
        for (var i = start; i < start + count; i++)
        {
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"2026-11-07T06:{i / 60 % 60:00}:{i % 60:00}+00:00 web-01 app[{1000 + (i % 500)}]: request {i} handled in {i % 900} ms for user u{i % 77} path /shop/item/{i}\n");
        }

        return Encoding.ASCII.GetBytes(text.ToString());
    }

    /// <summary>Bytes without any pattern, the same on every run: what compressed or encrypted data looks like.</summary>
    private static byte[] Junk(int length, int seed = 20261125)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static DetectionResult Detect(byte[] file, string name = "app.log") => DetectionSample.DetectBytes(file, name, []);

    private static void IsAFlaggedLog(DetectionResult result)
    {
        Assert.Equal((FileKind.Log, FormatDetector.GenericSourceId, ConfidenceBand.Fallback), (result.Kind, result.SourceId, result.Band));
        Assert.Contains(FormatDetector.FlagBinaryBytes, result.Flags);
        Assert.Contains("holds control bytes", result.Message, StringComparison.Ordinal);
    }

    private static void IsNotALog(DetectionResult result)
    {
        Assert.Equal((FileKind.Artifact, "unknown-binary"), (result.Kind, result.Format));
    }

    [Fact]
    public void ACleanLogIsALogWithoutTheFlag()
    {
        var result = Detect(Log);
        Assert.Equal((FileKind.Log, FormatDetector.GenericSourceId), (result.Kind, result.SourceId));
        Assert.Empty(result.Flags);
    }

    [Fact]
    public void OneNulDoesNotMakeALogNotALog()
    {
        byte[] file = [.. Log[..1000], 0, .. Log[1000..]];
        IsAFlaggedLog(Detect(file));

        // Nor in a file of a few lines, which is the whole of what an attacker may have been able to write.
        byte[] small = [.. Encoding.ASCII.GetBytes("2026-11-08T10:00:06+00:00 before the NUL "), 0, .. Encoding.ASCII.GetBytes(" after the NUL\n2026-11-08T10:00:07+00:00 a bell \a here\n")];
        IsAFlaggedLog(Detect(small));
    }

    [Fact]
    public void ANulInEveryLineIsStillALog()
    {
        var file = (byte[])Log.Clone();
        for (var i = 0; i < file.Length; i++)
        {
            if (file[i] == (byte)'\n' && i + 1 < file.Length)
            {
                file[i + 1] = 0;
            }
        }

        IsAFlaggedLog(Detect(file));
    }

    [Fact]
    public void ControlBytesScatteredThroughALogLeaveItALog()
    {
        var file = (byte[])Log.Clone();
        byte[] controls = [0x01, 0x02, 0x07, 0x08, 0x0B, 0x10];
        for (var i = 100; i < file.Length; i += 200)
        {
            file[i] = controls[i / 200 % controls.Length];
        }

        IsAFlaggedLog(Detect(file));
    }

    [Theory]
    [InlineData(4 << 10)]
    [InlineData(64 << 10)]
    [InlineData(1 << 20)]
    public void ABlockOfNulsInTheMiddleOfALogLeavesItALog(int block)
    {
        // What a crash or a power loss leaves: the file's blocks were allocated and never written.
        byte[] file = [.. Log[..150_000], .. new byte[block], .. Log[150_000..]];
        var result = Detect(file);
        Assert.Equal((FileKind.Log, FormatDetector.GenericSourceId), (result.Kind, result.SourceId));
    }

    [Theory]
    [InlineData(4 << 10)]
    [InlineData(64 << 10)]
    [InlineData(1 << 20)]
    public void ATrailingBlockOfNulsLeavesALogALog(int block)
    {
        byte[] file = [.. Log, .. new byte[block]];
        IsAFlaggedLog(Detect(file));
    }

    [Theory]
    [InlineData(4 << 10)]
    [InlineData(1 << 20)]
    public void ALogThatBeginsWithABlockOfNulsIsALog(int block)
    {
        byte[] file = [.. new byte[block], .. Log];
        IsAFlaggedLog(Detect(file));
    }

    [Fact]
    public void AFileOfNulsAloneIsNotALog()
    {
        IsNotALog(Detect(new byte[300_000]));
        IsNotALog(Detect(new byte[64]));
    }

    [Theory]
    [InlineData(64 << 10)]
    [InlineData(200 << 10)]
    [InlineData(600 << 10)]
    public void JunkBeforeALogDoesNotHideTheLog(int junk)
    {
        byte[] file = [.. Junk(junk), .. Log];
        var result = Detect(file);
        IsAFlaggedLog(result);
        Assert.Contains("the start of the file does not read as text", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(64 << 10)]
    [InlineData(600 << 10)]
    public void JunkAfterALogDoesNotChangeWhatItIs(int junk)
    {
        // The start reads as text, so nothing further is looked at; the junk is met by the parser.
        byte[] file = [.. Log, .. Junk(junk)];
        var result = Detect(file);
        Assert.Equal((FileKind.Log, FormatDetector.GenericSourceId), (result.Kind, result.SourceId));
    }

    [Fact]
    public void JunkInTheMiddleOfALogDoesNotChangeWhatItIs()
    {
        byte[] file = [.. Log[..160_000], .. Junk(Window), .. Log[160_000..]];
        Assert.Equal((FileKind.Log, FormatDetector.GenericSourceId), (Detect(file).Kind, Detect(file).SourceId));
    }

    [Fact]
    public void JunkAtBothEndsWithTheLogInTheMiddleIsALog()
    {
        // Neither the start nor the end reads as text; the window at half the length does.
        byte[] file = [.. Junk(300 << 10, 1), .. Log, .. Junk(300 << 10, 2)];
        var result = Detect(file);
        IsAFlaggedLog(result);
        Assert.Contains("further in", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WhatIsNotTextStaysNotALog()
    {
        // Compressed or encrypted bytes: about one in ten is a control byte, and line breaks come as often as in text.
        IsNotALog(Detect(Junk(256 << 10)));
        IsNotALog(Detect(Junk(600)));

        // Numbers as a program stores them.
        var packed = new MemoryStream();
        using (var writer = new BinaryWriter(packed, Encoding.ASCII, leaveOpen: true))
        {
            for (var i = 0; i < 12_000; i++)
            {
                writer.Write(i);
                writer.Write((long)i * 7919);
                writer.Write(i / 3.0);
            }
        }

        IsNotALog(Detect(packed.ToArray()));

        // Records with text in them among binary fields, as a database page or an executable has: under a tenth
        // of the bytes are control bytes, spread through the text.
        var records = new MemoryStream();
        for (var i = 0; i < 4000; i++)
        {
            records.Write([0x01, (byte)(i & 0x7F), 0x00, 0x04, 0x02]);
            records.Write(Encoding.ASCII.GetBytes($"row {i} of the table with a name and a value\n"));
        }

        IsNotALog(Detect(records.ToArray()));
    }

    [Fact]
    public void TheLimitIsThreePercentOfWhatIsLeftOutsidePadding()
    {
        // A control byte in every 40 bytes is 2.5 %: text. In every 25 it is 4 %: not text.
        var few = (byte[])Log.Clone();
        for (var i = 0; i < few.Length; i += 40)
        {
            few[i] = 0x01;
        }

        IsAFlaggedLog(Detect(few));

        var many = (byte[])Log.Clone();
        for (var i = 0; i < many.Length; i += 25)
        {
            many[i] = 0x01;
        }

        IsNotALog(Detect(many));
    }

    [Fact]
    public void ShortRunsOfNulsCountAndLongOnesArePadding()
    {
        // Runs of 15 NULs are content: one after every line is far over the limit.
        var chunks = new MemoryStream();
        foreach (var line in Encoding.ASCII.GetString(Log).Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(500))
        {
            chunks.Write(Encoding.ASCII.GetBytes(line + "\n"));
            chunks.Write(new byte[TextProbe.PaddingRun - 1]);
        }

        IsNotALog(Detect(chunks.ToArray()));

        // The same lines with runs of 16 are lines with padding between them.
        chunks = new MemoryStream();
        foreach (var line in Encoding.ASCII.GetString(Log).Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(500))
        {
            chunks.Write(Encoding.ASCII.GetBytes(line + "\n"));
            chunks.Write(new byte[TextProbe.PaddingRun]);
        }

        IsAFlaggedLog(Detect(chunks.ToArray()));
    }

    [Fact]
    public void TextWithControlBytesNeedsLinesAndTextWithoutNeedsNone()
    {
        // One line of 300 KiB with nothing odd in it is text, as it was before.
        var oneLine = Encoding.ASCII.GetBytes("{\"k\":[" + string.Join(',', Enumerable.Range(0, 14_000).Select(i => $"{{\"id\":{i},\"v\":\"x{i}\"}}")) + "]}");
        var clean = Detect(oneLine);
        Assert.Equal((FileKind.Log, FormatDetector.GenericSourceId), (clean.Kind, clean.SourceId));
        Assert.Empty(clean.Flags);

        // The same with a control byte now and then and no line break in 300 KiB is not text.
        var marked = (byte[])oneLine.Clone();
        for (var i = 500; i < marked.Length; i += 1000)
        {
            marked[i] = 0x02;
        }

        IsNotALog(Detect(marked));
    }

    [Fact]
    public void ALogASourceTakesIsFlaggedWhenItHoldsAnyControlByte()
    {
        byte[] file = [.. Log[..1000], 0x07, .. Log[1000..]];
        var result = DetectionSample.DetectBytes(file, "app.log", [new Fixed("alpha", 0.9f)]);
        Assert.Equal((FileKind.Log, "alpha"), (result.Kind, result.SourceId));
        Assert.Equal([FormatDetector.FlagBinaryBytes], result.Flags);

        var clean = DetectionSample.DetectBytes(Log, "app.log", [new Fixed("alpha", 0.9f)]);
        Assert.Empty(clean.Flags);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Utf16TextWithoutAMarkIsRecognisedBeforeAnyOfThisAndIsTextNotControlBytes(bool bigEndian)
    {
        // Half of the bytes of such a file are NUL, at every other position; that is the mark of the encoding.
        var encoding = bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode;
        var file = encoding.GetBytes(Encoding.ASCII.GetString(Log));
        var result = Detect(file, "export.log");

        Assert.Equal(bigEndian ? InputEncoding.Utf16BigEndian : InputEncoding.Utf16LittleEndian, result.Encoding);
        Assert.False(result.EncodingFromBom); // a guess, recorded as one
        Assert.Equal((FileKind.Log, FormatDetector.GenericSourceId), (result.Kind, result.SourceId));
        Assert.Empty(result.Flags);
    }

    [Fact]
    public void ABinaryThatMerelyHasManyNulsIsNotTakenForUtf16()
    {
        // NULs in runs and at any position, as structures padded to a boundary have them.
        var file = new MemoryStream();
        var random = new Random(7);
        for (var i = 0; i < 20_000; i++)
        {
            file.Write([(byte)random.Next(1, 255), (byte)random.Next(1, 255), (byte)random.Next(1, 255), 0, 0, 0, 0, 0, (byte)random.Next(1, 255), 0, 0]);
        }

        var result = Detect(file.ToArray(), "data");
        Assert.Equal(InputEncoding.Utf8, result.Encoding);
        IsNotALog(result);
    }

    [Fact]
    public void Utf16TextBehindJunkIsFoundFurtherIn()
    {
        // The head decides the encoding; when it is junk the file is read as bytes, where UTF-16 text is half NULs
        // in runs of one, which is not text. The file stays "not a log" and can be parsed as text by a person.
        byte[] file = [.. Junk(200 << 10), .. Encoding.Unicode.GetBytes(Encoding.ASCII.GetString(Log))];
        IsNotALog(Detect(file));
    }

    [Fact]
    public void ContentThatIsReadForwardOnlyIsLookedAtFurtherInToo()
    {
        // A compressed stream, and a member of an archive, cannot be read at any place. Windows are taken at
        // 256 KiB, 1 MiB, 4 MiB and 16 MiB of the content.
        byte[] content = [.. Junk(300 << 10), .. Lines(12_000)];
        var gzip = new MemoryStream();
        using (var z = new GZipStream(gzip, CompressionLevel.Fastest, leaveOpen: true))
        {
            z.Write(content);
        }

        var result = Detect(gzip.ToArray(), "app.log.gz");
        IsAFlaggedLog(result);
        Assert.Equal(["gzip"], result.Containers);

        var opened = 0;
        var forward = DetectionSample.Detect(
            () =>
            {
                opened++;
                return new ForwardOnly(content);
            },
            content.Length,
            "member.log",
            []);
        IsAFlaggedLog(forward);
        Assert.Equal(2, opened); // once for the start, once more to read on
    }

    [Fact]
    public void NothingFurtherIsReadWhenTheStartReadsAsTextOrASourceTakesTheFile()
    {
        var opened = 0;
        Stream Open()
        {
            opened++;
            return new MemoryStream(Log, writable: false);
        }

        DetectionSample.Detect(Open, Log.Length, "app.log", []);
        Assert.Equal(1, opened);

        byte[] junkFirst = [.. Junk(200 << 10), .. Log];
        opened = 0;
        DetectionSample.Detect(
            () =>
            {
                opened++;
                return new MemoryStream(junkFirst, writable: false);
            },
            junkFirst.Length,
            "app.log",
            [new Fixed("alpha", 0.9f)]);
        Assert.Equal(1, opened);
    }

    [Fact]
    public void TheRuleOfOneWindow()
    {
        Assert.Equal(WindowKind.Nothing, TextProbe.Read([]).Kind);
        Assert.Equal(WindowKind.Nothing, TextProbe.Read(new byte[5000]).Kind);
        Assert.Equal(WindowKind.Text, TextProbe.Read("no control bytes and no line break"u8).Kind);

        // 256 bytes must be left outside padding, or half of a window shorter than that.
        byte[] scrap = [.. new byte[60_000], .. Encoding.ASCII.GetBytes("a few bytes of text\n"), 0x01];
        Assert.Equal(WindowKind.Nothing, TextProbe.Read(scrap).Kind);
        byte[] small = [.. "one line of a log with a bell \a in it and no line break at its end"u8];
        Assert.Equal(WindowKind.Text, TextProbe.Read(small).Kind);

        // A line break every 4 KiB on average.
        byte[] longLines = [.. Enumerable.Repeat((byte)'a', 4000), 0x01, (byte)'\n', .. Enumerable.Repeat((byte)'b', 90)];
        Assert.Equal(WindowKind.Text, TextProbe.Read(longLines).Kind);
        byte[] longer = [.. Enumerable.Repeat((byte)'a', 4096), 0x01, (byte)'\n', .. Enumerable.Repeat((byte)'b', 4000)];
        Assert.Equal(WindowKind.NotText, TextProbe.Read(longer).Kind);
        byte[] tooLong = [.. Enumerable.Repeat((byte)'a', 9000), 0x01];
        Assert.Equal(WindowKind.NotText, TextProbe.Read(tooLong).Kind);

        var reading = TextProbe.Read([.. "abc"u8, 0, 0, .. new byte[100], .. "def\n"u8]);
        Assert.True(reading.Control);
        Assert.Equal(102, reading.ControlBytes);
        Assert.Equal(7, reading.Rest);
    }

    /// <summary>A source that gives every sample the same score.</summary>
    private sealed class Fixed(string id, float score) : ILogSource
    {
        public SourceInfo Info { get; } = new(id, [], id, 1);

        public Framing Framing => Framing.Line;

        public FieldSchema Schema { get; } = new(1, ImmutableArray<EventColumn>.Empty);

        public float Sniff(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, SniffContext ctx) => score;

        public void Parse(ReadOnlySequence<byte> framedChunk, ParseContext ctx, IBatchWriter batch) => throw new NotSupportedException();
    }

    /// <summary>Content that can be read from its start only.</summary>
    private sealed class ForwardOnly(byte[] content) : Stream
    {
        private readonly MemoryStream _inner = new(content, writable: false);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
