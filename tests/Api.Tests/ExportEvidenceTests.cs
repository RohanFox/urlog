using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Urlog.Core.Audit;
using Urlog.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Api.Tests;

/// <summary>
/// An export of events is evidence: by default every row says which file and offset it came from and carries its
/// raw line, and a second small export lists those files with their hashes. The brief form and a query's own
/// table are still there for whoever wants less.
/// </summary>
public sealed class ExportEvidenceTests
{
    private const string Password = "correct horse battery";

    private static readonly DateTime First = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(ApiFactory Factory, ApiClient Alice, long CaseId, EvidenceFile A, EvidenceFile B)> SceneAsync(Dictionary<string, string?>? settings = null)
    {
        var factory = new ApiFactory(settings);
        await factory.AddUserAsync("alice", Password);
        var alice = factory.Client();
        await alice.LoginAsync("alice", Password);
        var created = await ApiClient.ReadAsync<JsonElement>(await alice.PostAsync("/api/v1/cases", new { name = "incident" }));
        var caseId = created.GetProperty("id").GetInt64();
        var a = await factory.AddFileAsync(caseId, "access.log", "first file");
        var b = await factory.AddFileAsync(caseId, "error.log", "second file", archivePath: "bundle.zip!web-02/error.log");

        // Six events, newest last: four of the first file, two of the second.
        for (var i = 0; i < 6; i++)
        {
            factory.Search.Events.Add(new FakeEvent(First.AddSeconds(i), (ulong)(i < 4 ? a.Id : b.Id), (ulong)(i * 100), $"line {i}"));
        }

        await using var db = factory.Open();
        await db.Cases.Where(c => c.Id == caseId).ExecuteUpdateAsync(s => s.SetProperty(c => c.TsMin, First).SetProperty(c => c.TsMax, First.AddSeconds(5)), Ct);
        return (factory, alice, caseId, a, b);
    }

    private static async Task<List<JsonElement>> AuditAsync(ApiFactory factory)
    {
        await using var db = factory.Open();
        var rows = await db.Audit.AsNoTracking().Where(a => a.Action == AuditActions.Query).OrderBy(a => a.Seq).ToListAsync(Ct);
        return [.. rows.Select(r =>
        {
            using var document = JsonDocument.Parse(r.DetailsJson);
            return document.RootElement.Clone();
        })];
    }

