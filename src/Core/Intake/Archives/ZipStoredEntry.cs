using System.Buffers.Binary;

namespace Urlog.Core.Intake.Archives;

/// <summary>
/// Where one zip entry's bytes lie in the zip as they are stored (compressed, or encrypted), for an entry whose
/// content cannot be delivered. The runtime's reader does not hand out stored bytes, so the zip's own directory
/// is read here: the end-of-central-directory record, the central directory up to the entry, and the entry's
/// local header, with the zip64 forms of each.
/// </summary>
/// <param name="DataOffset">First stored byte of the entry in the zip.</param>
/// <param name="StoredSize">Stored bytes of the entry.</param>
/// <param name="StatedSize">The content size the directory states.</param>
/// <param name="Method">The compression method number (0 stored, 8 deflate, 12 bzip2, 99 AES, ...).</param>
public sealed record ZipStoredEntry(long DataOffset, long StoredSize, long StatedSize, ushort Method, bool Encrypted)
{
    private const uint EndOfDirectory = 0x06054B50;
    private const uint Zip64Locator = 0x07064B50;
    private const uint Zip64EndOfDirectory = 0x06064B50;
    private const uint DirectoryEntry = 0x02014B50;
    private const uint LocalHeader = 0x04034B50;

    /// <summary>The entry at <paramref name="ordinal"/> in directory order, or null when the directory cannot be followed that far.</summary>
    public static ZipStoredEntry? Locate(Stream zip, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(zip);
        if (ordinal < 0 || FindDirectory(zip) is not var (directoryAt, entries) || ordinal >= entries)
        {
            return null;
        }

        Span<byte> fixedPart = stackalloc byte[46];
        var at = directoryAt;
        for (var i = 0; ; i++)
        {
            if (!ReadAt(zip, at, fixedPart) || BinaryPrimitives.ReadUInt32LittleEndian(fixedPart) != DirectoryEntry)
            {
                return null;
            }

            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart[28..]);
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart[30..]);
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart[32..]);
            if (i < ordinal)
            {
                at += fixedPart.Length + nameLength + extraLength + commentLength;
                continue;
            }

            var flags = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart[8..]);
            var method = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart[10..]);
            long stored = BinaryPrimitives.ReadUInt32LittleEndian(fixedPart[20..]);
            long stated = BinaryPrimitives.ReadUInt32LittleEndian(fixedPart[24..]);
            long localAt = BinaryPrimitives.ReadUInt32LittleEndian(fixedPart[42..]);
            if (stored == uint.MaxValue || stated == uint.MaxValue || localAt == uint.MaxValue)
            {
                // The zip64 extra field holds, in this order, exactly those of the three that are 0xFFFFFFFF.
                var extra = new byte[extraLength];
                if (!ReadAt(zip, at + fixedPart.Length + nameLength, extra) || Zip64Extra(extra) is not { } wide)
                {
                    return null;
                }

                var next = 0;
                long Take()
                {
                    if (next + 8 > wide.Length)
                    {
                        return -1;
                    }

                    var value = BinaryPrimitives.ReadInt64LittleEndian(wide.AsSpan(next));
                    next += 8;
                    return value;
                }

                stated = stated == uint.MaxValue ? Take() : stated;
                stored = stored == uint.MaxValue ? Take() : stored;
                localAt = localAt == uint.MaxValue ? Take() : localAt;
                if (stated < 0 || stored < 0 || localAt < 0)
                {
                    return null;
                }
            }

            Span<byte> local = stackalloc byte[30];
            if (!ReadAt(zip, localAt, local) || BinaryPrimitives.ReadUInt32LittleEndian(local) != LocalHeader)
            {
                return null;
            }

            var dataAt = localAt + local.Length + BinaryPrimitives.ReadUInt16LittleEndian(local[26..]) + BinaryPrimitives.ReadUInt16LittleEndian(local[28..]);
            return dataAt + stored <= zip.Length ? new ZipStoredEntry(dataAt, stored, stated, method, (flags & 1) != 0) : null;
        }
    }

    private static (long DirectoryAt, long Entries)? FindDirectory(Stream zip)
    {
        // The record is the last thing in the file but for a comment of at most 65,535 bytes.
        var tail = new byte[(int)Math.Min(zip.Length, 22 + 65535 + 20)];
        var tailAt = zip.Length - tail.Length;
        if (!ReadAt(zip, tailAt, tail))
        {
            return null;
        }

        for (var i = tail.Length - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) != EndOfDirectory)
            {
                continue;
            }

            long entries = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 10));
            long directoryAt = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i + 16));
            if (entries != ushort.MaxValue && directoryAt != uint.MaxValue)
            {
                return (directoryAt, entries);
            }

            Span<byte> wide = stackalloc byte[56];
            if (i < 20 || BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i - 20)) != Zip64Locator
                || !ReadAt(zip, BinaryPrimitives.ReadInt64LittleEndian(tail.AsSpan(i - 12)), wide)
                || BinaryPrimitives.ReadUInt32LittleEndian(wide) != Zip64EndOfDirectory)
            {
                return null;
            }

            return (BinaryPrimitives.ReadInt64LittleEndian(wide[48..]), BinaryPrimitives.ReadInt64LittleEndian(wide[32..]));
        }

        return null;
    }

    private static byte[]? Zip64Extra(ReadOnlySpan<byte> extra)
    {
        while (extra.Length >= 4)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(extra);
            var size = BinaryPrimitives.ReadUInt16LittleEndian(extra[2..]);
            if (size > extra.Length - 4)
            {
                return null;
            }

            if (id == 1)
            {
                return extra.Slice(4, size).ToArray();
            }

            extra = extra[(4 + size)..];
        }

        return null;
    }

    private static bool ReadAt(Stream stream, long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset + buffer.Length > stream.Length)
        {
            return false;
        }

        stream.Position = offset;
        stream.ReadExactly(buffer);
        return true;
    }
}
