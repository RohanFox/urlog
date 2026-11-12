using System.Globalization;
using System.Text;
using Urlog.Sources.Nginx;

namespace Urlog.Sources.Tests.Nginx;

public sealed class ClfTimestampTests
{
    [Theory]
    [InlineData("01/Mar/2026:00:00:00 +0000", "2026-03-01T00:00:00Z")]
    [InlineData("31/Dec/2025:23:59:59 +0000", "2025-12-31T23:59:59Z")]
    [InlineData("01/Jan/1970:00:00:00 +0000", "1970-01-01T00:00:00Z")]
    [InlineData("29/Feb/2024:12:00:00 +0000", "2024-02-29T12:00:00Z")]
    [InlineData("01/Mar/2026:00:00:14 -0500", "2026-03-01T05:00:14Z")]
    [InlineData("01/Mar/2026:00:00:15 +0530", "2026-02-28T18:30:15Z")]
    [InlineData("01/Mar/2026:00:00:15 -1200", "2026-03-01T12:00:15Z")]
    [InlineData("01/Mar/2026:23:30:00 +1400", "2026-03-01T09:30:00Z")]
    [InlineData("30/Jun/2026:23:59:60 +0000", "2026-07-01T00:00:00Z")]
    [InlineData("15/Aug/2038:03:14:08 +0000", "2038-08-15T03:14:08Z")]
    [InlineData("10/Oct/2010:10:10:10 +0000", "2010-10-10T10:10:10Z")]
    public void ParsesEveryMonthOffsetAndTheLeapSecond(string text, string utc)
    {
        Assert.True(ClfTimestamp.TryParse(Encoding.ASCII.GetBytes(text), out var ms));
        var expected = DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUnixTimeMilliseconds();
        Assert.Equal(expected, ms);
    }

    [Fact]
    public void AgreesWithDateTimeOffsetOverAWideRange()
    {
        // Every day of 1990-2040 at a fixed time, every month name, both signs: the hand-rolled arithmetic vs the BCL.
        var months = new[] { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
        var rng = new Random(7);
        for (var date = new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc); date.Year < 2040; date = date.AddDays(1))
        {
            var hour = rng.Next(24);
            var minute = rng.Next(60);
            var second = rng.Next(60);
            var offsetMinutes = rng.Next(-14 * 60, 14 * 60 + 1);
            var sign = offsetMinutes < 0 ? '-' : '+';
            var abs = Math.Abs(offsetMinutes);
            var text = string.Create(CultureInfo.InvariantCulture, $"{date.Day:00}/{months[date.Month - 1]}/{date.Year:0000}:{hour:00}:{minute:00}:{second:00} {sign}{abs / 60:00}{abs % 60:00}");
            Assert.True(ClfTimestamp.TryParse(Encoding.ASCII.GetBytes(text), out var ms), text);
            var expected = new DateTimeOffset(date.Year, date.Month, date.Day, hour, minute, second, TimeSpan.FromMinutes(offsetMinutes)).ToUnixTimeMilliseconds();
            Assert.Equal(expected, ms);
        }
    }

    [Theory]
    [InlineData("01/Foo/2026:00:00:00 +0000")]
    [InlineData("01/mar/2026:00:00:00 +0000")]
    [InlineData("00/Mar/2026:00:00:00 +0000")]
    [InlineData("32/Mar/2026:00:00:00 +0000")]
    [InlineData("31/Apr/2026:00:00:00 +0000")]
    [InlineData("29/Feb/2026:00:00:00 +0000")]
    [InlineData("29/Feb/2100:00:00:00 +0000")]
    [InlineData("01/Mar/2026:24:00:00 +0000")]
    [InlineData("01/Mar/2026:00:60:00 +0000")]
    [InlineData("01/Mar/2026:00:00:61 +0000")]
    [InlineData("01/Mar/2026:00:00:00 +2400")]
    [InlineData("01/Mar/2026:00:00:00 +0060")]
    [InlineData("01/Mar/2026:00:00:00 *0000")]
    [InlineData("01/Mar/2026:00:00:00  0000")]
    [InlineData("01/Mar/2026:00:00:00 +000")]
    [InlineData("01/Mar/2026:00:00:00 +00000")]
    [InlineData("1/Mar/2026:00:00:00 +0000")]
    [InlineData("01-Mar-2026:00:00:00 +0000")]
    [InlineData("01/Mar/2026 00:00:00 +0000")]
    [InlineData("01/Mar/2O26:00:00:00 +0000")]
    [InlineData("")]
    public void RejectsBadInput(string text)
    {
        Assert.False(ClfTimestamp.TryParse(Encoding.ASCII.GetBytes(text), out var ms));
        Assert.Equal(0, ms);
    }

    [Fact]
    public void LeapYearFebruary29IsAcceptedOnlyInLeapYears()
    {
        Assert.True(ClfTimestamp.TryParse("29/Feb/2000:00:00:00 +0000"u8, out _));
        Assert.True(ClfTimestamp.TryParse("29/Feb/2024:00:00:00 +0000"u8, out _));
        Assert.False(ClfTimestamp.TryParse("29/Feb/1900:00:00:00 +0000"u8, out _));
        Assert.False(ClfTimestamp.TryParse("29/Feb/2023:00:00:00 +0000"u8, out _));
    }
}
