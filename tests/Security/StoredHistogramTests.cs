using System.Globalization;
using ClickHouse.Driver;
using ClickHouse.Driver.ADO;
using Urlog.Api.Search;
using Urlog.Core.Ingest;
using Urlog.Query;

namespace Urlog.Security.Tests;

/// <summary>
/// The stored histogram against the deployed accounts: for a search that narrows nothing it is the histogram the
/// events give, bucket by bucket, whatever the span, the zone and the range; it shows the committed generation of a
/// file and no other; a re-parse leaves no row of the old generation once its clean-up ran; and it reaches the query
/// account through <c>case_histogram_v</c> for the cases its request allows and for no other.
/// </summary>
public sealed class StoredHistogramTests(ClickHouseFixture ch, ITestOutputHelper output) : IClassFixture<ClickHouseFixture>
{
    private const int AccessDenied = 497;

    private static readonly string[] Severities = [string.Empty, "info", "err", "warning"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static long Ms(int year, int month, int day, int hour = 0, int minute = 0, int second = 0, int millisecond = 0) =>
        new DateTimeOffset(year, month, day, hour, minute, second, millisecond, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static DateTimeOffset At(long milliseconds) => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);

    /// <summary>Rows from <paramref name="from"/> every <paramref name="step"/> ms up to <paramref name="to"/>, hosts and severities in turn.</summary>
    private static List<SeedRow> Every(long from, long to, long step, string[] hosts)
    {
        var rows = new List<SeedRow>();
        var i = 0;
        for (var t = from; t < to; t += step, i++)
        {
            rows.Add(new SeedRow(t, hosts[i % hosts.Length], Severities[(i / 3) % Severities.Length]));
        }

        return rows;
    }

    private async Task WriteAsync(EventStoreAdmin store, ulong caseId, ulong fileId, uint generation, IReadOnlyList<SeedRow> rows, bool commit)
    {
        await ch.InsertRowsAsync(caseId, fileId, generation, rows);
        await store.FillHistogramAsync(caseId, fileId, generation, rows.Min(r => r.Milliseconds), rows.Max(r => r.Milliseconds), Ct);
        if (commit)
        {
            await store.CommitFileGenerationAsync(fileId, generation, Ct);
        }
    }

    private static List<string> Text(IReadOnlyList<HistogramBucket> buckets) =>
        [.. buckets.Select(b => $"{b.Start:yyyy-MM-ddTHH:mm:ss.fffZ} " + string.Join(",", b.Counts.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key}={c.Value}")))];

    private static string Sql(long milliseconds) => $"fromUnixTimestamp64Milli(toInt64({milliseconds.ToString(CultureInfo.InvariantCulture)}), 'UTC')";

    /// <summary>A zone with an IANA id, which the Windows runtime of the tests cannot look up: only its id reaches the server.</summary>
    private static TimeZoneInfo Named(string id) => TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromHours(2), id, id);

    [Fact]
    public async Task TheStoredHistogramIsTheLiveOneForEverySpanZoneAndRange()
    {
        const ulong caseId = 7;
        await using var ingest = ch.Ingest();
        var store = new EventStoreAdmin(ingest);

        // File 71, committed: March 2026 (the clocks change on the 8th in New York and on the 29th in Bucharest),
        // rows on the first and the last millisecond of a minute, rows without a usable time, and one dated 2031.
        var march = Every(Ms(2026, 3, 6), Ms(2026, 4, 1), 673_127, ["web-01", "web-02"]);
        march.AddRange(
        [
            new SeedRow(Ms(2026, 3, 10, 12, 0, 0, 0), "web-01", "err"),
            new SeedRow(Ms(2026, 3, 10, 12, 0, 59, 999), "web-01", "err"),
            new SeedRow(Ms(2026, 3, 10, 12, 1, 0, 0), "web-02", string.Empty),
            new SeedRow(Ms(2026, 3, 10, 11, 59, 59, 999), "web-02", "info"),
            new SeedRow(0, "web-01", string.Empty),
            new SeedRow(1, "web-01", string.Empty),
            new SeedRow(Ms(1970, 6, 1, 4, 5, 6, 7), "web-02", "info"),
            new SeedRow(Ms(1979, 12, 31, 23, 59, 59, 999), "web-02", string.Empty),
            new SeedRow(Ms(2031, 5, 5, 10, 10, 10, 10), "web-01", "warning"),
        ]);
        await WriteAsync(store, caseId, 71, 1, march, commit: true);

        // File 72, parsed twice: the autumn changes (25 October, 1 November). The first generation's rows and
        // buckets are left in the table, as they are until the clean-up runs.
        var autumn = Every(Ms(2026, 10, 23), Ms(2026, 11, 3), 547_003, ["web-02", "web-03"]);
        await WriteAsync(store, caseId, 72, 1, autumn, commit: true);
        var again = autumn.Where((_, i) => i % 2 == 0).Select(r => r with { Milliseconds = r.Milliseconds + 1_000, Host = "web-03" }).ToList();
        await WriteAsync(store, caseId, 72, 2, again, commit: true);

        // File 73: a parse with its rows and its buckets written and no commit.
        await WriteAsync(store, caseId, 73, 1, Every(Ms(2026, 3, 9), Ms(2026, 3, 12), 300_000, ["web-09"]), commit: false);

        var client = ch.QueryClient();
        var runner = new SearchRunner(new QuerySearchBackend(client), caseId);
        long inTable;
        long eventsInTable;
        await using (var admin = ch.Admin())
        {
            inTable = Convert.ToInt64(await admin.ExecuteScalarAsync($"SELECT sum(events) FROM case_histogram WHERE case_id = {caseId}", cancellationToken: Ct), CultureInfo.InvariantCulture);
            eventsInTable = Convert.ToInt64(await admin.ExecuteScalarAsync($"SELECT count() FROM events WHERE case_id = {caseId}", cancellationToken: Ct), CultureInfo.InvariantCulture);
        }

        var visible = long.Parse((await client.RunSqlAsync("SELECT count() FROM events_v", [caseId], Ct)).Rows[0][0], CultureInfo.InvariantCulture);
        Assert.Equal(march.Count + again.Count, visible);
        Assert.Equal(eventsInTable, inTable); // every stored row is in a bucket, the hidden generations too
        Assert.True(inTable > visible, "the table holds a superseded and an uncommitted generation");

        var ranges = new (string Name, long From, long To)[]
        {
            ("march on whole minutes", Ms(2026, 3, 6), Ms(2026, 4, 1)),
            ("two minutes, both ends inside a minute", Ms(2026, 3, 10, 11, 59, 30, 500), Ms(2026, 3, 10, 12, 1, 0, 1)),
            ("both files, both ends inside a minute", Ms(2026, 3, 7, 3, 17, 21, 123), Ms(2026, 11, 2, 22, 41, 7, 777)),
            ("one end on a minute", Ms(2026, 3, 10, 12, 0), Ms(2026, 3, 20, 7, 7, 7, 7)),
            ("the undated rows", 0, Ms(1980, 1, 1)),
            ("everything, the row of 2031 included", 0, Ms(2032, 1, 1)),
        };
        var spans = new[] { new HistogramSpan(1, 'm'), new HistogramSpan(5, 'm'), new HistogramSpan(1, 'h'), new HistogramSpan(12, 'h'), new HistogramSpan(1, 'd'), new HistogramSpan(1, 'w') };
        var zones = new[] { TimeZoneInfo.Utc, TimeZones.Find("+05:45"), Named("Europe/Bucharest"), Named("America/New_York") };
        var compared = 0;
        foreach (var (name, from, to) in ranges)
        {
            var expected = long.Parse((await client.RunSqlAsync($"SELECT count() FROM events_v WHERE ts >= {Sql(from)} AND ts < {Sql(to)}", [caseId], Ct)).Rows[0][0], CultureInfo.InvariantCulture);
            Assert.True(expected > 0, name);
            foreach (var zone in zones)
            {
                var plan = SearchPlan.Create(string.Empty, new TranspileOptions(caseId, At(from), At(to), zone, At(to)));
                foreach (var span in spans)
                {
                    var live = await runner.HistogramAsync(plan, span, Ct);
                    var stored = await runner.StoredHistogramAsync(plan, span, Ct);
                    Assert.NotNull(stored);
                    Assert.Equal(Text(live), Text(stored));
                    Assert.Equal(expected, stored.Sum(b => b.Counts.Values.Sum()));
                    compared++;
                }
            }

            output.WriteLine($"{name}: {expected:N0} events, {spans.Length * zones.Length} histograms equal");
        }

        Assert.Equal(ranges.Length * spans.Length * zones.Length, compared);

        // The zone does cut the days: a day in Bucharest does not start where a day in UTC starts.
        var days = SearchPlan.Create(string.Empty, new TranspileOptions(caseId, At(ranges[0].From), At(ranges[0].To), Named("Europe/Bucharest"), At(ranges[0].To)));
        var starts = (await runner.StoredHistogramAsync(days, new HistogramSpan(1, 'd'), Ct))!.Select(b => b.Start.UtcDateTime.Hour).Distinct().Order().ToList();
        Assert.Equal([21, 22], starts); // midnight at +02:00 until the 29th, at +03:00 from then

        // What the stored rows say per severity is what the events say per severity, and the rows of the file
        // that was never committed are in neither.
        var perSeverity = (await client.RunSqlAsync("SELECT severity, sum(events) FROM case_histogram_v GROUP BY severity ORDER BY severity", [caseId], Ct)).Rows.Select(r => (r[0], r[1])).ToList();
        Assert.Equal((await client.RunSqlAsync("SELECT severity, count() FROM events_v GROUP BY severity ORDER BY severity", [caseId], Ct)).Rows.Select(r => (r[0], r[1])), perSeverity);
        Assert.Equal(Severities.Length, perSeverity.Count);
        output.WriteLine("per severity: " + string.Join(", ", perSeverity));

        // No stored form: a span under a minute, a range inside one minute, a search that narrows something.
        var whole = SearchPlan.Create(string.Empty, new TranspileOptions(caseId, At(ranges[0].From), At(ranges[0].To), TimeZoneInfo.Utc, At(ranges[0].To)));
        Assert.Null(whole.StoredHistogram(new HistogramSpan(30, 's')));
        Assert.Null(SearchPlan.Create(string.Empty, new TranspileOptions(caseId, At(Ms(2026, 3, 10, 12, 0, 10)), At(Ms(2026, 3, 10, 12, 0, 50)), TimeZoneInfo.Utc, At(ranges[0].To))).StoredHistogram(new HistogramSpan(1, 'm')));
        // A parse with a time before 1970 or after 2106 gets no stored histogram at all: the fill refuses it.
        Assert.Throws<ArgumentOutOfRangeException>(() => CaseScopedEventsQuery.BuildHistogramFill(caseId, 74, 1, -1, Ms(2026, 3, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => CaseScopedEventsQuery.BuildHistogramFill(caseId, 74, 1, 0, Ms(2107, 1, 1)));
        Assert.Null(SearchPlan.Create("host=web-01", new TranspileOptions(caseId, At(ranges[0].From), At(ranges[0].To), TimeZoneInfo.Utc, At(ranges[0].To))).StoredHistogram(new HistogramSpan(1, 'h')));
    }

    [Fact]
    public async Task AReparseLeavesNoRowOfTheOldGenerationAfterItsCleanUp()
    {
        const ulong caseId = 9;
        const ulong fileId = 91;
        await using var ingest = ch.Ingest();
        var store = new EventStoreAdmin(ingest);
        var client = ch.QueryClient();
        async Task<long> ShownAsync() => long.Parse((await client.RunSqlAsync("SELECT sum(events) FROM case_histogram_v", [caseId], Ct)).Rows[0][0], CultureInfo.InvariantCulture);
        async Task<(long Events, long Buckets)> StoredAsync(uint generation)
        {
            await using var admin = ch.Admin();
            var events = Convert.ToInt64(await admin.ExecuteScalarAsync($"SELECT count() FROM events WHERE case_id = {caseId} AND file_id = {fileId} AND parse_generation = {generation}", cancellationToken: Ct), CultureInfo.InvariantCulture);
            var buckets = Convert.ToInt64(await admin.ExecuteScalarAsync($"SELECT sum(events) FROM case_histogram WHERE case_id = {caseId} AND file_id = {fileId} AND parse_generation = {generation}", cancellationToken: Ct), CultureInfo.InvariantCulture);
            return (events, buckets);
        }

        var first = Every(Ms(2026, 5, 1), Ms(2026, 5, 2), 120_000, ["web-01"]);
        var second = Every(Ms(2026, 5, 1), Ms(2026, 5, 2), 90_000, ["web-01", "web-02"]);
        await WriteAsync(store, caseId, fileId, 1, first, commit: true);
        Assert.Equal(first.Count, await ShownAsync());

        // The re-parse has written its rows and its buckets: the first generation is still what shows.
        await WriteAsync(store, caseId, fileId, 2, second, commit: false);
        Assert.Equal(first.Count, await ShownAsync());
        await store.CommitFileGenerationAsync(fileId, 2, Ct);
        Assert.Equal(second.Count, await ShownAsync());
        Assert.Equal((first.Count, first.Count), await StoredAsync(1)); // hidden, not yet deleted

        // The clean-up after the commit: the first generation is gone from both tables.
        await store.DeleteFileGenerationsBelowAsync(caseId, fileId, 2, Ct);
        Assert.Equal((0, 0), await StoredAsync(1));
        Assert.Equal((second.Count, second.Count), await StoredAsync(2));
        Assert.Equal(second.Count, await ShownAsync());

        // An attempt that failed after its buckets were written: never shown, and gone with the clean-up of the
        // failure, which keeps the committed generation.
        await WriteAsync(store, caseId, fileId, 3, first, commit: false);
        Assert.Equal(second.Count, await ShownAsync());
        await store.DeleteFileGenerationsExceptAsync(caseId, fileId, 2, Ct);
        Assert.Equal((0, 0), await StoredAsync(3));
        Assert.Equal((second.Count, second.Count), await StoredAsync(2));

        // Archive and purge: the case's partition goes whole.
        Assert.True(await store.CaseHistogramRowsAsync(caseId, Ct) > 0);
        await store.DropCaseHistogramAsync(caseId, Ct);
        Assert.Equal(0, await store.CaseHistogramRowsAsync(caseId, Ct));
        await store.DropCaseHistogramAsync(caseId, Ct); // a second drop finds nothing and is no error
        Assert.Equal("0", (await client.RunSqlAsync("SELECT count() FROM case_histogram_v", [caseId], Ct)).Rows[0][0]);
    }

    [Fact]
    public async Task ReadersDuringManyReparsesSeeOneGenerationWhole()
    {
        const ulong caseId = 8;
        const ulong fileId = 81;
        await using var ingest = ch.Ingest();
        var store = new EventStoreAdmin(ingest);
        var odd = Every(Ms(2026, 6, 1), Ms(2026, 6, 2), 2_160_000, ["web-01"]);   // 40 rows
        var even = Every(Ms(2026, 6, 1), Ms(2026, 6, 2), 1_440_000, ["web-02"]);  // 60 rows
        Assert.Equal((40, 60), (odd.Count, even.Count));
        await WriteAsync(store, caseId, fileId, 1, odd, commit: true);

        var runner = new SearchRunner(new QuerySearchBackend(ch.QueryClient()), caseId);
        var plan = SearchPlan.Create(string.Empty, new TranspileOptions(caseId, At(Ms(2026, 6, 1)), At(Ms(2026, 6, 2)), TimeZoneInfo.Utc, At(Ms(2026, 6, 2))));
        using var stop = new CancellationTokenSource();
        var reads = 0;
        var readers = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var buckets = (await runner.StoredHistogramAsync(plan, new HistogramSpan(1, 'h'), Ct))!;
                var total = buckets.Sum(b => b.Counts.Values.Sum());
                Assert.True(total is 40 or 60, $"a histogram of {total} events: neither generation");
                Interlocked.Increment(ref reads);
            }
        })).ToList();

        for (uint generation = 2; generation <= 31; generation++)
        {
            // The order of a re-parse: rows, buckets, commit, then the clean-up of what the commit replaced.
            await WriteAsync(store, caseId, fileId, generation, generation % 2 == 0 ? even : odd, commit: true);
            await store.DeleteFileGenerationsBelowAsync(caseId, fileId, generation, Ct);
        }

        await stop.CancelAsync();
        await Task.WhenAll(readers);
        output.WriteLine($"{reads} reads during 30 re-parses");
        Assert.True(reads >= 30, $"only {reads} reads");
    }

