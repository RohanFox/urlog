using Urlog.Query;

namespace Urlog.Query.Tests;

/// <summary>
/// What the vectors cannot hold: a zone with a calendar of its own (the vectors run where the runtime may have no
/// zone database, so they use UTC and fixed offsets), and the pivot the reader of a timechart does.
/// </summary>
public sealed class TimechartTests
{
    private static readonly DateTimeOffset From = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);

    private static TranspileResult Transpile(string spl, TimeZoneInfo zone) =>
        Transpiler.Transpile(spl, new TranspileOptions(1, From, To, zone, To));

    /// <summary>A zone as the runtime would hand it over where it knows the id: named like an IANA zone, with a daylight rule.</summary>
    private static TimeZoneInfo Zone(string id)
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            DateTime.MinValue.Date,
            DateTime.MaxValue.Date,
            TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 3, 5, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 4, 0, 0), 10, 5, DayOfWeek.Sunday));
        return TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromHours(2), id, id, id, [rule]);
    }

    [Fact]
    public void ADayIsCutOnTheDisplayZonesCalendarAndReturnedAsAnInstant()
    {
        var day = Transpile("| timechart span=1d count", Zone("Europe/Bucharest"));
        Assert.StartsWith("SELECT toTimeZone(toStartOfInterval(ts, toIntervalDay({span:UInt32}), {tz:String}), {utc:String}) AS `_time`", day.Sql, StringComparison.Ordinal);
        Assert.Contains(day.Parameters, p => p is { Name: "tz", Type: "String", Value: "Europe/Bucharest" });
        Assert.Contains(day.Parameters, p => p is { Name: "utc", Value: "UTC" });

        // A week comes back from the server as a date: it is made an instant again in the same zone.
        var week = Transpile("| timechart span=2w count", Zone("Europe/Bucharest"));
        Assert.StartsWith("SELECT toTimeZone(toDateTime(toStartOfInterval(ts, toIntervalWeek({span:UInt32}), {tz:String}), {tz:String}), {utc:String}) AS `_time`", week.Sql, StringComparison.Ordinal);
        Assert.Contains(week.Parameters, p => p is { Name: "span", Value: "2" });
    }

    [Fact]
    public void AnIanaZoneResolvesOnLinuxAndOnWindows()
    {
        // The build runs with invariant globalization. On Linux the id is read from the system's zone database. On
        // Windows the runtime would need ICU to map it to a Windows zone, so the id goes through the table of zone
        // ids instead. Either way the display zone the UI sends resolves, keeps its id on the way to the event
        // store, and an unknown one is refused before the transpiler sees it.
        var zone = TimeZones.Find("Europe/Bucharest");
        Assert.True(zone.SupportsDaylightSavingTime);
        Assert.Equal(TimeSpan.FromHours(3), zone.GetUtcOffset(new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(TimeSpan.FromHours(2), zone.GetUtcOffset(new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc)));
        var day = Transpile("| timechart span=1d count", zone);
        Assert.Contains(day.Parameters, p => p is { Name: "tz", Value: "Europe/Bucharest" });
        var unknown = Assert.Throws<ArgumentException>(() => TimeZones.Find("Mars/Olympus"));
        Assert.Contains("unknown timezone 'Mars/Olympus'", unknown.Message, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => TimeZones.Find("Nowhere/Atlantis"));
    }

    [Fact]
    public void UnderADayTheZoneDoesNotEnterTheSql()
    {
        var hour = Transpile("| timechart span=12h count", Zone("Europe/Bucharest"));
        Assert.StartsWith("SELECT toStartOfInterval(ts, toIntervalHour({span:UInt32})) AS `_time`", hour.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(hour.Parameters, p => p.Name is "tz" or "utc" or "shift");
    }

    [Fact]
    public void AZoneThatIsNeitherAnIanaIdNorAnOffsetIsRefusedForADayOrMore()
    {
        var odd = Zone("Our Head Office");
        var e = Assert.Throws<QueryException>(() => Transpile("| timechart span=1d count", odd));
        Assert.Contains("'Our Head Office' is neither an IANA zone", e.Message, StringComparison.Ordinal);
        Assert.Equal((12, 7), (e.Offset, e.Length));

        // The same zone is fine where no calendar is needed.
        Assert.Equal(ResultKind.Timechart, Transpile("| timechart span=1h count", odd).Kind);
    }

    [Fact]
    public void TheShapeSaysWhereTheColumnsAre()
    {
        var plain = Transpile("| timechart span=1h count", TimeZoneInfo.Utc);
        Assert.Equal(new TimechartShape(0, 1, null, null), plain.Timechart);
        Assert.Equal(["_time", "count"], plain.Columns);

        var series = Transpile("| timechart span=1h dc(user) as users by host", TimeZoneInfo.Utc);
        Assert.Equal(new TimechartShape(0, 3, 1, 2), series.Timechart);
        Assert.Equal(["_time", "in_top", "host.name", "users"], series.Columns);
        Assert.True(series.IsAggregate);
        Assert.Contains(series.Parameters, p => p is { Name: "top", Value: "10" });
    }

    [Fact]
    public void ThePivotGivesEachSeriesAColumnAndTheRestTheLast()
    {
        var query = Transpile("| timechart span=1h count by host", TimeZoneInfo.Utc);
        List<string[]> rows =
        [
            ["2026-03-01 00:00:00.000", "1", "web-02", "7"],
            ["2026-03-01 00:00:00.000", "1", "web-01", "5"],
            ["2026-03-01 00:00:00.000", "0", "", "40"],
            ["2026-03-01 01:00:00.000", "1", "web-01", "3"],
            ["2026-03-01 02:00:00.000", "0", "", "2"],
        ];
        var pivot = TimechartPivot.Pivot(new QueryTable(query.Columns, rows, TimeSpan.Zero), query);
        Assert.Equal(["_time", "web-01", "web-02", "other"], pivot.Columns);
        Assert.Equal(
            [
                ["2026-03-01 00:00:00.000", "5", "7", "40"],
                ["2026-03-01 01:00:00.000", "3", "", ""],
                ["2026-03-01 02:00:00.000", "", "", "2"],
            ],
            pivot.Rows);
    }

    [Fact]
    public void ARealSeriesCalledOtherKeepsItsNameAndTheRestSaysWhatItIs()
    {
        var query = Transpile("| timechart span=1h count by host", TimeZoneInfo.Utc);
        List<string[]> rows =
        [
            ["2026-03-01 00:00:00.000", "1", "other", "7"],
            ["2026-03-01 00:00:00.000", "0", "", "40"],
        ];
        var pivot = TimechartPivot.Pivot(new QueryTable(query.Columns, rows, TimeSpan.Zero), query);
        Assert.Equal(["_time", "other", "other (the rest)"], pivot.Columns);
        Assert.Equal(["2026-03-01 00:00:00.000", "7", "40"], pivot.Rows[0]);
    }

    [Fact]
    public void WithoutABySeriesThereIsNothingToPivot()
    {
        var query = Transpile("| timechart span=1h count", TimeZoneInfo.Utc);
        var table = new QueryTable(query.Columns, [["2026-03-01 00:00:00.000", "5"]], TimeSpan.Zero);
        Assert.Same(table, TimechartPivot.Pivot(table, query));
    }
}
