using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Urlog.Query;

/// <summary>One statement of a search: SQL over <c>events_v</c> and its bound parameters. Run through the gate like any other.</summary>
public sealed record SearchStatement(string Sql, ImmutableArray<QueryParameter> Parameters)
{
    /// <summary>Columns of the result that hold <c>values(f)</c>: the reader cuts such a cell to the cap, and a refusal for memory is explained by them.</summary>
    public ImmutableArray<int> ValuesColumns { get; init; } = [];
}

/// <summary>
/// The statements one search runs (design record, "Query language", The search API), all built from one
/// transpiled query so they share its filter and its parameters: the page of a listing, the histogram, the counts,
/// the export. Nothing the analyst wrote is in any text; a cursor's values are parameters like every other value.
/// </summary>
public sealed class SearchPlan
{
    /// <summary>Values of one sidebar field the counts statement returns, one more than shown so a row without a value can be dropped.</summary>
    public const int TopValues = 10;

    private const string KeyOrder = "ts DESC, file_id DESC, file_offset DESC";

    private readonly FieldCatalog _catalog;
    private readonly TimeZoneInfo _zone;
    private readonly ListingParts? _listing;

    private SearchPlan(TranspileResult query, FieldCatalog catalog, TimeZoneInfo zone)
    {
        Query = query;
        _catalog = catalog;
        _zone = zone;
        _listing = query.Listing;
        var columns = new List<string>();
        var select = new List<string>();
        if (_listing is not null)
        {
            foreach (var field in catalog.Fields.Where(f => f.Column is not null))
            {
                select.Add(field.Column!);
                columns.Add(field.Name);
            }

            select.Add("extras");
            columns.Add("extras");
            var generated = 0;
            foreach (var group in _listing.Rex)
            {
                select.Add($"{group.Expression} AS `{Names.Generated(generated++)}`");
                columns.Add(group.Name);
            }

            SortColumns = columns.Count;
            foreach (var key in _listing.Sort)
            {
                select.Add($"{key.Expression} AS `{Names.Generated(generated++)}`");
                columns.Add("sort:" + key.Name);
            }
        }

        PageColumns = [.. columns];
        PageSelect = string.Join(", ", select);
        TsColumn = columns.IndexOf("_time");
        FileIdColumn = columns.IndexOf("file.id");
        FileOffsetColumn = columns.IndexOf("file.offset");
    }

    public TranspileResult Query { get; }

    public ResultKind Kind => Query.Kind;

    /// <summary>True for a listing in the default order, newest first: the one a time window can serve.</summary>
    public bool TimeOrdered => _listing is { Sort.IsEmpty: true };

    /// <summary>True when the query narrows nothing but the range: its counts are the case's field statistics when the range is the case's span.</summary>
    public bool Unfiltered => _listing is { Filtered: false };

    /// <summary>The <c>head</c> of the query when it wrote one: the most rows the listing has.</summary>
    public int? Head => _listing?.Head;

    public int SortKeys => _listing?.Sort.Length ?? 0;

    /// <summary>
    /// The cells of a page row, in order: every catalog column by its field name, <c>extras</c>, the query's rex
    /// groups, then one <c>sort:name</c> cell per sort key from <see cref="SortColumns"/> on, which the cursor reads.
    /// </summary>
    public ImmutableArray<string> PageColumns { get; }

    /// <summary>Index of the first sort-key cell of a page row; the cells before it are the event.</summary>
    public int SortColumns { get; }

    public int TsColumn { get; }

    public int FileIdColumn { get; }

    public int FileOffsetColumn { get; }

    private string PageSelect { get; }

    public static SearchPlan Create(string spl, TranspileOptions options, FieldCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var fields = catalog ?? FieldCatalog.Embedded;
        // A result of the search API is the same result on every run: an aggregation's rows in a total order.
        return new SearchPlan(Transpiler.Transpile(spl, options with { TotalOrder = true }, fields), fields, options.DisplayZone);
    }

    /// <summary>
    /// A page of the listing: the rows after <paramref name="after"/> in the listing's order, at most
    /// <paramref name="limit"/>. <paramref name="windowFrom"/> bounds a newest-first listing to the times from it
    /// on (<see cref="ListingWindow"/>); the caller decides from the row count whether the window held the page.
    /// </summary>
    public SearchStatement Page(int limit, Cursor? after = null, DateTimeOffset? windowFrom = null)
    {
        var listing = Listing();
        var extra = new List<QueryParameter>();
        var sql = new StringBuilder("SELECT ").Append(PageSelect).Append(" FROM events_v WHERE ").Append(Filter(listing, after, windowFrom, extra));
        sql.Append(" ORDER BY ").Append(Order(listing)).Append(" LIMIT {s_limit:UInt64}");
        extra.Add(new QueryParameter("s_limit", "UInt64", limit.ToString(CultureInfo.InvariantCulture)));
        return Statement(sql.ToString(), extra);
    }

