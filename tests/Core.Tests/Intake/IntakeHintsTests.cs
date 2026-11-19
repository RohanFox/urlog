using Urlog.Core.Intake;

namespace Urlog.Core.Tests.Intake;

/// <summary>The zone offset and the year come from the file's own record, never from the day the test runs on.</summary>
public sealed class IntakeHintsTests
{
    private static readonly DateTime Summer = new(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Winter = new(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);

    private const string Berlin = "Europe/Berlin";

    private const string Tokyo = "Asia/Tokyo";

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("UTC", 0)]
    [InlineData("+02:00", 120)]
    [InlineData("-05:30", -330)]
    public void AFixedZoneHasOneOffset(string? zone, int minutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(minutes), IntakeHints.OffsetAt(zone, Summer));
        Assert.Equal(TimeSpan.FromMinutes(minutes), IntakeHints.OffsetAt(zone, Winter));
        Assert.False(IntakeHints.SpansTransition(zone, Winter, Summer));
    }

    [Fact]
    public void ANamedZoneIsResolvedAtTheFilesTime()
    {
        Assert.Equal(TimeSpan.FromHours(2), IntakeHints.OffsetAt(Berlin, Summer));
        Assert.Equal(TimeSpan.FromHours(1), IntakeHints.OffsetAt(Berlin, Winter));
    }

    [Fact]
    public void AnUnknownZoneIsReported()
    {
        Assert.False(IntakeHints.IsKnownZone("Mars/Olympus"));
        Assert.True(IntakeHints.IsKnownZone(Berlin));
        Assert.True(IntakeHints.IsKnownZone("+01:00"));
        Assert.Throws<TimeZoneNotFoundException>(() => IntakeHints.OffsetAt("Mars/Olympus", Summer));
    }

    [Fact]
    public void AFileAcrossATransitionIsFlaggedAndOneBesideItIsNot()
    {
        // Europe/Berlin left summer time on 2026-10-25 at 01:00 UTC.
        var before = new DateTime(2026, 10, 24, 22, 0, 0, DateTimeKind.Utc);
        var after = new DateTime(2026, 10, 25, 6, 0, 0, DateTimeKind.Utc);

        Assert.True(IntakeHints.SpansTransition(Berlin, before, after));
        Assert.False(IntakeHints.SpansTransition(Berlin, before.AddDays(-7), before));
        Assert.False(IntakeHints.SpansTransition(Berlin, after, after.AddDays(30)));
        Assert.True(IntakeHints.SpansTransition(Berlin, Winter, Summer));
        Assert.True(IntakeHints.SpansTransition(Berlin, Winter, Winter.AddYears(3)));
        Assert.False(IntakeHints.SpansTransition(Tokyo, Winter, Summer));
        Assert.False(IntakeHints.SpansTransition(Berlin, after, before));
    }

    [Fact]
    public void TheYearIsTheGivenOneThenTheFilesThenTheArrivals()
    {
        var received = new DateTime(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc);
        var fileTime = new DateTime(2025, 3, 2, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(new YearHint(2019, false), IntakeHints.Year(2019, fileTime, "Mar  1 00:00:01 host a: b"u8, received));
        Assert.Equal(new YearHint(2025, false), IntakeHints.Year(null, fileTime, "Mar  1 00:00:01 host a: b"u8, received));
        Assert.Equal(new YearHint(2026, true), IntakeHints.Year(null, null, "Mar  1 00:00:01 host a: b"u8, received));
    }

    [Fact]
    public void AFileThatBeganBeforeNewYearStartsInTheYearBefore()
    {
        var received = new DateTime(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc);
        var january = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(2025, IntakeHints.Year(null, january, "Dec 28 23:59:59 host a: b\nJan  1 00:00:00 host a: c"u8, received).Year);
        Assert.Equal(2025, IntakeHints.Year(null, january, "<34>Dec 28 23:59:59 host a: b"u8, received).Year);
        Assert.Equal(2026, IntakeHints.Year(null, january, "Jan  1 00:00:00 host a: c"u8, received).Year);
        Assert.Equal(2026, IntakeHints.Year(null, january, "2025-12-28T23:59:59Z host a: b"u8, received).Year);
    }

    [Theory]
    [InlineData("Nov 12 10:00:00 h p: m", 11)]
    [InlineData("<13>Feb  3 10:00:00 h p: m", 2)]
    [InlineData("November 12", 0)]
    [InlineData("192.0.2.1 - - [12/Nov/2026:10:00:00 +0000]", 0)]
    [InlineData("", 0)]
    [InlineData("<1234567>Nov 12", 0)]
    public void FirstMonth(string line, int expected) =>
        Assert.Equal(expected, IntakeHints.FirstMonth(System.Text.Encoding.UTF8.GetBytes(line)));

    [Fact]
    public void FirstMonthReadsUtf16()
    {
        var utf16 = System.Text.Encoding.Unicode.GetPreamble().Concat(System.Text.Encoding.Unicode.GetBytes("Oct  4 10:00:00 h p: m\r\n")).ToArray();

        Assert.Equal(10, IntakeHints.FirstMonth(utf16));
    }
}
