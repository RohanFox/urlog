using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using ClickHouse.Driver.Tcp;

namespace Urlog.Core.Ingest;

/// <summary>Where a statistics field reads its value: a column of <c>events</c> or a key of the extras map.</summary>
public enum StatsFieldKind
{
    Column,
    Extra,
}

/// <summary>
/// One field the statistics cover, as the field catalog describes it. The catalog lives in the query assembly,
/// which builds this list; nothing in it comes from an analyst or from the data.
/// </summary>
/// <param name="Name">The catalog name; the <c>field</c> of the rows written.</param>
/// <param name="Source">The column, or the extras key.</param>
/// <param name="ValueText">For a column: the SQL of its value as the text an analyst types (<c>toString(http_status)</c>).</param>
/// <param name="HasValue">For a column: the SQL test for "this row has a value", null when every row has one.</param>
/// <param name="TopValues">Whether the most frequent values are stored; without it only how many events have the field.</param>
public sealed partial record StatsField(string Name, StatsFieldKind Kind, string Source, string? ValueText, string? HasValue, bool TopValues)
{
    /// <summary>Throws unless the names are of the shapes the SQL text relies on.</summary>
    public StatsField Checked()
    {
        if (!NameShape().IsMatch(Name))
        {
            throw new ArgumentException($"'{Name}' is not a field name", nameof(Name));
        }

        if (Kind == StatsFieldKind.Column ? !ColumnShape().IsMatch(Source) : !NameShape().IsMatch(Source))
        {
            throw new ArgumentException($"'{Source}' is not a column or an extras key the statistics can name", nameof(Source));
        }

        if (Kind == StatsFieldKind.Column && TopValues && string.IsNullOrWhiteSpace(ValueText))
        {
            throw new ArgumentException($"{Name}: a column with top values needs the text of its value", nameof(ValueText));
        }

        return this;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex NameShape();

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ColumnShape();
}

/// <summary>What one run may cost and how much it keeps.</summary>
/// <param name="TimeLimit">For the whole run; each statement gets what is left of it as its <c>max_execution_time</c>.</param>
/// <param name="MaxDistinct">A field with more distinct values than this (an estimate) gets no top values.</param>
/// <param name="MaxExtrasKeys">The extras keys reported, most frequent first.</param>
public sealed record FieldStatsLimits(TimeSpan TimeLimit, int MaxThreads, long MaxMemoryBytes, int TopValues = 100, long MaxDistinct = 100_000, int MaxExtrasKeys = 1000);

/// <summary>
/// One row of a case's set. <c>("", "")</c> counts the events of the case; <c>(field, "")</c> the events that have
/// a value in the field, with an estimate of its distinct values where one was computed (0 otherwise);
/// <c>(field, value)</c> is one of the field's most frequent values with its exact count.
/// </summary>
public sealed record FieldStatsRow(string Field, string Value, ulong Count, ulong DistinctEstimate = 0);

/// <summary>How long one statement of a run took.</summary>
public sealed record StatementTiming(string Label, TimeSpan Elapsed);

/// <summary>A computed set: the events it covers, its rows, and what each statement cost.</summary>
public sealed record FieldStatsSet(ulong Events, IReadOnlyList<FieldStatsRow> Rows, IReadOnlyList<StatementTiming> Timings);

/// <summary>A run passed its time limit, on this side or on the server (159). Nothing was written.</summary>
public sealed class FieldStatsTimeoutException : Exception
{
    public FieldStatsTimeoutException()
    {
    }

    public FieldStatsTimeoutException(string message)
        : base(message)
    {
    }

    public FieldStatsTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The per-case field statistics (design record, "Query language", Autocomplete): which fields a case has, how
/// many events carry each, and the most frequent values with exact counts. Runs as the ingest account. It is the
/// one reader of <c>events</c> beside the view: every statement comes from
/// <see cref="CaseScopedEventsQuery.BuildVisible"/>, so it names one case and counts the rows <c>events_v</c> shows.
/// <para>
/// A row without a value in a field is not counted for it, by the rule of the query verbs: an empty string, the
/// unspecified address and a zero the catalog marks as absent are no values, so none of them is a top value.
/// </para>
/// <para>
/// Three kinds of statement. One pass over the columns: events, events with a value per field, and an estimate
/// of the distinct values. One <c>GROUP BY</c> per field whose estimate is within
/// <see cref="FieldStatsLimits.MaxDistinct"/>, which bounds the hash table. Two reads of the extras map: the keys
/// with their counts, and the top values of the keys the catalog lists. A key the catalog does not list is reported
/// by name and count only: nobody has said whether its values may be shown.
/// </para>
/// </summary>
public sealed class FieldStatsTask(ClickHouseTcpClient client)
{
    /// <summary>A set is written in one INSERT, far below the rows the server would split into two parts.</summary>
    public const int MaxRows = 500_000;

