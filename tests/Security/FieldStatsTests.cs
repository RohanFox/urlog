using System.Globalization;
using ClickHouse.Driver;
using ClickHouse.Driver.ADO;
using ClickHouse.Driver.Tcp;
using Urlog.Core.Ingest;
using Urlog.Query;

namespace Urlog.Security.Tests;

/// <summary>
/// The per-case field statistics against the deployed accounts: the task (as <c>urlog_ingest</c>) counts exactly
/// the rows <c>events_v</c> shows, a new set replaces the old one whole, and the statistics of a case reach the
/// query account through <c>field_stats_v</c> for the cases its request allows and for no other.
/// </summary>
public sealed class FieldStatsTests(ClickHouseFixture ch, ITestOutputHelper output) : IClassFixture<ClickHouseFixture>
{
    private const int AccessDenied = 497;

    private static readonly FieldStatsLimits Limits = new(TimeSpan.FromSeconds(60), 4, 2_000_000_000);

    private static readonly IReadOnlyList<StatsField> Fields = FieldStats.TaskFields(FieldCatalog.Embedded);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTime At(int second) => new(2026, 11, 18, 10, 0, second, DateTimeKind.Utc);

    private static FieldStatsSet Set(string prefix, int values) =>
        new((ulong)values, [new FieldStatsRow(string.Empty, string.Empty, (ulong)values), new FieldStatsRow("host.name", string.Empty, (ulong)values, (ulong)values), .. Enumerable.Range(0, values).Select(i => new FieldStatsRow("host.name", $"{prefix}{i:D3}", 1))], []);

    [Fact]
    public async Task TheTaskCountsTheRowsTheViewShowsAndNoUncommittedOrSupersededOnes()
    {
        const ulong caseId = 1;
        var client = ch.QueryClient();
        await using var ingest = ch.Ingest();
        var store = new EventStoreAdmin(ingest);

        // Beside the committed seed (file 1, generation 1): a first parse that is not committed, and a file whose
        // first generation was replaced by a second while the rows of the first are still in the table.
        await ch.InsertAsync(caseId, fileId: 600, generation: 1, month: 1, rows: 30);
        await ch.InsertAsync(caseId, fileId: 601, generation: 1, month: 1, rows: 25);
        await store.CommitFileGenerationAsync(601, 1, Ct);
        await ch.InsertAsync(caseId, fileId: 601, generation: 2, month: 2, rows: 35);
        await store.CommitFileGenerationAsync(601, 2, Ct);
        // And a generation above the committed one, as a re-parse under way leaves it.
        await ch.InsertAsync(caseId, fileId: 601, generation: 3, month: 2, rows: 11);

        long stored;
        await using (var admin = ch.Admin())
        {
            stored = Convert.ToInt64(await admin.ExecuteScalarAsync($"SELECT count() FROM events WHERE case_id = {caseId}", cancellationToken: Ct), CultureInfo.InvariantCulture);
        }

        var visible = long.Parse((await client.RunSqlAsync("SELECT count() FROM events_v", [caseId], Ct)).Rows[0][0], CultureInfo.InvariantCulture);
        Assert.Equal(ClickHouseFixture.RowsPerCase + 35, visible);
        Assert.Equal(visible + 30 + 25 + 11, stored); // the table holds more than the view shows

        var set = await new FieldStatsTask(ingest).ComputeAsync(caseId, Fields, Limits, Ct);
        output.WriteLine($"{set.Events} events, {set.Rows.Count} rows; " + string.Join(", ", set.Timings.Select(t => $"{t.Label} {t.Elapsed.TotalMilliseconds:N0} ms")));
        Assert.Equal((ulong)visible, set.Events);

        // Every field's top values, against the same GROUP BY through the view as the query account.
        foreach (var (field, column) in new[] { ("host.name", "host"), ("program", "program") })
        {
            var expected = (await client.RunSqlAsync($"SELECT {column}, count() FROM events_v WHERE notEmpty({column}) GROUP BY {column} ORDER BY count() DESC, {column} ASC", [caseId], Ct)).Rows
                .Select(r => (r[0], ulong.Parse(r[1], CultureInfo.InvariantCulture))).ToList();
            var actual = set.Rows.Where(r => r.Field == field && r.Value.Length > 0).Select(r => (r.Value, r.Count)).ToList();
            output.WriteLine($"{field}: {string.Join(", ", actual)}");
            Assert.NotEmpty(expected);
            Assert.Equal(expected, actual);
            Assert.Equal((ulong)visible, set.Rows.Single(r => r.Field == field && r.Value.Length == 0).Count);
        }

        // A field no visible row has a value in gets no row, and an empty value is never a top value: the seed
        // has no severity, no user, no status and no address.
        foreach (var absent in new[] { "log.level", "user.name", "http.response.status_code", "source.ip", "event.code" })
        {
            Assert.DoesNotContain(set.Rows, r => r.Field == absent);
        }

        Assert.DoesNotContain(set.Rows, r => r.Field.Length > 0 && r.Count == 0);
        Assert.Equal(set.Rows.Count, set.Rows.Select(r => (r.Field, r.Value)).Distinct().Count());
    }

