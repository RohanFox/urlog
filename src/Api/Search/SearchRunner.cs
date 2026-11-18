using System.Globalization;
using Urlog.Query;

namespace Urlog.Api.Search;

/// <summary>What the case record says about how dense the case is: its events and the length of its span.</summary>
public sealed record CaseDensity(long Events, TimeSpan Span);

/// <summary>One page of a listing as it was read: the rows, the window that found them, and the statements it took.</summary>
public sealed record ListingPage(IReadOnlyList<object?[]> Rows, TimeSpan? Window, int Statements);

/// <summary>The counts of a search: the total, and for each sidebar field its most frequent values over the result.</summary>
public sealed record SearchCounts(long Total, string Source, IReadOnlyList<FieldCounts> Fields);

public sealed record FieldCounts(string Name, IReadOnlyList<KeyValuePair<string, long>> Top);

/// <summary>
/// The statements of one search against the event store, in the shapes the routes send: a page read through time
/// windows (<see cref="ListingWindow"/>), the histogram, the counts. It holds no HTTP and no metadata, so the
/// stepping is tested against a stand-in and against a real store with the same code.
/// </summary>
public sealed class SearchRunner(ISearchBackend backend, ulong caseId)
{
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss.fff";

    /// <summary>
    /// The next <paramref name="limit"/> rows of the listing after <paramref name="after"/>. A newest-first
    /// listing is read through a window at the end of what is left of the range, grown on a miss, and after the
    /// last window by finding the page's oldest time from the keys alone; any other order is one statement.
    /// </summary>
    public async Task<ListingPage> PageAsync(SearchPlan plan, int limit, Cursor? after, CaseDensity? density, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (limit <= 0)
        {
            return new ListingPage([], null, 0);
        }

        if (!plan.TimeOrdered)
        {
            return new ListingPage((await backend.RunAsync(plan.Page(limit, after), caseId, limit, cancellationToken).ConfigureAwait(false)).Rows, null, 1);
        }

        var from = plan.Query.RangeFrom;
        var end = after is null ? plan.Query.RangeTo : ParseTime(after.Ts).AddMilliseconds(1);
        var left = end - from;
        var previous = after is { WindowSeconds: > 0 } ? TimeSpan.FromSeconds(after.WindowSeconds) : (TimeSpan?)null;
        var window = ListingWindow.Seed(density?.Events ?? 0, density?.Span ?? TimeSpan.Zero, limit, left, previous);
        var statements = 0;
        if (window is null)
        {
            // No density to size a window from, or the window is the range: the range itself is the read.
            return new ListingPage((await backend.RunAsync(plan.Page(limit, after), caseId, limit, cancellationToken).ConfigureAwait(false)).Rows, null, 1);
        }

        for (var tried = 1; window is { } length; tried++)
        {
            var rows = (await backend.RunAsync(plan.Page(limit, after, end - length), caseId, limit, cancellationToken).ConfigureAwait(false)).Rows;
            statements++;
            if (rows.Count >= limit)
            {
                return new ListingPage(rows, length, statements);
            }

            window = ListingWindow.Next(length, rows.Count, limit, left, tried);
        }

        // Keys first: the time of the oldest row of the page, from the three key columns, then the rows from there.
        var boundary = (await backend.RunAsync(plan.PageBoundary(limit, after), caseId, 1, cancellationToken).ConfigureAwait(false)).Rows;
        statements++;
        if (boundary.Count == 0 || Convert.ToInt64(boundary[0][1], CultureInfo.InvariantCulture) == 0)
        {
            return new ListingPage([], null, statements);
        }

        var oldest = boundary[0][0] switch
        {
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            DateTimeOffset dto => dto,
            var other => ParseTime(Convert.ToString(other, CultureInfo.InvariantCulture)!),
        };
        var page = (await backend.RunAsync(plan.Page(limit, after, oldest), caseId, limit, cancellationToken).ConfigureAwait(false)).Rows;
        return new ListingPage(page, null, statements + 1);
    }

    /// <summary>The cursor that asks for the rows after the last row of <paramref name="page"/>.</summary>
    public static Cursor NextCursor(SearchPlan plan, ListingPage page, string zone, DateTimeOffset now, string binding, int sentBefore)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(page);
        var last = page.Rows[^1];
        var sort = new string[plan.SortKeys];
        for (var i = 0; i < sort.Length; i++)
        {
            sort[i] = ClickHouseQueryClient.Text(last[plan.SortColumns + i]);
        }

