using System.Buffers.Binary;
using System.IO.Compression;

namespace Urlog.Core.Pipeline;

/// <summary>
/// Reads a gzip file through the runtime's <c>GZipStream</c> and finds out whether the stored bytes ended on a
/// member boundary. The runtime's stream cannot be asked: when its input runs out it returns end of stream,
/// whether the last member was finished or cut anywhere, its eight trailer bytes included.
/// <para>
/// So the input does not simply run out. When the stored bytes end, the decoder is given one more gzip member,
/// a sentinel whose content is a fixed random constant. A decoder that had finished the last real member reads
/// the sentinel as the next member and hands out exactly its content, which is taken away again: the file is
/// whole. A decoder that was still inside a member (its header, its data or its trailer) reads the sentinel's
/// bytes as the rest of that member, and what follows is an error, other bytes, or nothing: the file is cut
/// short, and whatever came out after the sentinel went in is dropped.
/// </para>
/// <para>
/// This rests on one behaviour of the runtime: <c>DeflateStream.Read</c> asks the stream under it for more input
/// only when the current read has produced nothing. Everything handed out before the sentinel is therefore from
/// stored bytes alone. The tests that cut files at every byte fail if a runtime changes this.
/// </para>
/// <para>
/// Each real member's CRC-32 and length are checked by the decoder itself, and a mismatch is an error. A cut
/// exactly between two members leaves a valid shorter file and cannot be seen.
/// </para>
/// <para>
/// Bytes after the last member that start no member (padding, anything else) are not content and not a
/// truncation. The decoder stops at them without a word, so they are counted here. The decoder does not say
/// where it stopped either, so the last <see cref="Window"/> stored bytes are handed to it one at a time: it asks
/// for the next byte only when it has used the one before, and the byte after which it stops asking is the first
/// trailing one. The count is marked inexact, and is then a lower bound, in two cases: trailing bytes that reach
/// back further than the window are met in the middle of a larger block (short by less than one block), and
/// trailing bytes that begin with 0x1F behind a file of several members cannot be told from a last member whose
/// length ends in 0x1F (short by at most one byte).
/// </para>
/// </summary>
internal sealed class GzipReader : IDisposable
{
    /// <summary>Stored bytes at the end of the file that are fed singly. Trailing bytes up to this many are counted exactly.</summary>
    public const int Window = 80 << 10;

    private const int BlockBytes = 64 << 10;

    // 32 bytes from a random generator, fixed here: content no log line and no decoder accident produces.
    private static readonly byte[] SentinelContent =
    [
        0x9E, 0x4B, 0xD1, 0x07, 0x6A, 0xF3, 0x2C, 0xB8, 0x55, 0xE0, 0x13, 0xC7, 0x8D, 0x31, 0xFA, 0x46,
        0x02, 0xBF, 0x79, 0xD4, 0x1E, 0x63, 0xA5, 0x90, 0xCC, 0x38, 0x5D, 0xE7, 0x24, 0x8A, 0xF1, 0x6B,
    ];

    private static readonly byte[] SentinelMember = Compress(SentinelContent);

    private readonly RawMeter _raw;
    private readonly Feeder _feeder;
    private readonly GZipStream _gzip;
    private long _decoded;
    private bool _done;

    public GzipReader(RawMeter raw)
    {
        _raw = raw;
        _feeder = new Feeder(raw);
        _gzip = new GZipStream(_feeder, CompressionMode.Decompress, leaveOpen: true);
    }

    /// <summary>True once the stored bytes ended inside a member.</summary>
    public bool Truncated { get; private set; }

    /// <summary>Stored bytes behind the last member that start no member.</summary>
    public long TrailingBytes { get; private set; }

    /// <summary>False when <see cref="TrailingBytes"/> is a lower bound; see the class comment.</summary>
    public bool TrailingBytesExact { get; private set; } = true;

    public int Read(Span<byte> buffer)
    {
        if (_done)
        {
            return 0;
        }

        int n;
        try
        {
            n = _gzip.Read(buffer);
        }
        catch (InvalidDataException) when (_feeder.SentinelGiven)
        {
            // The sentinel's bytes did not fit where the stored bytes stopped.
            Truncated = true;
            _done = true;
            return 0;
        }

        if (_feeder.SentinelGiven)
        {
            Truncated = !RestIsTheSentinel(buffer[..n]);
            _done = true;
            return 0;
        }

        if (n == 0)
        {
            // No bytes, and the decoder did not ask for the end: it stopped at bytes that start no member.
            CountTrailingBytes();
            _done = true;
            return 0;
        }

        _decoded += n;
        return n;
    }

    public void Dispose()
    {
        _gzip.Dispose();
        _feeder.Dispose();
    }

