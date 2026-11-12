using System.Buffers;
using System.Collections.Concurrent;

namespace Urlog.Core.Pipeline;

/// <summary>
/// The pools the pipeline owns: chunk buffers and event batches. Neither is <c>ArrayPool&lt;byte&gt;.Shared</c>:
/// the shared pool trims under memory pressure, which the first harness measured as hundreds of bytes per row of apparent
/// allocation on a busy box (design record "Parser plugin contract"). Sizes come from <see cref="PipelineOptions"/>
/// and the pipeline never rents beyond them, so a steady-state run allocates nothing per chunk or per batch.
/// </summary>
public sealed class PipelineBuffers
{
    public PipelineBuffers(PipelineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Options = options;
        Chunks = new ChunkPool(options);
        Batches = new BatchPool(options);
    }

    public PipelineOptions Options { get; }

    public ChunkPool Chunks { get; }

    public BatchPool Batches { get; }
}

/// <summary>
/// Chunk buffers: a dedicated <see cref="ArrayPool{T}"/> (<c>ArrayPool.Create</c>, which never trims) holding
/// <see cref="PipelineOptions.InFlightChunks"/> + 1 arrays of <see cref="PipelineOptions.ChunkBytes"/>: the reader holds two
/// at hand-off (the chunk it is yielding and the next one it has already rented), so the pool is one larger than the
/// in-flight count (without it the eleventh buffer was allocated and dropped on every file). A rent above
/// <see cref="PipelineOptions.ChunkBytes"/> (a record longer than a chunk) is served by a plain allocation and
/// dropped on return; that is the documented cost of an oversize record, not a leak into the pool.
/// </summary>
public sealed class ChunkPool
{
    private readonly ArrayPool<byte> _pool;
    private readonly int _maxPooled;
    private int _outstanding;
    private long _rented;
    private long _allocatedOversize;

    public ChunkPool(PipelineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _maxPooled = options.ChunkBytes;
        _pool = ArrayPool<byte>.Create(maxArrayLength: options.ChunkBytes, maxArraysPerBucket: options.InFlightChunks + 1); // + 1: the reader's hand-off buffer
    }

    /// <summary>Buffers rented and not yet returned.</summary>
    public int Outstanding => Volatile.Read(ref _outstanding);

    /// <summary>Rents since construction.</summary>
    public long Rented => Interlocked.Read(ref _rented);

    /// <summary>Rents that exceeded the pooled size and were allocated outright.</summary>
    public long OversizeAllocations => Interlocked.Read(ref _allocatedOversize);

    public byte[] Rent(int minimumLength)
    {
        // Allocate first, count after: a rent that throws (out of memory) must not leave Outstanding above zero,
        // or the pool reads as unbalanced for every later job that shares it.
        byte[] buffer;
        if (minimumLength > _maxPooled)
        {
            buffer = GC.AllocateUninitializedArray<byte>(minimumLength);
            Interlocked.Increment(ref _allocatedOversize);
        }
        else
        {
            buffer = _pool.Rent(minimumLength);
        }

        Interlocked.Increment(ref _outstanding);
        Interlocked.Increment(ref _rented);
        return buffer;
    }

    public void Return(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        Interlocked.Decrement(ref _outstanding);
        if (buffer.Length <= _maxPooled)
        {
            _pool.Return(buffer);
        }
    }
}

/// <summary>
/// Recycled <see cref="EventBatch"/> objects, <see cref="PipelineOptions.BatchPoolSize"/> of them, created on first
/// use and kept for the life of the pool. <see cref="Rent"/> blocks when every batch is out: that is the pipeline's
/// backpressure, parsers wait for the insert path instead of growing a queue.
/// </summary>
public sealed class BatchPool : IDisposable
{
    private readonly ConcurrentBag<EventBatch> _free = [];
    private readonly SemaphoreSlim _available;
    private readonly int _capacity;
    private int _created;
    private int _outstanding;

    public BatchPool(PipelineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _capacity = options.BatchRows;
        Size = options.BatchPoolSize;
        _available = new SemaphoreSlim(Size, Size);
    }

    /// <summary>Batches this pool will ever hold.</summary>
    public int Size { get; }

    /// <summary>Batch objects created so far (lazily, up to <see cref="Size"/>).</summary>
    public int Created => Volatile.Read(ref _created);

    /// <summary>Batches rented and not yet returned.</summary>
    public int Outstanding => Volatile.Read(ref _outstanding);

    /// <summary>Takes a cleared batch; blocks while all <see cref="Size"/> are in flight.</summary>
    public EventBatch Rent(CancellationToken cancellationToken = default)
    {
        _available.Wait(cancellationToken);
        if (!_free.TryTake(out var batch))
        {
            try
            {
                batch = new EventBatch(this, _capacity);
            }
            catch
            {
                // Nothing was handed out: give the slot back, or a failed allocation costs the pool a batch for good
                // and leaves Outstanding above zero.
                _available.Release();
                throw;
            }

            Interlocked.Increment(ref _created);
        }

        Interlocked.Increment(ref _outstanding);
        return batch;
    }

    /// <summary>Returns a batch; it is cleared here so the next renter starts at row 0. Called by the sink when it is done with it.</summary>
    public void Return(EventBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (!ReferenceEquals(batch.Owner, this))
        {
            throw new ArgumentException("batch belongs to another pool", nameof(batch));
        }

        batch.Clear();
        _free.Add(batch);
        Interlocked.Decrement(ref _outstanding);
        _available.Release();
    }

    public void Dispose() => _available.Dispose();
}
