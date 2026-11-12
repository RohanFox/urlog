using System.Collections.Immutable;
using System.Globalization;
using Urlog.Core.Metadata;
using Urlog.Core.Pipeline;
using Urlog.Core.Sources;

namespace Urlog.Core.Detection;

/// <summary>
/// What the detector is told about a file besides its bytes.
/// </summary>
/// <param name="FileName">The original name, or the archive path of a member.</param>
/// <param name="FileLength">Bytes of the stored file.</param>
/// <param name="Options">The file's per-source options, passed to sources that score a named layout.</param>
public sealed record DetectionInput(string FileName, long FileLength, IReadOnlyDictionary<string, string>? Options = null)
{
    /// <summary>Layers already removed from the sample, outermost first; the head is then the decoded content.</summary>
    public ImmutableArray<string> Containers { get; init; } = [];

    /// <summary>Set when removing the last layer failed inside the sample: the decoder's message.</summary>
    public string? DecodeError { get; init; }
}

/// <summary>
/// Classifies a file from its first 64 KB, its last 4 KB, its name and its size (design record, "Format detection").
/// A pure function: no I/O, no clock, the same inputs give the same record. <see cref="DetectionSample"/> reads the
/// two samples and removes one compression layer before calling it.
/// <para>
/// Order: empty, packet capture, compression, zip and tar, EVTX and the binary journal, artifact signatures, the
/// encoding, text that is a script or a page by its first line, then every source's score. A top score of 0.8 or
/// more is a log whatever the file is called. Below that an artifact extension makes it an artifact; 0.5 and above
/// is a probable log; below 0.5 a sample with binary bytes is an artifact and anything else goes to the generic source.
/// </para>
/// </summary>
public static class FormatDetector
{
    /// <summary>Bumped when a rule changes what an existing file would be detected as.</summary>
    public const int Version = 1;

    public const string GenericSourceId = "generic";

    public const string EvtxSourceId = "evtx";

    public const float ConfidentAt = 0.8f;

    public const float ProbableAt = 0.5f;

    public const string FlagExtensionMismatch = "extension_mismatch";

    public const string FlagBinaryBytes = "binary_bytes";

    public const string FlagDecodeError = "decode_error";

    private const double BinaryShareLimit = 0.05;

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>One instance of every source in <paramref name="catalog"/>, in registration order.</summary>
    public static IReadOnlyList<ILogSource> Instantiate(SourceCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return [.. catalog.All.Select(r => r.Factory())];
    }

    /// <param name="head">The first 64 KB of the content (after the layers in <see cref="DetectionInput.Containers"/>).</param>
    /// <param name="tail">The last 4 KB of the stored file; empty when a layer was removed or the head covers the file.</param>
    public static DetectionResult Detect(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, DetectionInput input, IReadOnlyList<ILogSource> sources)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(sources);

        var containers = input.Containers.IsDefault ? [] : input.Containers;
        var seed = new DetectionResult
        {
            RegisteredSources = [.. sources.Select(s => s.Info.Id)],
            Containers = containers,
            Flags = input.DecodeError is null ? [] : [FlagDecodeError],
            Message = input.DecodeError,
        };

        if (head.IsEmpty)
        {
            if (input.DecodeError is not null)
            {
                return seed with { Kind = FileKind.Artifact, Format = "damaged-" + (containers.IsEmpty ? "file" : containers[^1]), Basis = DetectionBasis.Content };
            }

            return seed with
            {
                Kind = FileKind.Artifact,
                Format = "empty",
                Basis = DetectionBasis.Content,
                Message = containers.IsEmpty ? "the file is empty" : "the compressed stream holds no bytes",
            };
        }

        if (MagicTable.PacketCapture(head) is { } capture)
        {
            return seed with
            {
                Kind = FileKind.Rejected,
                Format = capture,
                Basis = DetectionBasis.Magic,
                Message = "packet captures are not accepted: this tool reads logs, not network traffic",
            };
        }

        var compression = InputDecoding.DetectCompression(head);
        if (compression != CompressionKind.None)
        {
            return seed with
            {
                Kind = FileKind.Container,
                Containers = containers.Add(Names.Of(compression)),
                Basis = DetectionBasis.Magic,
                Message = containers.IsEmpty ? "compressed stream: the content was not sampled" : "a second compression layer: intake expands it",
            };
        }