    private void CountTrailingBytes()
    {
        var unfed = _feeder.Unfed + Drain();
        if (!_feeder.LastWasSingle)
        {
            // Stopped inside a block: its unused end is trailing too, and how much of it is not known.
            TrailingBytes = unfed;
            TrailingBytesExact = false;
            return;
        }

        // The decoder asked for the last chunk because it had used everything before it, and stopped on seeing
        // it: the chunk is the first of the trailing bytes.
        if (_feeder.LastChunk == 1)
        {
            TrailingBytes = 1 + unfed;
            return;
        }

        // A chunk of two is 0x1F with the byte after it (see the feeder). The 0x1F is either the first trailing
        // byte, or the last byte of the trailer with only the second byte trailing. The trailer ends with the
        // length of the last member, and for a file of one member that is everything decoded: whichever of the
        // two places holds that length is the trailer's. With several members the last one's length is not
        // known here, neither place can be proved, and the count is the lower of the two and marked inexact.
        var all = (uint)_decoded;
        var trailerEndsBeforeChunk = _feeder.LengthFieldEndingAt(0) == all;
        var trailerEndsInsideChunk = _feeder.LengthFieldEndingAt(1) == all;
        if (trailerEndsBeforeChunk != trailerEndsInsideChunk)
        {
            TrailingBytes = (trailerEndsBeforeChunk ? 2 : 1) + unfed;
            return;
        }

        TrailingBytes = 1 + unfed;
        TrailingBytesExact = false;
    }

    private long Drain()
    {
        var scratch = new byte[BlockBytes];
        long total = 0;
        int n;
        while ((n = _raw.Read(scratch)) > 0)
        {
            total += n;
        }

        return total;
    }

    /// <summary>True when everything the decoder produces from here on is the sentinel's content and nothing else.</summary>
    private bool RestIsTheSentinel(ReadOnlySpan<byte> first)
    {
        var seen = new byte[SentinelContent.Length + 1];
        var scratch = new byte[64];
        var length = 0;
        var chunk = first;
        while (true)
        {
            if (chunk.Length > seen.Length - length)
            {
                return false;
            }

            chunk.CopyTo(seen.AsSpan(length));
            length += chunk.Length;

            int n;
            try
            {
                n = _gzip.Read(scratch, 0, scratch.Length);
            }
            catch (InvalidDataException)
            {
                return false;
            }

            if (n == 0)
            {
                return seen.AsSpan(0, length).SequenceEqual(SentinelContent);
            }

            chunk = scratch.AsSpan(0, n);
        }
    }

    private static byte[] Compress(byte[] content)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.NoCompression, leaveOpen: true))
        {
            gzip.Write(content);
        }

        return output.ToArray();
    }

    /// <summary>
    /// What the decoder reads from: the stored bytes in blocks, their last <see cref="Window"/> bytes one at a
    /// time, then the sentinel member once, then the end.
    /// </summary>
    private sealed class Feeder(RawMeter raw) : Stream
    {
        private readonly byte[] _look = new byte[Window + (2 * BlockBytes)];
        private int _start;
        private int _end;
        private bool _rawEnded;
        private int _sentinelOffset = -1;

        public bool SentinelGiven => _sentinelOffset >= 0;

        /// <summary>Stored bytes read from the file and not handed to the decoder.</summary>
        public int Unfed => _end - _start;

        /// <summary>Size of the last chunk of stored bytes handed to the decoder.</summary>
        public int LastChunk { get; private set; }

        /// <summary>True when the last chunk came from the window at the end of the file.</summary>
        public bool LastWasSingle { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        /// <summary>
        /// The four stored bytes that end <paramref name="into"/> bytes into the last chunk (0: just before it),
        /// read as a little-endian length; null when the file has fewer bytes than that.
        /// </summary>
        public uint? LengthFieldEndingAt(int into)
        {
            var end = _start - LastChunk + into;
            return end >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(_look.AsSpan(end - 4, 4)) : null;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty)
            {
                return 0;
            }

            if (!SentinelGiven)
            {
                Fill();
                var available = _end - _start;
                int give;
                if (available > Window)
                {
                    give = Math.Min(buffer.Length, available - Window);
                    LastWasSingle = false;
                }
                else if (available > 0)
                {
                    // A lone 0x1F behind a finished member is taken for the start of another member, and the
                    // byte after it is then an error. Handing the two over together lets the decoder see that
                    // no member starts there.
                    give = _look[_start] == 0x1F && available > 1 && buffer.Length > 1 ? 2 : 1;
                    LastWasSingle = true;
                }
                else
                {
                    _sentinelOffset = 0;
                    give = 0;
                }

                if (give > 0)
                {
                    _look.AsSpan(_start, give).CopyTo(buffer);
                    _start += give;
                    LastChunk = give;
                    return give;
                }
            }

            var rest = Math.Min(buffer.Length, SentinelMember.Length - _sentinelOffset);
            SentinelMember.AsSpan(_sentinelOffset, rest).CopyTo(buffer);
            _sentinelOffset += rest;
            return rest;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <summary>Reads ahead until more than the window is in hand or the file has ended, so the window is known to be the file's end.</summary>
        private void Fill()
        {
            while (!_rawEnded && _end - _start <= Window)
            {
                if (_look.Length - _end < BlockBytes)
                {
                    // Keep a few bytes before the start: the length-field check looks back three.
                    var keep = Math.Min(_start, 8);
                    _look.AsSpan(_start - keep, _end - _start + keep).CopyTo(_look);
                    _end -= _start - keep;
                    _start = keep;
                }

                var n = raw.Read(_look.AsSpan(_end, Math.Min(BlockBytes, _look.Length - _end)));
                if (n == 0)
                {
                    _rawEnded = true;
                }
                else
                {
                    _end += n;
                }
            }
        }
    }
}
