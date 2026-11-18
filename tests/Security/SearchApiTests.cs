using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Urlog.Api.Search;
using Urlog.Core.Audit;
using Urlog.Core.Auth;
using Urlog.Core.Ingest;
using Urlog.Core.Jobs;
using Urlog.Core.Metadata;
using Urlog.Query;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Urlog.Security.Tests;

/// <summary>
/// The search routes of the API over the real event store, as the query account with the deployed profile: the
/// raw tab refuses the gate's hostile corpus with the gate's reasons, a user outside a case reads nothing of it
/// through any route, a listing is walked by cursor without a gap or a duplicate, the windowed read returns what
/// the plain statement returns, a client that leaves stops its statement, and every request leaves its audit row.
/// </summary>
public sealed class SearchApiTests(ClickHouseFixture ch, ITestOutputHelper output) : IClassFixture<ClickHouseFixture>
{
    private const string Password = "correct horse battery";

    private static readonly DateTimeOffset January = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset March = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The host with alice owning cases 1 and 2 and mallory owning case 3, the ids the fixture seeded events for;
    /// every case's recorded span is January and February 2026.
    /// </summary>
    private async Task<(Host Api, Client Alice, Client Mallory)> StartAsync(IDictionary<string, string?>? settings = null)
    {
        var api = new Host(ch, settings);
        await api.AddUserAsync("alice", Password);
        await api.AddUserAsync("mallory", Password);
        var alice = await api.LoginAsync("alice", Password);
        var mallory = await api.LoginAsync("mallory", Password);
        Assert.Equal(HttpStatusCode.Created, (await alice.PostAsync("/api/v1/cases", new { name = "one" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await alice.PostAsync("/api/v1/cases", new { name = "two" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await mallory.PostAsync("/api/v1/cases", new { name = "three" })).StatusCode);
        await using var db = api.Open();
        await db.Cases.ExecuteUpdateAsync(s => s.SetProperty(c => c.TsMin, January.UtcDateTime).SetProperty(c => c.TsMax, March.UtcDateTime.AddMilliseconds(-1)), Ct);
        return (api, alice, mallory);
    }

    private static async Task<List<(string Name, JsonElement Data)>> EventsAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        var events = new List<(string, JsonElement)>();
        foreach (var block in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = block.Split('\n');
            using var document = JsonDocument.Parse(lines[1]["data: ".Length..]);
            events.Add((lines[0]["event: ".Length..], document.RootElement.Clone()));
        }

        return events;
    }

    private static async Task<List<AuditRow>> QueryRowsAsync(Host api)
    {
        await using var db = api.Open();
        return await db.Audit.AsNoTracking().Where(a => a.Action == AuditActions.Query).OrderBy(a => a.Seq).ToListAsync(Ct);
    }

    [Fact]
    public async Task TheRawRouteRefusesTheHostileCorpusWithTheGatesReasonAndAuditsEachRefusal()
    {
        var (api, alice, _) = await StartAsync();
        await using var host = api;
        var corpus = RawSqlGateTests.Hostile().Select(r => (What: r.Data.Item1, Sql: r.Data.Item2, Reason: r.Data.Item3)).ToList();
        foreach (var (what, sql, reason) in corpus)
        {
            var response = await alice.PostAsync("/api/v1/cases/2/sql", new { sql });
            var body = await response.Content.ReadAsStringAsync(Ct);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{what}: {(int)response.StatusCode} {body}");
            using var problem = JsonDocument.Parse(body);
            Assert.Equal("query_rejected", problem.RootElement.GetProperty("code").GetString());

            // The message the analyst reads is the gate's own.
            Assert.Contains(reason, problem.RootElement.GetProperty("detail").GetString(), StringComparison.OrdinalIgnoreCase);
        }

        var rows = await QueryRowsAsync(api);
        output.WriteLine($"{corpus.Count} hostile texts refused through POST /sql, {rows.Count} audit rows");
        Assert.Equal(corpus.Count, rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            using var details = JsonDocument.Parse(rows[i].DetailsJson);
            Assert.Equal("rejected", details.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(QueryAudit.Sha256(corpus[i].Sql), details.RootElement.GetProperty("sha256").GetString());
            Assert.Equal(corpus[i].Sql, details.RootElement.GetProperty("text").GetString());
            Assert.Equal(2, rows[i].TargetId);
        }
    }

    [Fact]
    public async Task TheBenignShapesAnswerAndSeeTheRoutesCaseAlone()
    {
        var (api, alice, _) = await StartAsync();
        await using var host = api;
        foreach (var row in RawSqlGateTests.Benign())
        {
            var response = await alice.PostAsync("/api/v1/cases/2/sql", new { sql = row.Data.Item2 });
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{row.Data.Item1}: {await response.Content.ReadAsStringAsync(Ct)}");
        }

        // Alice is a member of case 1 too. The raw route of case 2 still shows case 2 and nothing else.
        var all = await alice.ReadAsync(await alice.PostAsync("/api/v1/cases/2/sql", new { sql = "SELECT case_id, count() AS c FROM events_v GROUP BY case_id" }));
        var only = Assert.Single(all.GetProperty("rows").EnumerateArray());
        Assert.Equal((2, ClickHouseFixture.RowsPerCase), (only[0].GetInt32(), only[1].GetInt32()));
        var other = await alice.ReadAsync(await alice.PostAsync("/api/v1/cases/2/sql", new { sql = "SELECT count() FROM events_v WHERE case_id = 1" }));
        Assert.Equal(0, other.GetProperty("rows")[0][0].GetInt32());
    }

    [Fact]
    public async Task AUserOutsideTheCaseReadsNothingOfItThroughAnyRoute()
    {
        var (api, alice, mallory) = await StartAsync();
        await using var host = api;
        var export = "/api/v1/cases/2/search/export?format=csv&query=";
        var sqlExport = "/api/v1/cases/2/sql/export?format=csv&sql=" + Uri.EscapeDataString("SELECT * FROM events_v");

        // The case's owner reads it through every route.
        foreach (var scenario in UnauthorisedUserTests.Scenarios())
        {
            var events = await EventsAsync(await alice.PostAsync("/api/v1/cases/2/search", new { query = scenario.Data.Item1 }));
            Assert.Equal("done", events[^1].Name);
            var answered = events.Single(e => e.Name is "page" or "table").Data.GetProperty("rows").GetArrayLength();
            Assert.True(answered > 0, scenario.Data.Item1);
        }

        Assert.Contains("GET /index.html 200", await (await alice.GetAsync(export)).Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Contains("GET /index.html 200", await (await alice.GetAsync(sqlExport)).Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        // Mallory is not a member: 404 on every route, with the body of a case that does not exist.
        var unknown = await (await mallory.PostAsync("/api/v1/cases/4242/search", new { query = "" })).Content.ReadAsStringAsync(Ct);
        foreach (var scenario in UnauthorisedUserTests.Scenarios())
        {
            var refused = await mallory.PostAsync("/api/v1/cases/2/search", new { query = scenario.Data.Item1 });
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
            Assert.Equal(unknown, await refused.Content.ReadAsStringAsync(Ct));
        }

        Assert.Equal(HttpStatusCode.NotFound, (await mallory.PostAsync("/api/v1/cases/2/sql", new { sql = "SELECT * FROM events_v" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mallory.GetAsync(export)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mallory.GetAsync(sqlExport)).StatusCode);

        // From her own case she cannot name the other one: not in the query language, not in SQL, not in an export.
        var named = await mallory.PostAsync("/api/v1/cases/3/search", new { query = "index=2" });
        Assert.Equal(HttpStatusCode.BadRequest, named.StatusCode);
        Assert.Contains("not an allowed case", await named.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        var raw = await mallory.ReadAsync(await mallory.PostAsync("/api/v1/cases/3/sql", new { sql = "SELECT count() FROM events_v WHERE case_id = 2" }));
        Assert.Equal(0, raw.GetProperty("rows")[0][0].GetInt32());
        var everything = await mallory.ReadAsync(await mallory.PostAsync("/api/v1/cases/3/sql", new { sql = "SELECT DISTINCT case_id FROM events_v" }));
        Assert.Equal([3], everything.GetProperty("rows").EnumerateArray().Select(r => r[0].GetInt32()));
        var exported = await (await mallory.GetAsync("/api/v1/cases/3/sql/export?format=csv&sql=" + Uri.EscapeDataString("SELECT case_id FROM events_v WHERE case_id = 2"))).Content.ReadAsStringAsync(Ct);
        Assert.Equal("case_id\r\n", exported);

        // No statement of hers reached case 2, and none of her refusals at the door left a query row.
        var rows = await QueryRowsAsync(api);
        Assert.DoesNotContain(rows, r => r.Actor == "mallory" && r.TargetId == 2);
    }

    [Fact]
    public async Task AListingIsWalkedByCursorWithoutAGapOrADuplicateOnEqualTimes()
    {
        // Three more files in case 1, all in March, each with a row on the same 200 instants.
        await using var ingest = ch.Ingest();
        var store = new EventStoreAdmin(ingest);
        foreach (var file in new ulong[] { 11, 12, 13 })
        {
            await ch.InsertAsync(1, file, 1, 3, 200);
            await store.CommitFileGenerationAsync(file, 1, Ct);
        }

        var (api, alice, _) = await StartAsync(new Dictionary<string, string?> { ["Urlog:Query:PageSize"] = "50" });
        await using var host = api;
        foreach (var query in new[] { "", "| sort host", "host=web-01 | sort -_time" })
        {
            var seen = new List<(string Ts, long File, long Offset)>();
            string? cursor = null;
            var pages = 0;
            do
            {
                object body = cursor is null ? new { query, from = "2026-03-01T00:00:00Z", to = "2026-04-01T00:00:00Z" } : new { query, cursor };
                var page = (await EventsAsync(await alice.PostAsync("/api/v1/cases/1/search", body))).Single(e => e.Name == "page").Data;
                var columns = page.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToList();
                foreach (var row in page.GetProperty("rows").EnumerateArray())
                {
                    seen.Add((row[columns.IndexOf("_time")].GetString()!, row[columns.IndexOf("file.id")].GetInt64(), row[columns.IndexOf("file.offset")].GetInt64()));
                }

                cursor = page.GetProperty("next").GetString();

                // A file committed while the walk is under way: its rows may or may not be met, none of the others twice.
                if (pages == 2 && query.Length == 0)
                {
                    await ch.InsertAsync(1, 14, 1, 3, 200);
                    await store.CommitFileGenerationAsync(14, 1, Ct);
                }
            }
            while (cursor is not null && ++pages < 40);

            var expected = query.StartsWith("host=", StringComparison.Ordinal) ? 300 : 600;
            var earlier = seen.Where(s => s.File != 14).ToList();
            output.WriteLine($"'{query}': {seen.Count} rows in {pages + 1} pages, {seen.Count - earlier.Count} of the file committed mid-walk");
            Assert.Equal(seen.Count, seen.Distinct().Count());
            Assert.Equal(expected, earlier.Count);
            if (query.Length == 0)
            {
                // Newest first by the whole key: three rows share every instant, and the order is still total.
                Assert.Equal(earlier.OrderByDescending(s => s.Ts, StringComparer.Ordinal).ThenByDescending(s => s.File).ThenByDescending(s => s.Offset), earlier);
            }
        }
    }

    [Fact]
    public async Task TheWindowedReadReturnsWhatThePlainStatementReturns()
    {
        var backend = new QuerySearchBackend(ch.QueryClient());
        var runner = new SearchRunner(backend, 2);

        // A density far above the real one makes the first windows too short: every path is taken, down to the keys.
        var dense = new CaseDensity(200_000_000, TimeSpan.FromDays(59));
        var real = new CaseDensity(ClickHouseFixture.RowsPerCase, TimeSpan.FromDays(59));
        foreach (var query in new[] { "", "host=web-01", "program=nginx GET", "nosuchterm", "host=web-02 | where host=web-02" })
        {
            var plan = SearchPlan.Create(query, new TranspileOptions(2, January, March, TimeZoneInfo.Utc, March));
            var plain = await runner.PageAsync(plan, 30, null, null, Ct);
            foreach (var density in new[] { dense, real })
            {
                var windowed = await runner.PageAsync(plan, 30, null, density, Ct);
                Assert.Equal(Keys(plan, plain), Keys(plan, windowed));
                output.WriteLine($"'{query}': {plain.Rows.Count} rows; windowed in {windowed.Statements} statement(s)");
            }

            // And the second page after a cursor, the same way.
            if (plain.Rows.Count == 30)
            {
                var cursor = SearchRunner.NextCursor(plan, plain, "UTC", March, "b", 0);
                Assert.Equal(Keys(plan, await runner.PageAsync(plan, 30, cursor, null, Ct)), Keys(plan, await runner.PageAsync(plan, 30, cursor, dense, Ct)));
            }
        }
    }

    [Fact]
    public async Task TheCountsAndTheHistogramAreExactOverTheSearch()
    {
        var runner = new SearchRunner(new QuerySearchBackend(ch.QueryClient()), 2);
        var plan = SearchPlan.Create("", new TranspileOptions(2, January, March, TimeZoneInfo.Utc, March));
        var catalog = FieldCatalog.Embedded;

        var counts = await runner.CountsAsync(plan, [catalog.Resolve("host"), catalog.Resolve("program"), catalog.Resolve("status")], Ct);
        Assert.Equal(ClickHouseFixture.RowsPerCase, counts.Total);
        Assert.Equal([("web-01", 100L), ("web-02", 100L)], counts.Fields[0].Top.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => (t.Key, t.Value)));
        Assert.Equal([("nginx", 200L)], counts.Fields[1].Top.Select(t => (t.Key, t.Value)));

        // No seeded row has a status: a row without a value is no value of the field.
        Assert.Empty(counts.Fields[2].Top);
        Assert.Equal(ClickHouseFixture.RowsPerCase, (await runner.CountsAsync(plan, [], Ct)).Total);

        var narrowed = SearchPlan.Create("host=web-01", new TranspileOptions(2, January, March, TimeZoneInfo.Utc, March));
        var one = await runner.CountsAsync(narrowed, [catalog.Resolve("host")], Ct);
        Assert.Equal((100L, 1), (one.Total, one.Fields[0].Top.Count));

        var span = HistogramSpan.For(January, March);
        var histogram = await runner.HistogramAsync(plan, span, Ct);
        Assert.Equal(ClickHouseFixture.RowsPerCase, histogram.Sum(b => b.Counts.Values.Sum()));
        Assert.True(histogram.Count <= HistogramSpan.MaxBuckets);
        Assert.Equal(histogram.OrderBy(b => b.Start), histogram);
    }

    [Fact]
    public async Task AnExportStreamsTheCaseAndStopsAtItsLimit()
    {
        var (api, alice, _) = await StartAsync(new Dictionary<string, string?> { ["Urlog:Query:MaxExportRows"] = "150" });
        await using var host = api;

        var csv = await alice.GetAsync("/api/v1/cases/2/search/export?format=csv&query=");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Equal("200", csv.Headers.GetValues("X-Urlog-Rows").Single());
        Assert.Equal("true", csv.Headers.GetValues("X-Urlog-Truncated").Single());
        var lines = (await csv.Content.ReadAsStringAsync(Ct)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("# sensitive fields: source.ip, message", lines[0]);
        Assert.Equal(152, lines.Length);

        // Newest first, instants in UTC.
        Assert.StartsWith("2026-02-01T01:39:00.000Z,web-02,nginx,", lines[2], StringComparison.Ordinal);

        var json = await alice.GetAsync("/api/v1/cases/2/search/export?format=json&query=" + Uri.EscapeDataString("| table host, raw | head 7"));
        Assert.False(json.Headers.Contains("X-Urlog-Truncated"));
        var objects = (await json.Content.ReadAsStringAsync(Ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("{\"sensitive_fields\":[\"raw\"]}", objects[0]);
        Assert.Equal(8, objects.Length);
        using var first = JsonDocument.Parse(objects[1]);
        Assert.Equal("GET /index.html 200", first.RootElement.GetProperty("raw").GetString());
    }

    [Fact]
    public async Task TwoExportsOfOneSearchAreTheSameBytesAndTheAuditRowHasTheirHash()
    {
        // Three files in case 3, all in April, each with a row on the same 200 instants: every instant is a tie.
        await using var ingest = ch.Ingest();
        var store = new EventStoreAdmin(ingest);
        foreach (var file in new ulong[] { 31, 32, 33 })
        {
            await ch.InsertAsync(3, file, 1, 4, 200);
            await store.CommitFileGenerationAsync(file, 1, Ct);
        }

        var (api, _, mallory) = await StartAsync(new Dictionary<string, string?> { ["Urlog:Query:MaxExportRows"] = "500" });
        await using var host = api;
        const string Range = "&from=2026-04-01T00:00:00Z&to=2026-05-01T00:00:00Z";
        var searches = new[]
        {
            ("csv", "| table _time, file.id, file.offset, host"),
            ("json", "| table _time, file.id, file.offset, host"),
            ("csv", "host=web-01 | sort host"),
            ("csv", "| stats count by host | sort -count"),
            ("json", "| stats count, dc(host) as hosts by file.id"),
        };
        var hashes = new List<string>();
        foreach (var (format, query) in searches)
        {
            var path = $"/api/v1/cases/3/search/export?format={format}&query={Uri.EscapeDataString(query)}{Range}";
            var first = await (await mallory.GetAsync(path)).Content.ReadAsByteArrayAsync(Ct);
            var second = await (await mallory.GetAsync(path)).Content.ReadAsByteArrayAsync(Ct);
            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(first));
            Assert.Equal(hash, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(second)));
            hashes.Add(hash);
            hashes.Add(hash);
            output.WriteLine($"{format} '{query}': {first.Length:N0} bytes twice, sha256 {hash[..16]}...");
        }

        // The audit row of each export has the hash of the file that was received.
        var rows = await QueryRowsAsync(api);
        Assert.Equal(hashes, rows.Select(r => JsonDocument.Parse(r.DetailsJson).RootElement.GetProperty("sent_sha256").GetString()));

        // The listing came in its whole order: newest first, and within an instant by file and offset.
        var listing = System.Text.Encoding.UTF8.GetString(await (await mallory.GetAsync($"/api/v1/cases/3/search/export?format=csv&query={Uri.EscapeDataString(searches[0].Item2)}{Range}")).Content.ReadAsByteArrayAsync(Ct));
        var keys = listing.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(l => l.Split(',')).Select(c => (Ts: c[0], File: long.Parse(c[1], CultureInfo.InvariantCulture), Offset: long.Parse(c[2], CultureInfo.InvariantCulture))).ToList();
        Assert.Equal(500, keys.Count);
        Assert.Equal(keys.OrderByDescending(k => k.Ts, StringComparer.Ordinal).ThenByDescending(k => k.File).ThenByDescending(k => k.Offset), keys);
        Assert.Equal(3, keys.Count(k => k.Ts == keys[0].Ts));

        // The limit cut the listing (600 rows against 500) and the row says so; the aggregations are whole.
        var cut = rows.Select(r => JsonDocument.Parse(r.DetailsJson).RootElement.GetProperty("cut").GetBoolean()).ToList();
        Assert.Equal([true, true, true, true, false, false, false, false, false, false], cut);

        // Equal counts keep their places in the stream as well: web-01 before web-02, both 300.
        var table = (await EventsAsync(await mallory.PostAsync("/api/v1/cases/3/search", new { query = "| stats count by host | sort -count", from = "2026-04-01T00:00:00Z", to = "2026-05-01T00:00:00Z" }))).Single(e => e.Name == "table").Data;
        Assert.Equal([("web-01", "300"), ("web-02", "300")], table.GetProperty("rows").EnumerateArray().Select(r => (r[0].GetString()!, r[1].GetString()!)));
    }

    [Fact]
    public async Task EarliestLatestAndValuesChooseTheSameOnEveryRun()
    {
        // Three files in case 3, all in May, each with a row on the same 200 instants and offsets 0 to 199: every
        // instant is a tie between three rows that differ in their file, and the case has 200 distinct offsets,
        // twice the cap of values(f).
        await using var ingest = ch.Ingest();
        var store = new EventStoreAdmin(ingest);
        foreach (var file in new ulong[] { 41, 42, 43 })
        {
            await ch.InsertAsync(3, file, 1, 5, 200);
            await store.CommitFileGenerationAsync(file, 1, Ct);
        }

        var (api, _, mallory) = await StartAsync();
        await using var host = api;
        const string Range = "&from=2026-05-01T00:00:00Z&to=2026-06-01T00:00:00Z";
        var searches = new[]
        {
            "| stats earliest(file.id) as first by host",
            "| stats latest(file.id) as last by host",
            "| stats values(file.offset) as offsets, values(file.id) as files",
        };
        foreach (var query in searches)
        {
            foreach (var format in new[] { "csv", "json" })
            {
                var path = $"/api/v1/cases/3/search/export?format={format}&query={Uri.EscapeDataString(query)}{Range}";
                var first = await (await mallory.GetAsync(path)).Content.ReadAsByteArrayAsync(Ct);
                var second = await (await mallory.GetAsync(path)).Content.ReadAsByteArrayAsync(Ct);
                var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(first));
                Assert.Equal(hash, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(second)));
                output.WriteLine($"{format} '{query}': {first.Length:N0} bytes twice, sha256 {hash[..16]}...");
            }
        }

        async Task<JsonElement> TableAsync(string query) =>
            (await EventsAsync(await mallory.PostAsync("/api/v1/cases/3/search", new { query, from = "2026-05-01T00:00:00Z", to = "2026-06-01T00:00:00Z" }))).Single(e => e.Name == "table").Data;
        static List<string[]> Rows(JsonElement table) => [.. table.GetProperty("rows").EnumerateArray().Select(r => r.EnumerateArray().Select(c => c.GetString()!).ToArray())];

        // Which row: among the three on the first instant of a host the one of the lowest file, among the three on
        // its last instant the one of the highest, as the listing orders them.
        Assert.Equal([["web-01", "41"], ["web-02", "41"]], Rows(await TableAsync(searches[0])));
        Assert.Equal([["web-01", "43"], ["web-02", "43"]], Rows(await TableAsync(searches[1])));

        // Which values: ascending, and above the cap the hundred smallest, with the row marked.
        var values = await TableAsync(searches[2]);
        var cell = Rows(values)[0];
        Assert.Equal("[" + string.Join(", ", Enumerable.Range(0, Transpiler.ValuesCap)) + ", ...]", cell[0]);
        Assert.Equal("[41, 42, 43]", cell[1]);
        Assert.Equal([0], values.GetProperty("values_capped_rows").EnumerateArray().Select(i => i.GetInt32()));

        // A group at the cap is whole and not marked: web-01 has the hundred even offsets.
        var atCap = await TableAsync("| stats values(file.offset) as offsets by host");
        Assert.Equal("[" + string.Join(", ", Enumerable.Range(0, Transpiler.ValuesCap).Select(i => i * 2)) + "]", Rows(atCap)[0][1]);
        Assert.Empty(atCap.GetProperty("values_capped_rows").EnumerateArray());
    }

    [Fact]
    public async Task AnExportCutsAValuesCellToTheCapAndNamesTheColumnAtItsStart()
    {
        // The seed of case 2: 200 instants, a hundred a host, and a hundred distinct offsets (each in both months).
        var (api, alice, _) = await StartAsync();
        await using var host = api;
        const string Over = "| stats values(file.offset) as offsets, values(host) as hosts, values(_time) as seen";
        const string AtCap = "| stats values(_time) as seen by host";
        static string Path(string format, string query) => $"/api/v1/cases/2/search/export?format={format}&query={Uri.EscapeDataString(query)}";

        var csv = (await (await alice.GetAsync(Path("csv", Over))).Content.ReadAsStringAsync(Ct)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal($"# values capped: a cell with {Transpiler.ValuesCap} values in these columns may stand for more: seen", csv[0]);
        Assert.Equal("offsets,hosts,seen", csv[1]);
        Assert.StartsWith("\"[" + string.Join(",", Enumerable.Range(0, Transpiler.ValuesCap)) + "]\",", csv[2], StringComparison.Ordinal);
        Assert.Equal(3, csv.Length);

        var json = (await (await alice.GetAsync(Path("json", Over))).Content.ReadAsStringAsync(Ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("{\"values_capped\":[\"seen\"]}", json[0]);
        using var row = JsonDocument.Parse(json[1]);
        Assert.Equal(Enumerable.Range(0, Transpiler.ValuesCap), row.RootElement.GetProperty("offsets").EnumerateArray().Select(v => v.GetInt32()));
        Assert.Equal(Transpiler.ValuesCap, row.RootElement.GetProperty("seen").GetArrayLength());
        Assert.Equal(["web-01", "web-02"], row.RootElement.GetProperty("hosts").EnumerateArray().Select(v => v.GetString()!));

        // A group at the cap is whole: no line, a hundred values.
        var whole = (await (await alice.GetAsync(Path("csv", AtCap))).Content.ReadAsStringAsync(Ct)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("host.name,seen", whole[0]);
        Assert.Equal(Transpiler.ValuesCap, whole[1].Split("Z").Length - 1);
        var wholeJson = (await (await alice.GetAsync(Path("json", AtCap))).Content.ReadAsStringAsync(Ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("{\"host.name\":\"web-01\",", wholeJson[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AValuesQueryThatPassesTheMemoryLimitSaysSoAndNamesTheWayOut()
    {
        // The query account's profile with a memory limit no aggregation fits in.
        await using (var admin = ch.Admin())
        {
            await admin.ExecuteAsync("CREATE USER IF NOT EXISTS small_memory_probe IDENTIFIED WITH plaintext_password BY 'probe' SETTINGS PROFILE 'urlog_query', max_memory_usage = 400000", cancellationToken: Ct);
            await admin.ExecuteAsync("GRANT SELECT ON default.events_v TO small_memory_probe", cancellationToken: Ct);
        }

        var (api, alice, _) = await StartAsync(new Dictionary<string, string?> { ["Urlog:ClickHouse:QueryUser"] = "small_memory_probe", ["Urlog:ClickHouse:QueryPassword"] = "probe" });
        await using var host = api;
        async Task<JsonElement> ProblemAsync(string query)
        {
            var response = await alice.GetAsync("/api/v1/cases/2/search/export?format=csv&query=" + Uri.EscapeDataString(query));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            return problem.RootElement.Clone();
        }

        var values = await ProblemAsync("| stats values(raw) as lines by host");
        Assert.Equal("query_error", values.GetProperty("code").GetString());
        Assert.Equal(ClickHouseQueryClient.ValuesOutOfMemory, values.GetProperty("detail").GetString());
        Assert.Contains("dc(f)", ClickHouseQueryClient.ValuesOutOfMemory, StringComparison.Ordinal);
        Assert.Contains("narrow the search", ClickHouseQueryClient.ValuesOutOfMemory, StringComparison.Ordinal);

        // The same refusal through the stream, after its first event.
        var events = await EventsAsync(await alice.PostAsync("/api/v1/cases/2/search", new { query = "| stats values(raw) as lines by host" }));
        Assert.Equal(ClickHouseQueryClient.ValuesOutOfMemory, events.Single(e => e.Name == "error").Data.GetProperty("message").GetString());

        // Without values(f) the refusal is the server's own, with its code: there is nothing to advise.
        var other = await ProblemAsync("| stats dc(raw) as lines by host");
        Assert.Equal("query_error", other.GetProperty("code").GetString());
        Assert.Contains("MEMORY_LIMIT_EXCEEDED", other.GetProperty("detail").GetString(), StringComparison.Ordinal);

        // The audit row of the first export carries the explanation as its reason.
        var first = JsonDocument.Parse((await QueryRowsAsync(api))[0].DetailsJson).RootElement;
        Assert.Equal("failed", first.GetProperty("outcome").GetString());
        Assert.Equal(ClickHouseQueryClient.ValuesOutOfMemory, first.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task AClientThatLeavesStopsItsStatementAndIsRecordedAsAbandoned()
    {
        var (api, alice, _) = await StartAsync();
        await using var host = api;

        // About two seconds on the server: 200 rows, a hundredth of a second each.
        const string Slow = "SELECT count() FROM events_v WHERE sleepEachRow(0.01) = 0 AND 'abandoned-marker' != ''";
        using var leaving = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pending = alice.PostAndLeaveAsync("/api/v1/cases/2/sql", new { sql = Slow }, leaving.Token);
        await using var admin = ch.Admin();
        async Task<ulong> InFlightAsync() => Convert.ToUInt64(
            await admin.ExecuteScalarAsync("SELECT count() FROM system.processes WHERE user = 'urlog_query' AND query LIKE '%abandoned-marker%' AND query NOT LIKE '%system.processes%' AND query NOT LIKE 'EXPLAIN%'", cancellationToken: Ct),
            CultureInfo.InvariantCulture);

        for (var i = 0; i < 200 && await InFlightAsync() == 0; i++)
        {
            await Task.Delay(10, Ct);
        }

        Assert.Equal(1UL, await InFlightAsync());
        var left = System.Diagnostics.Stopwatch.StartNew();
        await leaving.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        for (var i = 0; i < 100 && await InFlightAsync() != 0; i++)
        {
            await Task.Delay(10, Ct);
        }

        output.WriteLine($"gone from system.processes {left.ElapsedMilliseconds} ms after the client left");
        Assert.Equal(0UL, await InFlightAsync());
        Assert.True(left.Elapsed < TimeSpan.FromSeconds(1.5), "the statement ran on after its client had gone");

        List<AuditRow> rows = [];
        for (var i = 0; i < 300 && rows.Count == 0; i++)
        {
            await Task.Delay(10, Ct);
            rows = await QueryRowsAsync(api);
        }

        using var details = JsonDocument.Parse(Assert.Single(rows).DetailsJson);
        Assert.Equal("abandoned", details.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(0, host.Services.GetRequiredService<QuerySlots>().InUse);
    }

    private static List<(string Ts, ulong File, ulong Offset)> Keys(SearchPlan plan, ListingPage page) =>
        [.. page.Rows.Select(r => (ClickHouseQueryClient.Text(r[plan.TsColumn]), Convert.ToUInt64(r[plan.FileIdColumn], CultureInfo.InvariantCulture), Convert.ToUInt64(r[plan.FileOffsetColumn], CultureInfo.InvariantCulture)))];

    /// <summary>The production host on a metadata file of its own, its query account pointed at the fixture's container.</summary>
    private sealed class Host(ClickHouseFixture ch, IDictionary<string, string?>? settings) : WebApplicationFactory<Program>
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("urlog-search-");
        private readonly string _storeId = StoreIdentity.NewId();

        private string MetadataPath => Path.Combine(_directory.FullName, "metadata.db");

        public MetadataStore Store => Services.GetRequiredService<MetadataStore>();

        public MetadataDb Open() => Store.CreateDbContext();

        public async Task AddUserAsync(string name, string password)
        {
            await using var db = Open();
            await UserOperations.CreateAsync(db, Store.Audit, new AuditActor("cli"), name, UserRole.Analyst, PasswordHasher.Hash(password), DateTime.UtcNow);
        }

        public async Task<Client> LoginAsync(string name, string password)
        {
            var client = new Client(CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false, BaseAddress = new Uri("https://localhost") }));
            await client.LoginAsync(name, password);
            return client;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Urlog:Metadata:Path", MetadataPath);
            builder.UseSetting("Urlog:Ingest:Enabled", "false");
            builder.UseSetting("Urlog:Ingest:EvidenceRoot", Path.Combine(_directory.FullName, "intake"));
            builder.UseSetting("Urlog:ClickHouse:Host", ch.Host);
            builder.UseSetting("Urlog:ClickHouse:HttpPort", ch.HttpPort.ToString(CultureInfo.InvariantCulture));
            builder.UseSetting("Urlog:ClickHouse:NativePort", ch.NativePort.ToString(CultureInfo.InvariantCulture));
            builder.UseSetting("Urlog:ClickHouse:RetryInterval", "00:00:00.050");
            foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
            {
                builder.UseSetting(key, value);
            }

            // Paired by hand, as the other harnesses do: the identity row on the file's side, the same id answered for the store.
            var id = _storeId;
            var path = MetadataPath;
            Task.Run(async () =>
            {
                var store = new MetadataStore(path);
                await store.MigrateAsync();
                await using var db = store.CreateDbContext();
                db.StoreIdentity.Add(new StoreIdentityRow { StoreId = id, CreatedAt = DateTime.UtcNow, Origin = StoreIdentityOrigin.Created });
                await db.SaveChangesAsync();
            }).GetAwaiter().GetResult();
            SqliteConnection.ClearAllPools();
            builder.ConfigureTestServices(services => services.AddSingleton<IStoreIdentitySource>(new Identity(id)));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                SqliteConnection.ClearAllPools();
                try
                {
                    _directory.Delete(recursive: true);
                }
                catch (IOException)
                {
                    // A straggling handle on Windows: the OS cleans the temp directory eventually.
                }
            }
        }

        private sealed class Identity(string id) : IStoreIdentitySource
        {
            public string Endpoint => "test-store";

            public Task<string?> ReadStoreIdentityAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(id);
        }
    }

    /// <summary>An HTTP client that carries its session cookie and CSRF token.</summary>
    private sealed class Client(HttpClient http)
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

        private string? _cookie;
        private string? _csrf;

        public async Task LoginAsync(string name, string password)
        {
            var response = await SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { name, password }, CancellationToken.None);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            _cookie = response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            _csrf = document.RootElement.GetProperty("csrf_token").GetString();
        }

        public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(HttpMethod.Get, path, null, CancellationToken.None);

        public Task<HttpResponseMessage> PostAsync(string path, object body) => SendAsync(HttpMethod.Post, path, body, CancellationToken.None);

        /// <summary>A request the test walks away from.</summary>
        public Task<HttpResponseMessage> PostAndLeaveAsync(string path, object body, CancellationToken leave) => SendAsync(HttpMethod.Post, path, body, leave);

#pragma warning disable CA1822 // reads as a call on the client that made the request
        public async Task<JsonElement> ReadAsync(HttpResponseMessage response)
#pragma warning restore CA1822
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, text);
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }

        private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(method, path);
            if (_cookie is not null)
            {
                request.Headers.Add("Cookie", _cookie);
            }

            if (_csrf is not null)
            {
                request.Headers.Add("X-CSRF-Token", _csrf);
            }

            if (body is not null)
            {
                request.Content = JsonContent.Create(body, options: Json);
            }

            return await http.SendAsync(request, cancellationToken);
        }
    }
}
