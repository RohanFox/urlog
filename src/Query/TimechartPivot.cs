namespace Urlog.Query;

/// <summary>
/// Turns a timechart's long rows (bucket, in-top flag, series, value) into one row per bucket with a column per
/// series, which SQL cannot do because the column names are data. The series outside the top ten arrive as one
/// series with flag 0 and become the last column.
/// </summary>
public static class TimechartPivot
{
    public const string Rest = "other";

    public static QueryTable Pivot(QueryTable table, TranspileResult query)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(query);
        if (query.Timechart is not { Series: { } seriesAt, InTop: { } flagAt } shape)
        {
            return table; // no by clause: the rows are the chart already
        }

        var names = table.Rows.Where(r => r[flagAt] != "0").Select(r => r[seriesAt]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var hasRest = table.Rows.Any(r => r[flagAt] == "0");

        // A real series can be called "other" too; the rest then says what it is.
        var rest = names.Contains(Rest, StringComparer.Ordinal) ? Rest + " (the rest)" : Rest;
        var columns = new List<string> { table.Columns[shape.Time] };
        columns.AddRange(names);
        if (hasRest)
        {
            columns.Add(rest);
        }

        var index = names.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i + 1, StringComparer.Ordinal);
        var rows = new List<string[]>();
        string[]? current = null;
        foreach (var row in table.Rows)
        {
            if (current is null || current[0] != row[shape.Time])
            {
                current = new string[columns.Count];
                Array.Fill(current, "");
                current[0] = row[shape.Time];
                rows.Add(current);
            }

            current[row[flagAt] == "0" ? columns.Count - 1 : index[row[seriesAt]]] = row[shape.Value];
        }

        return new QueryTable(columns, rows, table.Elapsed);
    }
}
