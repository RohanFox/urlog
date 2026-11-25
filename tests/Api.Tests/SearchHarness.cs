using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Urlog.Api.Search;
using Urlog.Query;

namespace Urlog.Api.Tests;

/// <summary>One event of the stand-in event store: its key, and a message that tests look for in places it must not be.</summary>
internal sealed record FakeEvent(DateTime Ts, ulong FileId, ulong FileOffset, string Message, string Severity = "info");

/// <summary>
/// The event store behind the search routes, as a test sets it: a list of events that page statements are
/// answered from by their own parameters (range, window, cursor, limit), so a walk through the HTTP routes is a
/// walk through real keys; canned answers for everything else; a record of every statement with the case it was
/// run for; and a gate a test closes to hold statements in flight.
/// </summary>
internal sealed class FakeSearchBackend : ISearchBackend
{
    private static readonly string[] Columns = [.. FieldCatalog.Embedded.Fields.Where(f => f.Column is not null).Select(f => f.Name), "extras", SearchPlan.RawValidColumn];

    public List<FakeEvent> Events { get; } = [];

    public ConcurrentQueue<(string Kind, string Sql, ulong CaseId)> Calls { get; } = new();

    /// <summary>Thrown in place of an answer to a statement of this kind (page, boundary, histogram, stored, counts, count, query, sql, stream).</summary>
    public ConcurrentDictionary<string, Exception> Failures { get; } = new();

    /// <summary>Awaited before every answer; a test swaps in an open source to hold statements in flight.</summary>
    public TaskCompletionSource Gate { get; set; } = Completed();

    public Func<TranspileResult, QueryTable> OnQuery { get; set; } = _ => new QueryTable(["count"], [["0"]], TimeSpan.Zero);

    public Func<string, QueryRows> OnSql { get; set; } = _ => new QueryRows(["n"], [[1UL]], false, TimeSpan.Zero);

    /// <summary>An export stops after this many rows and waits on <see cref="StreamHold"/>, so a test can leave mid-file.</summary>
    public int? StreamHoldAfter { get; set; }

    public TaskCompletionSource StreamHold { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when an export has reached <see cref="StreamHoldAfter"/> and stands still.</summary>
    public TaskCompletionSource StreamHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Rows an export streams, in place of the events, when a test wants cells of its own.</summary>
    public (string[] Columns, List<object?[]> Rows)? Export { get; set; }

    public static TaskCompletionSource Completed()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    /// <summary>The stored bytes of an event's line, by file and offset, when a test wants bytes that are not its message.</summary>
    public Dictionary<(ulong File, ulong Offset), byte[]> RawBytes { get; } = [];

    public async Task<QueryRows> RunAsync(SearchStatement statement, ulong caseId, int maxRows, CancellationToken cancellationToken)
    {
        var kind = Kind(statement.Sql);
        Calls.Enqueue((kind, statement.Sql, caseId));
        await Gate.Task.WaitAsync(cancellationToken);
        if (Failures.TryGetValue(kind, out var failure))
        {
            throw failure;
        }

        if (kind == "generations")
        {
            // Every file of the stand-in is at its first generation.
            var files = Matching(statement).GroupBy(e => e.FileId).OrderBy(g => g.Key).Select(g => new object?[] { g.Key, 1U, (ulong)g.Count() }).ToList();
            return new QueryRows(["file_id", "any(parse_generation)", "count()"], files, false, TimeSpan.Zero);
        }

        if (kind == "cut")
        {
            var skip = int.Parse(statement.Parameters.Single(p => p.Name == "s_skip").Value, CultureInfo.InvariantCulture);
            var last = Matching(statement).Skip(skip).Take(1).Select(e => new object?[] { e.Ts, e.FileId, e.FileOffset }).ToList();
            return new QueryRows(["ts", "file_id", "file_offset"], last, false, TimeSpan.Zero);
        }

        if (kind == "files")
        {
            // The files of the rows an export would send: the newest rows up to its limit, grouped.
            var sent = Matching(statement).Take(Limit(statement)).GroupBy(e => e.FileId).OrderBy(g => g.Key).Select(g => new object?[] { g.Key, (ulong)g.Count() }).ToList();
            return new QueryRows(["file_id", "count()"], sent, false, TimeSpan.Zero);
        }

        if (kind == "bytes")
        {
            // The one event with that file, offset and time, as the store would find it by its key.
            string P(string name) => statement.Parameters.Single(p => p.Name == name).Value;
            var file = ulong.Parse(P("b_file"), CultureInfo.InvariantCulture);
            var offset = ulong.Parse(P("b_offset"), CultureInfo.InvariantCulture);
            var found = Events.FirstOrDefault(e => e.FileId == file && e.FileOffset == offset && e.Ts == Time(P("b_ts")) && P("b_host").Length == 0);
            if (found is null)
            {
                return new QueryRows(["base64Encode(raw)"], [], false, TimeSpan.Zero);
            }

            var bytes = RawBytes.TryGetValue((file, offset), out var stored) ? stored : Encoding.UTF8.GetBytes(found.Message);
            return new QueryRows(["base64Encode(raw)"], [[Convert.ToBase64String(bytes)]], false, TimeSpan.Zero);
        }

        var matching = Matching(statement);
        switch (kind)
        {
            case "boundary":
                var keys = matching.Take(Limit(statement)).ToList();
                return new QueryRows(["min(ts)", "count()"], [[keys.Count == 0 ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc) : keys[^1].Ts, (ulong)keys.Count]], false, TimeSpan.Zero);
            case "count":
                return new QueryRows(["count()"], [[(ulong)matching.Count]], false, TimeSpan.Zero);
            case "counts":
                return new QueryRows(["c0", "host", "c1"], [[1UL, string.Empty, (ulong)matching.Count]], false, TimeSpan.Zero);
            case "histogram" or "stored":
                var rows = matching.GroupBy(e => (Day: e.Ts.Date, e.Severity)).OrderBy(g => g.Key.Day).ThenBy(g => g.Key.Severity, StringComparer.Ordinal)
                    .Select(g => new object?[] { DateTime.SpecifyKind(g.Key.Day, DateTimeKind.Utc), g.Key.Severity, (ulong)g.Count() }).ToList();
                return new QueryRows(["c0", "severity", "count()"], rows, false, TimeSpan.Zero);
            default:
                var page = matching.Take(Math.Min(Limit(statement), maxRows)).Select(Row).ToList();
                return new QueryRows(Columns, page, false, TimeSpan.Zero);
        }
    }