        var extension = ArtifactExtensions.Of(input.FileName);
        if (head.StartsWith("PK\x03\x04"u8) || head.StartsWith("PK\x05\x06"u8) || head.StartsWith("PK\x07\x08"u8))
        {
            if (ZipDocument(head, extension) is { } document)
            {
                return seed with { Kind = FileKind.Artifact, Format = document, Basis = DetectionBasis.Magic };
            }

            return seed with { Kind = FileKind.Container, Containers = containers.Add("zip"), Basis = DetectionBasis.Magic };
        }

        if (head.Length >= 262 && head.Slice(257, 5).SequenceEqual("ustar"u8))
        {
            return seed with { Kind = FileKind.Container, Containers = containers.Add("tar"), Basis = DetectionBasis.Magic };
        }

        if (head.StartsWith("ElfFile\0"u8))
        {
            if (sources.Any(s => s.Info.Id == EvtxSourceId))
            {
                return seed with { Kind = FileKind.Log, SourceId = EvtxSourceId, Confidence = 1f, Band = ConfidenceBand.Confident, LayoutKnown = true, Basis = DetectionBasis.Magic };
            }

            return seed with
            {
                Kind = FileKind.Unparsed,
                Format = "evtx",
                Basis = DetectionBasis.Magic,
                Message = "Windows event log (.evtx): this build has no parser for it; the file is kept and detected again when one is added",
            };
        }

        if (head.StartsWith("LPKSHHRH"u8))
        {
            return seed with
            {
                Kind = FileKind.Unparsed,
                Format = "journal-binary",
                Basis = DetectionBasis.Magic,
                Message = "binary systemd journal: not parsed; export it with 'journalctl --file <journal> -o export' and upload the export",
            };
        }

        if (MagicTable.Match(head, extension) is { } hit)
        {
            return seed with { Kind = FileKind.Artifact, Format = hit.Format, Basis = DetectionBasis.Magic, Message = hit.Message ?? seed.Message };
        }

        // Encoding, then the same UTF-8 text the parse pipeline will produce.
        var encoding = InputDecoding.DetectEncoding(head, out var bomLength);
        var fromBom = bomLength > 0;
        if (!fromBom && TextProbe.LooksUtf16(head, out var bigEndian))
        {
            encoding = bigEndian ? InputEncoding.Utf16BigEndian : InputEncoding.Utf16LittleEndian;
        }

        byte[]? decodedHead = null;
        byte[]? decodedTail = null;
        if (encoding != InputEncoding.Utf8)
        {
            decodedHead = Transcode(head, encoding);
            // The tail starts on a code unit only when its offset in the file is even.
            var skip = (int)((input.FileLength - tail.Length) & 1);
            decodedTail = tail.Length > skip ? Transcode(tail[skip..], encoding) : [];
        }

        var textHead = decodedHead is null ? head : decodedHead;
        var textTail = decodedTail is null ? tail : decodedTail;
        seed = seed with { Encoding = encoding, EncodingFromBom = fromBom };

        var body = textHead.StartsWith(Utf8Bom) ? textHead[3..] : textHead;
        if (TextProbe.FirstLineArtifact(body) is { } firstLine)
        {
            return seed with { Kind = FileKind.Artifact, Format = firstLine, Basis = DetectionBasis.Content };
        }

        var nul = false;
        var control = TextProbe.CountControl(textHead, ref nul) + TextProbe.CountControl(textTail, ref nul);
        var share = (double)control / (textHead.Length + textTail.Length);
        var binary = nul || share > BinaryShareLimit;

        // Every source but the fallback scores the sample.
        var sniff = new SniffContext(input.FileName, Math.Max(input.FileLength, textHead.Length));
        var scores = ImmutableArray.CreateBuilder<SourceScore>(sources.Count);
        var top = -1;
        var runnerUp = 0f;
        foreach (var source in sources)
        {
            if (source.Info.Id == GenericSourceId)
            {
                continue;
            }

            SourceScore score;
            if (source is ISniffDetail detailed)
            {
                var detail = detailed.SniffDetailed(textHead, textTail, sniff, input.Options);
                score = new SourceScore(source.Info.Id, detail.Confidence, detail.Layout, detail.LayoutKnown);
            }
            else
            {
                score = new SourceScore(source.Info.Id, source.Sniff(textHead, textTail, sniff), null, true);
            }

            scores.Add(score);
            if (top < 0 || score.Score > scores[top].Score)
            {
                if (top >= 0)
                {
                    runnerUp = Math.Max(runnerUp, scores[top].Score);
                }

                top = scores.Count - 1;
            }
            else
            {
                runnerUp = Math.Max(runnerUp, score.Score);
            }
        }

