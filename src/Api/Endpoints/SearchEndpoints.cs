using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Urlog.Api.Auth;
using Urlog.Api.Search;
using Urlog.Core.Metadata;
using Urlog.Query;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Urlog.Api.Endpoints;

/// <summary>Body of <c>POST /api/v1/cases/{caseId}/search</c>. A member the route does not know is refused.</summary>
public sealed record SearchRequest
{
    public string? Query { get; init; }

    public string? From { get; init; }

    public string? To { get; init; }

    public string? Tz { get; init; }

    public string? Cursor { get; init; }
}

/// <summary>Body of <c>POST /api/v1/cases/{caseId}/sql</c>.</summary>
public sealed record SqlRequest
{
    public string? Sql { get; init; }
}

/// <summary>
/// The search routes of a case (design record, "Query language", The search API):
/// <c>POST .../search</c> answers as a stream of events (<c>meta</c>, then <c>histogram</c>, <c>page</c> and
/// <c>counts</c> each as soon as it is ready, or one <c>table</c> for an aggregation, then <c>done</c>), and with a
/// cursor the next page; <c>GET .../search/export</c> streams the result as a file; <c>POST .../sql</c> and
/// <c>GET .../sql/export</c> are the raw-SQL tab. Every statement runs as the query account with the route's case
/// as its whole allowed list, every request takes its slots before its first byte, and every request that
/// carried a query text leaves an audit row.
/// </summary>
public static class SearchEndpoints
{
    private const string Spl = "spl";
    private const string RawSql = "sql";
    private const int TimeoutExceeded = 159;
    private const int TooManyQueries = 202;

    private static readonly JsonSerializerOptions Strict = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static void Map(RouteGroupBuilder oneCase)
    {
        ArgumentNullException.ThrowIfNull(oneCase);
        oneCase.MapPost("/search", SearchAsync).RequireCaseRole(MemberRole.Ro).WithName("SearchCase");
        oneCase.MapGet("/search/export", ExportSearchAsync).RequireCaseRole(MemberRole.Ro).WithName("ExportCaseSearch");
        oneCase.MapPost("/sql", SqlAsync).RequireCaseRole(MemberRole.Ro).WithName("QueryCaseSql");
        oneCase.MapGet("/sql/export", ExportSqlAsync).RequireCaseRole(MemberRole.Ro).WithName("ExportCaseSql");
    }

    // ---- search -------------------------------------------------------------------------------------------------

