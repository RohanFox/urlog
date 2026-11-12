namespace Urlog.Core.Pipeline;

/// <summary>
/// Where full batches go. <see cref="Submit"/> is called on a parser thread and may block (bounded queue); the sink
/// owns the batch from then on and must call <see cref="EventBatch.Return"/> when it is done with it.
/// <see cref="CompleteAsync"/> is awaited once after every parser has flushed. <c>NativeInserter</c> is the ClickHouse sink.
/// </summary>
public interface IBatchSink
{
    void Submit(EventBatch batch);

    ValueTask CompleteAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Counts rows and raw bytes and returns the batch at once. The sink for <c>EndToEnd.PipelineNginx1G</c> (the
/// pipeline's own cost and allocation, nothing downstream) and for tests. Thread-safe.
/// </summary>
public sealed class CountingBatchSink : IBatchSink
{
    private long _rows;
    private long _rawBytes;
    private long _batches;
    private long _extras;

    public long Rows => Interlocked.Read(ref _rows);

    public long RawBytes => Interlocked.Read(ref _rawBytes);

    public long Batches => Interlocked.Read(ref _batches);

    public long Extras => Interlocked.Read(ref _extras);

    public void Submit(EventBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        Interlocked.Add(ref _rows, batch.RowCount);
        Interlocked.Add(ref _rawBytes, batch.RawBytes);
        Interlocked.Add(ref _extras, batch.ExtrasCount);
        Interlocked.Increment(ref _batches);
        batch.Return();
    }

    public ValueTask CompleteAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

/// <summary>Keeps every submitted batch alive for inspection. Tests only: the batches never go back to the pool until <see cref="ReturnAll"/>.</summary>
public sealed class CollectingBatchSink : IBatchSink
{
    private readonly List<EventBatch> _batches = [];
    private readonly Lock _lock = new();

    public IReadOnlyList<EventBatch> Batches
    {
        get
        {
            lock (_lock)
            {
                return [.. _batches];
            }
        }
    }

    public void Submit(EventBatch batch)
    {
        lock (_lock)
        {
            _batches.Add(batch);
        }
    }

    public ValueTask CompleteAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public void ReturnAll()
    {
        lock (_lock)
        {
            foreach (var b in _batches)
            {
                b.Return();
            }

            _batches.Clear();
        }
    }
}
