namespace Urlog.Core.Pipeline;

/// <summary>
/// Sizing of the parse pipeline. Every buffer comes from pools the pipeline owns (<see cref="PipelineBuffers"/>), so
/// the memory footprint is fixed by these numbers and does not depend on what <c>ArrayPool.Shared</c> decides to trim.
/// <para>
/// <b>Memory formula</b>: <c>(InFlightChunks + 1) x ChunkBytes</c> for chunk buffers (the reader holds two at hand-off, see
/// <see cref="ChunkPool"/>), plus
/// <c>(Parallelism + InFlightBatches) x BatchBytes</c> for event batches, where <c>BatchBytes is about BatchRows x (234 + mean record bytes)</c>
/// (fixed columns and string offsets are ~234 B/row; <c>raw</c> holds the record once, the message is a slice of it).
/// At the defaults on the 8-core reference box with nginx-shaped records (223 B):
/// 11 x 64 MiB = 704 MiB of chunks plus 12 x ~46 MB = ~550 MB of batches, about 1.25 GB.
/// <see cref="SmallBox"/> (8 GB machine): 5 x 16 MiB + 4 x ~23 MB = about 180 MB.
/// </para>
/// </summary>
public sealed record PipelineOptions
{
    /// <summary>Chunk size; records are never split across chunks. Use a power of two (the pool buckets by powers of two).</summary>
    public int ChunkBytes { get; init; } = 64 << 20;

    /// <summary>
    /// A record longer than the chunk grows the chunk buffer up to this size; one longer than
    /// <c>max(ChunkBytes, MaxRecordBytes)</c> is force-split and both halves are flagged for the parser.
    /// </summary>
    public int MaxRecordBytes { get; init; } = 16 << 20;

    /// <summary>Rows per batch handed to the insert path (design record: flush at 100k rows).</summary>
    public int BatchRows { get; init; } = 100_000;

    /// <summary>Parser threads. A <see cref="Urlog.Core.Sources.SourceInfo.Stateful"/> source always runs on one.</summary>
    public int Parallelism { get; init; } = Environment.ProcessorCount;

    /// <summary>
    /// Chunk buffers the pool holds: one being filled by the reader, one per parser, the rest queued between them.
    /// Must be at least <c>Parallelism + 2</c>.
    /// </summary>
    public int InFlightChunks { get; init; } = Environment.ProcessorCount + 2;

    /// <summary>ClickHouse writers. Informational for the pipeline; <see cref="InFlightBatches"/> defaults to one per writer.</summary>
    public int Writers { get; init; } = 4;

    /// <summary>Batches that may sit between the parsers and the sink (queued or being inserted). The batch pool holds <c>Parallelism + InFlightBatches</c>.</summary>
    public int InFlightBatches { get; init; } = 4;

    public static PipelineOptions Default { get; } = new();

    /// <summary>Minimum viable settings for an 8 GB machine: about 180 MB of pooled buffers.</summary>
    public static PipelineOptions SmallBox { get; } = new()
    {
        ChunkBytes = 16 << 20,
        Parallelism = 2,
        InFlightChunks = 4,
        InFlightBatches = 2,
        BatchRows = 50_000,
    };

    /// <summary>
    /// The sizing <c>urlog ingest</c> and the job runner share: <paramref name="parallelism"/> parsers with two chunks
    /// to spare, and one batch in flight per writer.
    /// </summary>
    public static PipelineOptions Sized(int parallelism, int writers) => new()
    {
        Parallelism = parallelism,
        InFlightChunks = parallelism + 2,
        Writers = writers,
        InFlightBatches = writers,
    };

    public int BatchPoolSize => Parallelism + InFlightBatches;

    /// <summary>Bytes one full batch occupies once its string buffers have grown to fit records of <paramref name="meanRecordBytes"/>.</summary>
    public long EstimateBatchBytes(int meanRecordBytes) => (long)BatchRows * (234 + (long)meanRecordBytes);

    /// <summary>Pooled memory at steady state: the formula from the type summary.</summary>
    public long EstimatePoolBytes(int meanRecordBytes) => ((InFlightChunks + 1L) * ChunkBytes) + (BatchPoolSize * EstimateBatchBytes(meanRecordBytes));

    public void Validate()
    {
        if (ChunkBytes < 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(ChunkBytes), ChunkBytes, "chunk must be at least 4 KiB");
        }

        if (MaxRecordBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRecordBytes), MaxRecordBytes, "must be positive");
        }

        if (BatchRows < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(BatchRows), BatchRows, "must be positive");
        }

        if (Parallelism < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Parallelism), Parallelism, "must be positive");
        }

        if (InFlightChunks < Parallelism + 2)
        {
            throw new ArgumentOutOfRangeException(nameof(InFlightChunks), InFlightChunks, $"must be at least Parallelism + 2 = {Parallelism + 2}: one chunk being read, one per parser, one queued");
        }

        if (InFlightBatches < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(InFlightBatches), InFlightBatches, "must be positive");
        }
    }
}
