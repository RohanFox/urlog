using System.Buffers.Binary;
using System.Text;
using SharpCompress.Compressors.BZip2;

namespace Urlog.Sources.Tests.Detection;

/// <summary>
/// Container encoders for the fixture writer whose output is fixed by this file alone: gzip and xz with stored
/// (uncompressed) blocks, zip with stored entries, ustar. A real compressor's bytes change with its library version,
/// which a byte-for-byte fixture test cannot live with. bzip2 has no stored form and goes through the pinned
/// SharpCompress encoder: a package bump that changes its output shows up as a failing regeneration test.
/// </summary>
internal static class FixtureContainers
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    /// <summary>A gzip member whose deflate stream is stored blocks only.</summary>
    public static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        output.Write([0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF]);
        var position = 0;
        do
        {
            var length = Math.Min(65535, data.Length - position);
            var last = position + length == data.Length;
            output.WriteByte((byte)(last ? 1 : 0));
            WriteUInt16(output, (ushort)length);
            WriteUInt16(output, (ushort)~length);
            output.Write(data, position, length);
            position += length;
        }
        while (position < data.Length);

        WriteUInt32(output, Crc32(data));
        WriteUInt32(output, (uint)data.Length);
        return output.ToArray();
    }

    public static byte[] Bzip2(byte[] data)
    {
        using var output = new MemoryStream();
        using (var bzip = BZip2Stream.Create(output, SharpCompress.Compressors.CompressionMode.Compress, false))
        {
            bzip.Write(data);
        }

        return output.ToArray();
    }

    /// <summary>An xz stream with one block of uncompressed LZMA2 chunks and a CRC32 check.</summary>
    public static byte[] Xz(byte[] data)
    {
        using var output = new MemoryStream();
        byte[] streamFlags = [0x00, 0x01];
        output.Write([0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00]);
        output.Write(streamFlags);
        WriteUInt32(output, Crc32(streamFlags));

        // Block header: size, flags (one filter), LZMA2, one property byte (256 KiB dictionary), padding, CRC32.
        byte[] blockHeader = [0x02, 0x00, 0x21, 0x01, 0x0C, 0x00, 0x00, 0x00];
        output.Write(blockHeader);
        WriteUInt32(output, Crc32(blockHeader));

        var compressedStart = output.Position;
        var position = 0;
        while (position < data.Length)
        {
            var length = Math.Min(65536, data.Length - position);
            output.WriteByte((byte)(position == 0 ? 0x01 : 0x02));
            output.WriteByte((byte)((length - 1) >> 8));
            output.WriteByte((byte)(length - 1));
            output.Write(data, position, length);
            position += length;
        }

        output.WriteByte(0x00);
        var compressedSize = output.Position - compressedStart;
        Pad4(output, compressedSize);
        WriteUInt32(output, Crc32(data));

        using var index = new MemoryStream();
        index.WriteByte(0x00);
        WriteVarint(index, 1);
        WriteVarint(index, (ulong)(12 + compressedSize + 4));
        WriteVarint(index, (ulong)data.Length);
        Pad4(index, index.Position);
        var indexBytes = index.ToArray();
        output.Write(indexBytes);
        WriteUInt32(output, Crc32(indexBytes));

        var footer = new byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(footer, (uint)(((indexBytes.Length + 4) / 4) - 1));
        footer[4] = streamFlags[0];
        footer[5] = streamFlags[1];
        WriteUInt32(output, Crc32(footer));
        output.Write(footer);
        output.Write("YZ"u8);
        return output.ToArray();
    }

    /// <summary>A zip archive with stored entries, dated 2026-03-01 00:00.</summary>
    public static byte[] Zip(params (string Name, byte[] Data)[] members)
    {
        const ushort Date = ((2026 - 1980) << 9) | (3 << 5) | 1;
        using var output = new MemoryStream();
        using var directory = new MemoryStream();
        foreach (var (name, data) in members)
        {
            var nameBytes = Encoding.ASCII.GetBytes(name);
            var offset = (uint)output.Position;
            var crc = Crc32(data);
            WriteUInt32(output, 0x04034B50);
            WriteUInt16(output, 20);
            WriteUInt16(output, 0);
            WriteUInt16(output, 0);
            WriteUInt16(output, 0);
            WriteUInt16(output, Date);
            WriteUInt32(output, crc);
            WriteUInt32(output, (uint)data.Length);
            WriteUInt32(output, (uint)data.Length);
            WriteUInt16(output, (ushort)nameBytes.Length);
            WriteUInt16(output, 0);
            output.Write(nameBytes);
            output.Write(data);

            WriteUInt32(directory, 0x02014B50);
            WriteUInt16(directory, 20);
            WriteUInt16(directory, 20);
            WriteUInt16(directory, 0);
            WriteUInt16(directory, 0);
            WriteUInt16(directory, 0);
            WriteUInt16(directory, Date);
            WriteUInt32(directory, crc);
            WriteUInt32(directory, (uint)data.Length);
            WriteUInt32(directory, (uint)data.Length);
            WriteUInt16(directory, (ushort)nameBytes.Length);
            WriteUInt16(directory, 0);
            WriteUInt16(directory, 0);
            WriteUInt16(directory, 0);
            WriteUInt16(directory, 0);
            WriteUInt32(directory, 0);
            WriteUInt32(directory, offset);
            directory.Write(nameBytes);
        }

        var directoryOffset = (uint)output.Position;
        var directoryBytes = directory.ToArray();
        output.Write(directoryBytes);
        WriteUInt32(output, 0x06054B50);
        WriteUInt16(output, 0);
        WriteUInt16(output, 0);
        WriteUInt16(output, (ushort)members.Length);
        WriteUInt16(output, (ushort)members.Length);
        WriteUInt32(output, (uint)directoryBytes.Length);
        WriteUInt32(output, directoryOffset);
        WriteUInt16(output, 0);
        return output.ToArray();
    }

    /// <summary>A ustar archive of regular files, every time and owner field zero.</summary>
    public static byte[] Tar(params (string Name, byte[] Data)[] members)
    {
        using var output = new MemoryStream();
        foreach (var (name, data) in members)
        {
            var header = new byte[512];
            Encoding.ASCII.GetBytes(name).CopyTo(header, 0);
            "0000644\0"u8.CopyTo(header.AsSpan(100));
            "0000000\0"u8.CopyTo(header.AsSpan(108));
            "0000000\0"u8.CopyTo(header.AsSpan(116));
            Encoding.ASCII.GetBytes(Convert.ToString(data.Length, 8).PadLeft(11, '0') + "\0").CopyTo(header, 124);
            "00000000000\0"u8.CopyTo(header.AsSpan(136));
            "        "u8.CopyTo(header.AsSpan(148));
            header[156] = (byte)'0';
            "ustar\0"u8.CopyTo(header.AsSpan(257));
            "00"u8.CopyTo(header.AsSpan(263));
            var sum = 0;
            foreach (var b in header)
            {
                sum += b;
            }

            Encoding.ASCII.GetBytes(Convert.ToString(sum, 8).PadLeft(6, '0') + "\0 ").CopyTo(header, 148);
            output.Write(header);
            output.Write(data);
            output.Write(new byte[(512 - (data.Length % 512)) % 512]);
        }

        output.Write(new byte[1024]);
        return output.ToArray();
    }

    private static void Pad4(Stream stream, long length)
    {
        while (length % 4 != 0)
        {
            stream.WriteByte(0);
            length++;
        }
    }

    private static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (var i = 0u; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }
}