    private static async Task SearchAsync(
        HttpContext http,
        CurrentUser me,
        MetadataDb db,
        ISearchBackend backend,
        IStoredHistogram stored,
        IFieldStatsSource statistics,
        QuerySlots slots,
        QueryAudit audit,
        IOptions<UrlogOptions> options,
        TimeProvider clock,
        ILoggerFactory loggers)
    {
        var @case = http.Features.GetRequiredFeature<CaseContext>().Case;
        var limits = options.Value.Query;
        var sw = Stopwatch.StartNew();
        if (await ReadBodyAsync<SearchRequest>(http).ConfigureAwait(false) is not { } request || !await SearchableAsync(http, @case).ConfigureAwait(false))
        {
            return;
        }

        if (request.Query is null)
        {
            await ProblemAsync(http, StatusCodes.Status400BadRequest, "bad_request", "query is required").ConfigureAwait(false);
            return;
        }

        var text = request.Query ?? string.Empty;
        var record = new QueryRecord(Spl, request.Cursor is null ? "search" : "page", text);
        if (text.Length > limits.MaxQueryLength)
        {
            await RefuseAsync(http, audit, me, @case, record, sw, QueryOutcome.Invalid, StatusCodes.Status400BadRequest, "query_syntax", $"the query is longer than {limits.MaxQueryLength:N0} characters").ConfigureAwait(false);
            return;
        }

        // The cursor first: it carries the range, the zone and the clock of the search it belongs to.
        Cursor? cursor = null;
        if (request.Cursor is not null)
        {
            try
            {
                cursor = Cursor.Decode(request.Cursor, (ulong)@case.Id, text);
            }
            catch (CursorException e)
            {
                await RefuseAsync(http, audit, me, @case, record, sw, QueryOutcome.Invalid, StatusCodes.Status400BadRequest, "bad_cursor", e.Message).ConfigureAwait(false);
                return;
            }

            if (cursor.Sent >= limits.MaxPagedRows)
            {
                await RefuseAsync(http, audit, me, @case, record, sw, QueryOutcome.Invalid, StatusCodes.Status400BadRequest, "cursor_limit", CursorLimit(limits)).ConfigureAwait(false);
                return;
            }
        }

        SearchPlan plan;
        string zoneId;
        DateTimeOffset now;
        try
        {
            (plan, zoneId, now) = Plan(@case, text, cursor?.Zone ?? request.Tz, cursor is null ? request.From : null, cursor is null ? request.To : null, cursor, clock);
        }
        catch (QueryException e)
        {
            await RefuseAsync(http, audit, me, @case, record, sw, QueryOutcome.Invalid, StatusCodes.Status400BadRequest, "query_syntax", e.Message, e.Offset, e.Length).ConfigureAwait(false);
            return;
        }
        catch (Exception e) when (e is ArgumentException or CursorException)
        {
            await RefuseAsync(http, audit, me, @case, record, sw, QueryOutcome.Invalid, StatusCodes.Status400BadRequest, e is CursorException ? "bad_cursor" : "bad_request", e.Message).ConfigureAwait(false);
            return;
        }

        record.From = plan.Query.RangeFrom;
        record.To = plan.Query.RangeTo;
        record.Zone = zoneId;
        record.FirstRow = cursor?.Sent;
        if (cursor is not null && (plan.Kind != ResultKind.Events || cursor.SortValues.Length != plan.SortKeys))
        {
            await RefuseAsync(http, audit, me, @case, record, sw, QueryOutcome.Invalid, StatusCodes.Status400BadRequest, "bad_cursor", "the cursor is not one this search issued").ConfigureAwait(false);
            return;
        }

        // A first search has three statements in flight (histogram, page, counts); a page or an aggregation has one.
        var need = plan.Kind == ResultKind.Events && cursor is null ? 3 : 1;
        using var lease = await slots.AcquireAsync(me.Id, need, export: false, http.RequestAborted).ConfigureAwait(false);
        if (lease is null)
        {
            await BusyAsync(http, audit, me, @case, record, sw, slots).ConfigureAwait(false);
            return;
        }

        var density = await DensityAsync(db, @case, http.RequestAborted).ConfigureAwait(false);
        var runner = new SearchRunner(backend, (ulong)@case.Id);
        var aborted = http.RequestAborted;

        // One statement that fails stops the other two.
        using var work = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        BeginStream(http);
        try
        {
            if (cursor is null)
            {
                await SendAsync(http, "meta", json =>
                {
                    json.WriteString("from", Instant(plan.Query.RangeFrom));
                    json.WriteString("to", Instant(plan.Query.RangeTo));
                    json.WriteString("tz", zoneId);
                    json.WriteString("result", plan.Kind switch { ResultKind.Events => "events", ResultKind.Stats => "stats", _ => "timechart" });
                    WriteStrings(json, "columns", plan.Kind == ResultKind.Events ? plan.Query.Columns : []);
                    json.WriteStartArray("warnings");
                    foreach (var warning in plan.Query.Warnings)
                    {
                        json.WriteStartObject();
                        json.WriteString("code", warning.Code);
                        json.WriteNumber("offset", warning.Span.Offset);
                        json.WriteNumber("length", warning.Span.Length);
                        json.WriteString("message", warning.Message);
                        json.WriteEndObject();
                    }

                    json.WriteEndArray();
                    WriteStrings(json, "fields", plan.Query.Fields.Select(f => f.Name));
                    json.WriteStartObject("span");
                    WriteInstant(json, "from", @case.TsMin);
                    WriteInstant(json, "to", @case.TsMax);
                    json.WriteNumber("undated_events", @case.UndatedRows);
                    json.WriteNumber("future_events", @case.FutureRows);
                    json.WriteEndObject();
                }).ConfigureAwait(false);
            }

            if (plan.Kind != ResultKind.Events)
            {
                record.Rows = await SendTableAsync(http, backend, plan, (ulong)@case.Id, limits, work.Token).ConfigureAwait(false);
            }
            else
            {
                var sent = cursor?.Sent ?? 0;
                var most = Math.Min(limits.MaxPagedRows, plan.Head ?? int.MaxValue);
                var page = PageEventAsync(runner, plan, cursor, density, Math.Min(limits.PageSize, most - sent), most, limits, zoneId, now, Cursor.Bind((ulong)@case.Id, text), work.Token);
                if (cursor is not null)
                {
                    var only = await page.ConfigureAwait(false);
                    record.Rows = only.Rows;
                    await SendAsync(http, "page", only.Body).ConfigureAwait(false);
                }
                else
                {
                    var pending = new List<Task<(string Kind, byte[] Body, long Rows)>>
                    {
                        page,
                        HistogramEventAsync(runner, stored, plan, @case, zoneId, work.Token),
                        CountsEventAsync(runner, statistics, db, plan, @case, loggers, work.Token),
                    };

                    // Each as soon as it is ready, in whatever order that is: every event names its kind.
                    await foreach (var finished in Task.WhenEach(pending).WithCancellation(aborted).ConfigureAwait(false))
                    {
                        var (kind, body, rows) = await finished.ConfigureAwait(false);
                        record.Rows += rows;
                        await SendAsync(http, kind, body).ConfigureAwait(false);
                    }
                }
            }

            await SendAsync(http, "done", json => json.WriteNumber("duration_ms", sw.ElapsedMilliseconds)).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException || !aborted.IsCancellationRequested)
        {
            await work.CancelAsync().ConfigureAwait(false);
            Classify(e, record);
            if (record.Outcome == QueryOutcome.Failed && e is not QueryFailedException)
            {
                Log.SearchFailed(loggers.CreateLogger(typeof(SearchEndpoints)), @case.Id, e);
            }

            await TrySendErrorAsync(http, record).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The client left. The cancelled reads closed their connections, and the query account's profile
            // stops a statement whose client has gone.
            record.Outcome = QueryOutcome.Abandoned;
        }
        finally
        {
            lease.Dispose();
            await AuditAfterStreamAsync(audit, me, @case, record, sw, loggers).ConfigureAwait(false);
        }
    }