    public async Task<QueryTable> RunAsync(TranspileResult query, ulong caseId, CancellationToken cancellationToken)
    {
        Calls.Enqueue(("query", query.Sql, caseId));
        await Gate.Task.WaitAsync(cancellationToken);
        return Failures.TryGetValue("query", out var failure) ? throw failure : OnQuery(query);
    }

    public async Task<QueryRows> RunSqlAsync(string sql, ulong caseId, int maxRows, CancellationToken cancellationToken)
    {
        Calls.Enqueue(("sql", sql, caseId));
        await Gate.Task.WaitAsync(cancellationToken);
        if (Failures.TryGetValue("sql", out var failure))
        {
            throw failure;
        }

        var all = OnSql(sql);
        return all.Rows.Count > maxRows ? all with { Rows = [.. all.Rows.Take(maxRows)], Truncated = true } : all;
    }

    public async Task<long> StreamAsync(SearchStatement? statement, string? sql, ulong caseId, Action<IReadOnlyList<string>> onColumns, Func<object?[], ValueTask<bool>> onRow, CancellationToken cancellationToken)
    {
        Calls.Enqueue(("stream", statement?.Sql ?? sql!, caseId));
        await Gate.Task.WaitAsync(cancellationToken);
        if (Failures.TryGetValue("stream", out var failure))
        {
            throw failure;
        }

        // The default rows of an export: the listing's six columns, and for an evidence export the file, the offset
        // and the raw line of each row after them, as the statement asks.
        var evidence = statement?.Sql.Contains(", file_id, file_offset, raw FROM events_v", StringComparison.Ordinal) == true;
        string[] names = evidence ? ["ts", "host", "program", "severity", "source_ip", "message", "file_id", "file_offset", "raw"] : ["ts", "host", "program", "severity", "source_ip", "message"];
        var (columns, rows) = Export ?? (names,
            [.. Events.OrderByDescending(e => (e.Ts, e.FileId, e.FileOffset)).Take(statement is null ? int.MaxValue : Limit(statement)).Select(e => evidence
                ? new object?[] { e.Ts, "web-01", "nginx", e.Severity, IPAddress.Parse("::ffff:10.0.0.5"), e.Message, e.FileId, e.FileOffset, "raw of " + e.Message }
                : new object?[] { e.Ts, "web-01", "nginx", e.Severity, IPAddress.Parse("::ffff:10.0.0.5"), e.Message })]);
        onColumns(columns);
        long sent = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sent == StreamHoldAfter)
            {
                StreamHeld.TrySetResult();
                await StreamHold.Task.WaitAsync(cancellationToken);
            }

