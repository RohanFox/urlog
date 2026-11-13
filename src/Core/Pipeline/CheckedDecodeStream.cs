using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.Xz;

namespace Urlog.Core.Pipeline;

/// <summary>
/// The decompressed content of one stored file, with a verdict on how the stored bytes ended
/// (<see cref="End"/>). The decoders cannot be asked: at a file cut short one returns end of stream as if it were
/// whole, and the others throw the same exceptions they throw for damage. So each format gets its own check of
/// its end structure, and the rule is the same for all four:
/// <list type="bullet">
/// <item>the end structure was read and agrees: <see cref="InputEndKind.Complete"/>;</item>
/// <item>the stored bytes are at their end and the structure is missing: <see cref="InputEndKind.Truncated"/>,
/// reported as an ordinary end of stream so everything decoded so far is parsed;</item>
/// <item>anything else is damage: the decoder's exception goes on and fails the job.</item>
/// </list>
/// gzip: whether the decoder had finished its last member (<see cref="GzipReader"/>). zstd: the decoder's frame state
/// (<see cref="ZstdFrameReader"/>). xz: a valid stream footer in the last twelve stored bytes. bzip2: the
/// end-of-stream marker near the end of the stored bytes. A cut exactly between two members, frames or
/// concatenated streams leaves a valid shorter file and is not detectable by anyone.
/// <para>
/// Concatenated xz and bzip2 streams are all read, one library stream per stored stream. Stored bytes behind
/// the last member, frame or stream that start no further one are trailing bytes: the input is complete, and
/// <see cref="InputEnd.TrailingBytes"/> says how many there are. Disposing this stream disposes the stored-bytes
/// stream under it; no decoder closes it earlier.
/// </para>
/// <para>
/// xz content is read from the library 16 KB at a time, because what the library decoded in the call that meets
/// the cut is lost with its exception: the content of a cut xz file ends up to 16 KB before the cut.
/// </para>
/// <para>
/// bzip2 content is handed out one block late. The library decodes a block that is cut short as if the missing
/// bits were ones, hands out the result and only then finds the block's CRC wrong, so its newest bytes cannot be
/// trusted. It reads stored bytes only while it loads a block, so a read that moved the stored position proves
/// the block before it passed its CRC; what was decoded before that read is released, the rest waits. At a cut
/// the waiting bytes are dropped: the content ends at the last block that was proved, less at most one read
/// (<see cref="Bzip2ReadBytes"/>) that held the end of that block and the start of the next.
/// </para>
/// </summary>
public sealed class CheckedDecodeStream : Stream
{
    private const int Bzip2ReadBytes = 4096;

    private const int XzReadBytes = 16 << 10;

    private static readonly byte[] XzMagic = [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00];

    private readonly Stream _stored;
    private readonly RawMeter _raw;
    private readonly CompressionKind _compression;
    private GzipReader? _gzip;
    private ZstdFrameReader? _zstd;
    private Stream? _library;
    private long _decoded;
    private bool _ended;
    private bool _libraryTruncated;
    private long _readTicks;
    private long _trailing;
    private bool _streamEnded;
    private byte[] _held = [];
    private int _heldServed;
    private int _heldProved;
    private int _heldLength;
    private bool _heldEnded;
    private byte[] _xz = [];
    private int _xzStart;
    private int _xzEnd;

    public CheckedDecodeStream(Stream stored, CompressionKind compression)
    {
        ArgumentNullException.ThrowIfNull(stored);
        if (!Enum.IsDefined(compression))
        {
            throw new ArgumentOutOfRangeException(nameof(compression), compression, "unknown compression");
        }

        _stored = stored;
        _raw = new RawMeter(stored);
        _compression = compression;
    }

    /// <summary>How the input ended; <see cref="InputEndKind.Unknown"/> until a read has returned 0.</summary>
    public InputEnd End { get; private set; }

    /// <summary>Decompressed bytes handed out so far.</summary>
    public long DecodedBytes => _decoded;

    /// <summary>Stored bytes the decoder has taken so far.</summary>
    public long StoredBytes => _raw.Bytes;

