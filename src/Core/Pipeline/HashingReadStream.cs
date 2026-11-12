using System.Diagnostics;
using System.Security.Cryptography;

namespace Urlog.Core.Pipeline;

/// <summary>
/// A read-only view of the evidence bytes that feeds every byte it hands out to SHA-256. It sits between the file
/// and <see cref="InputDecoding.OpenDecoded"/>, so the digest is over the raw bytes, before decompression or
/// transcoding, and the verify-before-parse hash shares the parse's one read of the file.
/// <para>
/// A decoder may stop before the end of its input (a gzip trailer, padding), so the caller ends with
/// <see cref="DrainAndFinish"/>, which reads whatever is left through the hash. <see cref="HashTime"/> is the time
/// spent inside the hash alone, reported per job.
/// </para>
/// </summary>
public sealed class HashingReadStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _hashTicks;
    private long _bytes;
    private bool _finished;

    public HashingReadStream(Stream inner, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!inner.CanRead)
        {
            throw new ArgumentException("the stream must be readable", nameof(inner));
        }

        _inner = inner;
        _leaveOpen = leaveOpen;
    }

    /// <summary>Raw bytes read and hashed so far.</summary>
    public long BytesHashed => Interlocked.Read(ref _bytes);

    /// <summary>Time spent in SHA-256 so far.</summary>
    public TimeSpan HashTime => Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _hashTicks));

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => BytesHashed;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var n = _inner.Read(buffer);
        Append(buffer[..n]);
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Append(buffer.Span[..n]);
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <summary>
    /// Reads the rest of the file through the hash and returns the digest as 64 lowercase hex characters, the form
    /// the intake record stores. One call; the stream reads nothing afterwards.
    /// </summary>
    public string DrainAndFinish(Span<byte> scratch)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        if (scratch.IsEmpty)
        {
            throw new ArgumentException("the scratch buffer is empty", nameof(scratch));
        }

        while (Read(scratch) > 0)
        {
        }

        _finished = true;
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        _hash.GetHashAndReset(digest);
        return Convert.ToHexStringLower(digest);
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
            _hash.Dispose();
            if (!_leaveOpen)
            {
                _inner.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private void Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        ObjectDisposedException.ThrowIf(_finished, this);
        var start = Stopwatch.GetTimestamp();
        _hash.AppendData(bytes);
        Interlocked.Add(ref _hashTicks, Stopwatch.GetTimestamp() - start);
        Interlocked.Add(ref _bytes, bytes.Length);
    }
}