    private static (SearchPlan Plan, string Zone, DateTimeOffset Now) Plan(CaseRecord @case, string text, string? tz, string? from, string? to, Cursor? cursor, TimeProvider clock)
    {
        var zoneId = string.IsNullOrWhiteSpace(tz) ? "UTC" : tz.Trim();
        var zone = TimeZones.Find(zoneId);
        var now = cursor is null ? clock.GetUtcNow() : DateTimeOffset.FromUnixTimeMilliseconds(cursor.Now);
        DateTimeOffset rangeFrom;
        DateTimeOffset rangeTo;
        if (cursor is not null)
        {
            rangeFrom = DateTimeOffset.FromUnixTimeMilliseconds(cursor.From);
            rangeTo = DateTimeOffset.FromUnixTimeMilliseconds(cursor.To);
        }
        else
        {
            // The default is the case's recorded span. A case without one has no event with a usable time; the
            // range is then every usable time up to now, which finds nothing unless something was just parsed.
            var span = new CaseSpan(@case.TsMin, @case.TsMax, @case.UndatedRows, @case.FutureRows).DefaultRange
                ?? (new DateTimeOffset(Urlog.Core.EventTime.Floor, TimeSpan.Zero), now);
            rangeFrom = string.IsNullOrWhiteSpace(from) ? span.From : TimeExpr.Parse(from, now, zone, out _);
            rangeTo = string.IsNullOrWhiteSpace(to) ? span.To : TimeExpr.Parse(to, now, zone, out _);
            if (rangeTo <= rangeFrom)
            {
                throw new ArgumentException($"the time range is empty: {Instant(rangeFrom)} to {Instant(rangeTo)}");
            }
        }

        var plan = SearchPlan.Create(text, new TranspileOptions((ulong)@case.Id, rangeFrom, rangeTo, zone, now));
        if (cursor is not null && (plan.Query.RangeFrom != rangeFrom || plan.Query.RangeTo != rangeTo))
        {
            // earliest= or latest= in the text resolved against the first request's clock gives the same range again.
            throw new CursorException("the cursor is not one this search issued");
        }

        return (plan, zoneId, now);
    }

    private static async Task<(string Kind, byte[] Body, long Rows)> PageEventAsync(SearchRunner runner, SearchPlan plan, Cursor? cursor, CaseDensity? density, int limit, int most, QueryOptions limits, string zone, DateTimeOffset now, string binding, CancellationToken cancellationToken)
    {
        var sentBefore = cursor?.Sent ?? 0;
        var page = await runner.PageAsync(plan, limit, cursor, density, cancellationToken).ConfigureAwait(false);
        var sent = sentBefore + page.Rows.Count;
        var full = limit > 0 && page.Rows.Count == limit;

        // The listing ends at the query's head; it is cut at the paging limit, which is what "capped" says.
        var next = full && sent < most ? SearchRunner.NextCursor(plan, page, zone, now, binding, sentBefore).Encode() : null;
        var capped = full && sent >= limits.MaxPagedRows;
        var body = Event("page", json =>
        {
            json.WriteNumber("first_row", sentBefore);
            WriteStrings(json, "columns", plan.PageColumns.Take(plan.SortColumns));
            json.WriteStartArray("rows");
            foreach (var row in page.Rows)
            {
                json.WriteStartArray();
                for (var i = 0; i < plan.SortColumns; i++)
                {
                    ExportWriter.WriteJson(json, row[i]);
                }

                json.WriteEndArray();
            }

            json.WriteEndArray();
            if (next is null)
            {
                json.WriteNull("next");
            }
            else
            {
                json.WriteString("next", next);
            }

            json.WriteBoolean("capped", capped);
            if (capped)
            {
                json.WriteString("message", CursorLimit(limits));
            }
        });
        return ("page", body, page.Rows.Count);
    }

    private static async Task<(string Kind, byte[] Body, long Rows)> HistogramEventAsync(SearchRunner runner, IStoredHistogram stored, SearchPlan plan, CaseRecord @case, string zone, CancellationToken cancellationToken)
    {
        var span = HistogramSpan.For(plan.Query.RangeFrom, plan.Query.RangeTo);
        IReadOnlyList<HistogramBucket>? buckets = null;
        var source = "query";
        if (plan.Unfiltered && WholeSpan(plan, @case))
        {
            buckets = await stored.FindAsync(@case.Id, plan.Query.RangeFrom, plan.Query.RangeTo, span, zone, cancellationToken).ConfigureAwait(false);
            source = buckets is null ? source : "stored";
        }

        buckets ??= await runner.HistogramAsync(plan, span, cancellationToken).ConfigureAwait(false);
        var body = Event("histogram", json =>
        {
            json.WriteString("span", span.Text);
            json.WriteString("source", source);
            json.WriteNumber("total", buckets.Sum(b => b.Counts.Values.Sum()));
            json.WriteStartArray("buckets");
            foreach (var bucket in buckets)
            {
                json.WriteStartObject();
                json.WriteString("start", Instant(bucket.Start));
                json.WriteStartObject("counts");
                foreach (var (severity, count) in bucket.Counts)
                {
                    json.WriteNumber(severity, count);
                }

                json.WriteEndObject();
                json.WriteEndObject();
            }

            json.WriteEndArray();
        });
        return ("histogram", body, 0);
    }

