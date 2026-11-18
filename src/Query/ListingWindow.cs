namespace Urlog.Query;

/// <summary>
/// The time windows a newest-first listing is read through (design record, "Query language", The search API).
/// The table is sorted by host before time and the view filters every row by its file's generation, so a listing
/// over a whole case costs a read of the case: 1.8 s at 25 M rows and 8.3 s at 100 M, measured. A statement
/// bounded to the newest stretch of time reads one part and a granule or two a host, about 70 ms at either size.
/// <para>
/// The first window is as long as <see cref="SeedPages"/> pages of events take at the case's recorded density
/// (events over span, both from the case record), or the length the previous page needed. A window that returns a
/// full page is the page: its rows are the newest of the range. One that returns fewer is tried again longer, by
/// what the rows it did find say is needed and at least <see cref="Growth"/> times, <see cref="MaxWindows"/>
/// windows at most. After that the caller finds the page's oldest time with a keys-only statement and reads the
/// rows from there.
/// </para>
/// </summary>
public static class ListingWindow
{
    /// <summary>Pages of events the first window is sized for: one step for an unfiltered listing, measured at 25 M and 100 M rows.</summary>
    public const int SeedPages = 32;

    /// <summary>The least a window grows after a miss.</summary>
    public const int Growth = 16;

    /// <summary>Windows tried before the keys-only statement.</summary>
    public const int MaxWindows = 3;

    private static readonly TimeSpan Shortest = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The first window's length, or null when a window is no use: the case has no recorded density, or the
    /// window would cover the whole range anyway.
    /// </summary>
    /// <param name="caseEvents">Events of the case, from the file records.</param>
    /// <param name="caseSpan">Length of the case's recorded span.</param>
    /// <param name="pageSize">Rows a page holds.</param>
    /// <param name="range">Length of the range still to list: from its start to its end, or to the cursor.</param>
    /// <param name="previous">The window that found the previous page, when there was one.</param>
    public static TimeSpan? Seed(long caseEvents, TimeSpan caseSpan, int pageSize, TimeSpan range, TimeSpan? previous = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        TimeSpan window;
        if (previous is { } last && last > TimeSpan.Zero)
        {
            window = last;
        }
        else
        {
            if (caseEvents <= 0 || caseSpan <= TimeSpan.Zero)
            {
                return null;
            }

            var seconds = caseSpan.TotalSeconds * SeedPages * pageSize / caseEvents;
            window = TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.FromDays(36_500).TotalSeconds));
        }

        if (window < Shortest)
        {
            window = Shortest;
        }

        return window < range ? window : null;
    }

    /// <summary>
    /// The window after one that returned <paramref name="found"/> rows of a page of <paramref name="pageSize"/>,
    /// or null when the next read is the whole remaining range.
    /// </summary>
    /// <param name="window">The window that missed.</param>
    /// <param name="found">Rows it returned, fewer than a page.</param>
    /// <param name="pageSize">Rows a page holds.</param>
    /// <param name="range">Length of the range still to list.</param>
    /// <param name="tried">Windows tried so far, this one included.</param>
    public static TimeSpan? Next(TimeSpan window, int found, int pageSize, TimeSpan range, int tried)
    {
        if (tried >= MaxWindows)
        {
            return null;
        }

        // Twice what the density of the found rows asks for, and never less than the fixed growth.
        double factor = found > 0 ? Math.Max(Growth, 2.0 * pageSize / found) : Growth;
        var seconds = window.TotalSeconds * factor;
        return seconds < range.TotalSeconds ? TimeSpan.FromSeconds(seconds) : null;
    }
}

/// <summary>
/// The bucket width of a search's histogram: the smallest of a fixed ladder that cuts the range into at most
/// <see cref="MaxBuckets"/> buckets. The units are the timechart's, so the buckets are cut by the same rule
/// (under a day in UTC from the epoch, a day or more on the display zone's calendar).
/// </summary>
/// <param name="Count">How many of the unit.</param>
/// <param name="Unit">One of <c>s m h d w</c>.</param>
public sealed record HistogramSpan(int Count, char Unit)
{
    public const int MaxBuckets = 300;

    private static readonly (int Count, char Unit, long Seconds)[] Ladder =
    [
        (1, 's', 1), (5, 's', 5), (10, 's', 10), (30, 's', 30),
        (1, 'm', 60), (5, 'm', 300), (10, 'm', 600), (30, 'm', 1800),
        (1, 'h', 3600), (3, 'h', 10_800), (6, 'h', 21_600), (12, 'h', 43_200),
        (1, 'd', 86_400), (1, 'w', 604_800), (4, 'w', 2_419_200),
    ];

    public long Seconds => Count * (Unit switch { 's' => 1L, 'm' => 60, 'h' => 3600, 'd' => 86_400, _ => 604_800 });

    /// <summary>The span as the query language writes it: <c>5m</c>, <c>1d</c>.</summary>
    public string Text => Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + Unit;

    public static HistogramSpan For(DateTimeOffset from, DateTimeOffset to)
    {
        var seconds = Math.Max(1, (long)Math.Ceiling((to - from).TotalSeconds));
        foreach (var step in Ladder)
        {
            if ((seconds + step.Seconds - 1) / step.Seconds <= MaxBuckets)
            {
                return new HistogramSpan(step.Count, step.Unit);
            }
        }

        // Longer than 23 years: whole multiples of four weeks.
        var weeks = (int)Math.Min(int.MaxValue / 2, ((seconds / 604_800 / MaxBuckets / 4) + 1) * 4);
        return new HistogramSpan(weeks, 'w');
    }
}
