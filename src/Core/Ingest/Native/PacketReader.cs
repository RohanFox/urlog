using System.Buffers.Binary;
using System.Text;
using ClickHouse.Driver.Compression;

namespace Urlog.Core.Ingest.Native;

/// <summary>A byte source the block parser reads from: the socket itself, or the decompressed frames of a Data packet.</summary>
public interface IPayloadSource
{
    byte ReadByte();

    void Read(Span<byte> destination);
}

public static class PayloadSourceExtensions
{
    public static ulong ReadVarUInt(this IPayloadSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ulong value = 0;
        var shift = 0;
        while (true)
        {
            var b = source.ReadByte();
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return value;
            }

            shift += 7;
            if (shift > 63)
            {
                throw new ClickHouseInsertException("malformed varint from the server");
            }
        }
    }

    public static int ReadInt32(this IPayloadSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Span<byte> tmp = stackalloc byte[4];
        source.Read(tmp);
        return BinaryPrimitives.ReadInt32LittleEndian(tmp);
    }

    public static string ReadString(this IPayloadSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var length = checked((int)source.ReadVarUInt());
        if (length == 0)
        {
            return "";
        }

        if (length > 64 << 20)
        {
            throw new ClickHouseInsertException($"server string of {length} bytes is not plausible");
        }

        var bytes = length <= 1024 ? stackalloc byte[length] : new byte[length];
        source.Read(bytes);
        return Encoding.UTF8.GetString(bytes);
    }
}

/// <summary>Buffered reads from the socket.</summary>
public sealed class PacketReader(Stream stream) : IPayloadSource
{
    private readonly byte[] _buffer = new byte[64 << 10];
    private int _pos;
    private int _len;

    /// <summary>True when bytes were read from the socket but not consumed yet.</summary>
    public bool HasBuffered => _pos < _len;

    public byte ReadByte()
    {
        if (_pos == _len)
        {
            Fill();
        }

        return _buffer[_pos++];
    }

    public void Read(Span<byte> destination)
    {
        while (!destination.IsEmpty)
        {
            if (_pos == _len)
            {
                Fill();
            }

            var n = Math.Min(destination.Length, _len - _pos);
            _buffer.AsSpan(_pos, n).CopyTo(destination);
            _pos += n;
            destination = destination[n..];
        }
    }

    private void Fill()
    {
        var n = stream.Read(_buffer, 0, _buffer.Length);
        if (n <= 0)
        {
            throw new EndOfStreamException("ClickHouse closed the connection");
        }

        _pos = 0;
        _len = n;
    }
}

/// <summary>
/// The decompressed byte stream of one Data packet: frames are pulled from the socket as the parser needs bytes,
/// each frame's CityHash128 is checked. <see cref="Remaining"/> must be zero when a block has been parsed; a
/// packet's compressed stream is a whole number of frames, so leftover bytes mean the parser and the server
/// disagree about the block layout.
/// </summary>
public sealed class FrameSource(PacketReader reader) : IPayloadSource
{
    private const byte MethodNone = 0x02;
    private const byte MethodLz4 = 0x82;
    private const int HeaderBytes = 1 + 4 + 4;

    private byte[] _compressed = new byte[64 << 10];
    private byte[] _data = new byte[64 << 10];
    private int _pos;
    private int _len;

    public int Remaining => _len - _pos;

    public void Reset()
    {
        _pos = 0;
        _len = 0;
    }

    public byte ReadByte()
    {
        if (_pos == _len)
        {
            NextFrame();
        }

        return _data[_pos++];
    }

    public void Read(Span<byte> destination)
    {
        while (!destination.IsEmpty)
        {
            if (_pos == _len)
            {
                NextFrame();
            }

            var n = Math.Min(destination.Length, _len - _pos);
            _data.AsSpan(_pos, n).CopyTo(destination);
            _pos += n;
            destination = destination[n..];
        }
    }

    private void NextFrame()
    {
        Span<byte> checksum = stackalloc byte[16];
        reader.Read(checksum);
        Span<byte> header = stackalloc byte[HeaderBytes];
        reader.Read(header);
        var method = header[0];
        var compressedSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header[1..]));
        var decompressedSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header[5..]));
        if (compressedSize < HeaderBytes || decompressedSize < 0 || decompressedSize > 1 << 30)
        {
            throw new ClickHouseInsertException("bad compressed frame header from the server");
        }

        // The checksum covers header + compressed data, so keep them contiguous.
        if (_compressed.Length < compressedSize)
        {
            _compressed = new byte[Math.Max(compressedSize, _compressed.Length * 2)];
        }

        header.CopyTo(_compressed);
        reader.Read(_compressed.AsSpan(HeaderBytes, compressedSize - HeaderBytes));
        var (low, high) = CityHash128.Hash(_compressed.AsSpan(0, compressedSize));
        if (low != BinaryPrimitives.ReadUInt64LittleEndian(checksum) || high != BinaryPrimitives.ReadUInt64LittleEndian(checksum[8..]))
        {
            throw new ClickHouseInsertException("checksum mismatch on a compressed frame from the server");
        }

        if (_data.Length < decompressedSize)
        {
            _data = new byte[Math.Max(decompressedSize, _data.Length * 2)];
        }

        var payload = _compressed.AsSpan(HeaderBytes, compressedSize - HeaderBytes);
        switch (method)
        {
            case MethodLz4:
                var produced = Lz4Compressor.Default.Decode(payload, _data.AsSpan(0, decompressedSize));
                if (produced != decompressedSize)
                {
                    throw new ClickHouseInsertException($"LZ4 frame decoded to {produced} bytes, header says {decompressedSize}");
                }

                break;
            case MethodNone:
                payload[..decompressedSize].CopyTo(_data);
                break;
            default:
                throw new ClickHouseInsertException($"compression method 0x{method:x2} from the server is not supported");
        }

        _pos = 0;
        _len = decompressedSize;
    }
}
