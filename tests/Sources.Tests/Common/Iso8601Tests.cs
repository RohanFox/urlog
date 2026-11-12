using System.Globalization;
using System.Text;
using Urlog.Sources.Common;

namespace Urlog.Sources.Tests.Common;

public sealed class Iso8601Tests
{
    [Theory]
    [InlineData("2026-03-01T00:00:00+00:00", "2026-03-01T00:00:00.000Z")]
    [InlineData("2026-03-01T00:00:00Z", "2026-03-01T00:00:00.000Z")]
    [InlineData("2026-03-01t00:00:00z", "2026-03-01T00:00:00.000Z")]
    [InlineData("2026-03-01 00:00:00Z", "2026-03-01T00:00:00.000Z")]
    [InlineData("2026-03-01T00:00:05+01:00", "2026-02-28T23:00:05.000Z")]
    [InlineData("2026-03-01T00:00:05-05:30", "2026-03-01T05:30:05.000Z")]
    [InlineData("2026-03-01T00:00:05+0100", "2026-02-28T23:00:05.000Z")]
    [InlineData("2026-03-01T00:00:05+01", "2026-02-28T23:00:05.000Z")]
    [InlineData("2026-03-01T00:00:00.5Z", "2026-03-01T00:00:00.500Z")]
    [InlineData("2026-03-01T00:00:00.123Z", "2026-03-01T00:00:00.123Z")]
    [InlineData("2026-03-01T00:00:00.123456789Z", "2026-03-01T00:00:00.123Z")]
    [InlineData("2026-03-01T00:00:00,25+00:00", "2026-03-01T00:00:00.250Z")]
    [InlineData("2024-02-29T23:59:60Z", "2024-03-01T00:00:00.000Z")]
    [InlineData("1970-01-01T00:00:00Z", "1970-01-01T00:00:00.000Z")]
    public void ParsesZonesFractionsAndTheLeapSecond(string text, string utc)
    {
        Assert.True(Iso8601.TryParse(Encoding.ASCII.GetBytes(text), out var ms), text);
        var expected = DateTimeOffset.ParseExact(utc, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUnixTimeMilliseconds();
        Assert.Equal(expected, ms);
    }

    [Theory]
    [InlineData("")]
    [InlineData("2026-03-01T00:00:00")]
    [InlineData("2026-03-01T00:00:00.")]
    [InlineData("2026-03-01T00:00:00.Z")]
    [InlineData("2026-03-01T00:00:00+1")]
    [InlineData("2026-03-01T00:00:00+100")]
    [InlineData("2026-03-01T00:00:00+01:0")]
    [InlineData("2026-03-01T00:00:00+24:00")]
    [InlineData("2026-03-01T00:00:00+01:60")]
    [InlineData("2026-03-01T00:00:00Zx")]
    [InlineData("2026-03-01X00:00:00Z")]
    [InlineData("2026/03/01T00:00:00Z")]
    [InlineData("2026-13-01T00:00:00Z")]
    [InlineData("2026-02-30T00:00:00Z")]
    [InlineData("2026-03-01T24:00:00Z")]
    [InlineData("2026-03-01T00:60:00Z")]
    [InlineData("2026-03-01T00:00:61Z")]
    [InlineData("01/Mar/2026:00:00:00 +0000")]
    public void RejectsNaiveAndMalformedTimestamps(string text)
    {
        Assert.False(Iso8601.TryParse(Encoding.ASCII.GetBytes(text), out var ms));
        Assert.Equal(0, ms);
    }
}