    /// <summary>
    /// Keys first, for a newest-first listing no window served: the time of the oldest of the next
    /// <paramref name="limit"/> rows and how many there are, read from the three key columns alone. The page is
    /// then <see cref="Page"/> with that time as its window.
    /// </summary>
    public SearchStatement PageBoundary(int limit, Cursor? after = null)
    {
        var listing = Listing();
        var extra = new List<QueryParameter>();
        var sql = $"SELECT min(ts), count() FROM (SELECT ts, file_id, file_offset FROM events_v WHERE {Filter(listing, after, null, extra)} ORDER BY {KeyOrder} LIMIT {{s_limit:UInt64}})";
        extra.Add(new QueryParameter("s_limit", "UInt64", limit.ToString(CultureInfo.InvariantCulture)));
        return Statement(sql, extra);
    }

    /// <summary>Events per bucket and severity over the search's filter and range: (bucket start, severity, count), oldest first.</summary>
    public SearchStatement Histogram(HistogramSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        var listing = Listing();
        var extra = new List<QueryParameter>();
        var bucket = Transpiler.BucketExpression(span.Count, span.Unit, _zone, extra, "s_h_")
            ?? throw new QueryException(new TextSpan(0, 0), $"a histogram by day is cut in the display timezone, and '{_zone.Id}' is neither an IANA zone (Europe/Bucharest) nor an offset (+02:00)");
        var severity = _catalog.Resolve("log.level").Column!;
        return Statement($"SELECT {bucket} AS `{Names.Generated(0)}`, {severity}, count() FROM events_v WHERE {listing.Where} GROUP BY `{Names.Generated(0)}`, {severity} ORDER BY `{Names.Generated(0)}` ASC, {severity} ASC", extra);
    }

    /// <summary>How many events the search matches.</summary>
    public SearchStatement Count() => Statement($"SELECT count() FROM events_v WHERE {Listing().Where}", []);

    /// <summary>
    /// The total and the most frequent values of each of <paramref name="fields"/> over the search, in one read:
    /// a row per grouping set, the empty set being the total. Cells: one flag per field (0 where the row is a
    /// value of that field), the fields, the count. Exact counts.
    /// </summary>
    public SearchStatement Counts(IReadOnlyList<FieldDef> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var listing = Listing();
        if (fields.Count == 0)
        {
            return Count();
        }

        if (fields.Any(f => f.Column is null))
        {
            throw new ArgumentException("the counts are over catalog columns", nameof(fields));
        }

        var columns = fields.Select(f => f.Column!).ToList();
        var flags = columns.Select((c, i) => $"grouping({c}) AS `{Names.Generated(i)}`").ToList();
        var by = string.Join(", ", columns.Select((_, i) => $"`{Names.Generated(i)}`"));
        var count = $"`{Names.Generated(columns.Count)}`";
        var sql = $"SELECT {string.Join(", ", flags)}, {string.Join(", ", columns)}, count() AS {count} FROM events_v WHERE {listing.Where} "
            + $"GROUP BY GROUPING SETS ((), {string.Join(", ", columns.Select(c => $"({c})"))}) ORDER BY {count} DESC LIMIT {{s_top:UInt64}} BY {by}";
        return Statement(sql, [new QueryParameter("s_top", "UInt64", (TopValues + 1).ToString(CultureInfo.InvariantCulture))]);
    }

    /// <summary>
    /// The query as an export reads it. A listing: the columns of <c>table</c> or the listing's own, in the
    /// listing's whole order (its sort keys, then <c>ts</c>, <c>file_id</c>, <c>file_offset</c>), at most the
    /// smaller of its <c>head</c> and <paramref name="cap"/> rows. The order is total, so two exports of one
    /// search are the same bytes and a cut at the limit falls on the same row. An aggregation: its own
    /// statement, whose order the plan already made total.
    /// </summary>
    public SearchStatement Export(long cap)
    {
        if (_listing is null)
        {
            return new SearchStatement(Query.Sql, Query.Parameters) { ValuesColumns = Query.ValuesColumns };
        }

        var limit = Math.Min(cap, _listing.Head ?? cap).ToString(CultureInfo.InvariantCulture);
        var sql = $"SELECT {_listing.Select} FROM events_v WHERE {_listing.Where} ORDER BY {Order(_listing)} LIMIT {{s_limit:UInt64}}";
        return Statement(sql, [new QueryParameter("s_limit", "UInt64", limit)]);
    }

