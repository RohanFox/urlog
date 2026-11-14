using System.Globalization;
using Urlog.Query;

namespace Urlog.Security.Tests;

/// <summary>
/// The mandatory unauthorised-user scenario (design record, "Security" 5): every query runs
/// (a) as the case's owner and returns what it should, (b) as a user whose ACL does not hold the case and
/// returns zero events, and (c) as that user through the raw-SQL tab, zero events. The scenarios are SPL over the
/// fixture's seed; <c>scenarios/</c> (the end-to-end suite) plugs in here when it exists.
/// </summary>
public sealed class UnauthorisedUserTests(ClickHouseFixture ch, ITestOutputHelper output) : IClassFixture<ClickHouseFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, int> Scenarios() => new()
    {
        { "", 100 },                                   // default listing: LIMIT 100 of the case's 200 rows
        { "| stats count", ClickHouseFixture.RowsPerCase },
        { "host=web-01 | stats count", ClickHouseFixture.RowsPerCase / 2 },
        { "| stats count by host", ClickHouseFixture.RowsPerCase },
        { "program=nginx | head 5", 5 },
        { "GET | stats count", ClickHouseFixture.RowsPerCase },
        { "host=web-02 earliest=2026-02-01T00:00:00Z latest=2026-02-02T00:00:00Z", 50 },

        // One query per verb after the first slice; an aggregate's count is its last column.
        { "| where host=web-01 | stats count", ClickHouseFixture.RowsPerCase / 2 },
        { "| stats dc(host) as hosts, values(host) as names, count", ClickHouseFixture.RowsPerCase },
        { "| stats count by host | where count>1", ClickHouseFixture.RowsPerCase },
        { "| table host, raw | head 7", 7 },
        { "| rex field=raw \"^(?<verb>[A-Z]+)\" | stats count by verb", ClickHouseFixture.RowsPerCase },
        { "| rex field=raw \"^(?<verb>[A-Z]+)\" | where verb=GET | head 9", 9 },
        { "| timechart span=1d count", ClickHouseFixture.RowsPerCase },
        { "| timechart span=1d count by host", ClickHouseFixture.RowsPerCase },
    };

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task OwnerSeesEventsUnauthorisedUserSeesNone(string spl, int ownerEvents)
    {
        const ulong caseId = 2;
        var client = ch.QueryClient();
        var options = new TranspileOptions(caseId, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc, new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        var query = Transpiler.Transpile(spl, options);

        var owner = await client.RunAsync(query, [caseId], Ct);
        var ownerSeen = Events(owner, query);
        output.WriteLine($"{spl,-70} owner {ownerSeen,4}  no-access {0,4}");
        Assert.Equal(ownerEvents, ownerSeen);

        // (b) a user with cases 1 and 3 but not 2, through the SPL path.
        var noAccess = await client.RunAsync(query, [1, 3], Ct);
        Assert.Equal(0, Events(noAccess, query));

        // (b') a user with no cases at all.
        var nobody = await client.RunAsync(query, [], Ct);
        Assert.Equal(0, Events(nobody, query));

        // (c) the raw-SQL tab as the no-access user, naming the case outright.
        var raw = await client.RunSqlAsync("SELECT * FROM events_v WHERE case_id = 2", [1, 3], Ct);
        Assert.Empty(raw.Rows);
        var rawCount = await client.RunSqlAsync("SELECT count() FROM events_v WHERE case_id = 2", [1, 3], Ct);
        Assert.Equal("0", rawCount.Rows[0][0]);
    }

    /// <summary>Events a result stands for: rows for a listing, the sum of the count column (the last one) for stats.</summary>
    private static int Events(QueryTable table, TranspileResult query) =>
        query.IsAggregate ? table.Rows.Sum(r => int.Parse(r[^1], CultureInfo.InvariantCulture)) : table.Rows.Count;
}