    private const int TimeoutExceeded = 159;

    public async Task<FieldStatsSet> ComputeAsync(ulong caseId, IReadOnlyList<StatsField> fields, FieldStatsLimits limits, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(limits);
        var clock = Stopwatch.StartNew();
        var timings = new List<StatementTiming>();
        var rows = new List<FieldStatsRow>();
        var columns = fields.Where(f => f.Kind == StatsFieldKind.Column).Select(f => f.Checked()).ToList();
        var extras = fields.Where(f => f.Kind == StatsFieldKind.Extra).Select(f => f.Checked()).ToList();

        // One pass: events, and per column how many have a value and about how many distinct values.
        var projection = new List<string> { "count()" };
        foreach (var f in columns)
        {
            projection.Add(f.HasValue is null ? "count()" : $"countIf({f.HasValue})");
            if (f.TopValues)
            {
                projection.Add(f.HasValue is null ? $"uniqCombined({f.Source})" : $"uniqCombinedIf({f.Source}, {f.HasValue})");
            }
        }

        var summary = await ReadAsync("presence", caseId, string.Join(", ", projection), null, null, false).ConfigureAwait(false);
        var first = summary[0];
        var events = Convert.ToUInt64(first[0], CultureInfo.InvariantCulture);
        rows.Add(new FieldStatsRow(string.Empty, string.Empty, events));
        if (events == 0)
        {
            return new FieldStatsSet(0, rows, timings);
        }

        var at = 1;
        var eligible = new List<StatsField>();
        foreach (var f in columns)
        {
            var present = Convert.ToUInt64(first[at++], CultureInfo.InvariantCulture);
            var distinct = f.TopValues ? Convert.ToUInt64(first[at++], CultureInfo.InvariantCulture) : 0;
            if (present == 0)
            {
                continue;
            }

            rows.Add(new FieldStatsRow(f.Name, string.Empty, present, distinct));
            if (f.TopValues && distinct <= (ulong)limits.MaxDistinct)
            {
                eligible.Add(f);
            }
        }

        var top = limits.TopValues.ToString(CultureInfo.InvariantCulture);
        foreach (var f in eligible)
        {
            var values = await ReadAsync("top " + f.Name, caseId, $"{f.ValueText} AS v, count() AS c", f.HasValue, $"GROUP BY v ORDER BY c DESC, v ASC LIMIT {top}", false).ConfigureAwait(false);
            foreach (var row in values)
            {
                if (Text(row[0]) is { Length: > 0 } value)
                {
                    rows.Add(new FieldStatsRow(f.Name, value, Convert.ToUInt64(row[1], CultureInfo.InvariantCulture)));
                }
            }
        }

        // The extras map: its keys, then the top values of the listed ones. A key that is also the name of a
        // column field is left out; the name resolves to the column in a query, so the key cannot be asked for.
        var taken = columns.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        var listed = new Dictionary<string, StatsField>(StringComparer.Ordinal);
        foreach (var f in extras)
        {
            listed[f.Source] = f;
        }

        var maxKeys = limits.MaxExtrasKeys.ToString(CultureInfo.InvariantCulture);
        var keys = await ReadAsync("extras keys", caseId, "k, count() AS c, uniqCombined(v) AS d", "notEmpty(v)", $"GROUP BY k ORDER BY c DESC, k ASC LIMIT {maxKeys}", true).ConfigureAwait(false);
        var wanted = new List<StatsField>();
        foreach (var row in keys)
        {
            var key = Text(row[0]);
            var name = listed.TryGetValue(key, out var known) ? known.Name : key;
            if (key.Length == 0 || !taken.Add(name))
            {
                continue;
            }

            var distinct = Convert.ToUInt64(row[2], CultureInfo.InvariantCulture);
            rows.Add(new FieldStatsRow(name, string.Empty, Convert.ToUInt64(row[1], CultureInfo.InvariantCulture), distinct));
            if (known is { TopValues: true } && distinct <= (ulong)limits.MaxDistinct)
            {
                wanted.Add(known);
            }
        }

        if (wanted.Count > 0)
        {
            var list = string.Join(", ", wanted.Select(f => "'" + f.Source + "'"));
            var values = await ReadAsync("extras top", caseId, "k, v, count() AS c", $"notEmpty(v) AND k IN ({list})", $"GROUP BY k, v ORDER BY k ASC, c DESC, v ASC LIMIT {top} BY k", true).ConfigureAwait(false);
            foreach (var row in values)
            {
                rows.Add(new FieldStatsRow(listed[Text(row[0])].Name, Text(row[1]), Convert.ToUInt64(row[2], CultureInfo.InvariantCulture)));
            }
        }

        if (rows.Count > MaxRows)
        {
            throw new InvalidOperationException($"the statistics of case {caseId} are {rows.Count} rows, over the {MaxRows} one insert holds");
        }

        return new FieldStatsSet(events, rows, timings);

        async Task<List<object?[]>> ReadAsync(string label, ulong id, string select, string? filter, string? tail, bool pairs)
        {
            var left = limits.TimeLimit - clock.Elapsed;
            if (left <= TimeSpan.Zero)
            {
                throw new FieldStatsTimeoutException($"the statistics of case {id} passed their time limit of {limits.TimeLimit.TotalSeconds:0} s before '{label}'");
            }

            var statement = new StatementLimits((int)Math.Ceiling(left.TotalSeconds), limits.MaxThreads, limits.MaxMemoryBytes);
            var sql = CaseScopedEventsQuery.BuildVisible(id, select, filter, tail, statement, pairs);
            var started = clock.Elapsed;
            var result = new List<object?[]>();
            try
            {
                await foreach (var row in client.QueryAsync(sql, cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    result.Add(row);
                }
            }
            catch (ClickHouseTcpServerException e) when (e.RawCode == TimeoutExceeded)
            {
                throw new FieldStatsTimeoutException($"the statistics of case {id} passed their time limit of {limits.TimeLimit.TotalSeconds:0} s in '{label}'", e);
            }

            timings.Add(new StatementTiming(label, clock.Elapsed - started));
            return result;
        }
    }

    /// <summary>
    /// Writes the set as one INSERT, every row with <paramref name="computedAt"/>, then removes the case's older
    /// rows. Readers go through <c>field_stats_v</c>, which shows the rows of the newest <c>computed_at</c> only:
    /// before the insert that is the old set, after it the new one, and the delete changes nothing a reader sees.
    /// </summary>
    public async Task WriteAsync(ulong caseId, FieldStatsSet set, DateTime computedAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentOutOfRangeException.ThrowIfZero(caseId);
        var rows = set.Rows;
        if (rows.Count == 0 || rows.Count > MaxRows)
        {
            throw new ArgumentException($"a set holds between 1 and {MaxRows} rows", nameof(set));
        }

        var at = new DateTime(computedAt.Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        var cases = ClickHouseTcpColumn.Create("case_id", Enumerable.Repeat(caseId, rows.Count).ToArray());
        var fields = ClickHouseTcpColumn.Create("field", rows.Select(r => r.Field).ToArray());
        var values = ClickHouseTcpColumn.Create("value", rows.Select(r => r.Value).ToArray());
        var counts = ClickHouseTcpColumn.Create("count", rows.Select(r => r.Count).ToArray());
        var distinct = ClickHouseTcpColumn.Create("distinct_estimate", rows.Select(r => r.DistinctEstimate).ToArray());
        var times = ClickHouseTcpColumn.Create("computed_at", Enumerable.Repeat(at, rows.Count).ToArray());
        await client.InsertAsync("INSERT INTO field_stats (case_id, field, value, count, distinct_estimate, computed_at) VALUES", [cases, fields, values, counts, distinct, times], cancellationToken: cancellationToken).ConfigureAwait(false);
        var before = at.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        await client.ExecuteAsync($"DELETE FROM field_stats WHERE {CaseScopedEventsQuery.Predicate(caseId)} AND computed_at < toDateTime64('{before}', 3, 'UTC')", cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string Text(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };
}