    /// <summary>
    /// Time spent inside this stream's reads so far: decoding, and under it reading the stored bytes and whatever
    /// the stored stream does with them (the job runner hashes them there). Two clock readings per read.
    /// </summary>
    public TimeSpan ReadTime => System.Diagnostics.Stopwatch.GetElapsedTime(0, _readTicks);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => _decoded; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_ended || buffer.IsEmpty)
        {
            return 0;
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var n = _compression switch
        {
            CompressionKind.None => _raw.Read(buffer),
            CompressionKind.Gzip => (_gzip ??= new GzipReader(_raw)).Read(buffer),
            CompressionKind.Zstd => (_zstd ??= new ZstdFrameReader(_raw)).Read(buffer),
            CompressionKind.Bzip2 => ReadProvedBzip2(buffer),
            _ => ReadXz(buffer),
        };
        _readTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
        if (n > 0)
        {
            _decoded += n;
            return n;
        }

        _ended = true;
        var truncated = _compression switch
        {
            CompressionKind.Gzip => _gzip!.Truncated,
            CompressionKind.Zstd => _zstd!.Truncated,
            CompressionKind.None => false,
            _ => _libraryTruncated,
        };
        var trailing = _compression switch
        {
            CompressionKind.Gzip => _gzip!.TrailingBytes,
            CompressionKind.Zstd => _zstd!.TrailingBytes,
            _ => _trailing,
        };
        End = truncated
            ? InputEnd.Truncated(_decoded, _raw.Bytes)
            : InputEnd.Complete(_decoded, _raw.Bytes - trailing, trailing, _gzip?.TrailingBytesExact ?? true);
        return 0;
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
            _gzip?.Dispose();
            _zstd?.Dispose();
            DisposeLibrary();
            _stored.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// xz in reads of a fixed size. When the library meets the cut it throws, and whatever it had decoded in that
    /// call is gone with the exception. Reading through a buffer of our own bounds that loss to one read and makes
    /// it the same whatever buffer the caller brings: the same stored bytes give the same content.
    /// </summary>
    private int ReadXz(Span<byte> buffer)
    {
        if (_xzStart == _xzEnd)
        {
            if (_xz.Length == 0)
            {
                _xz = new byte[XzReadBytes];
            }

            _xzStart = 0;
            _xzEnd = ReadLibrary(_xz);
            if (_xzEnd == 0)
            {
                return 0;
            }
        }

        var n = Math.Min(buffer.Length, _xzEnd - _xzStart);
        _xz.AsSpan(_xzStart, n).CopyTo(buffer);
        _xzStart += n;
        return n;
    }

    /// <summary>bzip2 one block late; see the class comment.</summary>
    private int ReadProvedBzip2(Span<byte> buffer)
    {
        while (true)
        {
            if (_heldServed < _heldProved)
            {
                var n = Math.Min(buffer.Length, _heldProved - _heldServed);
                _held.AsSpan(_heldServed, n).CopyTo(buffer);
                _heldServed += n;
                return n;
            }

            if (_heldEnded)
            {
                return 0;
            }

            // Everything proved is handed out: move what waits to the front and make room for one more read.
            var waiting = _heldLength - _heldServed;
            if (_heldServed > 0)
            {
                _held.AsSpan(_heldServed, waiting).CopyTo(_held);
                _heldServed = 0;
                _heldProved = 0;
                _heldLength = waiting;
            }

            if (_held.Length - _heldLength < Bzip2ReadBytes)
            {
                Array.Resize(ref _held, Math.Max(_held.Length * 2, _heldLength + (256 << 10)));
            }

            var storedBefore = _raw.Bytes;
            _streamEnded = false;
            var read = ReadLibrary(_held.AsSpan(_heldLength, Bzip2ReadBytes));
            if (_streamEnded || _raw.Bytes != storedBefore)
            {
                // A stream that ended with its marker, or a new block being loaded, proves what was decoded before.
                _heldProved = _heldLength;
            }

            if (read == 0)
            {
                // What is still unproved here belongs to a block that was cut: it is dropped.
                _heldEnded = true;
                continue;
            }

            _heldLength += read;
        }
    }

    /// <summary>xz and bzip2 through the library's streams; their exceptions are sorted into "cut short" and "damaged" here.</summary>
    private int ReadLibrary(Span<byte> buffer)
    {
        while (true)
        {
            int n;
            try
            {
                // Both libraries read their header when the stream is constructed.
                _library ??= _compression == CompressionKind.Xz
                    ? new XZStream(_raw)
                    : BZip2Stream.Create(_raw, SharpCompress.Compressors.CompressionMode.Decompress, decompressConcatenated: false);
                n = _library.Read(buffer);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // The libraries throw unrelated types for a short file and for a damaged one, sometimes the same
                // type for both, so the exception decides nothing: the stored bytes do.
                if (_raw.AtEnd() && !EndStructurePresent())
                {
                    _libraryTruncated = true;
                    return 0;
                }

                throw;
            }

            if (n > 0)
            {
                return n;
            }

            if (!EndStructurePresent())
            {
                if (_raw.AtEnd())
                {
                    _libraryTruncated = true;
                    return 0;
                }

                throw new InvalidDataException($"the {_compression} stream ended without its end structure and more bytes follow");
            }

            _streamEnded = true;
            if (!NextStream())
            {
                return 0;
            }
        }
    }

    /// <summary>
    /// After one xz or bzip2 stream: true when another stream starts. Otherwise the input is at its end, or holds
    /// the first bytes of a stream and nothing more (truncated), or holds bytes that start no stream, which are
    /// counted as trailing. xz allows zero padding between streams, so zeros only count when no stream follows.
    /// </summary>
    private bool NextStream()
    {
        DisposeLibrary();
        ReadOnlySpan<byte> magic = _compression == CompressionKind.Xz ? XzMagic : "BZh"u8;
        long zeros = 0;
        var b = _raw.ReadByte();
        while (b == 0 && _compression == CompressionKind.Xz)
        {
            zeros++;
            b = _raw.ReadByte();
        }

        if (b < 0)
        {
            _trailing = zeros;
            return false;
        }

        // bzip2: "BZh" and a block-size digit.
        Span<byte> head = stackalloc byte[XzMagic.Length];
        head = head[..(_compression == CompressionKind.Xz ? XzMagic.Length : 4)];
        head[0] = (byte)b;
        var got = 1 + _raw.ReadFully(head[1..]);
        var matches = head[..Math.Min(got, magic.Length)].SequenceEqual(magic[..Math.Min(got, magic.Length)])
            && (_compression == CompressionKind.Xz || got < 4 || head[3] is >= (byte)'1' and <= (byte)'9');
        if (matches && got == head.Length)
        {
            _raw.Unread(head);
            return true;
        }

        if (matches)
        {
            // The first bytes of another stream and nothing after them.
            _libraryTruncated = true;
            return false;
        }

        _trailing = zeros + got;
        Span<byte> scratch = stackalloc byte[4096];
        int n;
        while ((n = _raw.Read(scratch)) > 0)
        {
            _trailing += n;
        }

        return false;
    }

    private bool EndStructurePresent()
    {
        Span<byte> tail = stackalloc byte[RawMeter.TailBytes];
        tail = tail[.._raw.Tail(tail)];
        return _compression == CompressionKind.Xz ? XzFooterAtEnd(tail) : Bzip2EndMarkerNearEnd(tail);
    }

    /// <summary>The xz stream footer: CRC-32 of the six bytes after it, backward size, stream flags, <c>YZ</c>.</summary>
    private static bool XzFooterAtEnd(ReadOnlySpan<byte> tail)
    {
        // A damaged or padded file can end in zero bytes after the footer; padding comes in fours.
        while (tail.Length >= 16 && tail[^4..].IndexOfAnyExcept((byte)0) < 0)
        {
            tail = tail[..^4];
        }

        if (tail.Length < 12)
        {
            return false;
        }

        var footer = tail[^12..];
        return footer[10] == (byte)'Y' && footer[11] == (byte)'Z'
            && Crc32(footer.Slice(4, 6)) == System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(footer);
    }

    /// <summary>CRC-32 (IEEE) bit by bit: it runs over six bytes once per file.</summary>
    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return ~crc;
    }

    /// <summary>
    /// The bzip2 end-of-stream marker (the 48 bits 0x177245385090) with the 32 bits of the stream CRC behind it.
    /// bzip2 is a bit stream, so the marker sits at any bit offset; it is looked for in the last stored bytes.
    /// </summary>
    private static bool Bzip2EndMarkerNearEnd(ReadOnlySpan<byte> tail)
    {
        const ulong Marker = 0x177245385090;
        const ulong Mask = (1UL << 48) - 1;
        ulong window = 0;
        var bits = 0;
        var total = tail.Length * 8;
        foreach (var b in tail)
        {
            for (var bit = 7; bit >= 0; bit--)
            {
                window = (window << 1) | (uint)((b >> bit) & 1);
                if (++bits >= 48 && (window & Mask) == Marker && total - bits >= 32)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void DisposeLibrary()
    {
        try
        {
            _library?.Dispose();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A decoder that failed mid-stream can fail again while closing; the first failure is the one reported.
        }

        _library = null;
    }
}