    private static async Task<string[]> LinesAsync(HttpResponseMessage answer)
    {
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        return (await answer.Content.ReadAsStringAsync(Ct)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public async Task ADefaultExportOfEventsCarriesTheFileTheOffsetAndTheRawLineOfEveryRow()
    {
        var (factory, alice, caseId, a, b) = await SceneAsync();
        await using var _ = factory;
        using var __ = alice;

        var lines = await LinesAsync(await alice.GetAsync($"/api/v1/cases/{caseId}/search/export?format=csv&query="));
        Assert.Equal("ts,host,program,severity,source_ip,message,file_id,file_offset,raw", lines[1]);
        Assert.Equal(2 + 6, lines.Length);
        // Newest first: the last event, of the second file, at its offset, with its line.
        Assert.EndsWith($",line 5,{b.Id},500,raw of line 5", lines[2], StringComparison.Ordinal);
        Assert.EndsWith($",line 0,{a.Id},0,raw of line 0", lines[^1], StringComparison.Ordinal);

        var json = (await (await alice.GetAsync($"/api/v1/cases/{caseId}/search/export?format=json&query=")).Content.ReadAsStringAsync(Ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var row = JsonDocument.Parse(json[1]);
        Assert.Equal((b.Id, 500, "raw of line 5"), (row.RootElement.GetProperty("file_id").GetInt64(), row.RootElement.GetProperty("file_offset").GetInt64(), row.RootElement.GetProperty("raw").GetString()));

        // The statement asks the store for them after the listing's own columns, in the listing's order.
        var sql = factory.Search.Calls.First(c => c.Kind == "stream").Sql;
        Assert.StartsWith("SELECT ts, host, program, severity, source_ip, message, file_id, file_offset, raw FROM events_v WHERE ", sql, StringComparison.Ordinal);
        Assert.All(await AuditAsync(factory), r => Assert.Equal("evidence", r.GetProperty("columns").GetString()));
    }

    [Fact]
    public async Task TheBriefFormIsTheSixColumnsAndATableIsExactlyWhatTheQueryNames()
    {
        var (factory, alice, caseId, _, _) = await SceneAsync();
        await using var __ = factory;
        using var ___ = alice;

        var brief = await LinesAsync(await alice.GetAsync($"/api/v1/cases/{caseId}/search/export?format=csv&query=&columns=brief"));
        Assert.Equal("# sensitive fields: source.ip, message", brief[0]);
        Assert.Equal("ts,host,program,severity,source_ip,message", brief[1]);
        Assert.StartsWith("SELECT ts, host, program, severity, source_ip, message FROM events_v WHERE ", factory.Search.Calls.Last(c => c.Kind == "stream").Sql, StringComparison.Ordinal);

        // A query that names its columns gets those and nothing added, whatever the parameter says.
        // As many rows as the scene has events: an export whose rows are not the counted ones is broken off.
        factory.Search.Export = (["host", "message"], [.. Enumerable.Range(0, 6).Select(i => new object?[] { "web-01", "line " + i })]);
        foreach (var columns in new[] { "", "&columns=evidence", "&columns=brief" })
        {
            var table = await LinesAsync(await alice.GetAsync($"/api/v1/cases/{caseId}/search/export?format=csv&query=" + Uri.EscapeDataString("| table host, message") + columns));
            Assert.Contains("host.name,message", table);
            var sql = factory.Search.Calls.Last(c => c.Kind == "stream").Sql;
            Assert.DoesNotContain(", file_id, file_offset, raw FROM events_v", sql, StringComparison.Ordinal);
        }

        Assert.Equal(["brief", "table", "table", "table"], (await AuditAsync(factory)).Select(r => r.GetProperty("columns").GetString()));

        // An aggregation has no columns of this kind to say anything about.
        factory.Search.Export = (["host", "count"], [["web-01", 6UL]]);
        await alice.GetAsync($"/api/v1/cases/{caseId}/search/export?format=csv&query=" + Uri.EscapeDataString("| stats count by host"));
        Assert.False((await AuditAsync(factory))[^1].TryGetProperty("columns", out var none));
        _ = none;
    }

    [Fact]
    public async Task AnUnknownChoiceOfColumnsIsRefusedBeforeAnythingRuns()
    {
        var (factory, alice, caseId, _, _) = await SceneAsync();
        await using var __ = factory;
        using var ___ = alice;

        var answer = await alice.GetAsync($"/api/v1/cases/{caseId}/search/export?format=csv&query=&columns=everything");
        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Contains("evidence", (await ApiClient.ReadAsync<JsonElement>(answer)).GetProperty("title").GetString(), StringComparison.Ordinal);
        Assert.Empty(factory.Search.Calls);
    }

    [Fact]
    public async Task TheFilesOfAResultAreListedWithNameHostSizeHashAndTheirShareOfTheRows()
    {
        var (factory, alice, caseId, a, b) = await SceneAsync();
        await using var _ = factory;
        using var __ = alice;
        await using (var db = factory.Open())
        {
            var host = new Host { CaseId = caseId, Name = "web-01", Timezone = "UTC" };
            db.Hosts.Add(host);
            await db.SaveChangesAsync(Ct);
            await db.Files.Where(f => f.Id == a.Id).ExecuteUpdateAsync(s => s.SetProperty(f => f.HostId, host.Id), Ct);
        }

        var answer = await alice.GetAsync($"/api/v1/cases/{caseId}/search/export/files?format=csv&query=");
        var lines = await LinesAsync(answer);
        Assert.Equal("text/csv", answer.Content.Headers.ContentType?.MediaType);
        Assert.Equal($"case-{caseId}-export-files.csv", answer.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal("2", answer.Headers.GetValues("X-Urlog-Rows").Single());
        Assert.Equal(
        [
            "file_id,name,host,size,sha256,rows",
            $"{a.Id},access.log,web-01,{a.Size},{a.Sha256},4",
            // A member of an archive is named by its path in it; a file without a host has none.
            $"{b.Id},bundle.zip!web-02/error.log,,{b.Size},{b.Sha256},2",
        ], lines);

        var json = (await (await alice.GetAsync($"/api/v1/cases/{caseId}/search/export/files?format=json&query=")).Content.ReadAsStringAsync(Ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var first = JsonDocument.Parse(json[0]);
        Assert.Equal((a.Id, a.Sha256, 4), (first.RootElement.GetProperty("file_id").GetInt64(), first.RootElement.GetProperty("sha256").GetString(), first.RootElement.GetProperty("rows").GetInt64()));

        // Its own kind of request on the trail, with the bytes and the hash of what was sent, like any export.
        var rows = await AuditAsync(factory);
        Assert.All(rows, r => Assert.Equal("export_files", r.GetProperty("request").GetString()));
        Assert.Equal(2, rows[0].GetProperty("rows").GetInt64());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await answer.Content.ReadAsByteArrayAsync(Ct))), rows[0].GetProperty("sent_sha256").GetString());
        Assert.DoesNotContain(a.Sha256, rows[0].GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFilesAreThoseOfTheRowsTheExportSendsWhenTheExportIsCut()
    {
        // The export is cut at four rows, newest first: two of the second file and two of the first.
        var (factory, alice, caseId, a, b) = await SceneAsync(new Dictionary<string, string?> { ["Urlog:Query:MaxExportRows"] = "4" });
        await using var _ = factory;
        using var __ = alice;

        var lines = await LinesAsync(await alice.GetAsync($"/api/v1/cases/{caseId}/search/export/files?format=csv&query="));
        Assert.EndsWith(",2", lines.Single(l => l.StartsWith($"{a.Id},", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.EndsWith(",2", lines.Single(l => l.StartsWith($"{b.Id},", StringComparison.Ordinal)), StringComparison.Ordinal);
        var sql = factory.Search.Calls.Single(c => c.Kind == "files").Sql;
        Assert.Contains("ORDER BY ts DESC, file_id DESC, file_offset DESC LIMIT {s_limit:UInt64}) GROUP BY file_id ORDER BY file_id", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAggregationHasNoFilesToListAndABadQueryIsRefusedAsOnTheExport()
    {
        var (factory, alice, caseId, _, _) = await SceneAsync();
        await using var __ = factory;
        using var ___ = alice;

        var aggregation = await alice.GetAsync($"/api/v1/cases/{caseId}/search/export/files?format=csv&query=" + Uri.EscapeDataString("| stats count by host"));
        Assert.Equal(HttpStatusCode.BadRequest, aggregation.StatusCode);
        Assert.Contains("aggregation", (await ApiClient.ReadAsync<JsonElement>(aggregation)).GetProperty("title").GetString(), StringComparison.Ordinal);

        var syntax = await alice.GetAsync($"/api/v1/cases/{caseId}/search/export/files?format=csv&query=" + Uri.EscapeDataString("host=web-01 | eval x=1"));
        Assert.Equal("query_syntax", (await ApiClient.ReadAsync<JsonElement>(syntax)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync($"/api/v1/cases/{caseId}/search/export/files?format=xml&query=")).StatusCode);
        Assert.Empty(factory.Search.Calls);
    }

    [Fact]
    public async Task AFileOfAnotherCaseIsNeverNamed()
    {
        // The store answers with an id the case does not hold (it cannot, through the view; this is the second lock):
        // the line has the id and the count and nothing of the other case's record.
        var (factory, alice, caseId, a, _) = await SceneAsync();
        await using var __ = factory;
        using var ___ = alice;
        var other = await ApiClient.ReadAsync<JsonElement>(await alice.PostAsync("/api/v1/cases", new { name = "another" }));
        var foreign = await factory.AddFileAsync(other.GetProperty("id").GetInt64(), "secret-name.log", "foreign");
        factory.Search.Events.Add(new FakeEvent(First.AddSeconds(3), (ulong)foreign.Id, 7, "x"));

        var lines = await LinesAsync(await alice.GetAsync($"/api/v1/cases/{caseId}/search/export/files?format=csv&query="));
        Assert.Contains($"{foreign.Id},,,,,1", lines);
        Assert.DoesNotContain(lines, l => l.Contains("secret-name", StringComparison.Ordinal) || l.Contains(foreign.Sha256, StringComparison.Ordinal));
        _ = a;
    }

    [Fact]
    public async Task TheAuditRowsOfAnExportAndOfItsFilesCarryTheSameQueryHashAndRange()
    {
        var (factory, alice, caseId, _, _) = await SceneAsync();
        await using var __ = factory;
        using var ___ = alice;

        var search = "format=csv&query=" + Uri.EscapeDataString("line") + "&from=2026-03-01T00:00:00Z&to=2026-03-02T00:00:00Z";
        await LinesAsync(await alice.GetAsync($"/api/v1/cases/{caseId}/search/export?{search}"));
        await LinesAsync(await alice.GetAsync($"/api/v1/cases/{caseId}/search/export/files?{search}"));
        await LinesAsync(await alice.GetAsync($"/api/v1/cases/{caseId}/search/export/files?format=csv&query=" + Uri.EscapeDataString("line 1")));

        var rows = await AuditAsync(factory);
        Assert.Equal(["export", "export_files", "export_files"], rows.Select(r => r.GetProperty("request").GetString()));
        Assert.Equal(64, rows[0].GetProperty("sha256").GetString()!.Length);
        foreach (var member in new[] { "sha256", "from", "to", "tz" })
        {
            Assert.Equal(rows[0].GetProperty(member).GetString(), rows[1].GetProperty(member).GetString());
        }

        // Another query is another hash.
        Assert.NotEqual(rows[0].GetProperty("sha256").GetString(), rows[2].GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task AListingShorterThanTheLimitIsReadInOneStatementOverThePinnedFiles()
    {
        var (factory, alice, caseId, a, b) = await SceneAsync();
        await using var __ = factory;
        using var ___ = alice;

        var lines = await LinesAsync(await alice.GetAsync($"/api/v1/cases/{caseId}/search/export?format=csv&query="));
        Assert.Equal(8, lines.Length);

        // What it reads is taken first; no cut is looked for; the rows come from those files at those generations.
        // Then what it read is taken again, to see that it stayed.
        Assert.Equal(["generations", "stream", "generations"], factory.Search.Calls.Select(c => c.Kind));
        var sql = factory.Search.Calls.Single(c => c.Kind == "stream").Sql;
        Assert.Contains($" AND (file_id, parse_generation) IN (({a.Id}, 1), ({b.Id}, 1)) ORDER BY ", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("s_cts", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AListingLongerThanTheLimitIsCutByAKeyFoundFromTheKeysAlone()
    {
        var (factory, alice, caseId, a, b) = await SceneAsync(new Dictionary<string, string?> { ["Urlog:Query:MaxExportRows"] = "4" });
        await using var __ = factory;
        using var ___ = alice;

        var answer = await alice.GetAsync($"/api/v1/cases/{caseId}/search/export?format=csv&query=");
        var lines = await LinesAsync(answer);
        Assert.Equal(6, lines.Length);
        Assert.Equal("6", answer.Headers.GetValues("X-Urlog-Rows").Single());
        Assert.Equal("true", answer.Headers.GetValues("X-Urlog-Truncated").Single());

        Assert.Equal(["generations", "cut", "stream", "generations"], factory.Search.Calls.Select(c => c.Kind));
        var pin = $" AND (file_id, parse_generation) IN (({a.Id}, 1), ({b.Id}, 1))";

        // The first step sorts three key columns and nothing else, and skips to the last row of the file.
        var cut = factory.Search.Calls.Single(c => c.Kind == "cut").Sql;
        Assert.StartsWith("SELECT ts, file_id, file_offset FROM events_v WHERE ", cut, StringComparison.Ordinal);
        Assert.EndsWith(pin + " ORDER BY ts DESC, file_id DESC, file_offset DESC LIMIT 1 OFFSET {s_skip:UInt64}", cut, StringComparison.Ordinal);
        Assert.DoesNotContain("raw", cut, StringComparison.Ordinal);
        Assert.DoesNotContain("message", cut, StringComparison.Ordinal);

        // The second reads the rows that are not later than that key, in the same order, from the same files.
        var stream = factory.Search.Calls.Single(c => c.Kind == "stream").Sql;
        Assert.Contains(pin + " AND NOT (ts < {s_cts:DateTime64(3)} OR (ts = {s_cts:DateTime64(3)} AND (file_id < {s_cfid:UInt64} OR (file_id = {s_cfid:UInt64} AND file_offset < {s_coff:UInt64}))))"
            + " ORDER BY ts DESC, file_id DESC, file_offset DESC LIMIT {s_limit:UInt64}", stream, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExportWhoseRowsAreNotTheCountedOnesIsBrokenOffAndAuditedAsFailed()
    {
        var (factory, alice, caseId, _, _) = await SceneAsync();
        await using var __ = factory;
        using var ___ = alice;

        // Six rows were counted; the second step finds five, as when a file of the result is parsed again in between.
        factory.Search.Export = (["ts", "host", "program", "severity", "source_ip", "message", "file_id", "file_offset", "raw"],
            [.. Enumerable.Range(0, 5).Select(i => new object?[] { First.AddSeconds(i), "web-01", "nginx", string.Empty, null, "line", 1UL, (ulong)i, "line" })]);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var answer = await alice.GetAsync($"/api/v1/cases/{caseId}/search/export?format=csv&query=");
            await answer.Content.ReadAsByteArrayAsync(Ct);
        });

        // The server writes the row after it has broken off the download, so the client can see the break first.
        var rows = await AuditAsync(factory);
        for (var i = 0; i < 300 && rows.Count == 0; i++)
        {
            await Task.Delay(10, Ct);
            rows = await AuditAsync(factory);
        }

        var row = Assert.Single(rows);
        Assert.Equal("failed", row.GetProperty("outcome").GetString());
        Assert.False(row.GetProperty("complete").GetBoolean());
        Assert.Equal("the evidence of the result changed while it was exported: 6 rows were counted and 5 were read", row.GetProperty("reason").GetString());
    }
}
