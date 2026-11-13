using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using Urlog.Core.Metadata;
using Urlog.Core.Pipeline;

namespace Urlog.Core.Intake.Archives;

/// <summary>What holds a container's members once its compression is removed.</summary>
public enum ContainerFormat
{
    Zip,
    Tar,

    /// <summary>No archive: a second compression layer under the first. The one member is the stream itself.</summary>
    Stream,
}

/// <summary>How a container record is laid out: the compression around it, if any, and the archive inside.</summary>
public readonly record struct ContainerLayout(CompressionKind Compression, ContainerFormat Format)
{
    /// <summary>Reads the layout from the detection record's container chain (<c>["gzip","tar"]</c>, <c>["zip"]</c>, <c>["gzip","zstd"]</c>).</summary>
    /// <exception cref="InvalidDataException">The record holds no chain an expansion understands.</exception>
    public static ContainerLayout Of(EvidenceFile container)
    {
        ArgumentNullException.ThrowIfNull(container);
        var chain = new List<string>();
        if (!string.IsNullOrEmpty(container.Detection))
        {
            using var document = JsonDocument.Parse(container.Detection);
            if (document.RootElement.TryGetProperty("containers", out var containers) && containers.ValueKind == JsonValueKind.Array)
            {
                chain.AddRange(containers.EnumerateArray().Select(step => step.GetString() ?? string.Empty));
            }
        }

        if (chain.Count is 0 or > 2)
        {
            throw new InvalidDataException($"file {container.Id} is recorded as a container with the chain [{string.Join(", ", chain)}], which is not one or two steps");
        }

        var last = chain[^1];
        var format = last switch { "zip" => ContainerFormat.Zip, "tar" => ContainerFormat.Tar, _ => ContainerFormat.Stream };
        if (chain.Count == 1)
        {
            return format == ContainerFormat.Stream
                ? throw new InvalidDataException($"file {container.Id} is recorded as a container and its chain [{last}] names no archive")
                : new ContainerLayout(CompressionKind.None, format);
        }

        return new ContainerLayout(Names.Parse<CompressionKind>(chain[0]), format);
    }
}

/// <summary>One entry of a container as it is listed. Only regular files become members.</summary>
/// <param name="Ordinal">Position in the container's own order, from 0, counting every entry.</param>
/// <param name="Name">The entry's name as the container stores it. Data only: never used as a path on disk.</param>
/// <param name="Size">Bytes of the entry that are present (less than the entry states when the container is cut short).</param>
/// <param name="Offset">For tar: where the entry's data starts in the archive bytes. Null for zip.</param>
/// <param name="Cut">The container ends inside this entry's data.</param>
/// <param name="Crc32">For zip: the CRC-32 of the content as the directory states it. The runtime's reader does not check it.</param>
/// <param name="Encrypted">For zip: the entry is marked encrypted. The runtime's reader hands out its ciphertext as if it were content.</param>
public sealed record ArchiveEntry(int Ordinal, string Name, long Size, long? Offset, DateTime? ModifiedUtc, bool Regular, bool Cut, uint? Crc32 = null, bool Encrypted = false);

/// <summary>What a walk found besides the entries.</summary>
/// <param name="Truncated">The archive ends before its own end structure (a tar without its closing blocks, cut in a header or in data).</param>
public sealed record WalkSummary(int Entries, int Skipped, bool Truncated, string? Note);

/// <summary>
/// Lists a container and hands out its members' bytes, read-only. zip through the runtime's <c>ZipArchive</c>, tar
/// through <c>TarReader</c> for the listing and plain offsets for the data. Directories, links and devices are
/// listed and never followed or opened.
/// </summary>
public static class ArchiveWalker
{
    /// <summary>
    /// Walks every entry of the archive bytes in <paramref name="bodyPath"/> in order.
    /// <paramref name="visit"/> gets the entry and a function that opens its bytes; the stream it returns is valid
    /// until the visit returns.
    /// </summary>
    public static WalkSummary Walk(string bodyPath, ContainerFormat format, string memberName, Action<ArchiveEntry, Func<Stream>> visit)
    {
        ArgumentNullException.ThrowIfNull(bodyPath);
        ArgumentNullException.ThrowIfNull(visit);
        return format switch
        {
            ContainerFormat.Zip => WalkZip(bodyPath, visit),
            ContainerFormat.Tar => WalkTar(bodyPath, visit),
            _ => WalkStream(bodyPath, memberName, visit),
        };
    }