    /// <summary>
    /// Which <c>values(f)</c> columns of an exported aggregation hold a group over the cap, asked before the first
    /// byte so the file can say so at its start: one flag per column of <see cref="TranspileResult.ValuesColumns"/>,
    /// over the rows the export will send. Null when the result has no such column.
    /// </summary>
    public SearchStatement? ExportCapped(long cap)
    {
        if (_listing is not null || Query.ValuesColumns.IsDefaultOrEmpty)
        {
            return null;
        }

        // The columns are read by position: their names are generated aliases only the transpiler knows.
        var flags = Query.ValuesColumns.Select(i => $"max(length(tupleElement(r, {(i + 1).ToString(CultureInfo.InvariantCulture)})) > {{s_values:UInt64}})");
        var sql = $"SELECT {string.Join(", ", flags)} FROM (SELECT tuple(*) AS r FROM ({Query.Sql}) LIMIT {{s_rows:UInt64}})";
        return new SearchStatement(sql,
        [
            .. Query.Parameters,
            new QueryParameter("s_values", "UInt64", Transpiler.ValuesCap.ToString(CultureInfo.InvariantCulture)),
            new QueryParameter("s_rows", "UInt64", cap.ToString(CultureInfo.InvariantCulture)),
        ])
        {
            // It runs the aggregation, so it meets the memory limit where the export would.
            ValuesColumns = Query.ValuesColumns,
        };
    }

    private ListingParts Listing() => _listing ?? throw new InvalidOperationException("the query is an aggregation: it has no listing");

    private static string Order(ListingParts listing) =>
        listing.Sort.IsEmpty ? KeyOrder : string.Join(", ", listing.Sort.Select(k => k.Expression + (k.Descending ? " DESC" : " ASC"))) + ", " + KeyOrder;

    private static string Filter(ListingParts listing, Cursor? after, DateTimeOffset? windowFrom, List<QueryParameter> extra)
    {
        var where = new StringBuilder(listing.Where);
        if (windowFrom is { } from)
        {
            where.Append(" AND ts >= {s_wfrom:DateTime64(3)}");
            extra.Add(new QueryParameter("s_wfrom", "DateTime64(3)", Transpiler.Fmt(from)));
        }

        if (after is null)
        {
            return where.ToString();
        }

        if (after.SortValues.Length != listing.Sort.Length)
        {
            throw new CursorException("the cursor is not one this search issued");
        }

        // The keys in order, each with the comparison that means "later in the listing".
        var keys = new List<(string Expression, string Compare, string Parameter)>();
        for (var i = 0; i < listing.Sort.Length; i++)
        {
            var key = listing.Sort[i];
            var name = "s_k" + i.ToString(CultureInfo.InvariantCulture);
            var (type, reference) = key.Type switch
            {
                FieldType.Number => ("UInt64", $"{{{name}:UInt64}}"),
                FieldType.Real => ("Float64", $"{{{name}:Float64}}"),
                FieldType.Time => ("DateTime64(3)", $"{{{name}:DateTime64(3)}}"),
                FieldType.Ip => ("String", $"toIPv6({{{name}:String}})"),
                _ => ("String", $"{{{name}:String}}"),
            };
            extra.Add(new QueryParameter(name, type, after.SortValues[i]));
            keys.Add((key.Expression, key.Descending ? "<" : ">", reference));
        }

        extra.Add(new QueryParameter("s_ts", "DateTime64(3)", after.Ts));
        extra.Add(new QueryParameter("s_fid", "UInt64", after.FileId.ToString(CultureInfo.InvariantCulture)));
        extra.Add(new QueryParameter("s_off", "UInt64", after.FileOffset.ToString(CultureInfo.InvariantCulture)));
        keys.Add(("ts", "<", "{s_ts:DateTime64(3)}"));
        keys.Add(("file_id", "<", "{s_fid:UInt64}"));
        keys.Add(("file_offset", "<", "{s_off:UInt64}"));

        if (listing.Sort.IsEmpty)
        {
            // The plain bound first: it is what the sort key and the partitions prune on.
            where.Append(" AND ts <= {s_ts:DateTime64(3)}");
        }

        // Lexicographic "after": k1 later, or k1 equal and the rest later. Written out, since the directions differ.
        var predicate = $"{keys[^1].Expression} {keys[^1].Compare} {keys[^1].Parameter}";
        for (var i = keys.Count - 2; i >= 0; i--)
        {
            predicate = $"({keys[i].Expression} {keys[i].Compare} {keys[i].Parameter} OR ({keys[i].Expression} = {keys[i].Parameter} AND {predicate}))";
        }

        return where.Append(" AND ").Append(predicate).ToString();
    }

    private SearchStatement Statement(string sql, IReadOnlyList<QueryParameter> extra) =>
        new(sql, [.. Query.Parameters.Where(p => sql.Contains("{" + p.Name + ":", StringComparison.Ordinal)), .. extra]);
}
