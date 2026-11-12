using System.Text;
using Urlog.Sources.Syslog;

namespace Urlog.Sources.Tests.Syslog;

public sealed class Rfc3164TimestampTests
{
    [Theory]
    [InlineData("Mar  1 00:00:00", 0, 3, 1, 0, 0, 0, 0, 15)]
    [InlineData("Mar 1 00:00:00", 0, 3, 1, 0, 0, 0, 0, 14)]
    [InlineData("Mar 01 00:00:00", 0, 3, 1, 0, 0, 0, 0, 15)]
    [InlineData("Dec 31 23:59:59", 0, 12, 31, 23, 59, 59, 0, 15)]
    [InlineData("Jun 30 23:59:60", 0, 6, 30, 23, 59, 60, 0, 15)]
    [InlineData("Feb 29 12:34:56", 0, 2, 29, 12, 34, 56, 0, 15)]
    [InlineData("Jan  1 00:00:00.5", 0, 1, 1, 0, 0, 0, 500, 17)]
    [InlineData("Jan  1 00:00:00.123456789", 0, 1, 1, 0, 0, 0, 123, 25)]
    [InlineData("Jan  1 2026 00:00:00", 2026, 1, 1, 0, 0, 0, 0, 20)]
    [InlineData("*Jan  1 00:00:00.456:", 0, 1, 1, 0, 0, 0, 456, 21)]
    [InlineData(".Jan  1 00:00:00:", 0, 1, 1, 0, 0, 0, 0, 17)]
    [InlineData("Jan  1 00:00:00: host", 0, 1, 1, 0, 0, 0, 0, 16)]
    [InlineData("Jan  1 00:00:00 host prog: msg", 0, 1, 1, 0, 0, 0, 0, 15)]
    [InlineData("Jan  1 00:00:00. host", 0, 1, 1, 0, 0, 0, 0, 15)]
    public void ParsesTheVariants(string text, int year, int month, int day, int hour, int minute, int second, int millis, int consumed)
    {
        Assert.True(Rfc3164Timestamp.TryParse(Encoding.ASCII.GetBytes(text), out var parts, out var used), text);
        Assert.Equal(new Rfc3164Timestamp.Parts(year, month, day, hour, minute, second, millis), parts);
        Assert.Equal(consumed, used);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Mar  1 00:00:0")]
    [InlineData("mar  1 00:00:00")]
    [InlineData("Foo  1 00:00:00")]
    [InlineData("Mar  0 00:00:00")]
    [InlineData("Mar 32 00:00:00")]
    [InlineData("Feb 30 00:00:00")]
    [InlineData("Apr 31 00:00:00")]
    [InlineData("Mar   1 00:00:00")]
    [InlineData("Mar  1  00:00:00")]
    [InlineData("Mar  1 24:00:00")]
    [InlineData("Mar  1 00:60:00")]
    [InlineData("Mar  1 00:00:61")]
    [InlineData("Mar  1 00-00-00")]
    [InlineData("Mar  1 0:00:00")]
    [InlineData("Mar  1 202 00:00:00")]
    [InlineData("2026-03-01T00:00:00Z")]
    [InlineData("01/Mar/2026:00:00:00 +0000")]
    public void RejectsTheRest(string text)
    {
        Assert.False(Rfc3164Timestamp.TryParse(Encoding.ASCII.GetBytes(text), out _, out _), text);
    }

    [Theory]
    [InlineData("Mar  1 00:00:00", 2026, 0, "2026-03-01T00:00:00.000Z")]
    [InlineData("Mar  1 00:00:00", 2026, 2 * 3_600_000L, "2026-02-28T22:00:00.000Z")]
    [InlineData("Mar  1 00:00:00", 2026, -5 * 3_600_000L, "2026-03-01T05:00:00.000Z")]
    [InlineData("Feb 29 12:00:00", 2028, 0, "2028-02-29T12:00:00.000Z")]
    [InlineData("Feb 29 12:00:00", 2027, 0, "2027-03-01T12:00:00.000Z")]
    [InlineData("Jun 30 23:59:60", 2026, 0, "2026-07-01T00:00:00.000Z")]
    [InlineData("Dec 31 23:59:59.999", 1999, 0, "1999-12-31T23:59:59.999Z")]
    [InlineData("Jan  1 1970 00:00:00", 2026, 0, "1970-01-01T00:00:00.000Z")]
    public void ConvertsWithYearAndOffset(string text, int year, long offsetMilliseconds, string expected)
    {
        Assert.True(Rfc3164Timestamp.TryParse(Encoding.ASCII.GetBytes(text), out var parts, out _));
        var effectiveYear = parts.Year != 0 ? parts.Year : year;
        var ms = Rfc3164Timestamp.ToUnixMilliseconds(in parts, effectiveYear, offsetMilliseconds);
        Assert.Equal(expected, DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture));
    }
}