    /// <summary>Opens the bytes of one member, by the position its record keeps. The caller disposes the stream.</summary>
    public static Stream OpenMember(string bodyPath, ContainerFormat format, int ordinal, long? offset, long size)
    {
        var body = OpenBody(bodyPath);
        try
        {
            switch (format)
            {
                case ContainerFormat.Tar:
                    return new SliceStream(body, offset ?? throw new InvalidDataException("a tar member record without its data offset"), size);
                case ContainerFormat.Stream:
                    return body;
                default:
                    var archive = new ZipArchive(body, ZipArchiveMode.Read, leaveOpen: false);
                    if (ordinal < 0 || ordinal >= archive.Entries.Count)
                    {
                        archive.Dispose();
                        throw new InvalidDataException($"the zip has {archive.Entries.Count} entries and the member record names entry {ordinal}");
                    }

                    return new OwningStream(archive.Entries[ordinal].Open(), archive);
            }
        }
        catch
        {
            body.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens a zip entry's bytes as the zip stores them, for an entry whose content cannot be read. Null when the
    /// zip's directory cannot be followed to the entry. The caller disposes the stream.
    /// </summary>
    public static Stream? OpenStoredZipEntry(string bodyPath, int ordinal, out ZipStoredEntry? located)
    {
        var body = OpenBody(bodyPath);
        located = ZipStoredEntry.Locate(body, ordinal);
        if (located is null)
        {
            body.Dispose();
            return null;
        }

        return new SliceStream(body, located.DataOffset, located.StoredSize);
    }

    private static FileStream OpenBody(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);

    private static WalkSummary WalkZip(string bodyPath, Action<ArchiveEntry, Func<Stream>> visit)
    {
        using var body = OpenBody(bodyPath);
        using var archive = new ZipArchive(body, ZipArchiveMode.Read, leaveOpen: true);
        var skipped = 0;
        for (var i = 0; i < archive.Entries.Count; i++)
        {
            var entry = archive.Entries[i];
            // A zip directory is an entry whose name ends in a slash; a link is marked in attributes we do not read,
            // and its "content" is the link's target as text, which is recorded as what it is: a small file.
            var regular = !entry.FullName.EndsWith('/') && !entry.FullName.EndsWith('\\');
            skipped += regular ? 0 : 1;
            DateTime? modified = entry.LastWriteTime.Year > 1980 ? DateTime.SpecifyKind(entry.LastWriteTime.DateTime, DateTimeKind.Utc) : null;
            visit(new ArchiveEntry(i, entry.FullName, entry.Length, null, modified, regular, Cut: false, entry.Crc32, entry.IsEncrypted), entry.Open);
        }

        return new WalkSummary(archive.Entries.Count, skipped, Truncated: false, null);
    }

    private static WalkSummary WalkTar(string bodyPath, Action<ArchiveEntry, Func<Stream>> visit)
    {
        using var body = OpenBody(bodyPath);
        using var reader = new TarReader(body, leaveOpen: true);
        var ordinal = 0;
        var skipped = 0;
        string? note = null;
        long nextHeader = 0;
        while (true)
        {
            TarEntry? entry;
            try
            {
                entry = reader.GetNextEntry(copyData: false);
            }
            catch (Exception e) when (e is EndOfStreamException or InvalidDataException or FormatException)
            {
                // The reader cannot go on: the tar is cut, or what follows is no tar header. It refuses an entry
                // whose data is not all there, so that entry's header is read here: a member the archive ends
                // inside is recorded from the bytes that are present, and says it is cut.
                if (CutEntry(body, nextHeader, ordinal) is { } cutEntry)
                {
                    var from = cutEntry.Offset!.Value;
                    visit(cutEntry, () => new SliceStream(OpenBody(bodyPath), from, cutEntry.Size));
                    return new WalkSummary(ordinal + 1, skipped, Truncated: true, $"the tar ends inside the data of entry {ordinal} ({cutEntry.Size} bytes of it are there)");
                }

                note = $"the tar could not be read past entry {ordinal}: {e.Message}";
                return new WalkSummary(ordinal, skipped, Truncated: true, note);
            }

            if (entry is null)
            {
                return new WalkSummary(ordinal, skipped, Truncated: false, null);
            }

            var regular = entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile;
            // With a seekable archive the reader stands behind the entry's data, padded to a whole 512-byte
            // block, when it hands the entry over. So the data starts that far back, and it is the next Length
            // bytes of the archive, which is all a member record needs to find it again.
            var offset = body.Position - ((entry.Length + 511) / 512 * 512);
            var present = Math.Max(0, Math.Min(entry.Length, body.Length - offset));
            nextHeader = body.Position;
            var cut = regular && present < entry.Length;
            skipped += regular ? 0 : 1;
            var at = ordinal++;
            visit(new ArchiveEntry(at, entry.Name, regular ? present : 0, regular ? offset : null, entry.ModificationTime.UtcDateTime, regular, cut),
                () => new SliceStream(OpenBody(bodyPath), offset, present));
            if (cut)
            {
                note = $"the tar ends inside the data of entry {at} ({present} of {entry.Length} bytes are there)";
                return new WalkSummary(ordinal, skipped, Truncated: true, note);
            }
        }
    }

    /// <summary>
    /// The entry whose header stands at <paramref name="headerAt"/> when the archive ends inside its data: a
    /// plain ustar or v7 header of a regular file with a valid checksum. Null for anything else (a header that is
    /// cut itself, an extended header, bytes that are no header).
    /// </summary>
    private static ArchiveEntry? CutEntry(FileStream body, long headerAt, int ordinal)
    {
        Span<byte> header = stackalloc byte[512];
        if (headerAt < 0 || body.Length - headerAt < header.Length)
        {
            return null;
        }

        body.Position = headerAt;
        body.ReadExactly(header);
        var type = header[156];
        if (type is not ((byte)'0' or 0 or (byte)'7') || !Octal(header.Slice(148, 8), out var stated))
        {
            return null;
        }

        long sum = 0;
        for (var i = 0; i < header.Length; i++)
        {
            sum += i is >= 148 and < 156 ? (byte)' ' : header[i];
        }

        if (sum != stated || !Octal(header.Slice(124, 12), out var size) || !Octal(header.Slice(136, 12), out var seconds))
        {
            return null;
        }

        var dataAt = headerAt + header.Length;
        var present = body.Length - dataAt;
        if (present >= size)
        {
            return null; // the data is all there: whatever stopped the reader, it was not a cut inside this entry
        }

        var name = Text(header[..100]);
        var prefix = Text(header.Slice(345, 155));
        if (header.Slice(257, 5).SequenceEqual("ustar"u8) && prefix.Length > 0)
        {
            name = prefix + "/" + name;
        }

        return new ArchiveEntry(ordinal, name, present, dataAt, DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime, Regular: true, Cut: true);
    }

    private static bool Octal(ReadOnlySpan<byte> field, out long value)
    {
        value = 0;
        var digits = 0;
        foreach (var b in field)
        {
            if (b is (byte)' ' or 0)
            {
                if (digits > 0)
                {
                    break;
                }

                continue;
            }

            if (b is < (byte)'0' or > (byte)'7')
            {
                return false;
            }

            value = (value << 3) | (uint)(b - (byte)'0');
            digits++;
        }

        return digits > 0;
    }

    private static string Text(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        return System.Text.Encoding.UTF8.GetString(end < 0 ? field : field[..end]);
    }

    private static WalkSummary WalkStream(string bodyPath, string memberName, Action<ArchiveEntry, Func<Stream>> visit)
    {
        var info = new FileInfo(bodyPath);
        visit(new ArchiveEntry(0, memberName, info.Length, 0, null, Regular: true, Cut: false), () => OpenBody(bodyPath));
        return new WalkSummary(1, 0, Truncated: false, null);
    }

    /// <summary>A read-only window of a file: one tar member's data. Owns the file stream.</summary>
    private sealed class SliceStream(FileStream inner, long start, long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => _position = value < 0 || value > length ? throw new ArgumentOutOfRangeException(nameof(value)) : value;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var want = (int)Math.Min(buffer.Length, length - _position);
            if (want <= 0)
            {
                return 0;
            }

            inner.Position = start + _position;
            var n = inner.Read(buffer[..want]);
            _position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _position + offset, _ => length + offset };
            return _position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>A zip entry's stream that closes its archive (and the file under it) with itself.</summary>
    private sealed class OwningStream(Stream inner, IDisposable owner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                owner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