        return new Cursor(
            plan.Query.RangeFrom.ToUnixTimeMilliseconds(),
            plan.Query.RangeTo.ToUnixTimeMilliseconds(),
            zone,
            now.ToUnixTimeMilliseconds(),
            binding,
            [.. sort],
            ClickHouseQueryClient.Text(last[plan.TsColumn]),
            Convert.ToUInt64(last[plan.FileIdColumn], CultureInfo.InvariantCulture),
            Convert.ToUInt64(last[plan.FileOffsetColumn], CultureInfo.InvariantCulture),
            sentBefore + page.Rows.Count,
            page.Window is { } w ? Math.Max(1, (long)Math.Ceiling(w.TotalSeconds)) : 0);
    }

    /// <summary>The histogram of the search from the events: buckets oldest first, each with its events by severity.</summary>
    public async Task<IReadOnlyList<HistogramBucket>> HistogramAsync(SearchPlan plan, HistogramSpan span, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(span);

        // At most the bucket cap times the severities there are; the row limit is a guard, not a page.
        var rows = (await backend.RunAsync(plan.Histogram(span), caseId, 100_000, cancellationToken).ConfigureAwait(false)).Rows;
        var buckets = new List<HistogramBucket>();
        Dictionary<string, long>? counts = null;
        DateTimeOffset current = default;
        foreach (var row in rows)
        {
            var start = row[0] switch
            {
                DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
                DateTimeOffset dto => dto.ToUniversalTime(),
                var other => ParseTime(Convert.ToString(other, CultureInfo.InvariantCulture)!),
            };
            if (counts is null || start != current)
            {
                counts = new Dictionary<string, long>(StringComparer.Ordinal);
                buckets.Add(new HistogramBucket(start, counts));
                current = start;
            }

            counts[ClickHouseQueryClient.Text(row[1])] = Convert.ToInt64(row[2], CultureInfo.InvariantCulture);
        }

        return buckets;
    }

    /// <summary>
    /// The total and the ten most frequent values of each of <paramref name="fields"/> over the search, exact, in
    /// one statement. A row without a value in a field is no value of it, by the rule of the verbs.
    /// </summary>
    public async Task<SearchCounts> CountsAsync(SearchPlan plan, IReadOnlyList<FieldDef> fields, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(fields);
        var n = fields.Count;
        var rows = (await backend.RunAsync(plan.Counts(fields), caseId, ((n + 1) * (SearchPlan.TopValues + 1)) + 1, cancellationToken).ConfigureAwait(false)).Rows;
        if (n == 0)
        {
            return new SearchCounts(rows.Count == 0 ? 0 : Convert.ToInt64(rows[0][0], CultureInfo.InvariantCulture), "query", []);
        }

        long total = 0;
        var tops = fields.Select(_ => new List<KeyValuePair<string, long>>()).ToList();
        foreach (var row in rows)
        {
            var count = Convert.ToInt64(row[2 * n], CultureInfo.InvariantCulture);

            // The flag of a field is 0 in the rows that are its values; the total's row has none.
            var field = -1;
            for (var i = 0; i < n; i++)
            {
                if (Convert.ToInt64(row[i], CultureInfo.InvariantCulture) == 0)
                {
                    field = i;
                    break;
                }
            }

            if (field < 0)
            {
                total = count;
                continue;
            }

            var value = ClickHouseQueryClient.Text(row[n + field]);
            var absent = fields[field].Type switch
            {
                FieldType.Ip => value == "::",
                FieldType.Number => fields[field].ZeroIsAbsent && value == "0",
                _ => value.Length == 0,
            };
            if (!absent && tops[field].Count < SearchPlan.TopValues)
            {
                tops[field].Add(new KeyValuePair<string, long>(value, count));
            }
        }

        return new SearchCounts(total, "query", [.. fields.Select((f, i) => new FieldCounts(f.Name, tops[i]))]);
    }

    private static DateTimeOffset ParseTime(string text) =>
        new(DateTime.SpecifyKind(DateTime.ParseExact(text, TimeFormat, CultureInfo.InvariantCulture), DateTimeKind.Utc));
}
