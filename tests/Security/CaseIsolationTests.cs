using System.Globalization;
using ClickHouse.Driver.ADO;
using Urlog.Query;

namespace Urlog.Security.Tests;

/// <summary>
/// <c>events_v</c> under <c>readonly = 2</c> with <c>SQL_allowed_cases</c> as the per-request
/// HTTP setting shows exactly the listed cases, and <c>EXPLAIN indexes = 1</c> through the view still prunes
/// partitions on <c>case_id</c>. Everything here goes through the real query path (<see cref="ClickHouseQueryClient"/>:
/// HTTP, <c>urlog_query</c>, gated), except the one harness request that sends no setting at all.
/// </summary>
[Collection(SeedReaders.Name)]
public sealed class CaseIsolationTests(ClickHouseFixture ch, ITestOutputHelper output)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(new ulong[] { 1 }, "1")]
    [InlineData(new ulong[] { 2 }, "2")]
    [InlineData(new ulong[] { 1, 2 }, "1,2")]
    [InlineData(new ulong[] { 1, 2, 3 }, "1,2,3")]
    [InlineData(new ulong[] { }, "")]
    [InlineData(new ulong[] { 7 }, "")]
    public async Task RawSelectStarSeesExactlyTheAllowedCases(ulong[] allowed, string expectedCases)
    {
        var client = ch.QueryClient();
        var table = await client.RunSqlAsync("SELECT * FROM events_v", allowed, Ct);
        var caseColumn = table.Columns.ToList().IndexOf("case_id");
        Assert.True(caseColumn >= 0, "case_id column");
        var seen = table.Rows.Select(r => r[caseColumn]).Distinct().Order(StringComparer.Ordinal).ToList();
        output.WriteLine($"allowed [{string.Join(",", allowed)}]: {table.Rows.Count} rows, cases {string.Join(",", seen)}");
        Assert.Equal(expectedCases, string.Join(",", seen));
        Assert.Equal(allowed.Count(c => c <= 3) * ClickHouseFixture.RowsPerCase, table.Rows.Count);
    }

    [Fact]
    public async Task ACaseOutsideTheListIsInvisibleEvenWhenNamed()
    {
        var client = ch.QueryClient();
        var named = await client.RunSqlAsync("SELECT count() FROM events_v WHERE case_id = 2", [1], Ct);
        Assert.Equal("0", named.Rows[0][0]);
        var distinct = await client.RunSqlAsync("SELECT DISTINCT case_id FROM events_v ORDER BY case_id", [1, 3], Ct);
        Assert.Equal(["1", "3"], distinct.Rows.Select(r => r[0]).ToArray());
    }

    [Fact]
    public async Task SplPathSeesOnlyTheAllowedCase()
    {
        var client = ch.QueryClient();
        var options = Options(caseId: 2);

        // Case 2 as its owner: every row of case 2.
        var owner = await client.RunAsync(Transpiler.Transpile("| stats count", options), [2], Ct);
        Assert.Equal(ClickHouseFixture.RowsPerCase.ToString(CultureInfo.InvariantCulture), owner.Rows[0][0]);

        // The same query by a user whose ACL holds only case 1: the SQL names case 2, the setting does not, zero events.
        var other = await client.RunAsync(Transpiler.Transpile("| stats count", options), [1], Ct);
        Assert.Equal("0", other.Rows[0][0]);
        var listing = await client.RunAsync(Transpiler.Transpile("host=web-01", options), [1], Ct);
        Assert.Empty(listing.Rows);

        // index= cannot widen the scope either: the transpiler refuses a case outside AllowedCases before any SQL exists.
        var refused = Assert.Throws<QueryException>(() => Transpiler.Transpile("index=2 | stats count", Options(caseId: 1) with { AllowedCases = new HashSet<ulong> { 1 } }));
        Assert.Contains("not an allowed case", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoSettingAtAllMeansNoRows()
    {
        // The harness: the driver straight, no SQL_allowed_cases on the request. The view's getSettingOrDefault
        // yields '' and has([], case_id) is false for every row.
        await using var connection = new ClickHouseConnection(ch.HttpConnectionString(ClickHouseFixture.QueryUser, ClickHouseFixture.QueryPassword));
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count() FROM events_v";
        var count = Convert.ToInt64(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ExplainThroughTheViewPrunesPartitionsOnCaseIdAndTime()
    {
        var client = ch.QueryClient();

        // The transpiled path: case 1, February only. Six (case, month) partitions are seeded; one survives.
        var query = Transpiler.Transpile("host=web-01", Options(caseId: 1, from: new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), to: new DateTimeOffset(2026, 2, 1, 0, 30, 0, TimeSpan.Zero)));
        var plan = string.Join("\n", await client.ExplainAsync(query, [1], Ct));
        output.WriteLine(plan);
        Assert.Contains("Partition", plan, StringComparison.Ordinal);
        Assert.Contains("PrimaryKey", plan, StringComparison.Ordinal);
        Assert.Contains("Parts: 1/6", plan, StringComparison.Ordinal);
        Assert.Matches(@"Partition\s*\n[\s│]*Keys:\s*\n[\s│]*case_id\s*\n[\s│]*toYYYYMM\(ts\)", plan); // pruned on the partition key, case first
        Assert.Contains("(case_id in 1-element set)", plan, StringComparison.Ordinal); // the view's has([1], case_id), folded to a constant set

        // The raw tab: case 1 named, both months: two of six partitions, pruned on case_id alone. EXPLAIN as text
        // is not a SELECT and the gate refuses it; the tab's explain button goes through ExplainSqlAsync instead.
        await Assert.ThrowsAsync<RawSqlRejectedException>(() => client.RunSqlAsync("EXPLAIN indexes = 1 SELECT count() FROM events_v WHERE case_id = 1", [1], Ct));
        var rawPlan = string.Join("\n", await client.ExplainSqlAsync("SELECT count() FROM events_v WHERE case_id = 1 AND host = 'web-01'", [1], Ct));
        output.WriteLine(rawPlan);
        Assert.Contains("Parts: 2/6", rawPlan, StringComparison.Ordinal);
    }

    private static TranspileOptions Options(ulong caseId, DateTimeOffset? from = null, DateTimeOffset? to = null) => new(
        caseId,
        from ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        to ?? new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
        TimeZoneInfo.Utc,
        new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
}