        var best = top < 0 ? null : scores[top];
        var bestScore = best?.Score ?? 0f;
        seed = seed with { Scores = scores.ToImmutable(), Confidence = bestScore, BinaryShare = share };
        var artifactExtension = ArtifactExtensions.IsArtifact(extension);

        if (best is not null && bestScore >= ConfidentAt)
        {
            var flags = seed.Flags;
            var message = seed.Message;
            if (artifactExtension)
            {
                flags = flags.Add(FlagExtensionMismatch);
                message = Join(message, string.Create(CultureInfo.InvariantCulture, $"named .{extension} but the content is {best.SourceId} ({bestScore:F2})"));
            }

            return AsLog(seed, best, runnerUp >= ConfidentAt ? ConfidenceBand.Probable : ConfidenceBand.Confident, flags, message, binary, share) with
            {
                MismatchedExtension = artifactExtension ? extension : null,
            };
        }

        if (artifactExtension)
        {
            return seed with { Kind = FileKind.Artifact, Format = extension, Basis = DetectionBasis.Extension };
        }

        if (best is not null && bestScore >= ProbableAt)
        {
            return AsLog(seed, best, ConfidenceBand.Probable, seed.Flags, seed.Message, binary, share);
        }

        if (binary)
        {
            return seed with { Kind = FileKind.Artifact, Format = "unknown-binary", Basis = DetectionBasis.Content };
        }

        return seed with { Kind = FileKind.Log, SourceId = GenericSourceId, Band = ConfidenceBand.Fallback, LayoutKnown = true, Basis = DetectionBasis.Content };
    }

    /// <summary>The compression a chain step names, <see cref="CompressionKind.None"/> for <c>tar</c>, <c>zip</c> or anything else.</summary>
    public static CompressionKind CompressionOf(string step)
    {
        foreach (var kind in Enum.GetValues<CompressionKind>())
        {
            if (kind != CompressionKind.None && Names.Of(kind) == step)
            {
                return kind;
            }
        }

        return CompressionKind.None;
    }

    private static DetectionResult AsLog(DetectionResult seed, SourceScore best, ConfidenceBand band, ImmutableArray<string> flags, string? message, bool binary, double share)
    {
        if (binary)
        {
            flags = flags.Add(FlagBinaryBytes);
            message = Join(message, string.Create(CultureInfo.InvariantCulture, $"contains binary bytes ({share * 100:F1} % of the sample)"));
        }

        if (!best.LayoutKnown)
        {
            message = Join(message, $"{best.SourceId}: the lines have the shape but no built-in layout fits; set the source's format option");
        }

        return seed with
        {
            Kind = FileKind.Log,
            SourceId = best.SourceId,
            Band = band,
            Layout = best.Layout,
            LayoutKnown = best.LayoutKnown,
            Basis = DetectionBasis.Sniff,
            Flags = flags,
            Message = message,
        };
    }

    private static string Join(string? first, string second) => first is null ? second : first + "; " + second;

    /// <summary>
    /// A zip that is a document or a package: by the name of its first entry (OOXML, OpenDocument, Java), or by the
    /// file's extension. Null for an ordinary archive.
    /// </summary>
    private static string? ZipDocument(ReadOnlySpan<byte> head, string extension)
    {
        if (head.Length >= 30 && head.StartsWith("PK\x03\x04"u8))
        {
            var nameLength = head[26] | (head[27] << 8);
            if (head.Length >= 30 + nameLength)
            {
                var name = head.Slice(30, nameLength);
                if (name.SequenceEqual("[Content_Types].xml"u8) || name.StartsWith("_rels/"u8))
                {
                    return "ooxml";
                }

                if (name.SequenceEqual("mimetype"u8))
                {
                    return "opendocument";
                }

                if (name.StartsWith("META-INF/"u8))
                {
                    return "jar";
                }
            }
        }

        return ArtifactExtensions.IsZipDocument(extension) ? extension : null;
    }

    private static byte[] Transcode(ReadOnlySpan<byte> bytes, InputEncoding encoding)
    {
        using var source = new MemoryStream(bytes.ToArray(), writable: false);
        using var transcoder = InputDecoding.Transcode(source, encoding);
        using var text = new MemoryStream(bytes.Length);
        transcoder.CopyTo(text);
        return text.ToArray();
    }
}
