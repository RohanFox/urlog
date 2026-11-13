namespace Urlog.Core.Intake.Archives;

/// <summary>
/// The decoded content of one compressed file, held to the expansion ratio and the total-size cap: a single
/// compressed log can be a bomb as well as an archive can. The check is on bytes that came out, never on a size
/// the file states about itself.
/// </summary>
public sealed class LimitedDecodeStream(Stream inner, ArchiveLimits limits, long storedBytes) : Stream
{
    private long _decoded;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => _decoded; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var n = inner.Read(buffer);
        _decoded += n;
        limits.Check(_decoded, 0, storedBytes);
        return n;
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
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
