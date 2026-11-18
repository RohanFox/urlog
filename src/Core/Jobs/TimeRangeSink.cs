using Urlog.Core.Pipeline;

namespace Urlog.Core.Jobs;

/// <summary>
/// Passes every batch on to the job's sink and keeps the least and the greatest event time on the way: the file's
/// <c>ts_min</c> and <c>ts_max</c>, written with the commit. One scan of the timestamp column per batch, on the
/// parser thread that hands the batch over, so the tokenisers are not touched. A row whose time is below
/// <see cref="EventTime.Floor"/> (a reject, a line the source could not date, a timestamp parsed without a year) or
/// above the file's arrival plus <see cref="EventTime.FutureMargin"/> is left out of the range and counted.
/// </summary>
internal sealed class TimeRangeSink(IBatchSink inner, long ceilingMilliseconds = long.MaxValue) : IBatchSink
{
    private readonly Lock _lock = new();
    private readonly long _floor = EventTime.FloorMilliseconds;
    private long _min = long.MaxValue;
    private long _max = long.MinValue;
    private long _undated;
    private long _future;

    /// <summary>The least event time inside the bounds, UTC; null when no row carried one.</summary>
    public DateTime? Min => Read(ref _min, long.MaxValue);

    /// <summary>The greatest event time inside the bounds, UTC; null when no row carried one.</summary>
    public DateTime? Max => Read(ref _max, long.MinValue);

    /// <summary>Rows whose time is below the floor.</summary>
    public long Undated => Interlocked.Read(ref _undated);

    /// <summary>Rows whose time is later than the file can have been written.</summary>
    public long Future => Interlocked.Read(ref _future);

    public void Submit(EventBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var min = long.MaxValue;
        var max = long.MinValue;
        long undated = 0;
        long future = 0;
        foreach (var ts in batch.Ts.AsSpan(0, batch.RowCount))
        {
            if (ts < _floor)
            {
                undated++;
                continue;
            }

            if (ts > ceilingMilliseconds)
            {
                future++;
                continue;
            }

            min = Math.Min(min, ts);
            max = Math.Max(max, ts);
        }

        lock (_lock)
        {
            _undated += undated;
            _future += future;
            if (max != long.MinValue)
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
