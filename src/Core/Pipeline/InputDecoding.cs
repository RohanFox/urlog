using System.Buffers;
using System.Buffers.Binary;
using System.Text.Unicode;

namespace Urlog.Core.Pipeline;

public enum CompressionKind : byte
{
    None,
    Gzip,
    Zstd,
    Xz,
    Bzip2,
}

/// <summary>Byte encoding recorded on the file row at intake (design record, "Format detection", step 2).</summary>
public enum InputEncoding : byte
{
    Utf8,
    Utf16LittleEndian,
    Utf16BigEndian,
}

/// <summary>
/// Turns the stored evidence bytes into the UTF-8 stream the chunker reads: decompression (gzip, zstd, xz, bzip2)
/// then UTF-16 transcoding when the file row says so. Everything downstream, including <c>file_offset</c>, is in
/// terms of this decoded stream. The original file is only ever read.
/// </summary>
public static class InputDecoding
{
    private static ReadOnlySpan<byte> GzipMagic => [0x1F, 0x8B];

    private static ReadOnlySpan<byte> ZstdMagic => [0x28, 0xB5, 0x2F, 0xFD];

    private static ReadOnlySpan<byte> XzMagic => [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00];

    private static ReadOnlySpan<byte> Utf16LeBom => [0xFF, 0xFE];

    private static ReadOnlySpan<byte> Utf16BeBom => [0xFE, 0xFF];

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>Compression from the first bytes of a file (6 are enough).</summary>
    public static CompressionKind DetectCompression(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(GzipMagic))
        {
            return CompressionKind.Gzip;
        }

        if (head.StartsWith(ZstdMagic))
        {
            return CompressionKind.Zstd;
        }

        if (head.StartsWith(XzMagic))
        {
            return CompressionKind.Xz;
        }

        if (head.StartsWith("BZh"u8) && head.Length > 3 && head[3] is >= (byte)'1' and <= (byte)'9')
        {
            return CompressionKind.Bzip2;
        }

        return CompressionKind.None;
    }

    /// <summary>Encoding from a byte-order mark, if any; <paramref name="bomLength"/> is the bytes to skip.</summary>
    public static InputEncoding DetectEncoding(ReadOnlySpan<byte> head, out int bomLength)
    {
        if (head.StartsWith(Utf16LeBom))
        {
            bomLength = 2;
            return InputEncoding.Utf16LittleEndian;
        }

        if (head.StartsWith(Utf16BeBom))
        {
            bomLength = 2;
            return InputEncoding.Utf16BigEndian;
        }

        bomLength = head.StartsWith(Utf8Bom) ? 3 : 0;
        return InputEncoding.Utf8;
    }

    /// <summary>
    /// Wraps <paramref name="raw"/> in the matching decompressor; <see cref="CompressionKind.None"/> returns it
    /// unchanged. A compressed input cut short reads as an ordinary end of stream: ask <see cref="OpenChecked"/>'s
    /// stream how the input ended before treating the content as whole.
    /// </summary>
    public static Stream Decompress(Stream raw, CompressionKind compression)
    {
        ArgumentNullException.ThrowIfNull(raw);
        return compression == CompressionKind.None ? raw : new CheckedDecodeStream(raw, compression);
    }

    /// <summary>The decompressed content with a verdict on how the stored bytes ended, for every kind including <see cref="CompressionKind.None"/>.</summary>
    public static CheckedDecodeStream OpenChecked(Stream raw, CompressionKind compression) => new(raw, compression);

    /// <summary>Transcodes a UTF-16 stream to UTF-8 (BOM skipped and, when present, trusted over <paramref name="encoding"/>).</summary>
    public static Stream Transcode(Stream decompressed, InputEncoding encoding)
    {
        ArgumentNullException.ThrowIfNull(decompressed);
        return encoding switch
        {
            InputEncoding.Utf8 => decompressed,
            InputEncoding.Utf16LittleEndian => new Utf16ToUtf8Stream(decompressed, bigEndian: false),
            InputEncoding.Utf16BigEndian => new Utf16ToUtf8Stream(decompressed, bigEndian: true),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, "unknown encoding"),
        };
    }

    /// <summary>Decompression then transcoding: the stream the <see cref="ChunkReader"/> consumes.</summary>
    public static Stream OpenDecoded(Stream raw, CompressionKind compression, InputEncoding encoding) =>
        Transcode(Decompress(raw, compression), encoding);
}

