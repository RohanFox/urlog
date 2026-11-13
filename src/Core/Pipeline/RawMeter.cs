namespace Urlog.Core.Pipeline;

/// <summary>
/// The stored bytes as a decoder sees them: counted, with the last bytes handed out kept for the end-structure
/// checks, and with a push-back so a reader that took more than its share can return the rest. Disposing it does
/// nothing: one decoder closes the stream under it when it reaches the end, and the owner still has to read the
/// rest of the file through the hash.
/// </summary>
internal sealed class RawMeter(Stream inner) : Stream
{
    public const int TailBytes = 64;

    private readonly byte[] _tail = new byte[TailBytes];
    private byte[] _pushed = [];
    private int _pushedStart;
    private int _pushedEnd;
    private long _tailTotal;

    /// <summary>Bytes handed out and not pushed back.</summary>
    public long Bytes { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => Bytes; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        int n;
        if (_pushedStart < _pushedEnd)
        {
            n = Math.Min(buffer.Length, _pushedEnd - _pushedStart);
            _pushed.AsSpan(_pushedStart, n).CopyTo(buffer);
            _pushedStart += n;
        }
        else
        {
            n = inner.Read(buffer);
        }

        Bytes += n;
        Remember(buffer[..n]);
        return n;
    }

    public override int ReadByte()
    {
        Span<byte> one = stackalloc byte[1];
        return Read(one) == 0 ? -1 : one[0];
    }

    /// <summary>Reads until <paramref name="buffer"/> is full or the input ends; returns the bytes read.</summary>
    public int ReadFully(Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = Read(buffer[total..]);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    /// <summary>Returns bytes that were read and not used; the next read hands them out again.</summary>
    public void Unread(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        var kept = _pushedEnd - _pushedStart;
        var merged = new byte[bytes.Length + kept];
        bytes.CopyTo(merged);
        _pushed.AsSpan(_pushedStart, kept).CopyTo(merged.AsSpan(bytes.Length));
        _pushed = merged;
        _pushedStart = 0;
        _pushedEnd = merged.Length;
        Bytes -= bytes.Length;
    }

    /// <summary>True when no byte is left. A byte found is pushed back.</summary>
    public bool AtEnd()
    {
        Span<byte> one = stackalloc byte[1];
        if (Read(one) == 0)
        {
            return true;
        }

        Unread(one);
        return false;
    }

    /// <summary>The last bytes handed out, oldest first, at most <paramref name="destination"/>'s length; returns how many.</summary>
    public int Tail(Span<byte> destination)
    {
        var have = (int)Math.Min(Math.Min(_tailTotal, TailBytes), destination.Length);
        for (var i = 0; i < have; i++)
        {
            destination[i] = _tail[(int)((_tailTotal - have + i) % TailBytes)];
        }

        return have;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // Deliberately not forwarded; see the class comment.
        base.Dispose(disposing);
    }

    private void Remember(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > TailBytes)
        {
            _tailTotal += bytes.Length - TailBytes;
            bytes = bytes[^TailBytes..];
        }

        foreach (var b in bytes)
        {
            _tail[(int)(_tailTotal++ % TailBytes)] = b;
        }
    }
}
