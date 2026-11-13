using Urlog.Core.Pipeline;

namespace Urlog.Core.Jobs;

/// <summary>
/// Passes every batch on to the job's sink and keeps the least and the greatest event time on the way: the file's
/// <c>ts_min</c> and <c>ts_max</c>, written with the commit. One scan of the timestamp column per batch, on the
/// parser thread that hands the batch over, so the tokenisers are not touched. A row without a timestamp (a
/// reject, a line the source could not date) has the column's default and is left out.
/// </summary>
internal sealed class TimeRangeSink(IBatchSink inner) : IBatchSink
{
    private readonly Lock _lock = new();
    private long _min = long.MaxValue;
    private long _max = long.MinValue;

    /// <summary>The least event time seen, UTC; null when no row carried one.</summary>
    public DateTime? Min => Read(ref _min, long.MaxValue);

    /// <summary>The greatest event time seen, UTC; null when no row carried one.</summary>
    public DateTime? Max => Read(ref _max, long.MinValue);

    public void Submit(EventBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var min = long.MaxValue;
        var max = long.MinValue;
        foreach (var ts in batch.Ts.AsSpan(0, batch.RowCount))
        {
            if (ts <= 0)
            {
                continue;
            }

            min = Math.Min(min, ts);
            max = Math.Max(max, ts);
        }

        if (max != long.MinValue)
        {
            lock (_lock)
            {
                _min = Math.Min(_min, min);
                _max = Math.Max(_max, max);
            }
        }

        inner.Submit(batch);
    }

    public ValueTask CompleteAsync(CancellationToken cancellationToken) => inner.CompleteAsync(cancellationToken);

    private DateTime? Read(ref long field, long empty)
    {
        lock (_lock)
        {
            return field == empty ? null : DateTimeOffset.FromUnixTimeMilliseconds(field).UtcDateTime;
        }
    }
}
