namespace Urlog.Core.Pipeline;

/// <summary>
/// Live counters of one pipeline run, for the job runner's progress writes. Updated once per chunk by the reader and
/// the parsers, so the cost is a handful of interlocked adds per 64 MiB. Advisory: nothing resumes from them.
/// </summary>
public sealed class PipelineProgress
{
    private long _chunksRead;
    private long _bytesRead;
    private long _chunksParsed;
    private long _rows;
    private long _rejects;

    /// <summary>Chunks the reader has cut from the decoded stream.</summary>
    public long ChunksRead => Interlocked.Read(ref _chunksRead);

    /// <summary>Decoded bytes in those chunks.</summary>
    public long BytesRead => Interlocked.Read(ref _bytesRead);

    public long ChunksParsed => Interlocked.Read(ref _chunksParsed);

    /// <summary>Rows committed by the parsers (rejects included: a reject is a row).</summary>
    public long Rows => Interlocked.Read(ref _rows);

    public long Rejects => Interlocked.Read(ref _rejects);

    internal void ChunkRead(int bytes)
    {
        Interlocked.Increment(ref _chunksRead);
        Interlocked.Add(ref _bytesRead, bytes);
    }

    internal void ChunkParsed(long rows, long rejects)
    {
        Interlocked.Increment(ref _chunksParsed);
        Interlocked.Add(ref _rows, rows);
        Interlocked.Add(ref _rejects, rejects);
    }
}
