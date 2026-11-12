using System.Buffers.Binary;
using ClickHouse.Driver.Compression;

namespace Urlog.Core.Ingest.Native;

/// <summary>
/// The client side of the native protocol's byte stream: raw packet bytes (packet type, table name) and the
/// compressed payload (block info, columns). The payload goes through a 1 MiB staging buffer; each full buffer
/// becomes one frame: <c>CityHash128(16) | method(1) | compressed size(4) | decompressed size(4) | LZ4 block</c>,
/// the same framing and frame size clickhouse-client uses (<c>max_compress_block_size</c>). Frames collect in a
/// send buffer and go to the socket in a few large writes. Nothing here allocates after construction.
/// </summary>
public sealed class ProtocolWriter
{
    /// <summary>Payload bytes per compressed frame.</summary>
    public const int FrameBytes = 1 << 20;

    private const int FrameHeaderBytes = 16 + 1 + 4 + 4;
    private const byte MethodLz4 = 0x82;

    private static readonly Lz4Compressor Lz4 = new(Lz4Level.Fast, FrameBytes);

    private readonly Stream _stream;
    private readonly bool _compress;
    private readonly byte[] _staging = GC.AllocateUninitializedArray<byte>(FrameBytes);
    private readonly byte[] _out;
    private readonly int _sendThreshold;
    private int _stagingPos;
    private int _outPos;

    public ProtocolWriter(Stream stream, bool compress, int sendBufferBytes = 4 << 20)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _compress = compress;
        _sendThreshold = sendBufferBytes;
        _out = GC.AllocateUninitializedArray<byte>(sendBufferBytes + FrameHeaderBytes + Lz4.MaxEncodedLength(FrameBytes));
    }

    /// <summary>Bytes handed to the socket.</summary>
    public long BytesSent { get; private set; }

    /// <summary>Payload bytes closed into frames so far (before compression).</summary>
    public long PayloadBytes { get; private set; }

    /// <summary>Payload bytes written so far, including the open staging buffer: a position in the payload stream.</summary>
    public long PayloadPosition => PayloadBytes + _stagingPos;

    /// <summary>Frames closed so far.</summary>
    public long FramesWritten { get; private set; }

    public static int EncodeVarUInt(ulong value, Span<byte> destination)
    {
        var i = 0;
        while (value >= 0x80)
        {
            destination[i++] = (byte)(value | 0x80);
            value >>= 7;
        }

        destination[i++] = (byte)value;
        return i;
    }

    // ---- outside compression: packet headers

    public void Raw(ReadOnlySpan<byte> bytes)
    {
        if (_stagingPos != 0)
        {
            throw new InvalidOperationException("raw bytes inside a compressed payload; call EndPacket first");
        }

        EnsureOut(bytes.Length);
        bytes.CopyTo(_out.AsSpan(_outPos));
        _outPos += bytes.Length;
    }

    public void RawByte(byte value)
    {
        Span<byte> one = [value];
        Raw(one);
    }

    public void RawVarUInt(ulong value)
    {
        Span<byte> tmp = stackalloc byte[10];
        Raw(tmp[..EncodeVarUInt(value, tmp)]);
    }

    public void RawInt64(long value)
    {
        Span<byte> tmp = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(tmp, value);
        Raw(tmp);
    }

    public void RawString(ReadOnlySpan<byte> utf8)
    {
        RawVarUInt((ulong)utf8.Length);
        Raw(utf8);
    }

    // ---- inside the (possibly compressed) payload

    /// <summary>A span of <paramref name="size"/> bytes to fill, followed by <see cref="Advance"/>. Size must be at most <see cref="FrameBytes"/>.</summary>
    public Span<byte> GetSpan(int size)
    {
        if (FrameBytes - _stagingPos < size)
        {
            FlushFrame();
        }

        return _staging.AsSpan(_stagingPos, size);
    }

    public void Advance(int count) => _stagingPos += count;

    public void Write(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            var room = FrameBytes - _stagingPos;
            if (room == 0)
            {
                FlushFrame();
                room = FrameBytes;
            }

            var n = Math.Min(room, bytes.Length);
            bytes[..n].CopyTo(_staging.AsSpan(_stagingPos));
            _stagingPos += n;
            bytes = bytes[n..];
        }
    }

    public void WriteByte(byte value)
    {
        GetSpan(1)[0] = value;
        Advance(1);
    }

    public void WriteVarUInt(ulong value)
    {
        var span = GetSpan(10);
        Advance(EncodeVarUInt(value, span));
    }

    public void WriteInt32(int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(GetSpan(4), value);
        Advance(4);
    }

    public void WriteUInt64(ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(GetSpan(8), value);
        Advance(8);
    }

    public void WriteString(ReadOnlySpan<byte> utf8)
    {
        WriteVarUInt((ulong)utf8.Length);
        Write(utf8);
    }

    /// <summary>Closes the payload of the current packet: a packet's compressed stream is a whole number of frames.</summary>
    public void EndPacket() => FlushFrame();

    /// <summary>Pushes everything to the socket.</summary>
    public void Flush()
    {
        FlushFrame();
        SendOut();
        _stream.Flush();
    }

    private void FlushFrame()
    {
        if (_stagingPos == 0)
        {
            return;
        }

        PayloadBytes += _stagingPos;
        FramesWritten++;
        if (!_compress)
        {
            EnsureOut(_stagingPos);
            _staging.AsSpan(0, _stagingPos).CopyTo(_out.AsSpan(_outPos));
            _outPos += _stagingPos;
        }
        else
        {
            EnsureOut(FrameHeaderBytes + Lz4.MaxEncodedLength(_stagingPos));
            var frame = _out.AsSpan(_outPos);
            var compressed = Lz4.Encode(_staging.AsSpan(0, _stagingPos), frame[FrameHeaderBytes..]);
            frame[16] = MethodLz4;
            BinaryPrimitives.WriteUInt32LittleEndian(frame[17..], (uint)(compressed + 9));
            BinaryPrimitives.WriteUInt32LittleEndian(frame[21..], (uint)_stagingPos);
            var (low, high) = CityHash128.Hash(frame.Slice(16, 9 + compressed));
            BinaryPrimitives.WriteUInt64LittleEndian(frame, low);
            BinaryPrimitives.WriteUInt64LittleEndian(frame[8..], high);
            _outPos += FrameHeaderBytes + compressed;
        }

        _stagingPos = 0;
        if (_outPos >= _sendThreshold)
        {
            SendOut();
        }
    }

    private void EnsureOut(int bytes)
    {
        if (_out.Length - _outPos < bytes)
        {
            SendOut();
        }

        if (_out.Length - _outPos < bytes)
        {
            throw new InvalidOperationException($"a single write of {bytes} bytes exceeds the send buffer");
        }
    }

    private void SendOut()
    {
        if (_outPos == 0)
        {
            return;
        }

        _stream.Write(_out, 0, _outPos);
        BytesSent += _outPos;
        _outPos = 0;
    }
}
