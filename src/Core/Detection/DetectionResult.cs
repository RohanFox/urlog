using System.Buffers;
using System.Collections.Immutable;
using System.Text.Json;
using Urlog.Core.Metadata;
using Urlog.Core.Pipeline;

namespace Urlog.Core.Detection;

/// <summary>What a file is, as far as intake is concerned.</summary>
public enum FileKind
{
    /// <summary>Parsed by <see cref="DetectionResult.SourceId"/>.</summary>
    Log,

    /// <summary>An archive, or a second compression layer: intake expands it and detects each member.</summary>
    Container,

    /// <summary>Not a log. Hashed and recorded, never parsed.</summary>
    Artifact,

    /// <summary>A log format this build recognises and has no parser for. Recorded, not parsed, detected again later.</summary>
    Unparsed,

    /// <summary>Refused at intake with <see cref="DetectionResult.Message"/> (packet captures).</summary>
    Rejected,
}

/// <summary>How far the winning source's score carries: 0.8 and above, 0.5 up to 0.8, or the generic fallback.</summary>
public enum ConfidenceBand
{
    None,
    Confident,
    Probable,
    Fallback,
}

/// <summary>What decided the kind.</summary>
public enum DetectionBasis
{
    Magic,
    Extension,
    Content,
    Sniff,
}

/// <summary>One source's answer for the sample. <paramref name="LayoutKnown"/> is false when the source reports no layout.</summary>
public sealed record SourceScore(string SourceId, float Score, string? Layout, bool LayoutKnown);

/// <summary>
/// The detection record of one file (design record, "Format detection"). Kept whole on the file row as JSON
/// (<see cref="ToJson"/>) next to the columns the queue reads, and never erased by an override.
/// </summary>
public sealed record DetectionResult
{
    /// <summary>Version of the decision rules that produced the record.</summary>
    public int DetectorVersion { get; init; } = FormatDetector.Version;

    /// <summary>Ids of the sources registered when the file was detected: a record older than the current list is detected again.</summary>
    public ImmutableArray<string> RegisteredSources { get; init; } = [];

    public FileKind Kind { get; init; }

    /// <summary>For <see cref="FileKind.Log"/>: the source that parses the file, <c>generic</c> at the fallback.</summary>
    public string? SourceId { get; init; }

    /// <summary>The highest score any source gave, also when the generic fallback takes the file.</summary>
    public float Confidence { get; init; }

    public ConfidenceBand Band { get; init; }

    /// <summary>The winning source's layout name, null when it has none or could not tell.</summary>
    public string? Layout { get; init; }

    /// <summary>False with a winner that has layouts means "this source, layout unknown".</summary>
    public bool LayoutKnown { get; init; }

    /// <summary>Every registered source except the fallback, in registration order.</summary>
    public ImmutableArray<SourceScore> Scores { get; init; } = [];

    public InputEncoding Encoding { get; init; }

    /// <summary>True when a byte-order mark named the encoding; false when it was inferred or assumed.</summary>
    public bool EncodingFromBom { get; init; }

    /// <summary>Outermost first: <c>gzip</c>, <c>zstd</c>, <c>xz</c>, <c>bzip2</c>, <c>tar</c>, <c>zip</c>.</summary>
    public ImmutableArray<string> Containers { get; init; } = [];

    /// <summary>For a non-log: <c>pdf</c>, <c>pe</c>, <c>journal-binary</c>, <c>pcapng</c>, <c>rar</c>, <c>empty</c>, ...</summary>
    public string? Format { get; init; }

    public DetectionBasis Basis { get; init; }

    /// <summary><c>extension_mismatch</c>, <c>binary_bytes</c>, <c>decode_error</c>.</summary>
    public ImmutableArray<string> Flags { get; init; } = [];

    /// <summary>The artifact extension of a file that is a log by content, without the dot; null otherwise.</summary>
    public string? MismatchedExtension { get; init; }

    /// <summary>Share of NUL and control bytes in the sample, 0 to 1.</summary>
    public double BinaryShare { get; init; }

    /// <summary>What the analyst reads next to the file.</summary>
    public string? Message { get; init; }

    /// <summary>The one compression layer the parse pipeline undoes, when the chain starts with one.</summary>
    public CompressionKind Compression => Containers.IsDefaultOrEmpty ? CompressionKind.None : FormatDetector.CompressionOf(Containers[0]);

    /// <summary>What this record was made with, in the form <see cref="RegistryKeyOf"/> gives for a build.</summary>
    public string RegistryKey => RegistryKeyOf(DetectorVersion, RegisteredSources);

    /// <summary>
    /// The detector version and the registered source ids as one short text (<c>1:generic,nginx,syslog</c>), ids in
    /// ordinal order. Two records with the same key were made by detectors that know the same formats.
    /// </summary>
    public static string RegistryKeyOf(int detectorVersion, IEnumerable<string> sourceIds)
    {
        ArgumentNullException.ThrowIfNull(sourceIds);
        return detectorVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + string.Join(',', sourceIds.Order(StringComparer.Ordinal));
    }

    /// <summary>The key of a stored record, read from its JSON; null when the text holds neither member.</summary>
    public static string? RegistryKeyOfJson(string? detectionJson)
    {
        if (string.IsNullOrEmpty(detectionJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(detectionJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("detector_version", out var version) || !root.TryGetProperty("registered_sources", out var sources) || sources.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            return RegistryKeyOf(version.GetInt32(), sources.EnumerateArray().Select(s => s.GetString() ?? string.Empty));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The record as one JSON object with snake_case keys, stable key order.</summary>
    public string ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("detector_version", DetectorVersion);
            json.WriteStartArray("registered_sources");
            foreach (var id in RegisteredSources)
            {
                json.WriteStringValue(id);
            }

            json.WriteEndArray();
            json.WriteString("kind", Names.Snake(Kind.ToString()));
            WriteNullable(json, "source", SourceId);
            json.WriteNumber("confidence", Math.Round(Confidence, 4));
            json.WriteString("band", Names.Snake(Band.ToString()));
            WriteNullable(json, "layout", Layout);
            json.WriteBoolean("layout_known", LayoutKnown);
            json.WriteStartArray("scores");
            foreach (var score in Scores)
            {
                json.WriteStartObject();
                json.WriteString("source", score.SourceId);
                json.WriteNumber("score", Math.Round(score.Score, 4));
                WriteNullable(json, "layout", score.Layout);
                json.WriteBoolean("layout_known", score.LayoutKnown);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteString("encoding", Names.Snake(Encoding.ToString()));
            json.WriteBoolean("encoding_from_bom", EncodingFromBom);
            json.WriteStartArray("containers");
            foreach (var step in Containers)
            {
                json.WriteStringValue(step);
            }

            json.WriteEndArray();
            WriteNullable(json, "format", Format);
            json.WriteString("basis", Names.Snake(Basis.ToString()));
            json.WriteStartArray("flags");
            foreach (var flag in Flags)
            {
                json.WriteStringValue(flag);
            }

            json.WriteEndArray();
            WriteNullable(json, "mismatched_extension", MismatchedExtension);
            json.WriteNumber("binary_share", Math.Round(BinaryShare, 6));
            WriteNullable(json, "message", Message);
            json.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteNullable(Utf8JsonWriter json, string name, string? value)
    {
        if (value is null)
        {
            json.WriteNull(name);
        }
        else
        {
            json.WriteString(name, value);
        }
    }
}