    private static async Task<(string Kind, byte[] Body, long Rows)> CountsEventAsync(SearchRunner runner, IFieldStatsSource statistics, MetadataDb db, SearchPlan plan, CaseRecord @case, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        // Which fields have few enough values to count is what the case's field statistics know. Without a set
        // the counts are the total alone.
        CaseFieldStats? set = null;
        try
        {
            set = await statistics.ReadAsync((ulong)@case.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.FieldStatsUnavailable(loggers.CreateLogger(typeof(SearchEndpoints)), @case.Id, e.Message);
        }

        SearchCounts counts;
        var state = await CaseStatsOperations.GetAsync(db, @case.Id, cancellationToken).ConfigureAwait(false);
        var current = set?.ComputedAt is not null && state is { DirtySince: null, State: CaseStatsState.Ok };
        if (current && plan.Unfiltered && WholeSpan(plan, @case) && @case.UndatedRows == 0 && @case.FutureRows == 0)
        {
            // Nothing narrows the search and nothing lies outside its range: the stored statistics are these
            // counts, and the fields route already serves them.
            counts = new SearchCounts((long)set!.Events, "field_stats", []);
        }
        else
        {
            var fields = set?.ComputedAt is null
                ? []
                : FieldCatalog.Embedded.Fields
                    .Where(f => f.Column is not null && f.TopValues && f.Type is FieldType.Keyword or FieldType.Number or FieldType.Ip)
                    .Where(f => set.Fields.TryGetValue(f.Name, out var stat) && stat.Top.Count > 0)
                    .ToList();
            counts = await runner.CountsAsync(plan, fields, cancellationToken).ConfigureAwait(false);
        }

        var body = Event("counts", json =>
        {
            json.WriteNumber("total", counts.Total);
            json.WriteString("source", counts.Source);
            json.WriteStartArray("fields");
            foreach (var field in counts.Fields)
            {
                json.WriteStartObject();
                json.WriteString("name", field.Name);
                json.WriteStartArray("top");
                foreach (var (value, count) in field.Top)
                {
                    json.WriteStartObject();
                    json.WriteString("value", value);
                    json.WriteNumber("count", count);
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }

            json.WriteEndArray();
        });
        return ("counts", body, 0);
    }

    private static async Task<long> SendTableAsync(HttpContext http, ISearchBackend backend, SearchPlan plan, ulong caseId, QueryOptions limits, CancellationToken cancellationToken)
    {
        var table = await backend.RunAsync(plan.Query, caseId, cancellationToken).ConfigureAwait(false);
        if (plan.Kind == ResultKind.Timechart)
        {
            table = TimechartPivot.Pivot(table, plan.Query);
        }

        var rows = Math.Min(table.Rows.Count, limits.MaxTableRows);
        await SendAsync(http, "table", json =>
        {
            WriteStrings(json, "columns", table.Columns);
            json.WriteStartArray("rows");
            for (var r = 0; r < rows; r++)
            {
                json.WriteStartArray();
                foreach (var cell in table.Rows[r])
                {
                    json.WriteStringValue(cell);
                }

                json.WriteEndArray();
            }

            json.WriteEndArray();
            // A table cut to the limit says so, with how many rows the result has.
            json.WriteBoolean("truncated", table.Rows.Count > rows);
            json.WriteNumber("total_rows", table.Rows.Count);
            json.WriteStartArray("values_capped_rows");
            foreach (var index in table.CappedRows.Where(i => i < rows))
            {
                json.WriteNumberValue(index);
            }

            json.WriteEndArray();
        }).ConfigureAwait(false);
        return rows;
    }

    // ---- export -------------------------------------------------------------------------------------------------

    private static async Task ExportSearchAsync(
        HttpContext http,
        CurrentUser me,
        ISearchBackend backend,
        QuerySlots slots,
        QueryAudit audit,
        IOptions<UrlogOptions> options,
        TimeProvider clock,
        ILoggerFactory loggers,
        [FromQuery] string? format,
        [FromQuery] string? query,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string? tz,
        [FromQuery(Name = "formula_guard")] bool? formulaGuard)
    {
        var @case = http.Features.GetRequiredFeature<CaseContext>().Case;
        var limits = options.Value.Query;
        var sw = Stopwatch.StartNew();
        if (!await SearchableAsync(http, @case).ConfigureAwait(false) || await FormatAsync(http, format).ConfigureAwait(false) is not { } kind)
        {
            return;
        }

        if (query is null)
        {
            await ProblemAsync(http, StatusCodes.Status400BadRequest, "bad_request", "query is required").ConfigureAwait(false);
            return;
        }

        var record = new QueryRecord(Spl, "export", query);
        if (query.Length > limits.MaxQueryLength)
        {
            await RefuseAsync(http, audit, me, @case, record, sw, QueryOutcome.Invalid, StatusCodes.Status400BadRequest, "query_syntax", $"the query is longer than {limits.MaxQueryLength:N0} characters").ConfigureAwait(false);
            return;
        }

        SearchPlan plan;
        string zoneId;
        try
        {
            (plan, zoneId, _) = Plan(@case, query, tz, from, to, null, clock);
        }
        catch (QueryException e)
        {
            await RefuseAsync(http, audit, me, @case, record, sw, QueryOutcome.Invalid, StatusCodes.Status400BadRequest, "query_syntax", e.Message, e.Offset, e.Length).ConfigureAwait(false);
            return;
        }
        catch (ArgumentException e)
        {
            await RefuseAsync(http, audit, me, @case, record, sw, QueryOutcome.Invalid, StatusCodes.Status400BadRequest, "bad_request", e.Message).ConfigureAwait(false);
            return;
        }

        record.From = plan.Query.RangeFrom;
        record.To = plan.Query.RangeTo;
        record.Zone = zoneId;

        // The names the analyst sees, and which of them the catalog marks sensitive.
        var columns = plan.Query.Columns;
        var sensitive = columns.Select(SensitiveName).Where(n => n is not null).Select(n => n!).Distinct(StringComparer.Ordinal).ToList();
        await ExportAsync(http, me, backend, slots, audit, limits, loggers, @case, record, sw, kind, formulaGuard == true, plan.Export(limits.MaxExportRows), null, plan.Kind == ResultKind.Events ? plan.Count() : null, plan.Head, columns, sensitive).ConfigureAwait(false);
    }

    private static async Task ExportSqlAsync(
        HttpContext http,
        CurrentUser me,
        ISearchBackend backend,
        QuerySlots slots,
        QueryAudit audit,
        IOptions<UrlogOptions> options,
        ILoggerFactory loggers,
        [FromQuery] string? format,
        [FromQuery] string? sql,
        [FromQuery(Name = "formula_guard")] bool? formulaGuard)
    {
        var @case = http.Features.GetRequiredFeature<CaseContext>().Case;
        var sw = Stopwatch.StartNew();
        if (!await SearchableAsync(http, @case).ConfigureAwait(false) || await FormatAsync(http, format).ConfigureAwait(false) is not { } kind)
        {
            return;
        }

        if (sql is null)
        {
            await ProblemAsync(http, StatusCodes.Status400BadRequest, "bad_request", "sql is required").ConfigureAwait(false);
            return;
        }

        var record = new QueryRecord(RawSql, "export", sql);
        await ExportAsync(http, me, backend, slots, audit, options.Value.Query, loggers, @case, record, sw, kind, formulaGuard == true, null, sql, null, null, null, null).ConfigureAwait(false);
    }

    private static async Task ExportAsync(
        HttpContext http,
        CurrentUser me,
        ISearchBackend backend,
        QuerySlots slots,
        QueryAudit audit,
        QueryOptions limits,
        ILoggerFactory loggers,
        CaseRecord @case,
        QueryRecord record,
        Stopwatch sw,
        ExportFormat format,
        bool formulaGuard,
        SearchStatement? statement,
        string? sql,
        SearchStatement? count,
        int? head,
        IReadOnlyList<string>? columns,
        IReadOnlyList<string>? sensitive)
    {
        using var lease = await slots.AcquireAsync(me.Id, 1, export: true, http.RequestAborted).ConfigureAwait(false);
        if (lease is null)
        {
            await BusyAsync(http, audit, me, @case, record, sw, slots).ConfigureAwait(false);
            return;
        }

        var aborted = http.RequestAborted;
        var started = false;
        var cut = false;
        using var writer = new ExportWriter(http.Response.BodyWriter, format, formulaGuard);
        try
        {
            // How many rows there are, before the first byte: a file cut at the limit says so in its headers.
            if (count is not null)
            {
                var total = (await backend.RunAsync(count, (ulong)@case.Id, 1, aborted).ConfigureAwait(false)).Rows;
                var matched = total.Count == 0 ? 0 : Convert.ToInt64(total[0][0], CultureInfo.InvariantCulture);

                // A head in the query is the result's own end, not a cut.
                matched = Math.Min(matched, head ?? long.MaxValue);
                http.Response.Headers["X-Urlog-Rows"] = matched.ToString(CultureInfo.InvariantCulture);
                if (matched > limits.MaxExportRows)
                {
                    http.Response.Headers["X-Urlog-Truncated"] = "true";
                    cut = true;
                }
            }

            await backend.StreamAsync(
                statement,
                sql,
                (ulong)@case.Id,
                names =>
                {
                    var shown = columns is not null && columns.Count == names.Count ? columns : names;
                    var marked = sensitive ?? [.. shown.Select(SensitiveName).Where(n => n is not null).Select(n => n!).Distinct(StringComparer.Ordinal)];
                    http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                    http.Response.ContentType = format == ExportFormat.Csv ? "text/csv; charset=utf-8" : "application/x-ndjson";
                    http.Response.Headers.ContentDisposition = $"attachment; filename=\"case-{@case.Id.ToString(CultureInfo.InvariantCulture)}-export.{(format == ExportFormat.Csv ? "csv" : "jsonl")}\"";
                    http.Response.Headers.CacheControl = "no-store";
                    started = true;
                    writer.Begin(shown, marked);
                },
                async row =>
                {
                    if (writer.Rows >= limits.MaxExportRows)
                    {
                        // A row past the limit: the result is longer than the file.
                        cut = true;
                        return false;
                    }

                    await writer.WriteAsync(row, aborted).ConfigureAwait(false);
                    return true;
                },
                aborted).ConfigureAwait(false);
            await writer.CompleteAsync(aborted).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException || !aborted.IsCancellationRequested)
        {
            Classify(e, record);
            if (!started && !http.Response.HasStarted)
            {
                http.Response.Headers.Remove("X-Urlog-Rows");
                http.Response.Headers.Remove("X-Urlog-Truncated");
                lease.Dispose();
                record.Bytes = 0;
                record.Complete = false;
                await FinishRefusedAsync(http, audit, me, @case, record, sw, e, slots).ConfigureAwait(false);
                return;
            }

            // Part of the file is out: a broken download says more than a file that ends early without a word.
            Log.SearchFailed(loggers.CreateLogger(typeof(SearchEndpoints)), @case.Id, e);
            http.Abort();
        }
        catch (OperationCanceledException)
        {
            record.Outcome = QueryOutcome.Abandoned;
        }

        // What left the server, whole or as far as it got: the rows, the bytes and their hash. Never a row.
        lease.Dispose();
        record.Rows = writer.Rows;
        record.Bytes = writer.Bytes;
        record.SentSha256 = writer.Sha256();
        record.Cut = cut;
        record.Complete = record.Outcome == QueryOutcome.Ok;
        await AuditAfterStreamAsync(audit, me, @case, record, sw, loggers).ConfigureAwait(false);
    }

    // ---- raw SQL ------------------------------------------------------------------------------------------------

    private static async Task SqlAsync(HttpContext http, CurrentUser me, ISearchBackend backend, QuerySlots slots, QueryAudit audit, IOptions<UrlogOptions> options)
    {
        var @case = http.Features.GetRequiredFeature<CaseContext>().Case;
        var limits = options.Value.Query;
        var sw = Stopwatch.StartNew();
        if (await ReadBodyAsync<SqlRequest>(http).ConfigureAwait(false) is not { } request || !await SearchableAsync(http, @case).ConfigureAwait(false))
        {
            return;
        }

        if (request.Sql is null)
        {
            await ProblemAsync(http, StatusCodes.Status400BadRequest, "bad_request", "sql is required").ConfigureAwait(false);
            return;
        }

        var record = new QueryRecord(RawSql, "search", request.Sql);
        using var lease = await slots.AcquireAsync(me.Id, 1, export: false, http.RequestAborted).ConfigureAwait(false);
        if (lease is null)
        {
            await BusyAsync(http, audit, me, @case, record, sw, slots).ConfigureAwait(false);
            return;
        }

        QueryRows rows;
        try
        {
            rows = await backend.RunSqlAsync(request.Sql, (ulong)@case.Id, limits.MaxTableRows, http.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            lease.Dispose();
            record.Outcome = QueryOutcome.Abandoned;
            record.DurationMs = sw.ElapsedMilliseconds;
            await audit.WriteAsync(me.Actor, @case, record).ConfigureAwait(false);
            return;
        }
        catch (Exception e) when (e is RawSqlRejectedException or QueryFailedException)
        {
            lease.Dispose();
            Classify(e, record);
            await FinishRefusedAsync(http, audit, me, @case, record, sw, e, slots).ConfigureAwait(false);
            return;
        }

        lease.Dispose();
        record.Rows = rows.Rows.Count;
        record.DurationMs = sw.ElapsedMilliseconds;

        // The row before the answer: a result that left no row is what must not happen.
        await audit.WriteAsync(me.Actor, @case, record).ConfigureAwait(false);
        http.Response.ContentType = "application/json; charset=utf-8";
        http.Response.Headers.CacheControl = "no-store";
        await http.Response.BodyWriter.WriteAsync(Event(null, json =>
        {
            WriteStrings(json, "columns", rows.Columns);
            json.WriteStartArray("rows");
            foreach (var row in rows.Rows)
            {
                json.WriteStartArray();
                foreach (var cell in row)
                {
                    ExportWriter.WriteJson(json, cell);
                }

                json.WriteEndArray();
            }

            json.WriteEndArray();
            json.WriteBoolean("truncated", rows.Truncated);
            json.WriteNumber("duration_ms", sw.ElapsedMilliseconds);
        }), http.RequestAborted).ConfigureAwait(false);
    }

    // ---- shared -------------------------------------------------------------------------------------------------

    private static string CursorLimit(QueryOptions limits) =>
        $"narrow the search or the time range: a result is paged to {limits.MaxPagedRows:N0} events; the export carries up to {limits.MaxExportRows:N0}";

    private static bool WholeSpan(SearchPlan plan, CaseRecord @case) =>
        new CaseSpan(@case.TsMin, @case.TsMax, @case.UndatedRows, @case.FutureRows).DefaultRange is { } span
        && span.From == plan.Query.RangeFrom
        && span.To == plan.Query.RangeTo;

    private static string? SensitiveName(string column)
    {
        var catalog = FieldCatalog.Embedded;
        var field = catalog.Contains(column) ? catalog.Resolve(column) : catalog.Fields.FirstOrDefault(f => f.Column == column);
        return field is { Sensitive: true } ? field.Name : null;
    }

    private static async Task<CaseDensity?> DensityAsync(MetadataDb db, CaseRecord @case, CancellationToken cancellationToken)
    {
        if (@case.TsMin is not { } first || @case.TsMax is not { } last || last <= first)
        {
            return null;
        }

        var events = await db.Files.AsNoTracking().Where(f => f.CaseId == @case.Id).SumAsync(f => f.RowCount, cancellationToken).ConfigureAwait(false);
        return events > 0 ? new CaseDensity(events, last - first) : null;
    }

    /// <summary>Maps what a statement threw onto the audit row: the outcome and the reason.</summary>
    private static void Classify(Exception e, QueryRecord record)
    {
        switch (e)
        {
            case RawSqlRejectedException:
                record.Outcome = QueryOutcome.Rejected;
                record.Reason = e.Message;
                break;
            case QueryFailedException { ErrorCode: TimeoutExceeded }:
                record.Outcome = QueryOutcome.TimedOut;
                record.Reason = e.Message;
                break;
            case QueryFailedException { ErrorCode: TooManyQueries }:
                record.Outcome = QueryOutcome.Busy;
                record.Reason = e.Message;
                break;
            case QueryFailedException failed:
                record.Outcome = QueryOutcome.Failed;
                record.Reason = failed.Message;
                break;
            case QueryException or CursorException:
                record.Outcome = QueryOutcome.Invalid;
                record.Reason = e.Message;
                break;
            default:
                record.Outcome = QueryOutcome.Failed;
                record.Reason = "the event store did not answer";
                break;
        }
    }

    /// <summary>The answer to a request whose statement failed before its first byte: audited, then the problem.</summary>
    private static async Task FinishRefusedAsync(HttpContext http, QueryAudit audit, CurrentUser me, CaseRecord @case, QueryRecord record, Stopwatch sw, Exception e, QuerySlots slots)
    {
        record.DurationMs = sw.ElapsedMilliseconds;
        await audit.WriteAsync(me.Actor, @case, record).ConfigureAwait(false);
        switch (record.Outcome)
        {
            case QueryOutcome.Rejected:
                // The gate's reason, word for word: it is written for the analyst who typed the text.
                await ProblemAsync(http, StatusCodes.Status400BadRequest, "query_rejected", "the query was refused", e.Message).ConfigureAwait(false);
                break;
            case QueryOutcome.TimedOut:
                await ProblemAsync(http, StatusCodes.Status504GatewayTimeout, "query_timeout", "the query passed its time limit", e.Message).ConfigureAwait(false);
                break;
            case QueryOutcome.Busy:
                http.Response.Headers.RetryAfter = RetryAfter(slots);
                await ProblemAsync(http, StatusCodes.Status429TooManyRequests, "busy", "the event store is busy", $"retry after {RetryAfter(slots)} s").ConfigureAwait(false);
                break;
            case QueryOutcome.Invalid:
                await ProblemAsync(http, StatusCodes.Status400BadRequest, "query_syntax", e.Message).ConfigureAwait(false);
                break;
            default:
                var known = e is QueryFailedException;
                await ProblemAsync(http, known ? StatusCodes.Status400BadRequest : StatusCodes.Status502BadGateway, known ? "query_error" : "store_unavailable", known ? "the event store refused the query" : "the event store did not answer", known ? e.Message : null).ConfigureAwait(false);
                break;
        }
    }

    private static async Task RefuseAsync(HttpContext http, QueryAudit audit, CurrentUser me, CaseRecord @case, QueryRecord record, Stopwatch sw, string outcome, int status, string code, string message, int? offset = null, int? length = null)
    {
        record.Outcome = outcome;
        record.Reason = message;
        record.DurationMs = sw.ElapsedMilliseconds;
        await audit.WriteAsync(me.Actor, @case, record).ConfigureAwait(false);
        await ProblemAsync(http, status, code, message, null, offset, length).ConfigureAwait(false);
    }

    private static async Task BusyAsync(HttpContext http, QueryAudit audit, CurrentUser me, CaseRecord @case, QueryRecord record, Stopwatch sw, QuerySlots slots)
    {
        record.Outcome = QueryOutcome.Busy;
        record.Reason = "no query slot within the wait";
        record.DurationMs = sw.ElapsedMilliseconds;
        await audit.WriteAsync(me.Actor, @case, record).ConfigureAwait(false);
        http.Response.Headers.RetryAfter = RetryAfter(slots);
        await ProblemAsync(http, StatusCodes.Status429TooManyRequests, "busy", "too many queries are running", $"retry after {RetryAfter(slots)} s").ConfigureAwait(false);
    }

    private static string RetryAfter(QuerySlots slots) => Math.Max(1, (int)Math.Ceiling(slots.Wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

    private static async Task AuditAfterStreamAsync(QueryAudit audit, CurrentUser me, CaseRecord @case, QueryRecord record, Stopwatch sw, ILoggerFactory loggers)
    {
        record.DurationMs = sw.ElapsedMilliseconds;
        try
        {
            await audit.WriteAsync(me.Actor, @case, record).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // The answer is out and cannot be taken back; the failure is on the operator's log at error level.
            Log.QueryAuditFailed(loggers.CreateLogger(typeof(SearchEndpoints)), @case.Id, e);
        }
    }

    private static async Task<bool> SearchableAsync(HttpContext http, CaseRecord @case)
    {
        if (@case.State is CaseState.Active or CaseState.Closed)
        {
            return true;
        }

        await ProblemAsync(http, StatusCodes.Status409Conflict, "case_not_searchable", $"the case is {Urlog.Core.Metadata.Names.Of(@case.State)}: its events are not in the event store").ConfigureAwait(false);
        return false;
    }

    private static async Task<ExportFormat?> FormatAsync(HttpContext http, string? format)
    {
        switch (format)
        {
            case "csv":
                return ExportFormat.Csv;
            case "json":
                return ExportFormat.Json;
            default:
                await ProblemAsync(http, StatusCodes.Status400BadRequest, "bad_request", "format is csv or json").ConfigureAwait(false);
                return null;
        }
    }

    private static async Task<T?> ReadBodyAsync<T>(HttpContext http)
        where T : class
    {
        try
        {
            var body = await JsonSerializer.DeserializeAsync<T>(http.Request.Body, Strict, http.RequestAborted).ConfigureAwait(false);
            if (body is not null)
            {
                return body;
            }
        }
        catch (JsonException e)
        {
            await ProblemAsync(http, StatusCodes.Status400BadRequest, "bad_request", "the body is not the JSON this route takes", e.Message).ConfigureAwait(false);
            return null;
        }

        await ProblemAsync(http, StatusCodes.Status400BadRequest, "bad_request", "the body is empty").ConfigureAwait(false);
        return null;
    }

    private static Task ProblemAsync(HttpContext http, int status, string code, string title, string? detail = null, int? offset = null, int? length = null)
    {
        var problem = Problems.Make(status, title, "https://urlog/problems/" + code.Replace('_', '-'), detail);
        problem.Extensions["code"] = code;
        if (offset is { } at)
        {
            problem.Extensions["offset"] = at;
            problem.Extensions["length"] = length ?? 0;
        }

        http.Response.StatusCode = status;
        return http.Response.WriteAsJsonAsync(problem, options: null, Problems.ContentType, http.RequestAborted);
    }

    private static void BeginStream(HttpContext http)
    {
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["X-Accel-Buffering"] = "no";
    }

    private static Task SendAsync(HttpContext http, string kind, Action<Utf8JsonWriter> write) => SendAsync(http, kind, Event(kind, write));

    private static async Task SendAsync(HttpContext http, string kind, byte[] body)
    {
        var output = http.Response.BodyWriter;
        output.Write(Encoding.ASCII.GetBytes("event: " + kind + "\ndata: "));
        output.Write(body);
        output.Write("\n\n"u8);
        await output.FlushAsync(http.RequestAborted).ConfigureAwait(false);
    }

    private static async Task TrySendErrorAsync(HttpContext http, QueryRecord record)
    {
        try
        {
            await SendAsync(http, "error", json =>
            {
                json.WriteString("code", record.Outcome switch
                {
                    QueryOutcome.TimedOut => "query_timeout",
                    QueryOutcome.Busy => "busy",
                    QueryOutcome.Rejected => "query_rejected",
                    QueryOutcome.Invalid => "query_syntax",
                    _ => "query_error",
                });
                json.WriteString("message", record.Reason);
            }).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or InvalidOperationException)
        {
            // The client went while the error was on its way.
        }
    }

    /// <summary>One JSON object on one line; <paramref name="kind"/> is its first member when given, so every event names itself.</summary>
    private static byte[] Event(string? kind, Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            if (kind is not null)
            {
                json.WriteString("kind", kind);
            }

            write(json);
            json.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteStrings(Utf8JsonWriter json, string name, IEnumerable<string> values)
    {
        json.WriteStartArray(name);
        foreach (var value in values)
        {
            json.WriteStringValue(value);
        }

        json.WriteEndArray();
    }

    private static void WriteInstant(Utf8JsonWriter json, string name, DateTime? utc)
    {
        if (utc is { } value)
        {
            json.WriteString(name, Instant(new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc))));
        }
        else
        {
            json.WriteNull(name);
        }
    }

    private static string Instant(DateTimeOffset instant) => instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
