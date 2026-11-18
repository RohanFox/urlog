using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Urlog.Core;
using Urlog.Core.Ingest;
using Urlog.Core.Metadata;
using Urlog.Query;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Integration.Tests;

/// <summary>
/// The field statistics and the case span end to end: <c>urlog worker</c> parses the two source fixtures into one
/// case as the ingest account and computes the statistics behind them, and what the store then holds is what the
/// fixtures' expected rows say. The span of the case comes from the file records and keeps out times that are
/// no times.
/// </summary>
public sealed class FieldStatsWorkerTests(ClickHouseFixture ch, ITestOutputHelper output) : IClassFixture<ClickHouseFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Fixture(params string[] parts) => Path.Combine([RepoRoot(), "fixtures", .. parts]);

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "urlog.sln")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("urlog.sln was not found above the test binaries");
    }

    private static async Task<(EvidenceFile File, Job Job)> AddAsync(WorkerHarness h, long caseId, string name, byte[] content, string source, int? year = null)
    {
        var path = Path.Combine(h.Folder, name);
        await File.WriteAllBytesAsync(path, content, Ct);
        var added = await h.AddFileAsync(caseId, path, Convert.ToHexStringLower(SHA256.HashData(content)), source);
        if (year is { } y)
        {
            await using var db = h.Store.CreateDbContext();
            await db.Files.Where(f => f.Id == added.File.Id).ExecuteUpdateAsync(s => s.SetProperty(f => f.YearHint, (int?)y), Ct);
        }

        return added;
    }

    private static List<JsonElement> Expected(string source) =>
        [.. File.ReadLines(Fixture(source, "expected.jsonl")).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement)];

    [Fact]
    public async Task TheFixturesAsOneCaseGetTheCountsOfTheirExpectedRowsOnceHoweverOftenTheTaskRuns()
    {
        await using var h = await WorkerHarness.CreateAsync(ch);
        var @case = await h.AddCaseAsync("fixtures");
        var other = await h.AddCaseAsync("another case");
        var nginx = await AddAsync(h, @case.Id, "access.log", await File.ReadAllBytesAsync(Fixture("nginx", "sample.log"), Ct), "nginx");
        var syslog = await AddAsync(h, @case.Id, "messages", await File.ReadAllBytesAsync(Fixture("syslog", "sample.log"), Ct), "syslog", year: 2025);
        await AddAsync(h, other.Id, "other.log", Encoding.UTF8.GetBytes("203.0.113.99 - - [02/Mar/2026:00:00:00 +0000] \"GET /other HTTP/1.1\" 418 1 \"-\" \"probe\"\n"), "nginx");

        var worker = h.StartWorker("--once");
        Assert.True(await worker.WaitForExitAsync(300) == 0, worker.Output);
        output.WriteLine(worker.Output);
        Assert.Contains($"field statistics of case {@case.Id}:", worker.Output, StringComparison.Ordinal);

        var rows = (await h.JobAsync(nginx.Job.Id)).Rows + (await h.JobAsync(syslog.Job.Id)).Rows;
        var stats = await h.FieldStatsAsync(@case.Id);
        Assert.Equal(rows, stats.Single(r => r.Field.Length == 0).Count);
        Assert.Equal(rows, (await h.VisibleAsync(@case.Id)).Rows);

        // http.response.status_code -> 200 (n), 404 (n): the totals of the nginx fixture's expected rows. A syslog
        // line has no status and adds nothing, and 0 is not a value of this field.
        var expectedNginx = Expected("nginx");
        var statuses = expectedNginx.Select(e => e.TryGetProperty("http_status", out var s) ? s.GetInt32() : 0).Where(s => s != 0)
            .GroupBy(s => s).Select(g => (Value: g.Key.ToString(CultureInfo.InvariantCulture), Count: (long)g.Count())).OrderByDescending(g => g.Count).ThenBy(g => g.Value, StringComparer.Ordinal).ToList();
        var actual = stats.Where(r => r.Field == "http.response.status_code" && r.Value.Length > 0).Select(r => (r.Value, r.Count)).ToList();
        output.WriteLine("http.response.status_code: " + string.Join(", ", actual.Select(a => $"{a.Value} ({a.Count})")));
        Assert.Equal(statuses, actual);
        Assert.Contains(("200", 21L), actual);
        Assert.Contains(("404", 3L), actual);
        Assert.Equal(statuses.Sum(s => s.Count), stats.Single(r => r.Field == "http.response.status_code" && r.Value.Length == 0).Count);

        // The extras keys seen are the keys the two fixtures write with a value, each with its number of rows.
        var expectedKeys = expectedNginx.Concat(Expected("syslog"))
            .SelectMany(e => e.TryGetProperty("extras", out var x) ? x.EnumerateObject().Where(p => p.Value.GetString() is { Length: > 0 }).Select(p => p.Name) : [])
            .GroupBy(k => k, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (long)g.Count(), StringComparer.Ordinal);
        var catalog = FieldCatalog.Embedded;
        var columnFields = catalog.Fields.Where(f => f.Column is not null).Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        var seenKeys = stats.Where(r => r.Value.Length == 0 && r.Field.Length > 0 && !columnFields.Contains(r.Field)).ToDictionary(r => r.Field, r => r.Count, StringComparer.Ordinal);
        output.WriteLine("extras keys: " + string.Join(", ", seenKeys.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key} ({k.Value})")));
        Assert.Equal(expectedKeys.OrderBy(k => k.Key, StringComparer.Ordinal), seenKeys.OrderBy(k => k.Key, StringComparer.Ordinal));
        // A key the catalog does not list is named and counted, and none of its values is stored.
        var unlisted = seenKeys.Keys.Where(k => !catalog.Contains(k)).ToList();
        Assert.NotEmpty(unlisted);
        Assert.DoesNotContain(stats, r => unlisted.Contains(r.Field) && r.Value.Length > 0);
        // No value of a string column, and none of the request target.
        Assert.DoesNotContain(stats, r => r.Value.Length > 0 && r.Field is "user.name" or "url.path" or "user_agent.original" or "url.query" or "http.request.referrer" or "url.original");
        Assert.DoesNotContain(stats, r => r.Field.Length > 0 && r.Count == 0);

        // Nothing of the other case is in this one's set.
        Assert.DoesNotContain(stats, r => r.Value == "418");
        Assert.Contains(await h.FieldStatsAsync(other.Id), r => r.Value == "418");

        // One set: one computed_at, one row per (field, value); the case's state says so.
        Assert.Single(stats.Select(r => r.ComputedAt).Distinct());
        Assert.Equal(stats.Count, stats.Select(r => (r.Field, r.Value)).Distinct().Count());
        await using (var db = h.Store.CreateDbContext())
        {
            var state = await db.CaseStats.AsNoTracking().SingleAsync(s => s.CaseId == @case.Id, Ct);
            Assert.Equal((CaseStatsState.Ok, rows, (DateTime?)null), (state.State, state.Events, state.DirtySince));
            Assert.Equal(stats[0].ComputedAt, state.ComputedAt);
        }

        // The task again, twice: the same rows with the same counts, one row per (case, field, value), and a
        // later computed_at each time.
        var fields = FieldStats.TaskFields(catalog);
        var limits = new FieldStatsLimits(TimeSpan.FromSeconds(60), 4, 2_000_000_000);
        var store = h.EventStore();
        foreach (var later in new[] { stats[0].ComputedAt.AddSeconds(1), stats[0].ComputedAt.AddSeconds(2) })
        {
            await store.RefreshFieldStatsAsync((ulong)@case.Id, fields, limits, later, Ct);
            var again = await h.FieldStatsAsync(@case.Id);
            Assert.Equal(stats.Select(r => (r.Field, r.Value, r.Count, r.Distinct)), again.Select(r => (r.Field, r.Value, r.Count, r.Distinct)));
            Assert.Equal([later], again.Select(r => r.ComputedAt).Distinct());
        }
    }

    [Fact]
    public async Task ACaseWhoseFileHasRejectsOnLineOneDoesNotStartAtTheEpochAndQueryUsesItsSpan()
    {
        await using var h = await WorkerHarness.CreateAsync(ch);
        var @case = await h.AddCaseAsync("rejects first");
        var content = Encoding.UTF8.GetBytes(
            "this line is no access log line at all\n" +
            "203.0.113.7 - - [03/Mar/2026:10:00:00 +0000] \"GET /a HTTP/1.1\" 200 5 \"-\" \"ua\"\n" +
            "203.0.113.7 - - [05/Mar/2026:12:30:00 +0000] \"GET /b HTTP/1.1\" 404 5 \"-\" \"ua\"\n");
        var (file, job) = await AddAsync(h, @case.Id, "access.log", content, "nginx");

        var worker = h.StartWorker("--once");
        Assert.True(await worker.WaitForExitAsync(300) == 0, worker.Output);

        var done = await h.JobAsync(job.Id);
        Assert.Equal((JobState.Done, 1L), (done.State, done.Rejects));
        var record = await h.FileAsync(file.Id);
        var first = new DateTime(2026, 3, 3, 10, 0, 0, DateTimeKind.Utc);
        var last = new DateTime(2026, 3, 5, 12, 30, 0, DateTimeKind.Utc);
        Assert.Equal((first, last, 1L, 0L), (record.TsMin, record.TsMax, record.UndatedRows, record.FutureRows));
        // The rejected line is a row of the case, at the epoch, like before.
        await using (var admin = h.Admin())
        {
            var epoch = Convert.ToInt64(await admin.ExecuteScalarAsync($"SELECT countIf(ts = toDateTime64(0, 3, 'UTC')) FROM events WHERE case_id = {@case.Id}", cancellationToken: Ct), CultureInfo.InvariantCulture);
            Assert.Equal(1, epoch);
        }

        await using (var db = h.Store.CreateDbContext())
        {
            Assert.Equal(new CaseSpan(first, last, 1, 0), await CaseStatsOperations.SpanAsync(db, @case.Id, Ct));
        }

        // urlog query with the metadata file: the default range is that span, half-open, and the command says
        // what lies outside it. --sql prints the statement and its parameters and runs nothing.
        var query = await h.RunCliAsync(null, "query", "--db", h.Store.Path, "--case", @case.Id.ToString(CultureInfo.InvariantCulture), "--sql", "| stats count");
        output.WriteLine(query.ToString());
        Assert.Equal(0, query.ExitCode);
        Assert.Contains("2026-03-03 10:00:00.000", query.Output, StringComparison.Ordinal);
        Assert.Contains("2026-03-05 12:30:00.001", query.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("1970-01-01", query.Output, StringComparison.Ordinal);
        Assert.Contains("1 event(s) without a usable time", query.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASyslogFileReadWithAYearTooHighIsCountedAsFutureAndLeavesTheSpanAlone()
    {
        await using var h = await WorkerHarness.CreateAsync(ch);
        var @case = await h.AddCaseAsync("wrong year");
        var year = DateTime.UtcNow.Year;
        // RFC 3164 lines carry no year. The good file is read with last year, the other with a guess two years ahead.
        var good = Encoding.UTF8.GetBytes("<34>Mar  1 10:00:00 web-01 sshd[1]: session opened for user a\n<34>Mar  2 11:00:00 web-01 sshd[1]: session closed for user a\n");
        var wrong = Encoding.UTF8.GetBytes("<34>Mar  3 10:00:00 web-02 sshd[2]: session opened for user b\n<34>Mar  4 11:00:00 web-02 sshd[2]: session closed for user b\n<34>Mar  5 12:00:00 web-02 sshd[2]: session opened for user c\n");
        var (goodFile, _) = await AddAsync(h, @case.Id, "good.log", good, "syslog", year: year - 1);
        var (wrongFile, wrongJob) = await AddAsync(h, @case.Id, "wrong.log", wrong, "syslog", year: year + 2);

        var worker = h.StartWorker("--once");
        Assert.True(await worker.WaitForExitAsync(300) == 0, worker.Output);

        Assert.Equal((JobState.Done, 3L), ((await h.JobAsync(wrongJob.Id)).State, (await h.JobAsync(wrongJob.Id)).Rows));
        var wrongRecord = await h.FileAsync(wrongFile.Id);
        Assert.Equal((null, null, 0L, 3L), (wrongRecord.TsMin, wrongRecord.TsMax, wrongRecord.UndatedRows, wrongRecord.FutureRows));
        var goodRecord = await h.FileAsync(goodFile.Id);
        Assert.Equal((new DateTime(year - 1, 3, 1, 10, 0, 0, DateTimeKind.Utc), new DateTime(year - 1, 3, 2, 11, 0, 0, DateTimeKind.Utc), 0L, 0L), (goodRecord.TsMin, goodRecord.TsMax, goodRecord.UndatedRows, goodRecord.FutureRows));

        await using (var db = h.Store.CreateDbContext())
        {
            // The span is the good file's; the three rows dated two years ahead are counted on the case.
            Assert.Equal(new CaseSpan(goodRecord.TsMin, goodRecord.TsMax, 0, 3), await CaseStatsOperations.SpanAsync(db, @case.Id, Ct));
        }

        // They are rows of the case all the same: visible, and in its statistics.
        Assert.Equal(5, (await h.VisibleAsync(@case.Id)).Rows);
        await using (var admin = h.Admin())
        {
            var ahead = Convert.ToInt64(await admin.ExecuteScalarAsync($"SELECT countIf(toYear(ts) = {year + 2}) FROM events WHERE case_id = {@case.Id}", cancellationToken: Ct), CultureInfo.InvariantCulture);
            Assert.Equal(3, ahead);
        }

        var stats = await h.FieldStatsAsync(@case.Id);
        Assert.Equal(5, stats.Single(r => r.Field.Length == 0).Count);
        Assert.Equal([("web-02", 3L), ("web-01", 2L)], stats.Where(r => r.Field == "host.name" && r.Value.Length > 0).Select(r => (r.Value, r.Count)));
        Assert.True(EventTime.Floor < goodRecord.TsMin);
    }
}
