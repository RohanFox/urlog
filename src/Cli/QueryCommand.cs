using System.Globalization;
using System.Text;
using Urlog.Query;

namespace Urlog.Cli;

/// <summary>
/// <c>urlog query --case &lt;id&gt; [--db metadata.db] [--from t] [--to t] [--tz zone] [--now t] [--sql] [--explain] '&lt;spl&gt;'</c>:
/// transpile, run over HTTP as the read-only user with <c>SQL_allowed_cases</c> per request, print an aligned
/// table, the row count and the elapsed time. <c>--sql</c> prints the SQL and parameters and stops;
/// <c>--explain</c> prints <c>EXPLAIN indexes = 1</c> after the table. The default range is the case's span,
/// never "last 24 h": with <c>--db</c> the span the metadata file records for the case (read only), without it
/// the span of the case's visible events through <c>events_v</c>, times before 1980 left out.
/// </summary>
internal static class QueryCommand
{
    public static async Task<int> RunAsync(CliArgs args, CancellationToken ct)
    {
        var (result, client) = await PrepareAsync(args, ct).ConfigureAwait(false);
        PrintWarnings(result);
        if (args.Flag("sql"))
        {
            Console.WriteLine(result.Sql);
            foreach (var p in result.Parameters)
            {
                Console.WriteLine($"  {p.Name} ({p.Type}) = {p.Value}");
            }

            return 0;
        }

        var table = await GuardAsync(() => client.RunAsync(result, cancellationToken: ct)).ConfigureAwait(false);
        if (table.ValuesCapped)
        {
            Console.Error.WriteLine($"warning: values_capped: {table.CappedRows.Count} row(s) hold more than {Transpiler.ValuesCap} values; their lists end in ...");
        }

        table = TimechartPivot.Pivot(table, result);
        PrintTable(table, hideEmptyColumns: !result.IsAggregate);
        Console.WriteLine($"{table.Rows.Count:N0} rows ({table.Elapsed.TotalMilliseconds:N0} ms); range {Transpiler.Fmt(result.RangeFrom)} .. {Transpiler.Fmt(result.RangeTo)} UTC, case {result.CaseId}");
        if (args.Flag("explain"))
        {
            Console.WriteLine();
            foreach (var line in await GuardAsync(() => client.ExplainAsync(result, cancellationToken: ct)).ConfigureAwait(false))
            {
                Console.WriteLine(line);
            }
        }

        return 0;
    }

    /// <summary>A query the gate or the server refused is an error message and exit code 1, like a query the parser refused.</summary>
    public static async Task<T> GuardAsync<T>(Func<Task<T>> run)
    {
        try
        {
            return await run().ConfigureAwait(false);
        }
        catch (RawSqlRejectedException e)
        {
            throw new CliException("query refused: " + e.Message);
        }
        catch (QueryFailedException e)
        {
            throw new CliException($"query error from the server ({e.ErrorCode.ToString(CultureInfo.InvariantCulture)}): {e.Message}");
        }
    }

