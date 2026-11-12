using System.Buffers;

namespace Urlog.Core.Pipeline;

/// <summary>
/// A framed chunk: a pooled buffer whose first <see cref="Length"/> bytes hold whole records (unless
/// <see cref="ForcedSplit"/>), plus where it sits in the decoded file. The consumer owns it from the moment the
/// reader yields it and must call <see cref="Return"/> exactly once.
/// </summary>
public sealed class Chunk
{
    private readonly ChunkPool _pool;
    private byte[]? _buffer;

    internal Chunk(ChunkPool pool, byte[] buffer, int length, long fileOffset, int index, bool forcedSplit, bool continuesSplit)
    {
        _pool = pool;
        _buffer = buffer;
        Length = length;
        FileOffset = fileOffset;
        Index = index;
        ForcedSplit = forcedSplit;
        ContinuesSplit = continuesSplit;
    }

    /// <summary>Bytes in this chunk.</summary>
    public int Length { get; }

    /// <summary>Offset of the first byte in the decoded (decompressed, UTF-8) stream.</summary>
    public long FileOffset { get; }

    /// <summary>Zero-based position of the chunk in the file.</summary>
    public int Index { get; }

    /// <summary>The chunk ends inside a record that exceeded the size limit; the rest is at the start of the next chunk.</summary>
    public bool ForcedSplit { get; }

    /// <summary>The chunk starts inside a record cut from the previous chunk.</summary>
    public bool ContinuesSplit { get; }

    public ReadOnlyMemory<byte> Memory => Buffer.AsMemory(0, Length);

    public ReadOnlySpan<byte> Span => Buffer.AsSpan(0, Length);

    /// <summary>Single-segment view handed to <see cref="Urlog.Core.Sources.ILogSource.Parse"/>.</summary>
    public ReadOnlySequence<byte> Sequence => new(Memory);

    private byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(Chunk), "chunk already returned to the pool");

    /// <summary>Gives the buffer back to the pool. Idempotent.</summary>
    public void Return()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            _pool.Return(buffer);
        }
    }
}
