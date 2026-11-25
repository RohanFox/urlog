using System.Net;
using System.Text.Json;
using Urlog.Api.Search;
using Urlog.Core.Audit;
using Urlog.Core.Metadata;
using Urlog.Query;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Api.Tests;

/// <summary>
/// The search routes of a case against a stand-in event store: what the stream sends and in what shape, the
/// cursor's walk and its limits, the export's lines, the error answers, the slots, and the audit row of every
/// request that carried a query text. What the statements return from a real store is tested there.
/// </summary>
public sealed class SearchEndpointTests
{
    private const string Password = "correct horse battery";
    private const string Marker = "zq-result-marker-7731";

    private static readonly DateTime First = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(ApiClient Owner, long CaseId)> CaseAsync(ApiFactory factory, int events = 0, long undated = 0, long future = 0)
    {
        await factory.AddUserAsync("alice", Password);
        var owner = factory.Client();
        await owner.LoginAsync("alice", Password);
        var created = await ApiClient.ReadAsync<JsonElement>(await owner.PostAsync("/api/v1/cases", new { name = "incident" }));
        var caseId = created.GetProperty("id").GetInt64();

        // Three events on every millisecond that has any, across two files: equal times are the rule, not the exception.
        for (var i = 0; i < events; i++)
        {
            factory.Search.Events.Add(new FakeEvent(First.AddMilliseconds(i / 3), (ulong)(1 + (i % 2)), (ulong)(i * 100), $"{Marker} line {i}", i % 5 == 0 ? "error" : "info"));
        }

        if (events > 0)
        {
            await using var db = factory.Open();
            await db.Cases.Where(c => c.Id == caseId).ExecuteUpdateAsync(
                s => s.SetProperty(c => c.TsMin, First).SetProperty(c => c.TsMax, First.AddMilliseconds((events - 1) / 3)).SetProperty(c => c.UndatedRows, undated).SetProperty(c => c.FutureRows, future), Ct);
        }

        return (owner, caseId);
    }

    private static async Task<List<JsonElement>> QueryRowsAsync(ApiFactory factory)
    {
        await using var db = factory.Open();
        var rows = await db.Audit.AsNoTracking().Where(a => a.Action == AuditActions.Query).OrderBy(a => a.Seq).ToListAsync(Ct);
        return [.. rows.Select(r =>
        {
            Assert.Equal(AuditTargets.Query, r.TargetType);
            using var document = JsonDocument.Parse(r.DetailsJson);
            return document.RootElement.Clone();
        })];
    }