    /// <summary>Parses the common options, resolves the range and transpiles. Shared with <c>bench query</c>.</summary>
    public static async Task<(TranspileResult Result, ClickHouseQueryClient Client)> PrepareAsync(CliArgs args, CancellationToken ct)
    {
        if (args.Positional.Count != 1)
        {
            throw new CliException("query takes exactly one argument: the SPL in quotes");
        }

        var caseId = args.ULong("case", 0);
        if (caseId == 0)
        {
            throw new CliException("--case <id> is required (a positive integer)");
        }

        TimeZoneInfo zone;
        try
        {
            zone = TimeZones.Find(args.Option("tz") ?? "UTC");
        }
        catch (ArgumentException e)
        {
            throw new CliException("--tz: " + e.Message.Split(" (Parameter", 2)[0]);
        }
        var now = args.Option("now") is { } nowText ? TimeExpr.Parse(nowText, DateTimeOffset.UtcNow, zone, out _) : DateTimeOffset.UtcNow;
        var connection = new Connection(args);
        Console.Error.WriteLine(connection.AnnounceQuery());
        var client = connection.Query();

        DateTimeOffset from, to;
        if (args.Option("from") is { } fromText && args.Option("to") is { } toText)
        {
            from = TimeExpr.Parse(fromText, now, zone, out _);
            to = TimeExpr.Parse(toText, now, zone, out _);
        }
        else if (args.Option("from") is not null || args.Option("to") is not null)
        {
            throw new CliException("--from and --to go together");
        }
        else if (args.Option("db") is { } db)
        {
            // The case's recorded span, from its file records: what the API uses as the default range.
            var recorded = await Urlog.Core.Metadata.CaseStatsOperations.ReadSpanAsync(db, checked((long)caseId), ct).ConfigureAwait(false)
                ?? throw new CliException($"{db} has no case {caseId} with a recorded span (no such file or case, or a metadata file from before the span was recorded)");
            (from, to) = recorded.DefaultRange ?? throw new CliException($"case {caseId} has no event with a usable time; give --from and --to");
            if (recorded.UndatedEvents > 0 || recorded.FutureEvents > 0)
            {
                Console.Error.WriteLine($"range     outside the case span: {recorded.UndatedEvents:N0} event(s) without a usable time, {recorded.FutureEvents:N0} dated after their file arrived; --from and --to reach them");
            }
        }
        else
        {
            // No metadata file: the span of the visible events, times below the floor left out.
            var span = await client.CaseSpanAsync(caseId, ct).ConfigureAwait(false) ?? throw new CliException($"case {caseId} has no visible events with a usable time (nothing ingested, or not committed)");
            (from, to) = span;
        }

        var options = new TranspileOptions(caseId, from, to, zone, now);
        try
        {
            return (Transpiler.Transpile(args.Positional[0], options), client);
        }
        catch (QueryException e)
        {
            var spl = args.Positional[0];
            var sb = new StringBuilder();
            sb.Append("query error at ").Append(e.Offset.ToString(CultureInfo.InvariantCulture)).Append(": ").Append(e.Message).Append('\n');
            sb.Append("  ").Append(spl).Append('\n');
            sb.Append("  ").Append(' ', Math.Min(e.Offset, spl.Length)).Append('^', Math.Max(1, Math.Min(e.Length, spl.Length - e.Offset)));
            throw new CliException(sb.ToString());
        }
    }

    private static void PrintWarnings(TranspileResult result)
    {
        foreach (var w in result.Warnings)
        {
            Console.Error.WriteLine($"warning: {w.Code} at {w.Span.Offset}: {w.Message}");
        }
    }

    /// <summary>Columns padded to their widest cell (capped), the last column unpadded; columns empty in every row are dropped for event listings.</summary>
    public static void PrintTable(QueryTable table, bool hideEmptyColumns, int maxWidth = 64)
    {
        var keep = Enumerable.Range(0, table.Columns.Count).Where(c => !hideEmptyColumns || table.Rows.Any(r => r[c].Length > 0)).ToList();
        if (keep.Count == 0)
        {
            keep = [.. Enumerable.Range(0, table.Columns.Count)];
        }

        var widths = keep.Select(c => Math.Min(maxWidth, Math.Max(table.Columns[c].Length, table.Rows.Count == 0 ? 0 : table.Rows.Max(r => r[c].Length)))).ToList();
        Console.WriteLine(Line(keep.Select(c => table.Columns[c]).ToList(), widths));
        Console.WriteLine(Line(widths.Select(w => new string('-', w)).ToList(), widths));
        foreach (var row in table.Rows)
        {
            Console.WriteLine(Line(keep.Select(c => row[c]).ToList(), widths));
        }

        static string Line(IReadOnlyList<string> cells, IReadOnlyList<int> widths)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < cells.Count; i++)
            {
                var cell = cells[i].Replace('\n', ' ').Replace('\r', ' ');
                if (i == cells.Count - 1)
                {
                    sb.Append(cell);
                }
                else
                {
                    sb.Append(cell.Length > widths[i] ? cell[..(widths[i] - 1)] + "…" : cell.PadRight(widths[i])).Append("  ");
                }
            }

            return sb.ToString();
        }
    }
}
