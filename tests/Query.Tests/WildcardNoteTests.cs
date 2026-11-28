using Urlog.Query;

namespace Urlog.Query.Tests;

/// <summary>
/// The note on a wildcard comparison says where the wildcard is. Only a single <c>*</c> at the end is a prefix
/// search; every other pattern is compared row by row, and only one that starts with a wildcard has a leading one.
/// The vectors record a warning as code and span, so the two texts are pinned here.
/// </summary>
public sealed class WildcardNoteTests
{
    private const string Leading = "a leading wildcard on {0} scans every row";
    private const string RowByRow = "{0}: only a single * at the end is a prefix search; this pattern is compared row by row";

    private static readonly TranspileOptions Options = new(
        1,
        new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero),
        TimeZoneInfo.Utc,
        new DateTimeOffset(2026, 3, 15, 12, 0, 0, TimeSpan.Zero));

    /// <summary>The percent note gives the analyst's own term with the wildcard that does what the percent signs were meant to.</summary>
    [Theory]
    [InlineData("%item%", "*item*")]
    [InlineData("NOT %20%", "*20*")]
    [InlineData("url.path=%item/6%", "*item/6*")]
    [InlineData("url.path!=%item%", "*item*")]
    [InlineData("select LIKE %admin%", "*admin*")]
    public void ThePercentNoteWritesTheTermWithTheWildcard(string spl, string rewrite)
    {
        var warning = Assert.Single(Transpiler.Transpile(spl, Options).Warnings, w => w.Code == "percent_term");
        Assert.Equal(WarningLevel.Meaning, warning.Level);
        Assert.Equal($"% is searched as a character; the wildcard is *: write {rewrite}", warning.Message);
    }

    [Theory]
    [InlineData("*a", Leading)]
    [InlineData("?a", Leading)]
    [InlineData("*a*", Leading)]
    [InlineData("a?", RowByRow)]
    [InlineData("a*b", RowByRow)]
    [InlineData("a*b*", RowByRow)]
    [InlineData("a**", RowByRow)]
    [InlineData("a*", null)]
    public void TheNoteSaysWhereTheWildcardIs(string value, string? expected)
    {
        // A string field and a keyword field: the two types the note is for.
        foreach (var (field, name) in new[] { ("url.path", "url.path"), ("host", "host.name") })
        {
            foreach (var op in new[] { "=", "!=" })
            {
                var spl = field + op + value;
                var result = Transpiler.Transpile(spl, Options);
                if (expected is null)
                {
                    Assert.Empty(result.Warnings);
                    continue;
                }

                var warning = Assert.Single(result.Warnings);
                Assert.Equal("unindexed", warning.Code);
                Assert.Equal(new TextSpan(0, spl.Length), warning.Span);
                Assert.Equal(string.Format(System.Globalization.CultureInfo.InvariantCulture, expected, name), warning.Message);
            }
        }
    }
}
