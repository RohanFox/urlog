using System.Buffers;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Urlog.Query;

namespace Urlog.Query.Tests;

/// <summary>
/// The statements a search builds beside the query's own: their texts, the cursor that pages a listing, the time
/// windows a newest-first listing is read through, and the histogram's span. The texts are pinned, and like every
/// other SQL of the query path they hold nothing the analyst or a cursor supplied.
/// </summary>
public sealed partial class SearchPlanTests
{
    private const string Filter = "case_id = {case:UInt64} AND ts >= {from:DateTime64(3)} AND ts < {to:DateTime64(3)}";
    private const string Columns = "ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, message, raw, http_method, http_version, http_response_body_bytes, http_referrer, user_agent, url_query, syslog_facility, process_pid, file_id, file_offset, host_id, extras";

    private static readonly DateTimeOffset From = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly SearchValues<char> SqlCharacters =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 _.,(){}[]:=<>!`");

    [GeneratedRegex("`([^`]*)`")]
    private static partial Regex Backquoted();

    private static SearchPlan Plan(string spl, TimeZoneInfo? zone = null) =>
        SearchPlan.Create(spl, new TranspileOptions(7, From, To, zone ?? TimeZoneInfo.Utc, new DateTimeOffset(2026, 3, 15, 12, 0, 0, TimeSpan.Zero)));

    private static Cursor At(SearchPlan plan, params string[] sortValues) =>
        new(From.ToUnixTimeMilliseconds(), To.ToUnixTimeMilliseconds(), "UTC", 0, Cursor.Bind(7, "q"), [.. sortValues], "2026-03-09 10:11:12.345", 4, 9000, 200, 600);

    [Fact]
    public void ThePageOfADefaultListingIsEveryColumnInKeyOrder()
    {
        var plan = Plan("");
        Assert.True(plan.TimeOrdered);
        Assert.False(plan.Unfiltered is false);
        var page = plan.Page(200);
        Assert.Equal($"SELECT {Columns} FROM events_v WHERE {Filter} ORDER BY ts DESC, file_id DESC, file_offset DESC LIMIT {{s_limit:UInt64}}", page.Sql);
        Assert.Equal(["case", "from", "to", "s_limit"], page.Parameters.Select(p => p.Name));
        Assert.Equal("200", page.Parameters[^1].Value);

        // The cells are named by the catalog, and the three key cells are where the plan says.
        Assert.Equal("_time", plan.PageColumns[plan.TsColumn]);
        Assert.Equal("file.id", plan.PageColumns[plan.FileIdColumn]);
        Assert.Equal("file.offset", plan.PageColumns[plan.FileOffsetColumn]);
        Assert.Equal(plan.PageColumns.Length, plan.SortColumns);
        Assert.Equal("extras", plan.PageColumns[^1]);
    }

    [Fact]
    public void ACursorAsksForTheRowsStrictlyAfterItsKey()
    {
        var plan = Plan("");
        var page = plan.Page(200, At(plan));

        // The plain bound first, for the key range; then the full comparison, which is what makes the order total.
        Assert.Equal(
            $"SELECT {Columns} FROM events_v WHERE {Filter} AND ts <= {{s_ts:DateTime64(3)}} AND (ts < {{s_ts:DateTime64(3)}} OR (ts = {{s_ts:DateTime64(3)}} AND (file_id < {{s_fid:UInt64}} OR (file_id = {{s_fid:UInt64}} AND file_offset < {{s_off:UInt64}})))) ORDER BY ts DESC, file_id DESC, file_offset DESC LIMIT {{s_limit:UInt64}}",
            page.Sql);
        Assert.Equal(
            [("s_ts", "DateTime64(3)", "2026-03-09 10:11:12.345"), ("s_fid", "UInt64", "4"), ("s_off", "UInt64", "9000"), ("s_limit", "UInt64", "200")],
            page.Parameters.Skip(3).Select(p => (p.Name, p.Type, p.Value)));
    }

    [Fact]
    public void AWindowBoundsTheListingAndKeysFirstReadsTheKeyColumnsOnly()
    {
        var plan = Plan("status=503");
        var window = plan.Page(200, null, new DateTimeOffset(2026, 3, 31, 23, 50, 0, TimeSpan.Zero));
        Assert.Contains("AND ts >= {s_wfrom:DateTime64(3)} ORDER BY", window.Sql, StringComparison.Ordinal);
        Assert.Equal("2026-03-31 23:50:00.000", window.Parameters.Single(p => p.Name == "s_wfrom").Value);

        var boundary = plan.PageBoundary(200, At(plan));
        Assert.StartsWith("SELECT min(ts), count() FROM (SELECT ts, file_id, file_offset FROM events_v WHERE case_id = {case:UInt64}", boundary.Sql, StringComparison.Ordinal);
        Assert.EndsWith("ORDER BY ts DESC, file_id DESC, file_offset DESC LIMIT {s_limit:UInt64})", boundary.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("raw", boundary.Sql, StringComparison.Ordinal);
        Assert.False(plan.Unfiltered);
    }

    [Fact]
    public void ASortPutsItsKeysBeforeTheThreeAndTheCursorComparesEachInItsDirection()
    {
        var plan = Plan("| sort -bytes host");
        Assert.False(plan.TimeOrdered);
        Assert.Equal(2, plan.SortKeys);
        Assert.Equal(["sort:http.response.body.bytes", "sort:host.name"], plan.PageColumns.Skip(plan.SortColumns));

        var page = plan.Page(50, At(plan, "1024", "web-01"));
        Assert.EndsWith(
            "AND (http_response_body_bytes < {s_k0:UInt64} OR (http_response_body_bytes = {s_k0:UInt64} AND (host > {s_k1:String} OR (host = {s_k1:String} AND (ts < {s_ts:DateTime64(3)} OR (ts = {s_ts:DateTime64(3)} AND (file_id < {s_fid:UInt64} OR (file_id = {s_fid:UInt64} AND file_offset < {s_off:UInt64})))))))) "
            + "ORDER BY http_response_body_bytes DESC, host ASC, ts DESC, file_id DESC, file_offset DESC LIMIT {s_limit:UInt64}",
            page.Sql,
            StringComparison.Ordinal);
        Assert.Equal("1024", page.Parameters.Single(p => p.Name == "s_k0").Value);

        // No plain time bound here: the order is not by time, so it would cut rows the listing still owes.
        Assert.DoesNotContain("ts <= {s_ts", page.Sql, StringComparison.Ordinal);

        // A cursor with another number of sort values is not this search's.
        Assert.Throws<CursorException>(() => plan.Page(50, At(plan, "1024")));
    }

    [Fact]
    public void TheHistogramCountsBySeverityOverTheSearchsFilter()
    {
        var hours = Plan("error").Histogram(new HistogramSpan(3, 'h'));
        Assert.StartsWith("SELECT toStartOfInterval(ts, toIntervalHour({s_h_span:UInt32})) AS `c0`, severity, count() FROM events_v WHERE case_id = {case:UInt64}", hours.Sql, StringComparison.Ordinal);
        Assert.EndsWith("GROUP BY `c0`, severity ORDER BY `c0` ASC, severity ASC", hours.Sql, StringComparison.Ordinal);
        Assert.Equal("3", hours.Parameters.Single(p => p.Name == "s_h_span").Value);

        // A day is cut on the display zone's calendar, by the timechart's own rule.
        // (A zone object with an IANA id: the build's invariant globalization cannot look one up on Windows.)
        var bucharest = TimeZoneInfo.CreateCustomTimeZone("Europe/Bucharest", TimeSpan.FromHours(2), "Europe/Bucharest", "EET");
        var days = Plan("", bucharest).Histogram(new HistogramSpan(1, 'd'));
        Assert.Contains("toTimeZone(toStartOfInterval(ts, toIntervalDay({s_h_span:UInt32}), {s_h_tz:String}), {s_h_utc:String})", days.Sql, StringComparison.Ordinal);
        Assert.Equal("Europe/Bucharest", days.Parameters.Single(p => p.Name == "s_h_tz").Value);
    }

    [Fact]
    public void TheCountsAreOneStatementOfGroupingSetsAndWithoutFieldsTheTotalAlone()
    {
        var plan = Plan("error");
        var catalog = FieldCatalog.Embedded;
        var counts = plan.Counts([catalog.Resolve("host"), catalog.Resolve("status")]);
        Assert.StartsWith("SELECT grouping(host) AS `c0`, grouping(http_status) AS `c1`, host, http_status, count() AS `c2` FROM events_v WHERE ", counts.Sql, StringComparison.Ordinal);
        Assert.EndsWith("GROUP BY GROUPING SETS ((), (host), (http_status)) ORDER BY `c2` DESC LIMIT {s_top:UInt64} BY `c0`, `c1`", counts.Sql, StringComparison.Ordinal);
        Assert.Equal("11", counts.Parameters.Single(p => p.Name == "s_top").Value);

        Assert.StartsWith("SELECT count() FROM events_v WHERE case_id = {case:UInt64}", plan.Counts([]).Sql, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => plan.Counts([catalog.Resolve("x_not_in_the_catalog")]));
    }

    [Fact]
    public void AnExportOfAListingHasItsColumnsInTheListingsWholeOrder()
    {
        // The query's own statement orders by time alone; rows that share an instant would come in the order the
        // parts were read, and two exports of one search would differ. The export orders by the whole key.
        var all = Plan("| table _time user.name").Export(1_000_000);
        Assert.Equal(
            $"SELECT ts AS `_time`, user_name AS `user.name` FROM events_v WHERE {Filter} ORDER BY ts DESC, file_id DESC, file_offset DESC LIMIT {{s_limit:UInt64}}",
            all.Sql);
        Assert.Equal("1000000", all.Parameters.Single(p => p.Name == "s_limit").Value);
        Assert.DoesNotContain(all.Parameters, p => p.Name == "limit");

        // The listing's default columns, and a sort's keys before the three.
        Assert.StartsWith("SELECT ts, host, program, severity, source_ip, message FROM events_v WHERE ", Plan("").Export(10).Sql, StringComparison.Ordinal);
        Assert.EndsWith(
            " ORDER BY http_response_body_bytes DESC, host ASC, ts DESC, file_id DESC, file_offset DESC LIMIT {s_limit:UInt64}",
            Plan("| sort -bytes host").Export(10).Sql,
            StringComparison.Ordinal);

        // The smaller of the query's head and the limit.
        Assert.Equal("50", Plan("| head 50").Export(1_000_000).Parameters.Single(p => p.Name == "s_limit").Value);
        Assert.Equal("1000", Plan("| head 5000").Export(1000).Parameters.Single(p => p.Name == "s_limit").Value);
    }

    [Fact]
    public void AnAggregationsOrderEndsInATotalTieBreakForTheStreamAndTheExport()
    {
        // No sort: the by fields, then the aggregates.
        var plain = Plan("| stats count by host");
        Assert.EndsWith(" GROUP BY host ORDER BY `host.name` ASC, `c0` ASC", plain.Query.Sql, StringComparison.Ordinal);

        // A sort first, then what it left out: two hosts with the same count never change places.
        var sorted = Plan("| stats count, dc(user) as users by host, program | sort -count | head 20");
        Assert.EndsWith(" ORDER BY `c0` DESC, `host.name` ASC, `program` ASC, `c1` ASC LIMIT {limit:UInt64}", sorted.Query.Sql, StringComparison.Ordinal);

        // A key the sort already names is not named twice, and a column table left out is ordered by its expression.
        Assert.EndsWith(" ORDER BY `host.name` DESC, `c0` ASC", Plan("| stats count by host | sort -host").Query.Sql, StringComparison.Ordinal);
        Assert.EndsWith(" ORDER BY `host.name` ASC, count() ASC", Plan("| stats count by host | table host").Query.Sql, StringComparison.Ordinal);

        // The export of an aggregation is that statement; it has no listing to page.
        Assert.Equal(sorted.Query.Sql, sorted.Export(1_000_000).Sql);
        Assert.Throws<InvalidOperationException>(() => plain.Page(10));

        // Outside the search API the transpiler writes what it always wrote: the option is the plan's.
        var options = new TranspileOptions(7, From, To, TimeZoneInfo.Utc, From);
        Assert.EndsWith(" GROUP BY host", Transpiler.Transpile("| stats count by host", options).Sql, StringComparison.Ordinal);
        Assert.EndsWith(" GROUP BY host ORDER BY `host.name` ASC, `c0` ASC", Transpiler.Transpile("| stats count by host", options with { TotalOrder = true }).Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void ARexGroupIsACellOfThePageWhateverTableLists()
    {
        var plan = Plan("| rex field=message \"user (?<who>\\w+)\" | table host");
        Assert.Contains("who", plan.PageColumns);
        var page = plan.Page(10);
        Assert.Contains("extractGroups(", page.Sql, StringComparison.Ordinal);

        // The pattern stays bound although table left the group out of the query's own columns.
        Assert.Contains(page.Parameters, p => p.Value == "user (?<who>\\w+)");
    }

    [Theory]
    [InlineData("")]
    [InlineData("status=503 \"GET /admin\" | sort -bytes")]
    [InlineData("x_custom=`weird` | rex field=message \"(?<grp>a+)\" | sort grp | head 40")]
    [InlineData("src_ip=10.0.0.0/8 NOT user=root' OR 1=1-- | sort user")]
    public void NoStatementHoldsAnythingTheAnalystOrACursorSupplied(string spl)
    {
        var plan = Plan(spl);
        const string Hostile = "x' OR 1=1; DROP TABLE events --`\\";
        var cursor = new Cursor(From.ToUnixTimeMilliseconds(), To.ToUnixTimeMilliseconds(), "UTC", 0, "b", [.. Enumerable.Repeat(Hostile, plan.SortKeys)], "2026-03-09 10:11:12.345", 4, 9000, 200, 600);
        var statements = new List<SearchStatement>
        {
            plan.Page(200), plan.Page(200, cursor), plan.Page(200, cursor, From.AddDays(3)), plan.Histogram(new HistogramSpan(1, 'h')), plan.Count(),
            plan.Counts([FieldCatalog.Embedded.Resolve("host"), FieldCatalog.Embedded.Resolve("source.ip")]), plan.Export(1000),
        };
        if (plan.TimeOrdered)
        {
            statements.Add(plan.PageBoundary(200, cursor));
        }

        foreach (var statement in statements)
        {
            Assert.True(statement.Sql.AsSpan().IndexOfAnyExcept(SqlCharacters) < 0, statement.Sql);
            Assert.DoesNotContain("DROP", statement.Sql, StringComparison.Ordinal);
            Assert.All(Backquoted().Matches(statement.Sql), m => Assert.True(Names.IsGenerated(m.Groups[1].Value) || FieldCatalog.Embedded.Contains(m.Groups[1].Value), m.Value));

            // Every placeholder is bound, once, and nothing is bound that the text does not name.
            Assert.All(statement.Parameters, p => Assert.Contains("{" + p.Name + ":" + p.Type + "}", statement.Sql, StringComparison.Ordinal));
            Assert.Equal(statement.Parameters.Length, statement.Parameters.Select(p => p.Name).Distinct().Count());
        }
    }

    [Fact]
    public void ACursorRoundTripsAndBelongsToOneCaseAndOneText()
    {
        var cursor = new Cursor(1, 2_000, "Europe/Bucharest", 1_500, Cursor.Bind(7, "error | sort host"), ["web-01"], "2026-03-09 10:11:12.345", 4, 9000, 200, 600);
        var text = cursor.Encode();
        Assert.DoesNotContain('=', text);
        Assert.DoesNotContain('+', text);
        var back = Cursor.Decode(text, 7, "error | sort host");
        Assert.Equal(cursor with { SortValues = [] }, back with { SortValues = [] });
        Assert.Equal<string>(cursor.SortValues, back.SortValues);

        Assert.Throws<CursorException>(() => Cursor.Decode(text, 8, "error | sort host"));
        Assert.Throws<CursorException>(() => Cursor.Decode(text, 7, "error | sort user"));
        Assert.Throws<CursorException>(() => Cursor.Decode("", 7, "q"));
        Assert.Throws<CursorException>(() => Cursor.Decode("bm90LWpzb24", 7, "q"));
        Assert.Throws<CursorException>(() => Cursor.Decode("not base64url!", 7, "q"));
        Assert.Throws<CursorException>(() => Cursor.Decode((cursor with { Ts = "yesterday" }).Encode(), 7, "error | sort host"));
        Assert.Throws<CursorException>(() => Cursor.Decode((cursor with { Sent = -1 }).Encode(), 7, "error | sort host"));
        Assert.Throws<CursorException>(() => Cursor.Decode(new string('A', 9000), 7, "q"));
    }

    [Fact]
    public void TheFirstWindowIsThirtyTwoPagesAtTheCasesDensity()
    {
        // 25.2 M events over 113 days, pages of 200: 32 pages are 6,400 events, about 41 minutes.
        var seed = ListingWindow.Seed(25_216_914, TimeSpan.FromDays(113), 200, TimeSpan.FromDays(113));
        Assert.NotNull(seed);
        Assert.InRange(seed.Value.TotalSeconds, 2470, 2485);

        // The window that found the previous page is where the next one starts.
        Assert.Equal(TimeSpan.FromSeconds(600), ListingWindow.Seed(25_216_914, TimeSpan.FromDays(113), 200, TimeSpan.FromDays(100), TimeSpan.FromSeconds(600)));

        // No use: nothing to size it from, or it would be the range anyway.
        Assert.Null(ListingWindow.Seed(0, TimeSpan.FromDays(113), 200, TimeSpan.FromDays(113)));
        Assert.Null(ListingWindow.Seed(1000, TimeSpan.Zero, 200, TimeSpan.FromDays(1)));
        Assert.Null(ListingWindow.Seed(1000, TimeSpan.FromDays(10), 200, TimeSpan.FromDays(10)));
        Assert.Equal(TimeSpan.FromSeconds(1), ListingWindow.Seed(long.MaxValue / 4, TimeSpan.FromSeconds(10), 200, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void AWindowThatMissedGrowsByWhatItFoundAndAtLeastSixteenTimes()
    {
        var window = TimeSpan.FromMinutes(10);
        var range = TimeSpan.FromDays(100);

        // Nothing found, or most of a page: the fixed growth.
        Assert.Equal(TimeSpan.FromMinutes(160), ListingWindow.Next(window, 0, 200, range, 1));
        Assert.Equal(TimeSpan.FromMinutes(160), ListingWindow.Next(window, 150, 200, range, 1));

        // Four rows of 200: fifty times too short, and twice that asked for.
        Assert.Equal(TimeSpan.FromMinutes(1000), ListingWindow.Next(window, 4, 200, range, 1));

        // Three windows at most, and never one as long as what is left.
        Assert.NotNull(ListingWindow.Next(window, 0, 200, range, 2));
        Assert.Null(ListingWindow.Next(window, 0, 200, range, ListingWindow.MaxWindows));
        Assert.Null(ListingWindow.Next(window, 0, 200, TimeSpan.FromMinutes(100), 1));
        Assert.Equal((32, 16, 3), (ListingWindow.SeedPages, ListingWindow.Growth, ListingWindow.MaxWindows));
    }

    [Theory]
    [InlineData(60, "1s")]
    [InlineData(300, "1s")]
    [InlineData(301, "5s")]
    [InlineData(3600, "30s")]
    [InlineData(86_400, "5m")]
    [InlineData(7 * 86_400, "1h")]
    [InlineData(113 * 86_400, "12h")]
    [InlineData(300 * 86_400, "1d")]
    [InlineData(3 * 365 * 86_400, "1w")]
    [InlineData(20 * 365 * 86_400, "4w")]
    [InlineData(60L * 365 * 86_400, "12w")]
    public void TheHistogramsSpanIsTheSmallestThatGivesAtMostThreeHundredBuckets(long seconds, string expected)
    {
        var span = HistogramSpan.For(From, From.AddSeconds(seconds));
        Assert.Equal(expected, span.Text);
        Assert.True((seconds + span.Seconds - 1) / span.Seconds <= HistogramSpan.MaxBuckets);
    }
}