    [Fact]
    public async Task TheHistogramOfACaseOutsideTheListIsNotReadable()
    {
        await using var ingest = ch.Ingest();
        var store = new EventStoreAdmin(ingest);
        // Cases the other tests of this class do not write.
        await WriteAsync(store, 11, 111, 1, Every(Ms(2026, 7, 1), Ms(2026, 7, 2), 3_600_000, ["eleven"]), commit: true);
        await WriteAsync(store, 12, 121, 1, [.. Every(Ms(2026, 7, 1), Ms(2026, 7, 3), 3_600_000, ["twelve"]).Select(r => r with { Severity = "twelve" })], commit: true);
        var client = ch.QueryClient();
        Assert.Equal("24", (await client.RunSqlAsync("SELECT sum(events) FROM case_histogram_v", [11], Ct)).Rows[0][0]);

        // The raw tab as a user of case 11: the view asked for case 12, by name and without one.
        foreach (var sql in new[]
                 {
                     "SELECT count() FROM case_histogram_v WHERE case_id = 12",
                     "SELECT count() FROM case_histogram_v WHERE case_id != 11",
                     "SELECT count() FROM case_histogram_v WHERE severity = 'twelve'",
                     "SELECT count() FROM (SELECT * FROM case_histogram_v) WHERE case_id = 12",
                     "SELECT count() FROM events_v e RIGHT JOIN case_histogram_v h ON e.case_id = h.case_id WHERE h.case_id = 12",
                 })
        {
            var rows = await client.RunSqlAsync(sql, [11], Ct);
            output.WriteLine($"{sql} -> {rows.Rows[0][0]}");
            Assert.Equal("0", rows.Rows[0][0]);
        }

        Assert.Equal(["11"], (await client.RunSqlAsync("SELECT DISTINCT case_id FROM case_histogram_v ORDER BY case_id", [11], Ct)).Rows.Select(r => r[0]));
        Assert.Equal(["11", "12"], (await client.RunSqlAsync("SELECT DISTINCT case_id FROM case_histogram_v ORDER BY case_id", [11, 12], Ct)).Rows.Select(r => r[0]));
        Assert.Empty((await client.RunSqlAsync("SELECT DISTINCT case_id FROM case_histogram_v", [], Ct)).Rows);

        // The view does not show which file or generation a bucket came from, and the table keeps no host.
        Assert.Equal(["case_id", "severity", "bucket", "events"], (await client.RunSqlAsync("SELECT * FROM case_histogram_v LIMIT 1", [11], Ct)).Columns);

        // The table itself: refused by the gate, and by the grant when the gate is out of the way.
        var refused = await Assert.ThrowsAsync<RawSqlRejectedException>(() => client.RunSqlAsync("SELECT * FROM case_histogram", [11, 12], Ct));
        Assert.Contains("case_histogram is not readable", refused.Message, StringComparison.Ordinal);
        await using var connection = new ClickHouseConnection(ch.HttpConnectionString(ClickHouseFixture.QueryUser, ClickHouseFixture.QueryPassword));
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count() FROM case_histogram";
        command.CustomSettings["SQL_allowed_cases"] = "[11,12]";
        var denied = await Assert.ThrowsAsync<ClickHouseServerException>(() => command.ExecuteScalarAsync(Ct));
        Assert.Equal(AccessDenied, denied.ErrorCode);

        // Without a case list the view shows nothing, as events_v does.
        command.CommandText = "SELECT count() FROM case_histogram_v";
        command.CustomSettings.Clear();
        Assert.Equal(0UL, Convert.ToUInt64(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture));

        // The ingest account writes the table and cannot read it.
        var unread = await Assert.ThrowsAsync<ClickHouse.Driver.Tcp.ClickHouseTcpServerException>(async () => await ingest.ExecuteScalarAsync("SELECT count() FROM case_histogram", cancellationToken: Ct));
        Assert.Equal(AccessDenied, unread.RawCode);
    }
}