    [Fact]
    public async Task AReaderGetsTheWholeOldSetOrTheWholeNewOneAlsoBetweenTheInsertAndTheDelete()
    {
        const ulong caseId = 2;
        var client = ch.QueryClient();
        await using var ingest = ch.Ingest();
        var task = new FieldStatsTask(ingest);

        await task.WriteAsync(caseId, Set("old-", 50), At(1), Ct);
        var first = await client.FieldStatsAsync(caseId, Ct);
        Assert.Equal((At(1), 50UL, 50), (first.ComputedAt, first.Events, first.Fields["host.name"].Top.Count));

        // The new set is inserted and the old rows are still there: what a reader meets between the two statements.
        // Twenty values stay, thirty leave, ten are new.
        var next = new FieldStatsSet(30, [new FieldStatsRow(string.Empty, string.Empty, 30), new FieldStatsRow("host.name", string.Empty, 30, 30),
            .. Enumerable.Range(0, 20).Select(i => new FieldStatsRow("host.name", $"old-{i:D3}", 2)), .. Enumerable.Range(0, 10).Select(i => new FieldStatsRow("host.name", $"new-{i:D3}", 2))], []);
        var rows = next.Rows;
        await ingest.InsertAsync(
            "INSERT INTO field_stats (case_id, field, value, count, distinct_estimate, computed_at) VALUES",
            [
                ClickHouseTcpColumn.Create("case_id", Enumerable.Repeat(caseId, rows.Count).ToArray()),
                ClickHouseTcpColumn.Create("field", rows.Select(r => r.Field).ToArray()),
                ClickHouseTcpColumn.Create("value", rows.Select(r => r.Value).ToArray()),
                ClickHouseTcpColumn.Create("count", rows.Select(r => r.Count).ToArray()),
                ClickHouseTcpColumn.Create("distinct_estimate", rows.Select(r => r.DistinctEstimate).ToArray()),
                ClickHouseTcpColumn.Create("computed_at", Enumerable.Repeat(At(2), rows.Count).ToArray()),
            ],
            cancellationToken: Ct);
        await using (var admin = ch.Admin())
        {
            // 52 rows of the old set and 32 of the new one are in the table, whatever a merge has folded since.
            var raw = Convert.ToInt64(await admin.ExecuteScalarAsync($"SELECT count() FROM field_stats FINAL WHERE case_id = {caseId}", cancellationToken: Ct), CultureInfo.InvariantCulture);
            Assert.Equal(62, raw);
        }

        var between = await client.FieldStatsAsync(caseId, Ct);
        Assert.Equal((At(2), 30UL), (between.ComputedAt, between.Events));
        Assert.Equal(30, between.Fields["host.name"].Top.Count);
        Assert.All(between.Fields["host.name"].Top, t => Assert.Equal(2UL, t.Value));
        // The raw tab sees the same through the view.
        Assert.Equal("32", (await client.RunSqlAsync("SELECT count() FROM field_stats_v", [caseId], Ct)).Rows[0][0]);

        // A whole write: the older rows are gone from the table, not only from the view.
        await task.WriteAsync(caseId, Set("third-", 5), At(3), Ct);
        await using (var admin = ch.Admin())
        {
            var left = Convert.ToInt64(await admin.ExecuteScalarAsync($"SELECT count() FROM field_stats WHERE case_id = {caseId}", cancellationToken: Ct), CultureInfo.InvariantCulture);
            Assert.Equal(7, left);
        }
    }

