using System.Globalization;
using System.Text.Json;
using ClickHouse.Driver.ADO;
using ClickHouse.Driver.ADO.Parameters;
using Urlog.Query;

namespace Urlog.Security.Tests;

/// <summary>
/// The verbs against the pinned server. The gate only parses a statement; these run it: every recorded vector
/// executes as the read-only user, and the seed (two hosts, one program, one line of text, no user, no address,
/// no status) answers what a row without a value does, what a pattern that does not match does, where the values
/// cap falls, how the series outside the top ten fold into one, and where a day starts when the clocks change.
/// </summary>
public sealed class QueryVerbTests(ClickHouseFixture ch, ITestOutputHelper output) : IClassFixture<ClickHouseFixture>
{
    private const ulong Case = 1;
    private static readonly DateTimeOffset From = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TranspileResult Transpile(string spl, TimeZoneInfo? zone = null) =>
        Transpiler.Transpile(spl, new TranspileOptions(Case, From, To, zone ?? TimeZoneInfo.Utc, To));

    private Task<QueryTable> RunAsync(string spl) => ch.QueryClient().RunAsync(Transpile(spl), [Case], Ct);

    [Fact]
    public async Task EveryVectorRunsOnTheServer()
    {
        var client = ch.QueryClient();
        var culture = CultureInfo.InvariantCulture;
        var ran = 0;
        var failures = new List<string>();
        var dir = Path.Combine(ClickHouseFixture.RepoRoot, "src", "Query", "tests", "vectors");
        foreach (var file in Directory.GetFiles(dir, "*.json").Order(StringComparer.Ordinal))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var vector in doc.RootElement.EnumerateArray())
            {
                if (!vector.TryGetProperty("expect", out _))
                {
                    continue;
                }

                var name = $"{Path.GetFileName(file)} / {vector.GetProperty("name").GetString()}";
                string Option(string key, string fallback) =>
                    vector.TryGetProperty("options", out var o) && o.TryGetProperty(key, out var v) ? v.ToString() : fallback;
                var options = new TranspileOptions(
                    ulong.Parse(Option("case", "1"), culture),
                    DateTimeOffset.Parse(Option("from", "2026-03-01T00:00:00Z"), culture),
                    DateTimeOffset.Parse(Option("to", "2026-04-01T00:00:00Z"), culture),
                    TimeZones.Find(Option("tz", "UTC")),
                    DateTimeOffset.Parse(Option("now", "2026-03-15T12:00:00Z"), culture))
                {
                    AllowedCases = new HashSet<ulong> { 1, 7 },
                };
                try
                {
                    var query = Transpiler.Transpile(vector.GetProperty("spl").GetString()!, options);
                    var table = await client.RunAsync(query, [1], Ct);
                    Assert.Equal(query.Columns.Length, table.Columns.Count);
                    ran++;
                }
                catch (Exception e) when (e is QueryFailedException or RawSqlRejectedException or QueryException)
                {
                    failures.Add($"{name}: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        output.WriteLine($"{ran} vectors ran");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.True(ran >= 180, $"{ran} vectors");
    }

    [Fact]
    public async Task ARowWithoutAValueIsInNoByGroupAndFieldEqualsStarFindsOnlyRowsThatHaveOne()
    {
        var all = ClickHouseFixture.RowsPerCase.ToString(CultureInfo.InvariantCulture);
        Assert.Equal(all, (await RunAsync("| stats count")).Rows[0][0]);

        // The seed writes a host and a program on every row, and no user, address or status on any.
        var byHost = await RunAsync("| stats count by host | sort host");
        Assert.Equal([["web-01", "100"], ["web-02", "100"]], byHost.Rows);
        Assert.Empty((await RunAsync("| stats count by user")).Rows);
        Assert.Empty((await RunAsync("| stats count by src_ip")).Rows);
        Assert.Empty((await RunAsync("| stats count by status")).Rows);
        Assert.Empty((await RunAsync("| stats count by no_such_key")).Rows);
        Assert.Empty((await RunAsync("| stats count by host, user")).Rows); // one empty by field is enough

        // A number whose 0 the catalog does not mark as absent is a value like any other.
        Assert.Equal([["0", all]], (await RunAsync("| stats count by bytes")).Rows);

        Assert.Equal("0", (await RunAsync("user=* | stats count")).Rows[0][0]);
        Assert.Equal("0", (await RunAsync("src_ip=* | stats count")).Rows[0][0]);
        Assert.Equal("0", (await RunAsync("status=* | stats count")).Rows[0][0]);
        Assert.Equal("0", (await RunAsync("no_such_key=* | stats count")).Rows[0][0]);
        Assert.Equal(all, (await RunAsync("host=* | stats count")).Rows[0][0]);
        Assert.Equal(all, (await RunAsync("bytes=* _time=* raw=* | stats count")).Rows[0][0]);
        Assert.Equal(all, (await RunAsync("status!=* NOT user=* | stats count")).Rows[0][0]);

        // An aggregate over a field reads the rows that have it.
        var over = await RunAsync("| stats count, dc(user) as users, dc(host) as hosts, avg(status) as s, values(user) as names");
        Assert.Equal([all, "0", "2", "NaN", "[]"], over.Rows[0]);
    }

    [Fact]
    public async Task RexGroupsAreColumnsAndAGroupThatDidNotMatchIsAFieldWithoutAValue()
    {
        var all = ClickHouseFixture.RowsPerCase.ToString(CultureInfo.InvariantCulture);
        const string Pattern = "| rex field=raw \"^(?<verb>[A-Z]+) (?<target>\\S+) (?<code>\\d+)\" ";

        var listing = await RunAsync(Pattern + "| head 1");
        Assert.Equal(["ts", "host", "program", "severity", "source_ip", "message", "verb", "target", "code"], listing.Columns);
        Assert.Equal(["GET", "/index.html", "200"], listing.Rows[0][6..]);

        Assert.Equal([["GET", "200", all]], (await RunAsync(Pattern + "| stats count by verb, code")).Rows);
        Assert.Equal([["200", "200"]], (await RunAsync(Pattern + "| stats avg(code) as mean, max(code) as top")).Rows);
        Assert.Equal(all, (await RunAsync(Pattern + "| where verb=GET code=2* | stats count")).Rows[0][0]);
        Assert.Equal([["/index.html"]], (await RunAsync(Pattern + "| table target | head 1")).Rows);

        // Rex on a keyword column, on an extras key and on the message all compile and run.
        Assert.Equal([["01", "100"], ["02", "100"]], (await RunAsync("| rex field=host \"-(?<n>\\d+)$\" | stats count by n | sort n")).Rows);
        Assert.Empty((await RunAsync("| rex field=no_such_key \"(?<k>.+)\" | stats count by k")).Rows);

        // No match: the group is empty, so the row is in no group and name=* does not find it.
        Assert.Empty((await RunAsync("| rex field=raw \"(?<nope>ZZZ)\" | stats count by nope")).Rows);
        Assert.Equal("0", (await RunAsync("| rex field=raw \"(?<nope>ZZZ)\" | where nope=* | stats count")).Rows[0][0]);
        Assert.Equal(all, (await RunAsync("| rex field=raw \"(?<nope>ZZZ)\" | where nope!=* | stats count")).Rows[0][0]);
        Assert.Equal("", (await RunAsync("| rex field=raw \"(?<nope>ZZZ)\" | head 1")).Rows[0][^1]);
    }

    [Fact]
    public async Task ValuesShowsAHundredAndMarksTheGroupThatHasMore()
    {
        // Two hundred instants in one group; fifty offsets a host; two hosts.
        var capped = await RunAsync("| stats values(_time) as seen, values(host) as hosts");
        Assert.True(capped.ValuesCapped);
        Assert.Equal([0], capped.CappedRows);
        Assert.Equal(Transpiler.ValuesCap, capped.Rows[0][0].Split(", ").Length - 1); // a hundred values and the mark
        Assert.EndsWith(", ...]", capped.Rows[0][0], StringComparison.Ordinal);
        Assert.Equal(2, capped.Rows[0][1].Split(", ").Length);

        var exactly = await RunAsync("| stats values(file.offset) as offsets");
        Assert.False(exactly.ValuesCapped); // exactly a hundred distinct offsets: at the cap, not over it
        Assert.Equal(Transpiler.ValuesCap, exactly.Rows[0][0].Split(", ").Length);

        var one = await RunAsync("| stats values(_time) as seen by host | sort host");
        Assert.Equal([], one.CappedRows); // a hundred a host
        Assert.Equal(2, one.Rows.Count);

        Assert.Equal([["web-01", "[web-01]"]], (await RunAsync("| stats values(host) as hosts by host | where hosts=web-01")).Rows);
    }

    [Fact]
    public async Task TimechartKeepsTenSeriesAndFoldsTheRestIntoOne()
    {
        // A hundred offsets, each on two rows (one a month): ten keep their series, ninety fold into the rest.
        var query = Transpile("| timechart span=1d count by file.offset");
        var table = await ch.QueryClient().RunAsync(query, [Case], Ct);
        var shape = query.Timechart!;
        var top = table.Rows.Where(r => r[shape.InTop!.Value] == "1").ToList();
        var rest = table.Rows.Where(r => r[shape.InTop!.Value] == "0").ToList();
        Assert.Equal(Transpiler.TopSeries, top.Select(r => r[shape.Series!.Value]).Distinct().Count());
        Assert.Equal(2 * Transpiler.TopSeries, top.Sum(r => int.Parse(r[shape.Value], CultureInfo.InvariantCulture)));
        Assert.Equal(ClickHouseFixture.RowsPerCase - (2 * Transpiler.TopSeries), rest.Sum(r => int.Parse(r[shape.Value], CultureInfo.InvariantCulture)));
        Assert.All(rest, r => Assert.Equal("0", r[shape.Series!.Value])); // the type's empty value, never a real series
        Assert.Equal(["0", "1", "2", "3", "4", "5", "6", "7", "8", "9"], top.Select(r => r[shape.Series!.Value]).Distinct().Order(StringComparer.Ordinal)); // ties go to the smaller key

        var pivot = TimechartPivot.Pivot(table, query);
        Assert.Equal(1 + Transpiler.TopSeries + 1, pivot.Columns.Count);
        Assert.Equal(TimechartPivot.Rest, pivot.Columns[^1]);
        Assert.Equal(["2026-01-01 00:00:00.000", "2026-02-01 00:00:00.000"], pivot.Rows.Select(r => r[0]));
        Assert.All(pivot.Rows, r => Assert.Equal("90", r[^1]));

        // Ten or fewer series: nothing is folded.
        var hosts = await ch.QueryClient().RunAsync(Transpile("| timechart span=1h count by host"), [Case], Ct);
        Assert.DoesNotContain(hosts.Rows, r => r[1] == "0");
        Assert.Equal(ClickHouseFixture.RowsPerCase, hosts.Rows.Sum(r => int.Parse(r[3], CultureInfo.InvariantCulture)));

        var plain = await RunAsync("| timechart span=1h count");
        Assert.Equal(["_time", "count"], plain.Columns);
        Assert.Equal(["2026-01-01 00:00:00.000", "60"], plain.Rows[0]);
        Assert.Equal(["2026-01-01 01:00:00.000", "40"], plain.Rows[1]);

        // A series field without a value is no series; an address and a keyword are series like any other.
        Assert.Empty((await RunAsync("| timechart span=1d count by user")).Rows);
        Assert.Empty((await RunAsync("| timechart span=1d count by src_ip")).Rows);
        Assert.Equal(4, (await RunAsync("| timechart span=1d dc(file.offset) as n by host")).Rows.Count);
    }

    public static TheoryData<string, string, string, string> Buckets() => new()
    {
        // zone, span, an instant (UTC), the start of its bucket (UTC)
        { "Europe/Bucharest", "1d", "2026-03-28 23:30:00", "2026-03-28 22:00:00" }, // 29 March, local 01:30, the short day begins at local midnight
        { "Europe/Bucharest", "1d", "2026-03-29 20:59:59", "2026-03-28 22:00:00" }, // its last second: 23 hours later
        { "Europe/Bucharest", "1d", "2026-03-29 21:00:00", "2026-03-29 21:00:00" }, // 30 March starts at 21:00 UTC now
        { "Europe/Bucharest", "1d", "2026-10-24 21:00:00", "2026-10-24 21:00:00" }, // 25 October, the long day
        { "Europe/Bucharest", "1d", "2026-10-25 21:59:59", "2026-10-24 21:00:00" }, // its last second: 25 hours later
        { "Europe/Bucharest", "1d", "2026-10-25 22:00:00", "2026-10-25 22:00:00" },
        { "America/Santiago", "1d", "2026-09-06 12:00:00", "2026-09-06 04:00:00" }, // midnight does not exist that day: the day starts at 01:00 local
        { "America/Santiago", "1d", "2026-09-06 03:59:59", "2026-09-05 04:00:00" },
        { "Europe/Bucharest", "1w", "2026-03-26 01:30:00", "2026-03-22 22:00:00" }, // Monday 23 March, local midnight
        { "Europe/Bucharest", "1w", "2026-03-29 23:00:00", "2026-03-29 21:00:00" }, // the next Monday, in summer time
        { "+02:00", "1d", "2026-03-28 23:30:00", "2026-03-28 22:00:00" },
        { "+02:00", "1d", "2026-03-29 21:30:00", "2026-03-28 22:00:00" }, // an offset has no clock change: every day is 24 hours
        { "+02:00", "1d", "2026-03-29 22:00:00", "2026-03-29 22:00:00" },
        { "-05:30", "1w", "2026-03-26 01:30:00", "2026-03-23 05:30:00" },
        { "UTC", "1d", "2026-03-29 23:59:59", "2026-03-29 00:00:00" },
        { "UTC", "3d", "2026-03-29 23:59:59", "2026-03-29 00:00:00" }, // three-day buckets count from 1970-01-01: day 20,541 starts one
        { "UTC", "3d", "2026-03-28 23:59:59", "2026-03-26 00:00:00" },
        { "Europe/Bucharest", "1h", "2026-03-29 00:30:00", "2026-03-29 00:00:00" }, // under a day: UTC, whatever the zone
        { "+05:30", "1h", "2026-03-29 00:45:00", "2026-03-29 00:00:00" }, // an hourly bucket starts at :30 local there
    };

    [Theory]
    [MemberData(nameof(Buckets))]
    public async Task ABucketStartsWhereTheDisplayZonesCalendarSays(string zone, string span, string instant, string bucket)
    {
        // The bucket expression exactly as the transpiler writes it, over one instant instead of the table. The
        // runtime here may not know the zone, so it is handed over by name with a daylight rule: only the id reaches the SQL.
        var display = zone.Contains('/', StringComparison.Ordinal) ? NamedZone(zone) : TimeZones.Find(zone);
        var query = Transpile($"| timechart span={span} count", display);
        const string Head = "SELECT ";
        var end = query.Sql.IndexOf(" AS `_time`", StringComparison.Ordinal);
        var expression = query.Sql[Head.Length..end];

        await using var connection = new ClickHouseConnection(ch.HttpConnectionString(ClickHouseFixture.QueryUser, ClickHouseFixture.QueryPassword));
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT toString(toTimeZone({expression}, 'UTC')) FROM (SELECT toDateTime64({{instant:String}}, 3, 'UTC') AS ts)";
        foreach (var p in query.Parameters.Where(p => p.Name is "span" or "tz" or "utc" or "shift").Append(new QueryParameter("instant", "String", instant)))
        {
            var parameter = (ClickHouseDbParameter)command.CreateParameter();
            parameter.ParameterName = p.Name;
            parameter.ClickHouseType = p.Type;
            parameter.Value = p.Type switch
            {
                "UInt32" => uint.Parse(p.Value, CultureInfo.InvariantCulture),
                "Int64" => long.Parse(p.Value, CultureInfo.InvariantCulture),
                _ => p.Value,
            };
            command.Parameters.Add(parameter);
        }

        var start = (string)(await command.ExecuteScalarAsync(Ct))!;
        output.WriteLine($"{zone} span={span}: {instant} -> {start}");
        Assert.Equal(bucket, start);
    }

    [Fact]
    public async Task APatternTheCheckerLetsThroughAndTheServerRefusesIsAQueryError()
    {
        // The transpiler never writes such a pattern (the checker refuses it first); a result edited to carry one
        // stands in for a gap in the checker. The gate parses and passes it, the server refuses to compile it, and
        // the caller gets a query error with the server's code, not a driver exception.
        var query = Transpile("| rex field=raw \"(?<a>x)\" | stats count by a");
        var broken = query with { Parameters = [.. query.Parameters.Select(p => p.Value == "(?<a>x)" ? p with { Value = "(?<a>x)(?=y)" } : p)] };
        var failed = await Assert.ThrowsAsync<QueryFailedException>(() => ch.QueryClient().RunAsync(broken, [Case], Ct));
        output.WriteLine($"{failed.ErrorCode}: {failed.Message}");
        Assert.Equal(427, failed.ErrorCode); // CANNOT_COMPILE_REGEXP
    }

    [Fact]
    public async Task TheLimitsOfAPatternAreInsideWhatTheServerCompiles()
    {
        // The largest patterns the checker accepts: sixteen groups, a thousand bytes, a repetition of a hundred.
        var groups = string.Concat(Enumerable.Range(0, Re2Syntax.MaxGroups).Select(i => $"(?<g{i}>\\w?)"));
        Assert.Equal(ClickHouseFixture.RowsPerCase.ToString(CultureInfo.InvariantCulture), (await RunAsync($"| rex field=raw \"{groups}\" | where g0=* | stats count")).Rows[0][0]);
        var longest = "(?<a>G)" + new string('.', Re2Syntax.MaxPatternBytes - 7);
        Assert.Equal("0", (await RunAsync($"| rex field=raw \"{longest}\" | where a=* | stats count")).Rows[0][0]);
        Assert.Single((await RunAsync($"| rex field=raw \"(?<a>(?:[A-Z ]|\\W|\\d){{1,{Re2Syntax.MaxRepeat}}})\" | stats count by a")).Rows);
    }

    private static TimeZoneInfo NamedZone(string id)
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            DateTime.MinValue.Date,
            DateTime.MaxValue.Date,
            TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 3, 5, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 4, 0, 0), 10, 5, DayOfWeek.Sunday));
        return TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromHours(2), id, id, id, [rule]);
    }
}