    [Fact]
    public async Task ASearchSendsMetaFirstThenTheThreeKindsThenDone()
    {
        await using var factory = new ApiFactory();
        var (owner, caseId) = await CaseAsync(factory, events: 30, undated: 212, future: 3);
        using var _ = owner;

        var response = await owner.PostAsync($"/api/v1/cases/{caseId}/search", new { query = "" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await SearchStreams.ReadAsync(response);

        // meta first and done last; the three between them in whatever order they were ready, each naming its kind.
        Assert.Equal("meta", events[0].Name);
        Assert.Equal("done", events[^1].Name);
        Assert.Equal(["counts", "histogram", "page"], events.Skip(1).Take(3).Select(e => e.Name).Order());
        Assert.All(events, e => Assert.Equal(e.Name, e.Data.GetProperty("kind").GetString()));

        // No zone named: UTC. No range given: the case's recorded span, and the two counts beside it.
        var meta = events[0].Data;
        Assert.Equal("UTC", meta.GetProperty("tz").GetString());
        Assert.Equal("events", meta.GetProperty("result").GetString());
        Assert.Equal("2026-03-01T00:00:00.000Z", meta.GetProperty("from").GetString());
        Assert.Equal("2026-03-01T00:00:00.010Z", meta.GetProperty("to").GetString());
        Assert.Equal((212, 3), (meta.GetProperty("span").GetProperty("undated_events").GetInt64(), meta.GetProperty("span").GetProperty("future_events").GetInt64()));

        var page = events.Single(e => e.Name == "page").Data;
        Assert.Equal(30, page.GetProperty("rows").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("next").ValueKind);
        var columns = page.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToList();
        Assert.Contains("raw", columns);
        Assert.Contains("extras", columns);
        Assert.Equal("2026-03-01T00:00:00.009Z", page.GetProperty("rows")[0][columns.IndexOf("_time")].GetString());
        Assert.Equal("10.0.0.5", page.GetProperty("rows")[0][columns.IndexOf("source.ip")].GetString());

        Assert.Equal(30, events.Single(e => e.Name == "histogram").Data.GetProperty("total").GetInt64());
        Assert.Equal(30, events.Single(e => e.Name == "counts").Data.GetProperty("total").GetInt64());

        // Every statement ran for the route's case and no other.
        Assert.All(factory.Search.Calls, c => Assert.Equal((ulong)caseId, c.CaseId));

        var row = Assert.Single(await QueryRowsAsync(factory));
        Assert.Equal(("spl", "search", "ok", 30), (row.GetProperty("language").GetString(), row.GetProperty("request").GetString(), row.GetProperty("outcome").GetString(), row.GetProperty("rows").GetInt64()));
        Assert.Equal(QueryAudit.Sha256(string.Empty), row.GetProperty("sha256").GetString());
        Assert.Equal("UTC", row.GetProperty("tz").GetString());
    }

    [Fact]
    public async Task TheHistogramOfAnUnfilteredSearchComesFromTheStoredOneWhenTheCaseHasIt()
    {
        await using var factory = new ApiFactory();
        var (owner, caseId) = await CaseAsync(factory, events: 30);
        using var _ = owner;
        long fileId;
        await using (var db = factory.Open())
        {
            // A span of a day, so the search cuts it in minutes or more: the stored rows are minutes.
            await db.Cases.Where(c => c.Id == caseId).ExecuteUpdateAsync(s => s.SetProperty(c => c.TsMin, First.AddDays(-1)), Ct);
            var file = new EvidenceFile
            {
                CaseId = caseId,
                Sha256 = new string('c', 64),
                Size = 10,
                OriginalName = "a.log",
                StoredPath = "a.log",
                Source = FileSource.Web,
                Uploader = "alice",
                ReceivedAt = DateTime.UtcNow,
                Encoding = "utf8",
                State = FileState.Parsed,
                ParseGeneration = 2,
                HistogramGeneration = 2,
            };
            db.Files.Add(file);
            await db.SaveChangesAsync(Ct);
            fileId = file.Id;
            db.Jobs.Add(new Job { CaseId = caseId, FileId = fileId, Kind = JobKind.Reparse, Generation = 2, State = JobState.Done, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync(Ct);
        }

        async Task<(string Source, long Total, string Kinds)> SearchAsync(string query, object? range = null)
        {
            factory.Search.Calls.Clear();
            var response = await owner.PostAsync($"/api/v1/cases/{caseId}/search", range ?? new { query });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var histogram = (await SearchStreams.ReadAsync(response)).Single(e => e.Name == "histogram").Data;
            return (histogram.GetProperty("source").GetString()!, histogram.GetProperty("total").GetInt64(), string.Join(",", factory.Search.Calls.Select(c => c.Kind).Where(k => k is "histogram" or "stored")));
        }

        // Every parsed file has its minutes: one statement, the stored one, through the view and for the route's case.
        Assert.Equal(("stored", 30, "stored"), await SearchAsync(string.Empty));
        var statement = factory.Search.Calls.Single(c => c.Kind == "stored");
        Assert.Equal((ulong)caseId, statement.CaseId);
        Assert.Contains("FROM case_histogram_v WHERE case_id = {case:UInt64}", statement.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("FROM case_histogram ", statement.Sql, StringComparison.Ordinal);

        // A range of its own that narrows nothing is served the same way; a search that narrows is read from the events.
        Assert.Equal(("stored", 30, "stored"), await SearchAsync(string.Empty, new { query = string.Empty, from = "2026-02-28T12:00:00Z", to = "2026-03-02T00:00:00Z" }));
        Assert.Equal(("query", 30, "histogram"), await SearchAsync("host=web-01"));

        // The stored read fails: the events are read and the analyst has a histogram all the same.
        factory.Search.Failures["stored"] = new QueryFailedException(60, "Code: 60. UNKNOWN_TABLE", new InvalidOperationException());
        Assert.Equal(("query", 30, "stored,histogram"), await SearchAsync(string.Empty));
        factory.Search.Failures.Clear();

        // A file whose committed parse wrote no minutes (it was parsed before they were kept): the seam is not asked.
        await using (var db = factory.Open())
        {
            await db.Files.Where(f => f.Id == fileId).ExecuteUpdateAsync(s => s.SetProperty(f => f.HistogramGeneration, (int?)null), Ct);
        }

        Assert.Equal(("query", 30, "histogram"), await SearchAsync(string.Empty));

        // And one whose minutes are of an older generation than its committed parse.
        await using (var db = factory.Open())
        {
            await db.Files.Where(f => f.Id == fileId).ExecuteUpdateAsync(s => s.SetProperty(f => f.HistogramGeneration, (int?)1), Ct);
        }

        Assert.Equal(("query", 30, "histogram"), await SearchAsync(string.Empty));
    }

    [Fact]
    public async Task TheRequestMayNameAnotherZoneAndAnUnknownOneIsRefused()
    {
        await using var factory = new ApiFactory();
        var (owner, caseId) = await CaseAsync(factory, events: 6);
        using var _ = owner;

        // An absolute time without an offset is read in the display zone.
        var events = await SearchStreams.ReadAsync(await owner.PostAsync($"/api/v1/cases/{caseId}/search", new { query = "", from = "2026-03-01T01:00:00", to = "2026-03-01T03:00:00", tz = "+02:00" }));
        Assert.Equal("+02:00", events[0].Data.GetProperty("tz").GetString());
        Assert.Equal("2026-02-28T23:00:00.000Z", events[0].Data.GetProperty("from").GetString());

        var unknown = await owner.PostAsync($"/api/v1/cases/{caseId}/search", new { query = "", tz = "Mars/Olympus" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("unknown timezone", await unknown.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        // A member the route does not know is refused, limits included: none comes from a request.
        var extra = await owner.PostAsync($"/api/v1/cases/{caseId}/search", new { query = "", page_size = 5000 });
        Assert.Equal(HttpStatusCode.BadRequest, extra.StatusCode);
    }

    [Fact]
    public async Task ACaseWithoutASpanAnswersAnEmptyResultNotAnError()
    {
        await using var factory = new ApiFactory();
        var (owner, caseId) = await CaseAsync(factory);
        using var _ = owner;

        var events = await SearchStreams.ReadAsync(await owner.PostAsync($"/api/v1/cases/{caseId}/search", new { query = "" }));
        Assert.Equal(JsonValueKind.Null, events[0].Data.GetProperty("span").GetProperty("from").ValueKind);
        Assert.Equal(0, events.Single(e => e.Name == "page").Data.GetProperty("rows").GetArrayLength());
        Assert.Equal("done", events[^1].Name);
    }

    [Fact]
    public async Task AWalkByCursorSeesEveryKeyOnceInOrderWithEqualTimesAcrossPages()
    {
        await using var factory = new ApiFactory(new Dictionary<string, string?> { ["Urlog:Query:PageSize"] = "50" });
        var (owner, caseId) = await CaseAsync(factory, events: 430);
        using var _ = owner;

        var seen = new List<(string Ts, ulong File, ulong Offset)>();
        string? cursor = null;
        var requests = 0;
        do
        {
            object body = cursor is null ? new { query = "" } : new { query = "", cursor };
            var events = await SearchStreams.ReadAsync(await owner.PostAsync($"/api/v1/cases/{caseId}/search", body));
            var page = events.Single(e => e.Name == "page").Data;

            // A page request is the page and the end, nothing else.
            Assert.Equal(cursor is null ? 5 : 2, events.Count);
            Assert.Equal(seen.Count, page.GetProperty("first_row").GetInt32());
            var columns = page.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToList();
            foreach (var row in page.GetProperty("rows").EnumerateArray())
            {
                seen.Add((row[columns.IndexOf("_time")].GetString()!, row[columns.IndexOf("file.id")].GetUInt64(), row[columns.IndexOf("file.offset")].GetUInt64()));
            }

            cursor = page.GetProperty("next").GetString();
            requests++;
        }
        while (cursor is not null && requests < 50);

        // 430 events, 3 to a millisecond: every page boundary but a few falls inside a run of equal times.
        Assert.Equal(430, seen.Count);
        Assert.Equal(430, seen.Distinct().Count());
        var expected = factory.Search.Events.OrderByDescending(e => (e.Ts, e.FileId, e.FileOffset)).Select(e => (e.FileId, e.FileOffset)).ToList();
        Assert.Equal(expected, seen.Select(s => (s.File, s.Offset)));

        // One audit row a request, the pages with the ordinal of their first row.
        var rows = await QueryRowsAsync(factory);
        Assert.Equal(requests, rows.Count);
        Assert.Equal("search", rows[0].GetProperty("request").GetString());
        Assert.Equal(("page", 50), (rows[1].GetProperty("request").GetString(), rows[1].GetProperty("first_row").GetInt32()));
    }

    [Fact]
    public async Task TheWalkStopsAtThePagingLimitAndADeeperCursorIsRefused()
    {
        await using var factory = new ApiFactory(new Dictionary<string, string?> { ["Urlog:Query:PageSize"] = "50", ["Urlog:Query:MaxPagedRows"] = "100" });
        var (owner, caseId) = await CaseAsync(factory, events: 300);
        using var _ = owner;

        var first = (await SearchStreams.ReadAsync(await owner.PostAsync($"/api/v1/cases/{caseId}/search", new { query = "" }))).Single(e => e.Name == "page").Data;
        Assert.False(first.GetProperty("capped").GetBoolean());
        var second = (await SearchStreams.ReadAsync(await owner.PostAsync($"/api/v1/cases/{caseId}/search", new { query = "", cursor = first.GetProperty("next").GetString() }))).Single(e => e.Name == "page").Data;

        // The 100th row is the last one paged: no cursor after it, and the page says why.
        Assert.Equal(50, second.GetProperty("rows").GetArrayLength());
        Assert.True(second.GetProperty("capped").GetBoolean());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("next").ValueKind);
        Assert.Contains("narrow the search", second.GetProperty("message").GetString(), StringComparison.Ordinal);

        // A cursor that claims to stand at the limit, written by hand: refused with the same advice.
        var honest = Cursor.Decode(first.GetProperty("next").GetString()!, (ulong)caseId, string.Empty);
        var deeper = await owner.PostAsync($"/api/v1/cases/{caseId}/search", new { query = "", cursor = (honest with { Sent = 100 }).Encode() });
        Assert.Equal(HttpStatusCode.BadRequest, deeper.StatusCode);
        var problem = await ApiClient.ReadAsync<JsonElement>(deeper);
        Assert.Equal("cursor_limit", problem.GetProperty("code").GetString());
        Assert.Contains("narrow the search", problem.GetProperty("title").GetString(), StringComparison.Ordinal);

        // A cursor of another search, and one that is no cursor.
        var other = await owner.PostAsync($"/api/v1/cases/{caseId}/search", new { query = "error", cursor = first.GetProperty("next").GetString() });
        Assert.Equal("bad_cursor", (await ApiClient.ReadAsync<JsonElement>(other)).GetProperty("code").GetString());
        var junk = await owner.PostAsync($"/api/v1/cases/{caseId}/search", new { query = "", cursor = "not-a-cursor" });
        Assert.Equal("bad_cursor", (await ApiClient.ReadAsync<JsonElement>(junk)).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("yesterday", null)]
    [InlineData(null, "-24")]
    [InlineData("-3x", null)]
    [InlineData("2026-13-45T00:00:00Z", null)]
    public async Task ARangeThatIsNotATimeIs400WithTheParsersMessageAndIsAudited(string? from, string? to)
    {
        await using var factory = new ApiFactory();
        var (owner, caseId) = await CaseAsync(factory, events: 3);
        using var _ = owner;
        var bad = from ?? to!;

        // The range of a search is part of its address in the interface, so a person can type one by hand.
        var response = await owner.PostAsync($"/api/v1/cases/{caseId}/search", new { query = "", from, to });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ApiClient.ReadAsync<JsonElement>(response);
        Assert.Equal("bad_request", problem.GetProperty("code").GetString());
        Assert.Contains(bad, problem.GetProperty("title").GetString(), StringComparison.Ordinal);
        Assert.Empty(factory.Search.Calls);

        var row = Assert.Single(await QueryRowsAsync(factory));
        Assert.Equal("invalid", row.GetProperty("outcome").GetString());

        // The export reads the same range from its address.
        var export = await owner.GetAsync($"/api/v1/cases/{caseId}/search/export?format=csv&query=&{(from is null ? "to" : "from")}={Uri.EscapeDataString(bad)}");
        Assert.Equal(HttpStatusCode.BadRequest, export.StatusCode);
        Assert.Equal("bad_request", (await ApiClient.ReadAsync<JsonElement>(export)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task AQueryThatDoesNotParseIs400WithTheSpanAndIsAudited()
    {
        await using var factory = new ApiFactory();
        var (owner, caseId) = await CaseAsync(factory, events: 3);
        using var _ = owner;

        var response = await owner.PostAsync($"/api/v1/cases/{caseId}/search", new { query = "host=web-01 | eval x=1" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ApiClient.ReadAsync<JsonElement>(response);
        Assert.Equal("query_syntax", problem.GetProperty("code").GetString());
        Assert.Contains("not supported in v1", problem.GetProperty("title").GetString(), StringComparison.Ordinal);
        Assert.Equal(14, problem.GetProperty("offset").GetInt32());
        Assert.Empty(factory.Search.Calls);

        var row = Assert.Single(await QueryRowsAsync(factory));
        Assert.Equal(("invalid", "host=web-01 | eval x=1"), (row.GetProperty("outcome").GetString(), row.GetProperty("text").GetString()));
        Assert.Contains("not supported in v1", row.GetProperty("reason").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAggregationIsOneTableEvent()
    {
        await using var factory = new ApiFactory();
        var (owner, caseId) = await CaseAsync(factory, events: 3);
        using var _ = owner;
        factory.Search.OnQuery = q => new QueryTable([.. q.Columns], [["web-01", "12"], ["web-02", "7"]], TimeSpan.Zero);

        var events = await SearchStreams.ReadAsync(await owner.PostAsync($"/api/v1/cases/{caseId}/search", new { query = "| stats count by host" }));
        Assert.Equal(["meta", "table", "done"], events.Select(e => e.Name));
        Assert.Equal("stats", events[0].Data.GetProperty("result").GetString());
        var table = events[1].Data;
        Assert.Equal(["host.name", "count"], table.GetProperty("columns").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal("12", table.GetProperty("rows")[0][1].GetString());
        Assert.False(table.GetProperty("truncated").GetBoolean());
        Assert.Equal(2, table.GetProperty("total_rows").GetInt32());
    }

    [Fact]
    public async Task AnAggregationCutToTheTableLimitSaysSoWithTheFullRowCount()
    {
        await using var factory = new ApiFactory(new Dictionary<string, string?> { ["Urlog:Query:MaxTableRows"] = "3" });
        var (owner, caseId) = await CaseAsync(factory, events: 3);
        using var _ = owner;
        factory.Search.OnQuery = q => new QueryTable([.. q.Columns], [.. Enumerable.Range(0, 7).Select(i => new[] { $"web-{i:00}", (100 - i).ToString(System.Globalization.CultureInfo.InvariantCulture) })], TimeSpan.Zero);

        var events = await SearchStreams.ReadAsync(await owner.PostAsync($"/api/v1/cases/{caseId}/search", new { query = "| stats count by host" }));
        var table = events.Single(e => e.Name == "table").Data;

        // The first three rows in the result's own order, and the event says there are seven.
        Assert.Equal(["web-00", "web-01", "web-02"], table.GetProperty("rows").EnumerateArray().Select(r => r[0].GetString()));
        Assert.True(table.GetProperty("truncated").GetBoolean());
        Assert.Equal(7, table.GetProperty("total_rows").GetInt32());

        // The audit row counts what was sent.
        Assert.Equal(3, Assert.Single(await QueryRowsAsync(factory)).GetProperty("rows").GetInt64());
    }

    [Fact]
    public async Task AStatementThatFailsMidStreamIsAnErrorEventAndTheRowSaysHow()
    {
        await using var factory = new ApiFactory();
        var (owner, caseId) = await CaseAsync(factory, events: 3);
        using var _ = owner;
        factory.Search.Failures["page"] = new QueryFailedException(159, "Code: 159. DB::Exception: Timeout exceeded: elapsed 60.0 seconds", new InvalidOperationException());

        var events = await SearchStreams.ReadAsync(await owner.PostAsync($"/api/v1/cases/{caseId}/search", new { query = "" }));
        Assert.Equal("meta", events[0].Name);
        Assert.Equal("error", events[^1].Name);
        Assert.Equal("query_timeout", events[^1].Data.GetProperty("code").GetString());
        Assert.DoesNotContain(events, e => e.Name == "done");
        Assert.Equal("timed_out", Assert.Single(await QueryRowsAsync(factory)).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task TheRawTabAnswersATableAndTheGatesReasonIsTheMessage()
    {
        await using var factory = new ApiFactory(new Dictionary<string, string?> { ["Urlog:Query:MaxTableRows"] = "2" });
        var (owner, caseId) = await CaseAsync(factory);
        using var _ = owner;
        factory.Search.OnSql = _ => new QueryRows(["host", "c"], [["web-01", 12UL], ["web-02", 7UL], ["web-03", 1UL]], false, TimeSpan.Zero);

        var ok = await owner.PostAsync($"/api/v1/cases/{caseId}/sql", new { sql = "SELECT host, count() AS c FROM events_v GROUP BY host" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var table = await ApiClient.ReadAsync<JsonElement>(ok);
        Assert.Equal(2, table.GetProperty("rows").GetArrayLength());
        Assert.True(table.GetProperty("truncated").GetBoolean());
        Assert.Equal(12, table.GetProperty("rows")[0][1].GetInt64());
        Assert.Equal((ulong)caseId, factory.Search.Calls.Single().CaseId);

        const string Reason = "SETTINGS is not allowed in a query";
        factory.Search.Failures["sql"] = new RawSqlRejectedException(Reason);
        var refused = await owner.PostAsync($"/api/v1/cases/{caseId}/sql", new { sql = "SELECT 1 SETTINGS max_threads = 64" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var problem = await ApiClient.ReadAsync<JsonElement>(refused);
        Assert.Equal(("query_rejected", Reason), (problem.GetProperty("code").GetString(), problem.GetProperty("detail").GetString()));

        factory.Search.Failures["sql"] = new QueryFailedException(60, "Code: 60. DB::Exception: Unknown table expression identifier 'nope'", new InvalidOperationException());
        var failed = await owner.PostAsync($"/api/v1/cases/{caseId}/sql", new { sql = "SELECT * FROM nope" });
        Assert.Equal("query_error", (await ApiClient.ReadAsync<JsonElement>(failed)).GetProperty("code").GetString());

        factory.Search.Failures["sql"] = new QueryFailedException(159, "Code: 159. DB::Exception: Timeout exceeded", new InvalidOperationException());
        Assert.Equal(HttpStatusCode.GatewayTimeout, (await owner.PostAsync($"/api/v1/cases/{caseId}/sql", new { sql = "SELECT sleep(3)" })).StatusCode);

        var rows = await QueryRowsAsync(factory);
        Assert.Equal(["ok", "rejected", "failed", "timed_out"], rows.Select(r => r.GetProperty("outcome").GetString()));
        Assert.All(rows, r => Assert.Equal("sql", r.GetProperty("language").GetString()));
        Assert.Equal(Reason, rows[1].GetProperty("reason").GetString());
        Assert.Equal(QueryAudit.Sha256("SELECT 1 SETTINGS max_threads = 64"), rows[1].GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task AnExportNamesItsSensitiveFieldsAndIsCutAtTheLimit()
    {
        await using var factory = new ApiFactory(new Dictionary<string, string?> { ["Urlog:Query:MaxExportRows"] = "4" });
        var (owner, caseId) = await CaseAsync(factory, events: 9);
        using var _ = owner;

        var csv = await owner.GetAsync($"/api/v1/cases/{caseId}/search/export?format=csv&query=");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        Assert.Equal("9", csv.Headers.GetValues("X-Urlog-Rows").Single());
        Assert.Equal("true", csv.Headers.GetValues("X-Urlog-Truncated").Single());
        var lines = (await csv.Content.ReadAsStringAsync(Ct)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        // The listing's columns hold an address and the message: named before the header. Then four rows, not five.
        // By default every row also carries the file and offset it came from and its raw line.
        Assert.Equal("# sensitive fields: source.ip, message, raw", lines[0]);
        Assert.Equal("ts,host,program,severity,source_ip,message,file_id,file_offset,raw", lines[1]);
        Assert.Equal(6, lines.Length);
        Assert.StartsWith("2026-03-01T00:00:00.002Z,web-01,nginx,", lines[2], StringComparison.Ordinal);

        var json = await owner.GetAsync($"/api/v1/cases/{caseId}/search/export?format=json&query=");
        var objects = (await json.Content.ReadAsStringAsync(Ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("{\"sensitive_fields\":[\"source.ip\",\"message\",\"raw\"]}", objects[0]);
        Assert.Equal(5, objects.Length);
        Assert.Equal("10.0.0.5", JsonDocument.Parse(objects[1]).RootElement.GetProperty("source_ip").GetString());

        // No sensitive column, no such line.
        factory.Search.Export = (["host", "count"], [["web-01", 12UL]]);
        var plain = await owner.GetAsync($"/api/v1/cases/{caseId}/search/export?format=csv&query=" + Uri.EscapeDataString("| stats count by host"));
        Assert.Equal("host.name,count\r\nweb-01,12\r\n", await plain.Content.ReadAsStringAsync(Ct));

        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync($"/api/v1/cases/{caseId}/search/export?format=xml&query=")).StatusCode);
        var rows = await QueryRowsAsync(factory);
        Assert.Equal([("export", 4L), ("export", 4L), ("export", 1L)], rows.Select(r => (r.GetProperty("request").GetString()!, r.GetProperty("rows").GetInt64())));
    }

    [Fact]
    public async Task TheAuditRowOfAnExportCarriesTheHashOfTheBytesThatWereSent()
    {
        await using var factory = new ApiFactory(new Dictionary<string, string?> { ["Urlog:Query:MaxExportRows"] = "7" });
        var (owner, caseId) = await CaseAsync(factory, events: 9);
        using var _ = owner;

        var csv = await (await owner.GetAsync($"/api/v1/cases/{caseId}/search/export?format=csv&query=")).Content.ReadAsByteArrayAsync(Ct);
        var json = await (await owner.GetAsync($"/api/v1/cases/{caseId}/search/export?format=json&query=" + Uri.EscapeDataString("| head 5"))).Content.ReadAsByteArrayAsync(Ct);
        var raw = await (await owner.GetAsync($"/api/v1/cases/{caseId}/sql/export?format=csv&sql=" + Uri.EscapeDataString("SELECT * FROM events_v"))).Content.ReadAsByteArrayAsync(Ct);

        var rows = await QueryRowsAsync(factory);
        Assert.Equal(3, rows.Count);
        foreach (var (row, file) in rows.Zip([csv, json, raw]))
        {
            // The file in the caller's hands is the file the row describes: same length, same SHA-256.
            Assert.Equal(file.Length, row.GetProperty("bytes").GetInt64());
            Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(file)), row.GetProperty("sent_sha256").GetString());
            Assert.True(row.GetProperty("complete").GetBoolean());
            Assert.DoesNotContain(Marker, row.GetRawText(), StringComparison.Ordinal);
        }

        // Nine events against a limit of seven: cut. A head of five: the result's own end. Nine raw rows: cut.
        Assert.Equal([(7L, true), (5L, false), (7L, true)], rows.Select(r => (r.GetProperty("rows").GetInt64(), r.GetProperty("cut").GetBoolean())));

        // A search is no export: its row has none of the four.
        await owner.PostAsync($"/api/v1/cases/{caseId}/search", new { query = "" });
        var search = (await QueryRowsAsync(factory))[^1];
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null, JsonValueKind.Null), (search.GetProperty("bytes").ValueKind, search.GetProperty("sent_sha256").ValueKind, search.GetProperty("complete").ValueKind));
    }

    [Fact]
    public async Task AnExportItsClientLeavesRecordsWhatWasSentAndThatItIsIncomplete()
    {
        await using var factory = new ApiFactory();
        var (owner, caseId) = await CaseAsync(factory, events: 3);
        using var _ = owner;

        // Rows long enough that the writer flushes while the client is still there, then the read stands still.
        factory.Search.Export = (["host", "message"], [.. Enumerable.Range(0, 2000).Select(i => new object?[] { "web-01", new string('x', 100) + i })]);
        factory.Search.StreamHoldAfter = 1500;

        using var leaving = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/cases/{caseId}/search/export?format=csv&query=" + Uri.EscapeDataString("| stats count by host"));
        request.Headers.Add(ApiFactory.ClientAddressHeader, owner.Address);
        request.Headers.Add("Cookie", owner.Cookie);
        var response = await owner.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, leaving.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The client reads what comes (a writer that is not read from waits at its flush), and goes once 1,500
        // rows are written and the read of the event store stands still.
        var body = await response.Content.ReadAsStreamAsync(Ct);
        var reading = Task.Run(
            async () =>
            {
                var buffer = new byte[8192];
                try
                {
                    while (await body.ReadAsync(buffer, leaving.Token) > 0)
                    {
                    }
                }
                catch (Exception e) when (e is OperationCanceledException or IOException)
                {
                    // The test left.
                }
            },
            Ct);
        await factory.Search.StreamHeld.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await leaving.CancelAsync();
        await reading;
        response.Dispose();

        List<JsonElement> rows = [];
        for (var i = 0; i < 300 && rows.Count == 0; i++)
        {
            await Task.Delay(10, Ct);
            rows = await QueryRowsAsync(factory);
        }

        var row = Assert.Single(rows);
        Assert.Equal(("abandoned", "export", false), (row.GetProperty("outcome").GetString(), row.GetProperty("request").GetString(), row.GetProperty("complete").GetBoolean()));

        // What was written up to that point: 1,500 rows of the 2,000, their bytes and the hash of those bytes.
        Assert.Equal(1500, row.GetProperty("rows").GetInt64());
        var expected = new System.Text.StringBuilder("host.name,count\r\n");
        for (var i = 0; i < 1500; i++)
        {
            expected.Append("web-01,").Append('x', 100).Append(i).Append("\r\n");
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(expected.ToString());
        Assert.Equal(bytes.Length, row.GetProperty("bytes").GetInt64());
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), row.GetProperty("sent_sha256").GetString());
        Assert.False(row.GetProperty("cut").GetBoolean());
    }

    [Fact]
    public async Task CellsAreWrittenUnchangedUnlessTheCallerAsksForTheFormulaGuard()
    {
        await using var factory = new ApiFactory();
        var (owner, caseId) = await CaseAsync(factory, events: 1);
        using var _ = owner;
        factory.Search.Export = (["host", "user_agent"], [["web-01", "=cmd|' /C calc'!A0"], ["web-02", "+1,\"quoted\""], ["web-03", "curl/8.5"]]);
        var path = $"/api/v1/cases/{caseId}/search/export?query=" + Uri.EscapeDataString("| stats count by host");

        var asStored = await (await owner.GetAsync(path + "&format=csv")).Content.ReadAsStringAsync(Ct);
        Assert.Equal("host.name,count\r\nweb-01,=cmd|' /C calc'!A0\r\nweb-02,\"+1,\"\"quoted\"\"\"\r\nweb-03,curl/8.5\r\n", asStored);

        var guarded = await (await owner.GetAsync(path + "&format=csv&formula_guard=true")).Content.ReadAsStringAsync(Ct);
        var lines = guarded.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("# altered:", lines[0], StringComparison.Ordinal);
        Assert.Equal(["host.name,count", "web-01,'=cmd|' /C calc'!A0", "web-02,\"'+1,\"\"quoted\"\"\"", "web-03,curl/8.5"], lines.Skip(1));

        // JSON is never altered, whatever the caller asks.
        var json = await (await owner.GetAsync(path + "&format=json&formula_guard=true")).Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("altered", json, StringComparison.Ordinal);
        Assert.Contains("\"=cmd|", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AUserAtTheirLimitIsAnswered429AndDoesNotStopASecondUser()
    {
        await using var factory = new ApiFactory(new Dictionary<string, string?> { ["Urlog:Query:SlotWait"] = "00:00:00.200" });
        var (owner, caseId) = await CaseAsync(factory, events: 3);
        using var _ = owner;
        await factory.AddUserAsync("bob", Password);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsync($"/api/v1/cases/{caseId}/members", new { user = "bob", role = "ro" })).StatusCode);
        using var bob = factory.Client();
        await bob.LoginAsync("bob", Password);

        // Alice's first search holds its three statements; a raw query takes her fourth slot.
        factory.Search.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = owner.PostAsync($"/api/v1/cases/{caseId}/search", new { query = "" });
        var fourth = owner.PostAsync($"/api/v1/cases/{caseId}/sql", new { sql = "SELECT 1" });
        var slots = factory.Services.GetService(typeof(QuerySlots)) as QuerySlots;
        Assert.NotNull(slots);
        for (var i = 0; i < 200 && slots.InUse < 4; i++)
        {
            await Task.Delay(10, Ct);
        }

        Assert.Equal(4, slots.InUse);

        // Her next request finds no slot within the wait: 429 with Retry-After, before any byte of a stream.
        var refused = await owner.PostAsync($"/api/v1/cases/{caseId}/search", new { query = "error" });
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("1", refused.Headers.GetValues("Retry-After").Single());
        Assert.Equal("busy", (await ApiClient.ReadAsync<JsonElement>(refused)).GetProperty("code").GetString());

        // Bob is not held up by her: his search takes its three slots at once.
        var bobs = bob.PostAsync($"/api/v1/cases/{caseId}/search", new { query = "" });
        for (var i = 0; i < 200 && slots.InUse < 7; i++)
        {
            await Task.Delay(10, Ct);
        }

        Assert.Equal(7, slots.InUse);

        factory.Search.Gate.SetResult();
        Assert.Equal("done", (await SearchStreams.ReadAsync(await held))[^1].Name);
        Assert.Equal("done", (await SearchStreams.ReadAsync(await bobs))[^1].Name);
        Assert.Equal(HttpStatusCode.OK, (await fourth).StatusCode);
        Assert.Equal(0, slots.InUse);

        var rows = await QueryRowsAsync(factory);
        Assert.Equal("busy", rows.Single(r => r.GetProperty("text").GetString() == "error").GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task AClientThatLeavesIsRecordedAsAbandoned()
    {
        await using var factory = new ApiFactory();
        var (owner, caseId) = await CaseAsync(factory, events: 3);
        using var _ = owner;
        factory.Search.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var leaving = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/cases/{caseId}/search") { Content = System.Net.Http.Json.JsonContent.Create(new { query = "" }) };
        request.Headers.Add(ApiFactory.ClientAddressHeader, owner.Address);
        request.Headers.Add("Cookie", owner.Cookie);
        request.Headers.Add(Urlog.Api.Auth.CsrfMiddleware.HeaderName, owner.Csrf);
        var pending = owner.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, leaving.Token);
        for (var i = 0; i < 200 && factory.Search.Calls.Count < 3; i++)
        {
            await Task.Delay(10, Ct);
        }

        Assert.Equal(3, factory.Search.Calls.Count);
        // The meta event is out, so the response has begun; the three statements are held. The client goes.
        var response = await pending;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await leaving.CancelAsync();
        response.Dispose();

        List<JsonElement> rows = [];
        for (var i = 0; i < 300 && rows.Count == 0; i++)
        {
            await Task.Delay(10, Ct);
            rows = await QueryRowsAsync(factory);
        }

        Assert.Equal("abandoned", Assert.Single(rows).GetProperty("outcome").GetString());
        Assert.Equal(0, ((QuerySlots)factory.Services.GetService(typeof(QuerySlots))!).InUse);
    }

    [Fact]
    public async Task NoAuditRowHoldsACellOfAResult()
    {
        await using var factory = new ApiFactory(new Dictionary<string, string?> { ["Urlog:Query:PageSize"] = "20" });
        var (owner, caseId) = await CaseAsync(factory, events: 60);
        using var _ = owner;
        factory.Search.OnSql = _ => new QueryRows(["message"], [[Marker + " from sql"]], false, TimeSpan.Zero);
        factory.Search.OnQuery = q => new QueryTable([.. q.Columns], [[Marker, "3"]], TimeSpan.Zero);

        var first = (await SearchStreams.ReadAsync(await owner.PostAsync($"/api/v1/cases/{caseId}/search", new { query = "" }))).Single(e => e.Name == "page").Data;
        Assert.Contains(Marker, first.GetRawText(), StringComparison.Ordinal);
        await owner.PostAsync($"/api/v1/cases/{caseId}/search", new { query = "", cursor = first.GetProperty("next").GetString() });
        await owner.PostAsync($"/api/v1/cases/{caseId}/search", new { query = "| stats count by message" });
        await owner.PostAsync($"/api/v1/cases/{caseId}/sql", new { sql = "SELECT message FROM events_v LIMIT 1" });
        await owner.GetAsync($"/api/v1/cases/{caseId}/search/export?format=json&query=");
        await owner.GetAsync($"/api/v1/cases/{caseId}/sql/export?format=csv&sql=" + Uri.EscapeDataString("SELECT message FROM events_v"));

        await using var db = factory.Open();
        var rows = await db.Audit.AsNoTracking().Where(a => a.Action == AuditActions.Query).ToListAsync(Ct);
        Assert.Equal(6, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.DoesNotContain(Marker, r.DetailsJson, StringComparison.Ordinal);
            Assert.DoesNotContain("10.0.0.5", r.DetailsJson, StringComparison.Ordinal);
            Assert.Equal(caseId, r.TargetId);
            Assert.Equal("alice", r.Actor);
        });

        // The chain still verifies with the query rows in it.
        Assert.True((await AuditVerifier.VerifyAsync(factory.MetadataPath, Ct)).Ok);
    }
}