    [Fact]
    public async Task ReadersDuringManySwapsNeverSeeAMixedSet()
    {
        const ulong caseId = 3;
        var client = ch.QueryClient();
        await using var ingest = ch.Ingest();
        var task = new FieldStatsTask(ingest);
        await task.WriteAsync(caseId, Set("a-", 40), At(0), Ct);

        using var stop = new CancellationTokenSource();
        var reads = 0;
        var readers = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                // Read refuses rows of two sets; what is left to check is that the one set is whole.
                var stats = await client.FieldStatsAsync(caseId, Ct);
                var top = stats.Fields["host.name"].Top;
                var prefix = top[0].Key[..2];
                Assert.True(top.Count == (prefix == "a-" ? 40 : 60) && top.All(t => t.Key.StartsWith(prefix, StringComparison.Ordinal)), $"{top.Count} values, first {top[0].Key}, last {top[^1].Key}");
                Assert.Equal((ulong)top.Count, stats.Events);
                Interlocked.Increment(ref reads);
            }
        })).ToList();

        for (var i = 1; i <= 30; i++)
        {
            await task.WriteAsync(caseId, i % 2 == 0 ? Set("a-", 40) : Set("b-", 60), At(0).AddSeconds(i), Ct);
        }

        await stop.CancelAsync();
        await Task.WhenAll(readers);
        output.WriteLine($"{reads} reads during 30 swaps");
        Assert.True(reads >= 30, $"only {reads} reads");
    }

    [Fact]
    public async Task TheStatisticsOfACaseOutsideTheListAreNotReadable()
    {
        var client = ch.QueryClient();
        await using var ingest = ch.Ingest();
        var task = new FieldStatsTask(ingest);
        // Cases the other tests of this class do not write.
        await task.WriteAsync(11, Set("eleven-", 3), At(1), Ct);
        await task.WriteAsync(12, Set("twelve-", 4), At(1), Ct);

        // The endpoint's read: its own case, whole, and nothing of the other.
        var mine = await client.FieldStatsAsync(11, Ct);
        Assert.Equal(["eleven-000", "eleven-001", "eleven-002"], mine.Fields["host.name"].Top.Select(t => t.Key));
        Assert.Contains("WHERE case_id = {case:UInt64}", FieldStats.Sql, StringComparison.Ordinal);

        // The raw tab as a user of case 11: the view asked for case 12, by name and without one.
        foreach (var sql in new[]
                 {
                     "SELECT count() FROM field_stats_v WHERE case_id = 12",
                     "SELECT count() FROM field_stats_v WHERE case_id != 11",
                     "SELECT count() FROM field_stats_v WHERE value LIKE 'twelve-%'",
                     "SELECT count() FROM (SELECT * FROM field_stats_v) WHERE case_id = 12",
                     "SELECT count() FROM events_v e RIGHT JOIN field_stats_v f ON e.case_id = f.case_id WHERE f.case_id = 12",
                 })
        {
            var rows = await client.RunSqlAsync(sql, [11], Ct);
            output.WriteLine($"{sql} -> {rows.Rows[0][0]}");
            Assert.Equal("0", rows.Rows[0][0]);
        }

        Assert.Equal(["11"], (await client.RunSqlAsync("SELECT DISTINCT case_id FROM field_stats_v ORDER BY case_id", [11], Ct)).Rows.Select(r => r[0]));
        Assert.Equal(["11", "12"], (await client.RunSqlAsync("SELECT DISTINCT case_id FROM field_stats_v ORDER BY case_id", [11, 12], Ct)).Rows.Select(r => r[0]));
        Assert.Empty((await client.RunSqlAsync("SELECT DISTINCT case_id FROM field_stats_v", [], Ct)).Rows);

        // The table itself: refused by the gate, and by the grant when the gate is out of the way.
        var refused = await Assert.ThrowsAsync<RawSqlRejectedException>(() => client.RunSqlAsync("SELECT * FROM field_stats", [11, 12], Ct));
        Assert.Contains("field_stats is not readable", refused.Message, StringComparison.Ordinal);
        await using var connection = new ClickHouseConnection(ch.HttpConnectionString(ClickHouseFixture.QueryUser, ClickHouseFixture.QueryPassword));
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count() FROM field_stats";
        command.CustomSettings["SQL_allowed_cases"] = "[11,12]";
        var denied = await Assert.ThrowsAsync<ClickHouseServerException>(() => command.ExecuteScalarAsync(Ct));
        Assert.Equal(AccessDenied, denied.ErrorCode);

        // Without a case list the view shows nothing, as events_v does.
        command.CommandText = "SELECT count() FROM field_stats_v";
        command.CustomSettings.Clear();
        Assert.Equal(0UL, Convert.ToUInt64(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task ARunPastItsTimeLimitIsATimeoutAndWritesNothing()
    {
        await using var ingest = ch.Ingest();
        var task = new FieldStatsTask(ingest);
        await Assert.ThrowsAsync<FieldStatsTimeoutException>(() => task.ComputeAsync(1, Fields, Limits with { TimeLimit = TimeSpan.Zero }, Ct));
    }
}
