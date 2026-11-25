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
/// Classifies a file from its first 64 KB, its last 4 KB, its name and its size (design record, "Format detection"),
/// and, when its start does not read as text, from windows further into it. A pure function of what it is handed:
/// no clock, the same inputs give the same record. <see cref="DetectionSample"/> reads the samples and removes
/// one compression layer before calling it.
/// <para>
/// Order: empty, packet capture, compression, zip and tar, EVTX and the binary journal, artifact signatures, the
/// encoding, text that is a script or a page by its first line, then every source's score. A top score of 0.8 or
/// more is a log whatever the file is called. Below that an artifact extension makes it an artifact; 0.5 and above
/// is a probable log. Below 0.5 the content decides between the generic source and an artifact: control bytes
/// alone never make a text file "not a log". The file is text when any sampled window reads as text
/// (<see cref="TextProbe.Read"/>): its start, its end, or a window further in, which are looked at only when the
/// start does not read as text. A log that holds control bytes anywhere in what was sampled carries the flag
/// <c>binary_bytes</c>. Someone who controls a file can still shape it to pass or to fail these tests, so a file
/// kept as "not a log" stays counted, listed with its reason, and one action away from being parsed as text.
/// </para>
/// </summary>
public static class FormatDetector
{
    /// <summary>Bumped when a rule changes what an existing file would be detected as.</summary>
    public const int Version = 2;

    public const string GenericSourceId = "generic";

    public const string EvtxSourceId = "evtx";

    /// <summary>The format of a file that is not a log by its bytes alone: no signature, no name, and no window that reads as text.</summary>
    public const string UnknownBinary = "unknown-binary";

    public const float ConfidentAt = 0.8f;

    public const float ProbableAt = 0.5f;

    public const string FlagExtensionMismatch = "extension_mismatch";

    public const string FlagBinaryBytes = "binary_bytes";

    public const string FlagDecodeError = "decode_error";

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>One instance of every source in <paramref name="catalog"/>, in registration order.</summary>
    public static IReadOnlyList<ILogSource> Instantiate(SourceCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return [.. catalog.All.Select(r => r.Factory())];
    }

    /// <param name="head">The first 64 KB of the content (after the layers in <see cref="DetectionInput.Containers"/>).</param>
    /// <param name="tail">The last 4 KB of the stored file; empty when a layer was removed or the head covers the file.</param>
    /// <param name="further">
    /// Reads windows further into the content, in file order. Asked for once, and only when no source took the
    /// file and its start and end do not read as text. Null: there is nothing more to look at.
    /// </param>
    public static DetectionResult Detect(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, DetectionInput input, IReadOnlyList<ILogSource> sources, Func<IReadOnlyList<byte[]>>? further = null)
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

        var headReading = TextProbe.Read(textHead);
        var tailReading = TextProbe.Read(textTail);
        var share = (double)(headReading.ControlBytes + tailReading.ControlBytes) / (textHead.Length + textTail.Length);
        // A log a source takes is flagged when what was sampled of it holds any control byte.
        var binary = headReading.Control || tailReading.Control;

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

        // No source took it. It is text for the generic source when any window of it reads as text: the start,
        // the end, or, only when neither does, a window further in.
        var fallback = seed with { Kind = FileKind.Log, SourceId = GenericSourceId, Band = ConfidenceBand.Fallback, LayoutKnown = true, Basis = DetectionBasis.Content };
        if (headReading.Kind == WindowKind.Text)
        {
            return binary ? Flagged(fallback, share, null) : fallback;
        }

        if (tailReading.Kind == WindowKind.Text)
        {
            return Flagged(fallback, share, "the start of the file does not read as text; its end does");
        }

        var position = 0;
        foreach (var window in further?.Invoke() ?? [])
        {
            position++;
            var text = encoding == InputEncoding.Utf8 ? window : Transcode(window, encoding);
            if (TextProbe.Read(text).Kind == WindowKind.Text)
            {
                return Flagged(fallback, share, string.Create(CultureInfo.InvariantCulture, $"the start of the file does not read as text; window {position} further in does"));
            }
        }

        return seed with { Kind = FileKind.Artifact, Format = UnknownBinary, Basis = DetectionBasis.Content };
    }

    private static DetectionResult Flagged(DetectionResult log, double share, string? where)
    {
        var message = string.Create(CultureInfo.InvariantCulture, $"holds control bytes ({share * 100:F1} % of the sample)");
        return log with { Flags = log.Flags.Add(FlagBinaryBytes), Message = Join(Join(log.Message, message), where) };
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
            message = Join(message, string.Create(CultureInfo.InvariantCulture, $"holds control bytes ({share * 100:F1} % of the sample)"));
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

    private static string Join(string? first, string? second) => first is null ? second ?? string.Empty : second is null ? first : first + "; " + second;

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
