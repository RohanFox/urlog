using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using Urlog.Core.Detection;
using Urlog.Core.Pipeline;
using Urlog.Core.Sources;

namespace Urlog.Core.Tests.Detection;

/// <summary>The decision rules of the detector, with sources that return a fixed score: what wins, in which order, and what is recorded.</summary>
public sealed class FormatDetectorTests
{
    private static readonly byte[] Text = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("a line of ordinary text 12345\n", 40)));

    [Fact]
    public void AConfidentSourceWinsAndEveryScoreIsKept()
    {
        var result = Detect(Text, "app.log", new Scored("alpha", 0.3f), new Scored("beta", 0.93f), new Scored("generic", 0f));

        Assert.Equal(FileKind.Log, result.Kind);
        Assert.Equal("beta", result.SourceId);
        Assert.Equal(ConfidenceBand.Confident, result.Band);
        Assert.Equal(0.93f, result.Confidence);
        Assert.Equal(DetectionBasis.Sniff, result.Basis);
        Assert.Equal([new SourceScore("alpha", 0.3f, null, true), new SourceScore("beta", 0.93f, null, true)], result.Scores);
        Assert.Equal(["alpha", "beta", "generic"], result.RegisteredSources);
        Assert.Equal(FormatDetector.Version, result.DetectorVersion);
        Assert.Empty(result.Flags);
        Assert.Null(result.Message);
    }

    [Theory]
    [InlineData(0.8f, ConfidenceBand.Confident)]
    [InlineData(0.79f, ConfidenceBand.Probable)]
    [InlineData(0.5f, ConfidenceBand.Probable)]
    public void TheBandsMeetAtPointEightAndPointFive(float score, ConfidenceBand band)
    {
        var result = Detect(Text, "app.log", new Scored("alpha", score));
        Assert.Equal("alpha", result.SourceId);
        Assert.Equal(band, result.Band);
    }

    [Fact]
    public void BelowPointFiveTheGenericSourceTakesTheFileAndTheBestLoserIsRecorded()
    {
        var result = Detect(Text, "app.log", new Scored("alpha", 0.49f), new Scored("beta", 0.1f), new Throwing("generic"));

        Assert.Equal(FileKind.Log, result.Kind);
        Assert.Equal(FormatDetector.GenericSourceId, result.SourceId);
        Assert.Equal(ConfidenceBand.Fallback, result.Band);
        Assert.Equal(0.49f, result.Confidence);
        Assert.Equal(2, result.Scores.Length); // the fallback is never asked to score
    }

    [Fact]
    public void TwoConfidentSourcesMakeTheFirstRegisteredAProbableWinner()
    {
        var result = Detect(Text, "app.log", new Scored("alpha", 0.9f), new Scored("beta", 0.9f));
        Assert.Equal("alpha", result.SourceId);
        Assert.Equal(ConfidenceBand.Probable, result.Band);

        result = Detect(Text, "app.log", new Scored("alpha", 0.85f), new Scored("beta", 0.95f));
        Assert.Equal("beta", result.SourceId);
        Assert.Equal(ConfidenceBand.Probable, result.Band);
    }

    [Fact]
    public void AConfidentLogKeepsItsKindUnderAnArtifactNameAndSaysSo()
    {
        var result = Detect(Text, "upload.php", new Scored("alpha", 0.97f));

        Assert.Equal(FileKind.Log, result.Kind);
        Assert.Equal("alpha", result.SourceId);
        Assert.Equal(ConfidenceBand.Confident, result.Band);
        Assert.Equal([FormatDetector.FlagExtensionMismatch], result.Flags);
        Assert.Equal("php", result.MismatchedExtension);
        Assert.Equal("named .php but the content is alpha (0.97)", result.Message);
    }

    [Theory]
    [InlineData(0.79f)]
    [InlineData(0.5f)]
    [InlineData(0f)]
    public void BelowTheConfidentBandAnArtifactExtensionMakesAnArtifact(float score)
    {
        var result = Detect(Text, "upload.php", new Scored("alpha", score));

        Assert.Equal(FileKind.Artifact, result.Kind);
        Assert.Equal("php", result.Format);
        Assert.Equal(DetectionBasis.Extension, result.Basis);
        Assert.Equal(score, result.Confidence);
        Assert.Single(result.Scores);
    }

    [Fact]
    public void BinaryBytesMakeAnArtifactOnlyWhenNoSourceReachesPointFive()
    {
        byte[] withNul = [.. Text, .. new byte[200], .. Text];

        var log = Detect(withNul, "app.log", new Scored("alpha", 0.5f));
        Assert.Equal(FileKind.Log, log.Kind);
        Assert.Equal(ConfidenceBand.Probable, log.Band);
        Assert.Equal([FormatDetector.FlagBinaryBytes], log.Flags);
        Assert.Equal(200.0 / withNul.Length, log.BinaryShare, 6);
        Assert.StartsWith("holds control bytes (7.", log.Message, StringComparison.Ordinal);

        // Under 0.5 no source takes the file, and the content decides: text around a block of NULs is a log.
        var fallback = Detect(withNul, "app.log", new Scored("alpha", 0.49f));
        Assert.Equal((FileKind.Log, FormatDetector.GenericSourceId), (fallback.Kind, fallback.SourceId));
        Assert.Equal([FormatDetector.FlagBinaryBytes], fallback.Flags);

        // Bytes that are not text are an artifact.
        var noise = new byte[4096];
        new Random(5).NextBytes(noise);
        var artifact = Detect(noise, "app.log", new Scored("alpha", 0.49f));
        Assert.Equal(FileKind.Artifact, artifact.Kind);
        Assert.Equal("unknown-binary", artifact.Format);
        Assert.Equal(DetectionBasis.Content, artifact.Basis);
    }

    [Fact]
    public void OneNulIsNotBinaryAndMoreThanThreePercentOfControlBytesIs()
    {
        byte[] oneNul = [.. Text, 0];
        var kept = Detect(oneNul, "x");
        Assert.Equal((FileKind.Log, FormatDetector.GenericSourceId), (kept.Kind, kept.SourceId));
        Assert.Equal([FormatDetector.FlagBinaryBytes], kept.Flags);

        var controls = (byte[])Text.Clone();
        for (var i = 0; i < controls.Length; i += 16)
        {
            controls[i] = 0x01; // 6.25 %
        }

        Assert.Equal("unknown-binary", Detect(controls, "x").Format);

        var few = (byte[])Text.Clone();
        for (var i = 0; i < few.Length; i += 40)
        {
            few[i] = 0x01; // 2.5 %
        }

        var log = Detect(few, "x");
        Assert.Equal((FileKind.Log, FormatDetector.GenericSourceId), (log.Kind, log.SourceId));
        Assert.Equal([FormatDetector.FlagBinaryBytes], log.Flags);

        // Tab, CR, form feed, escape and bytes above 0x7F are text.
        var plain = Detect("col\tcol\r\n\f\u001b[0m caf\u00e9 \u00ff\n"u8.ToArray(), "x");
        Assert.Equal(FormatDetector.GenericSourceId, plain.SourceId);
        Assert.Empty(plain.Flags);
    }

    [Fact]
    public void MagicIsDecisiveBeforeAnySourceIsAsked()
    {
        var pdf = "%PDF-1.7\n1 0 obj\n"u8.ToArray();
        var result = Detect(pdf, "access.log", new Throwing("alpha"));

        Assert.Equal(FileKind.Artifact, result.Kind);
        Assert.Equal("pdf", result.Format);
        Assert.Equal(DetectionBasis.Magic, result.Basis);
        Assert.Empty(result.Scores);
    }

    [Theory]
    [InlineData("#!/bin/sh\necho x\n", "script")]
    [InlineData("<?php echo 1;\n", "php")]
    [InlineData("\n  <!DOCTYPE HTML>\n<html>", "html")]
    [InlineData("<HTML><body>", "html")]
    public void TextThatIsAScriptOrAPageByItsFirstLineIsAnArtifactBeforeAnySourceIsAsked(string text, string format)
    {
        var result = Detect(Encoding.UTF8.GetBytes(text), "noext", new Throwing("alpha"));
        Assert.Equal(FileKind.Artifact, result.Kind);
        Assert.Equal(format, result.Format);
        Assert.Equal(DetectionBasis.Content, result.Basis);
    }

    [Fact]
    public void XmlIsNotAnArtifactByItsFirstLine()
    {
        var result = Detect("<?xml version=\"1.0\"?>\n<Events></Events>\n"u8.ToArray(), "export.xml", new Scored("alpha", 0.9f));
        Assert.Equal("alpha", result.SourceId);
    }

    [Fact]
    public void AnAsciiSignatureOfABinaryFormatNeedsAControlByteNearIt()
    {
        Assert.Equal(FormatDetector.GenericSourceId, Detect("MZ-7 unit started\nMZ-7 unit stopped\n"u8.ToArray(), "unit.log").SourceId);
        Assert.Equal(FormatDetector.GenericSourceId, Detect("regfile opened\nOggSomething\n"u8.ToArray(), "unit.log").SourceId);
        Assert.Equal("pe", Detect([(byte)'M', (byte)'Z', 0x90, 0x00, 0x03, 0x00, 0x00, 0x00], "unit.log").Format);
    }

    [Fact]
    public void ATwoByteSignatureCountsOnlyWithItsExtension()
    {
        var bm = "BMW 320d service record\nBMW 520i service record\n"u8.ToArray();
        Assert.Equal(FormatDetector.GenericSourceId, Detect(bm, "cars.log").SourceId);
        Assert.Equal("bmp", Detect(bm, "cars.bmp").Format);
        Assert.Equal(DetectionBasis.Magic, Detect(bm, "cars.bmp").Basis);
    }

    [Fact]
    public void APacketCaptureIsRejectedWhateverItIsCalled()
    {
        byte[] pcap = [0xD4, 0xC3, 0xB2, 0xA1, 2, 0, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        var result = Detect(pcap, "access.log", new Throwing("alpha"));
        Assert.Equal(FileKind.Rejected, result.Kind);
        Assert.Equal("pcap", result.Format);
        Assert.Contains("packet captures are not accepted", result.Message, StringComparison.Ordinal);

        // The PCAPNG block type is "\n\r\r\n": text that starts that way is text.
        Assert.Equal(FileKind.Log, Detect("\n\r\r\nplain text follows here\n"u8.ToArray(), "x.log").Kind);
    }

    [Fact]
    public void TheBinaryJournalIsUnparsedWithTheExportCommand()
    {
        byte[] journal = [.. "LPKSHHRH"u8, .. new byte[240]];
        var result = Detect(journal, "system.journal", new Throwing("alpha"));
        Assert.Equal(FileKind.Unparsed, result.Kind);
        Assert.Equal("journal-binary", result.Format);
        Assert.Contains("journalctl", result.Message, StringComparison.Ordinal);
        Assert.Contains("-o export", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEventLogIsUnparsedUntilASourceForItIsRegistered()
    {
        byte[] evtx = [.. "ElfFile\0"u8, .. new byte[120]];

        var without = Detect(evtx, "Security.evtx", new Throwing("alpha"));
        Assert.Equal(FileKind.Unparsed, without.Kind);
        Assert.Equal("evtx", without.Format);
        Assert.NotNull(without.Message);

        var with = Detect(evtx, "Security.evtx", new Throwing("alpha"), new Throwing("evtx"));
        Assert.Equal(FileKind.Log, with.Kind);
        Assert.Equal("evtx", with.SourceId);
        Assert.Equal(ConfidenceBand.Confident, with.Band);
        Assert.Equal(DetectionBasis.Magic, with.Basis);
        Assert.NotEqual(without.RegisteredSources, with.RegisteredSources); // what tells a later re-detect the record is stale
    }

    [Fact]
    public void ArchivesAreContainersAndZipDocumentsAreArtifacts()
    {
        var tar = new byte[512];
        "ustar"u8.CopyTo(tar.AsSpan(257));
        Assert.Equal(["tar"], Detect(tar, "logs.tar").Containers);
        Assert.Equal(FileKind.Container, Detect(tar, "logs.tar").Kind);

        Assert.Equal(["zip"], Detect(Zip("web-01/access.log"), "logs.zip").Containers);
        Assert.Equal("ooxml", Detect(Zip("[Content_Types].xml"), "anything.zip").Format);
        Assert.Equal("opendocument", Detect(Zip("mimetype"), "anything").Format);
        Assert.Equal("jar", Detect(Zip("META-INF/MANIFEST.MF"), "anything").Format);
        Assert.Equal("xlsx", Detect(Zip("xl/workbook.xml"), "sheet.xlsx").Format);
        Assert.Equal(FileKind.Artifact, Detect(Zip("xl/workbook.xml"), "sheet.xlsx").Kind);
    }

    [Fact]
    public void ACompressedHeadIsAContainerUntilItsLayerIsRemoved()
    {
        byte[] gzip = [0x1F, 0x8B, 8, 0, 0, 0, 0, 0, 0, 3];
        var raw = Detect(gzip, "access.log.gz", new Throwing("alpha"));
        Assert.Equal(FileKind.Container, raw.Kind);
        Assert.Equal(["gzip"], raw.Containers);

        var input = new DetectionInput("access.log.gz", 1234) { Containers = ["gzip"] };
        var peeled = FormatDetector.Detect(Text, default, input, [new Scored("alpha", 0.9f)]);
        Assert.Equal(FileKind.Log, peeled.Kind);
        Assert.Equal(["gzip"], peeled.Containers);
        Assert.Equal(CompressionKind.Gzip, peeled.Compression);

        var nested = FormatDetector.Detect(gzip, default, input, [new Throwing("alpha")]);
        Assert.Equal(FileKind.Container, nested.Kind);
        Assert.Equal(["gzip", "gzip"], nested.Containers);
    }

    [Fact]
    public void AnEmptyFileIsAnArtifactCalledEmpty()
    {
        var result = Detect([], "access.log", new Throwing("alpha"));
        Assert.Equal(FileKind.Artifact, result.Kind);
        Assert.Equal("empty", result.Format);
        Assert.Equal("the file is empty", result.Message);
    }

    [Fact]
    public void ADecodeErrorWithNothingDecodedIsADamagedArtifactAndWithSomethingDecodedAFlag()
    {
        var input = new DetectionInput("x.log.xz", 99) { Containers = ["xz"], DecodeError = "the xz stream is damaged: bad header" };

        var nothing = FormatDetector.Detect(default, default, input, []);
        Assert.Equal(FileKind.Artifact, nothing.Kind);
        Assert.Equal("damaged-xz", nothing.Format);
        Assert.Equal([FormatDetector.FlagDecodeError], nothing.Flags);

        var something = FormatDetector.Detect(Text, default, input, [new Scored("alpha", 0.9f)]);
        Assert.Equal(FileKind.Log, something.Kind);
        Assert.Equal([FormatDetector.FlagDecodeError], something.Flags);
        Assert.Contains("damaged", something.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false, InputEncoding.Utf16LittleEndian, true)]
    [InlineData(true, true, InputEncoding.Utf16BigEndian, true)]
    [InlineData(false, false, InputEncoding.Utf16LittleEndian, false)]
    [InlineData(false, true, InputEncoding.Utf16BigEndian, false)]
    public void Utf16IsRecognisedWithAndWithoutAMarkAndSourcesSeeUtf8(bool bom, bool bigEndian, InputEncoding expected, bool fromBom)
    {
        var text = string.Concat(Enumerable.Repeat("a line of ordinary text\n", 20));
        var encoding = bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode;
        byte[] file = [.. bom ? encoding.GetPreamble() : [], .. encoding.GetBytes(text)];
        var seen = new Recording("alpha", 0.9f);

        var result = Detect(file, "export.log", seen);

        Assert.Equal(expected, result.Encoding);
        Assert.Equal(fromBom, result.EncodingFromBom);
        Assert.Equal("alpha", result.SourceId);
        Assert.Equal(text, Encoding.UTF8.GetString(seen.Head!));
        Assert.Empty(result.Flags); // the zero bytes of UTF-16 are not binary bytes
    }

    [Fact]
    public void AUtf8MarkIsRecordedAndTheSampleStaysUtf8()
    {
        byte[] file = [0xEF, 0xBB, 0xBF, .. Text];
        var result = Detect(file, "app.log", new Scored("alpha", 0.9f));
        Assert.Equal(InputEncoding.Utf8, result.Encoding);
        Assert.True(result.EncodingFromBom);
    }

    [Fact]
    public void ALayoutSourceIsAskedWithTheFilesOptionsAndItsLayoutIsKept()
    {
        var options = new Dictionary<string, string> { ["layouts.format"] = "custom" };
        var source = new Layouts("layouts", 0.95f, "combined", known: true);
        var result = FormatDetector.Detect(Text, default, new DetectionInput("a.log", Text.Length, options), [source]);

        Assert.Same(options, source.Options);
        Assert.Equal("combined", result.Layout);
        Assert.True(result.LayoutKnown);
        Assert.Equal(new SourceScore("layouts", 0.95f, "combined", true), Assert.Single(result.Scores));
    }

    [Fact]
    public void AnUnknownLayoutIsAProbableLogThatSaysWhatToSet()
    {
        var result = Detect(Text, "a.log", new Layouts("layouts", 0.6f, null, known: false));

        Assert.Equal(FileKind.Log, result.Kind);
        Assert.Equal("layouts", result.SourceId);
        Assert.Equal(ConfidenceBand.Probable, result.Band);
        Assert.Null(result.Layout);
        Assert.False(result.LayoutKnown);
        Assert.Contains("no built-in layout fits", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameInputsGiveTheSameRecord()
    {
        ILogSource[] sources = [new Scored("alpha", 0.61f), new Layouts("layouts", 0.6f, "x", known: true)];
        var first = Detect(Text, "a.cfg", sources).ToJson();
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(first, Detect(Text, "a.cfg", sources).ToJson());
        }
    }

    [Fact]
    public void TheRecordSerialisesEveryField()
    {
        byte[] withNul = [.. Text, 0];
        var json = Detect(withNul, "upload.php", new Layouts("layouts", 0.9f, "combined", known: true), new Scored("generic", 0f)).ToJson();
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(FormatDetector.Version, root.GetProperty("detector_version").GetInt32());
        Assert.Equal(["layouts", "generic"], root.GetProperty("registered_sources").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("log", root.GetProperty("kind").GetString());
        Assert.Equal("layouts", root.GetProperty("source").GetString());
        Assert.Equal(0.9, root.GetProperty("confidence").GetDouble(), 4);
        Assert.Equal("confident", root.GetProperty("band").GetString());
        Assert.Equal("combined", root.GetProperty("layout").GetString());
        Assert.True(root.GetProperty("layout_known").GetBoolean());
        Assert.Equal("layouts", root.GetProperty("scores")[0].GetProperty("source").GetString());
        Assert.Equal("utf8", root.GetProperty("encoding").GetString());
        Assert.False(root.GetProperty("encoding_from_bom").GetBoolean());
        Assert.Equal(0, root.GetProperty("containers").GetArrayLength());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, root.GetProperty("format").ValueKind);
        Assert.Equal("sniff", root.GetProperty("basis").GetString());
        Assert.Equal(["extension_mismatch", "binary_bytes"], root.GetProperty("flags").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("php", root.GetProperty("mismatched_extension").GetString());
        Assert.True(root.GetProperty("binary_share").GetDouble() > 0);
        Assert.Contains("control bytes", root.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("access.log", "log")]
    [InlineData("access.log.3", "log")]
    [InlineData("access.log.3.gz", "log")]
    [InlineData("access.log-20260301", "log")]
    [InlineData("access.log-20260301.gz", "log")]
    [InlineData("access.log.2026-03-01", "log")]
    [InlineData("access.log_20260301.bz2", "log")]
    [InlineData("bundle.zip!web-01/nginx/access.log.1", "log")]
    [InlineData(@"C:\evidence\Shell.PHP", "php")]
    [InlineData("report.pdf.gz", "pdf")]
    [InlineData("backup.tar.gz", "tar")]
    [InlineData("messages", "")]
    [InlineData("web-01", "")]
    [InlineData("syslog.1", "")]
    [InlineData(".bashrc", "")]
    [InlineData("trailing.", "")]
    public void TheExtensionIgnoresCompressionRotationAndPath(string name, string extension)
    {
        Assert.Equal(extension, ArtifactExtensions.Of(name));
    }

    [Fact]
    public void LogExportExtensionsAreNeverArtifactExtensions()
    {
        foreach (var extension in new[] { "log", "txt", "out", "err", "json", "jsonl", "ndjson", "csv", "tsv", "xml", "evtx", "journal", "" })
        {
            Assert.False(ArtifactExtensions.IsArtifact(extension), extension);
        }
    }

    private static DetectionResult Detect(byte[] file, string name, params ILogSource[] sources) =>
        FormatDetector.Detect(file, default, new DetectionInput(name, file.Length), sources);

    private static byte[] Zip(string firstEntry)
    {
        var name = Encoding.ASCII.GetBytes(firstEntry);
        var header = new byte[30 + name.Length + 4];
        "PK\x03\x04"u8.CopyTo(header);
        header[26] = (byte)name.Length;
        name.CopyTo(header, 30);
        return header;
    }

    private class Scored(string id, float score) : ILogSource
    {
        public SourceInfo Info { get; } = new(id, [], id, 1);

        public Framing Framing => Framing.Line;

        public FieldSchema Schema { get; } = new(1, ImmutableArray<EventColumn>.Empty);

        public virtual float Sniff(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, SniffContext ctx) => score;

        public void Parse(ReadOnlySequence<byte> framedChunk, ParseContext ctx, IBatchWriter batch) => throw new NotSupportedException();
    }

    private sealed class Throwing(string id) : Scored(id, 0f)
    {
        public override float Sniff(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, SniffContext ctx) => throw new InvalidOperationException($"{Info.Id} must not be asked");
    }

    private sealed class Recording(string id, float score) : Scored(id, score)
    {
        public byte[]? Head { get; private set; }

        public override float Sniff(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, SniffContext ctx)
        {
            Head = head.ToArray();
            return base.Sniff(head, tail, ctx);
        }
    }

    private sealed class Layouts(string id, float score, string? layout, bool known) : Scored(id, 0f), ISniffDetail
    {
        public IReadOnlyDictionary<string, string>? Options { get; private set; }

        public override float Sniff(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, SniffContext ctx) => throw new InvalidOperationException("the detector asks SniffDetailed");

        public SniffDetail SniffDetailed(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, SniffContext ctx, IReadOnlyDictionary<string, string>? options)
        {
            Options = options;
            return new SniffDetail(score, layout, known);
        }
    }
}
