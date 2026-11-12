using Urlog.Core.Metadata;
using Urlog.Core.Pipeline;
using Urlog.Core.Sources;

namespace Urlog.Core.Detection;

/// <summary>
/// The I/O around <see cref="FormatDetector"/>: reads the first 64 KB and the last 4 KB of a stored file, read-only,
/// and when the file is one compressed stream decodes its first 64 KB so the detector sees the content. Call it
/// after the file's SHA-256 exists (evidence rule 1). One compression layer is removed, as in the parse pipeline; a
/// second one comes back as a container.
/// </summary>
public static class DetectionSample
{
    public const int HeadBytes = 64 << 10;

    public const int TailBytes = 4 << 10;

    /// <summary>Detects the file at <paramref name="path"/>; <paramref name="fileName"/> is the name to judge (the original name, or an archive path).</summary>
    public static DetectionResult DetectFile(string path, string fileName, IReadOnlyList<ILogSource> sources, IReadOnlyDictionary<string, string>? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        var length = new FileInfo(path).Length;
        return Detect(() => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.None), length, fileName, sources, options);
    }

    /// <summary>Detects <paramref name="file"/> held in memory, with the same sampling as a stored file.</summary>
    public static DetectionResult DetectBytes(byte[] file, string fileName, IReadOnlyList<ILogSource> sources, IReadOnlyDictionary<string, string>? options = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        return Detect(() => new MemoryStream(file, writable: false), file.Length, fileName, sources, options);
    }

    /// <param name="open">Opens the stored bytes for reading from the start; called once or twice, each stream is disposed.</param>
    public static DetectionResult Detect(Func<Stream> open, long length, string fileName, IReadOnlyList<ILogSource> sources, IReadOnlyDictionary<string, string>? options = null)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(fileName);
        var head = new byte[HeadBytes];
        var tail = Array.Empty<byte>();
        int headLength;
        using (var raw = open())
        {
            headLength = ReadFully(raw, head);
            if (length > headLength && raw.CanSeek)
            {
                tail = new byte[(int)Math.Min(TailBytes, length - headLength)];
                raw.Seek(-tail.Length, SeekOrigin.End);
                var n = ReadFully(raw, tail);
                tail = n == tail.Length ? tail : tail[..n];
            }
        }

        var input = new DetectionInput(fileName, length, options);
        var compression = InputDecoding.DetectCompression(head.AsSpan(0, headLength));
        if (compression == CompressionKind.None)
        {
            return FormatDetector.Detect(head.AsSpan(0, headLength), tail, input, sources);
        }

        // One compressed stream: the detector judges the first 64 KB of what it holds. The tail of the stored
        // file is compressed bytes and says nothing.
        var decoded = new byte[HeadBytes];
        var decodedLength = 0;
        string? error = null;
        using (var raw = open())
        {
            try
            {
                // Some decoders read their header on construction, so that is inside the try as well.
                using var stream = InputDecoding.Decompress(raw, compression);
                while (decodedLength < decoded.Length)
                {
                    var n = stream.Read(decoded, decodedLength, decoded.Length - decodedLength);
                    if (n == 0)
                    {
                        break;
                    }

                    decodedLength += n;
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // The four decoders throw unrelated exception types for damaged input; each of them means "damaged".
                error = $"the {Names.Of(compression)} stream is damaged: {e.Message}";
            }
        }

        input = input with { Containers = [Names.Of(compression)], DecodeError = error };
        return FormatDetector.Detect(decoded.AsSpan(0, decodedLength), default, input, sources);
    }

    private static int ReadFully(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = stream.Read(buffer, total, buffer.Length - total);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }
}