            if (!await onRow(row))
            {
                break;
            }

            sent++;
        }

        return sent;
    }

    public static string Kind(string sql) =>
        sql.StartsWith("SELECT base64Encode(raw) FROM events_v", StringComparison.Ordinal) ? "bytes"
        : sql.StartsWith("SELECT file_id, any(parse_generation), count() FROM events_v", StringComparison.Ordinal) ? "generations"
        : sql.Contains(" LIMIT 1 OFFSET {s_skip:UInt64}", StringComparison.Ordinal) ? "cut"
        : sql.StartsWith("SELECT file_id, count() FROM (SELECT file_id FROM events_v", StringComparison.Ordinal) ? "files"
        : sql.StartsWith("SELECT min(ts), count() FROM (", StringComparison.Ordinal) ? "boundary"
        : sql.Contains("GROUPING SETS", StringComparison.Ordinal) ? "counts"
        : sql.StartsWith("SELECT count() FROM events_v", StringComparison.Ordinal) ? "count"
        : sql.Contains("FROM case_histogram_v", StringComparison.Ordinal) ? "stored"
        : sql.Contains("toStartOfInterval", StringComparison.Ordinal) && sql.Contains("GROUP BY", StringComparison.Ordinal) ? "histogram"
        : "page";

    private static int Limit(SearchStatement statement) =>
        statement.Parameters.FirstOrDefault(p => p.Name == "s_limit") is { } limit ? int.Parse(limit.Value, CultureInfo.InvariantCulture) : int.MaxValue;

    private static DateTime Time(string text) => DateTime.SpecifyKind(DateTime.ParseExact(text, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture), DateTimeKind.Utc);

    /// <summary>The events a statement's own parameters leave, newest first by the full key.</summary>
    private List<FakeEvent> Matching(SearchStatement statement)
    {
        string? P(string name) => statement.Parameters.FirstOrDefault(p => p.Name == name)?.Value;
        IEnumerable<FakeEvent> events = Events;
        if (P("from") is { } from)
        {
            events = events.Where(e => e.Ts >= Time(from));
        }

        if (P("to") is { } to)
        {
            events = events.Where(e => e.Ts < Time(to));
        }

        if (P("s_wfrom") is { } window)
        {
            events = events.Where(e => e.Ts >= Time(window));
        }

        if (P("s_ts") is { } ts)
        {
            var key = (Time(ts), ulong.Parse(P("s_fid")!, CultureInfo.InvariantCulture), ulong.Parse(P("s_off")!, CultureInfo.InvariantCulture));
            events = events.Where(e => (e.Ts, e.FileId, e.FileOffset).CompareTo(key) < 0);
        }

        return [.. events.OrderByDescending(e => (e.Ts, e.FileId, e.FileOffset))];
    }

    private static object?[] Row(FakeEvent e)
    {
        var row = new object?[Columns.Length];
        for (var i = 0; i < row.Length; i++)
        {
            row[i] = Columns[i] switch
            {
                "_time" => e.Ts,
                "file.id" => e.FileId,
                "file.offset" => e.FileOffset,
                "message" => e.Message,
                "log.level" => e.Severity,
                "source.ip" => IPAddress.Parse("::ffff:10.0.0.5"),
                "extras" => new Dictionary<string, string> { ["k"] = "v" },
                SearchPlan.RawValidColumn => (byte)1,
                _ => string.Empty,
            };
        }

        return row;
    }
}

/// <summary>One event of a search stream as a test reads it.</summary>
internal sealed record StreamEvent(string Name, JsonElement Data);

internal static class SearchStreams
{
    /// <summary>Reads a whole <c>text/event-stream</c> body: every event with its name and its JSON data.</summary>
    public static async Task<List<StreamEvent>> ReadAsync(HttpResponseMessage response)
    {
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync();
        var events = new List<StreamEvent>();
        foreach (var block in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = block.Split('\n');
            var name = lines.Single(l => l.StartsWith("event: ", StringComparison.Ordinal))["event: ".Length..];
            var data = lines.Single(l => l.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..];
            using var document = JsonDocument.Parse(data);
            events.Add(new StreamEvent(name, document.RootElement.Clone()));
        }

        return events;
    }

    public static string Utf8(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}