/// <summary>
/// Read-only UTF-16 to UTF-8 transcoder. Invalid sequences become U+FFFD; a surrogate pair split across reads is
/// kept until its second half arrives. Internal buffers are two fixed arrays allocated once per stream.
/// </summary>
internal sealed class Utf16ToUtf8Stream : Stream
{
    private const int RawBytes = 64 << 10;

    private readonly Stream _inner;
    private readonly byte[] _raw = new byte[RawBytes];
    private readonly char[] _chars = new char[RawBytes / 2];
    private bool _bigEndian;
    private int _rawLength;
    private int _charStart;
    private int _charEnd;
    private bool _innerEof;
    private bool _started;

    public Utf16ToUtf8Stream(Stream inner, bool bigEndian)
    {
        _inner = inner;
        _bigEndian = bigEndian;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length < 4)
        {
            throw new ArgumentException("destination must hold at least one UTF-8 scalar (4 bytes)", nameof(buffer));
        }

        while (true)
        {
            if (_charStart < _charEnd)
            {
                var final = _innerEof && _rawLength == 0;
                var status = Utf8.FromUtf16(_chars.AsSpan(_charStart, _charEnd - _charStart), buffer, out var read, out var written, replaceInvalidSequences: true, isFinalBlock: final);
                _charStart += read;
                if (written > 0)
                {
                    return written;
                }

                if (status == OperationStatus.DestinationTooSmall)
                {
                    throw new InvalidOperationException("transcoder made no progress into a 4+ byte buffer");
                }
            }

            if (_innerEof)
            {
                if (_rawLength == 1 && _charStart == _charEnd)
                {
                    // A dangling odd byte at the very end: one replacement character.
                    _rawLength = 0;
                    _chars[0] = '�';
                    _charStart = 0;
                    _charEnd = 1;
                    continue;
                }

                return 0;
            }

            Fill();
        }
    }

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
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Fill()
    {
        // Keep the unconsumed chars (at most one high surrogate) at the front.
        var pending = _charEnd - _charStart;
        if (pending > 0 && _charStart > 0)
        {
            Array.Copy(_chars, _charStart, _chars, 0, pending);
        }

        _charStart = 0;
        _charEnd = pending;

        var n = _inner.Read(_raw, _rawLength, _raw.Length - _rawLength);
        if (n == 0)
        {
            _innerEof = true;
            return;
        }

        _rawLength += n;
        var start = 0;
        if (!_started)
        {
            if (_rawLength < 2)
            {
                return; // need both BOM bytes (or EOF) before deciding; the caller fills again
            }

            _started = true;
            if (_raw[0] == 0xFF && _raw[1] == 0xFE)
            {
                _bigEndian = false;
                start = 2;
            }
            else if (_raw[0] == 0xFE && _raw[1] == 0xFF)
            {
                _bigEndian = true;
                start = 2;
            }
        }

        var units = (_rawLength - start) / 2;
        var room = _chars.Length - _charEnd;
        units = Math.Min(units, room);
        var source = _raw.AsSpan(start, units * 2);
        var dest = _chars.AsSpan(_charEnd, units);
        if (_bigEndian == !BitConverter.IsLittleEndian)
        {
            source.CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(dest));
        }
        else
        {
            var asUnits = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(source);
            BinaryPrimitives.ReverseEndianness(asUnits, System.Runtime.InteropServices.MemoryMarshal.Cast<char, ushort>(dest));
        }

        _charEnd += units;
        var consumed = start + (units * 2);
        var leftover = _rawLength - consumed;
        if (leftover > 0)
        {
            Array.Copy(_raw, consumed, _raw, 0, leftover);
        }

        _rawLength = leftover;
    }
}
