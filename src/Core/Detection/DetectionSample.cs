using Urlog.Core.Metadata;
using Urlog.Core.Pipeline;
using Urlog.Core.Sources;

namespace Urlog.Core.Detection;

/// <summary>
/// The I/O around <see cref="FormatDetector"/>: reads the first 64 KB and the last 4 KB of a stored file, read-only,
/// and when the file is one compressed stream decodes its first 64 KB so the detector sees the content. Call it
/// after the file's SHA-256 exists (evidence rule 1). One compression layer is removed, as in the parse pipeline; a
/// second one comes back as a container.
/// <para>
/// When the detector finds that neither the start nor the end reads as text, it asks for windows further in,
/// since the start of a file is not the file: a log can have junk before it, after it or inside it. A file that
/// can be read at any place gives 64 KB at a quarter, a half and three quarters of its length and its last
/// 64 KB. Content that can only be read forward (a compressed stream, a member of an archive) gives 64 KB at
/// 256 KB, 1 MB, 4 MB and 16 MB of the content, so at most 16 MB is decoded for a file that is not a log by
/// its start; what lies beyond is not looked at.
/// </para>
/// </summary>
public static class DetectionSample
{
    public const int HeadBytes = 64 << 10;

    public const int TailBytes = 4 << 10;

    /// <summary>The size of a window further into the file.</summary>
    public const int WindowBytes = 64 << 10;

    /// <summary>Where the windows of forward-only content begin.</summary>
    private static readonly long[] ForwardOffsets = [256L << 10, 1L << 20, 4L << 20, 16L << 20];

    /// <summary>Bytes of content handed back for the hints: enough for one first line.</summary>
    public const int ContentBytes = 256;

    /// <summary>Detects the file at <paramref name="path"/>; <paramref name="fileName"/> is the name to judge (the original name, or an archive path).</summary>
    public static DetectionResult DetectFile(string path, string fileName, IReadOnlyList<ILogSource> sources, IReadOnlyDictionary<string, string>? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        var length = new FileInfo(path).Length;
        return Detect(() => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.None), length, fileName, sources, options);
    }

    /// <summary>
    /// As <see cref="DetectFile(string, string, IReadOnlyList{ILogSource}, IReadOnlyDictionary{string, string}?)"/>, and
    /// hands back the first bytes of the content (decompressed when the file is one compressed stream) for the
    /// hints intake derives from the first line.
    /// </summary>
    public static DetectionResult DetectFile(string path, string fileName, IReadOnlyList<ILogSource> sources, IReadOnlyDictionary<string, string>? options, out byte[] content)
    {
        ArgumentNullException.ThrowIfNull(path);
        var length = new FileInfo(path).Length;
        return Detect(() => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.None), length, fileName, sources, options, out content);
    }

    /// <summary>Detects <paramref name="file"/> held in memory, with the same sampling as a stored file.</summary>
    public static DetectionResult DetectBytes(byte[] file, string fileName, IReadOnlyList<ILogSource> sources, IReadOnlyDictionary<string, string>? options = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        return Detect(() => new MemoryStream(file, writable: false), file.Length, fileName, sources, options);
    }

    /// <param name="open">Opens the stored bytes for reading from the start; called up to three times, each stream is disposed.</param>
    public static DetectionResult Detect(Func<Stream> open, long length, string fileName, IReadOnlyList<ILogSource> sources, IReadOnlyDictionary<string, string>? options = null) =>
        Detect(open, length, fileName, sources, options, out _);

    /// <param name="open">Opens the stored bytes for reading from the start; called up to three times, each stream is disposed.</param>
    /// <param name="content">The first bytes of the content the detector judged, at most <see cref="ContentBytes"/>.</param>
    public static DetectionResult Detect(Func<Stream> open, long length, string fileName, IReadOnlyList<ILogSource> sources, IReadOnlyDictionary<string, string>? options, out byte[] content)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(fileName);
        var head = new byte[HeadBytes];
        var tail = Array.Empty<byte>();
        int headLength;
        var seekable = false;
        using (var raw = open())
        {
            headLength = ReadFully(raw, head);
            seekable = raw.CanSeek;
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
            content = head.AsSpan(0, Math.Min(headLength, ContentBytes)).ToArray();
            var covered = headLength;
            return FormatDetector.Detect(
                head.AsSpan(0, headLength),
                tail,
                input,
                sources,
                () => seekable ? WindowsAnywhere(open, length, covered) : WindowsForward(open, CompressionKind.None));
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

        content = decoded.AsSpan(0, Math.Min(decodedLength, ContentBytes)).ToArray();
        input = input with { Containers = [Names.Of(compression)], DecodeError = error };
        return FormatDetector.Detect(
            decoded.AsSpan(0, decodedLength),
            default,
            input,
            sources,
            () => error is null ? WindowsForward(open, compression) : []);
    }

    /// <summary>Windows at a quarter, a half and three quarters of the file and at its end, none of them inside the head.</summary>
    private static List<byte[]> WindowsAnywhere(Func<Stream> open, long length, int covered)
    {
        var windows = new List<byte[]>();
        var last = (long)covered;
        using var raw = open();
        foreach (var wanted in new[] { length / 4, length / 2, length / 4 * 3, length - WindowBytes })
        {
            // On an even offset, so a window of UTF-16 text starts on a code unit; never before what was read.
            var offset = Math.Max(wanted & ~1L, last);
            if (offset >= length)
            {
                continue;
            }

            var window = new byte[(int)Math.Min(WindowBytes, length - offset)];
            raw.Seek(offset, SeekOrigin.Begin);
            var n = ReadFully(raw, window);
            if (n > 0)
            {
                windows.Add(n == window.Length ? window : window[..n]);
            }

            last = offset + n;
        }

        return windows;
    }

    /// <summary>Windows of content that is read from its start only, at fixed distances into it.</summary>
    private static List<byte[]> WindowsForward(Func<Stream> open, CompressionKind compression)
    {
        var windows = new List<byte[]>();
        var skip = new byte[WindowBytes];
        try
        {
            using var raw = open();
            using var stream = compression == CompressionKind.None ? raw : InputDecoding.Decompress(raw, compression);
            var position = 0L;
            foreach (var offset in ForwardOffsets)
            {
                while (position < offset)
                {
                    var n = stream.Read(skip, 0, (int)Math.Min(skip.Length, offset - position));
                    if (n == 0)
                    {
                        return windows;
                    }

                    position += n;
                }

                var window = new byte[WindowBytes];
                var read = ReadFully(stream, window);
                position += read;
                if (read > 0)
                {
                    windows.Add(read == window.Length ? window : window[..read]);
                }

                if (read < window.Length)
                {
                    break;
                }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Damaged further in: what was read before the damage is what there is to judge.
        }

        return windows;
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
