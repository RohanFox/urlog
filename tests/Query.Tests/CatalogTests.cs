using Urlog.Core.Sources;
using Urlog.Query;

namespace Urlog.Query.Tests;

public sealed class CatalogTests
{
    [Fact]
    public void EveryExposedColumnHasExactlyOneCatalogField()
    {
        var catalog = FieldCatalog.Embedded;
        var byColumn = catalog.Fields.Where(f => f.Column is not null).GroupBy(f => f.Column!).ToDictionary(g => g.Key, g => g.Count());
        foreach (var column in EventColumns.All)
        {
            if (column == EventColumn.CaseId || !EventColumns.IsExposed(column))
            {
                continue; // the case is index=, and the alias's building blocks are not fields
            }

            if (column == EventColumn.Extras || column == EventColumn.ParseGeneration)
            {
                continue; // extras is reached through keys; the generation is the view's concern
            }

            Assert.True(byColumn.TryGetValue(EventColumns.Name(column), out var n) && n == 1, $"{EventColumns.Name(column)} is listed {n} times");
        }

        Assert.Equal(1, byColumn[EventColumns.MessageAlias]);
        Assert.Equal(FieldType.Text, catalog.Resolve("message").Type);
        Assert.Equal(FieldType.Text, catalog.Resolve("raw").Type);
    }

    [Fact]
    public void AliasesResolveCaseInsensitivelyAndUnknownNamesGoToExtras()
    {
        var catalog = FieldCatalog.Embedded;
        Assert.Equal("source.ip", catalog.Resolve("src_ip").Name);
        Assert.Equal("source.ip", catalog.Resolve("CLIENTIP").Name);
        Assert.Equal("http.request.method", catalog.Resolve("method").Name);
        Assert.Equal("log.syslog.facility.name", catalog.Resolve("facility").Name);
        Assert.Equal("process.pid", catalog.Resolve("pid").Name);
        Assert.Equal("url.original", catalog.Resolve("request_uri").Name); // extras-backed
        Assert.Equal("url.original", catalog.Resolve("request_uri").ExtraKey);

        var unknown = catalog.Resolve("proxy_upstream_name");
        Assert.True(unknown.IsUnknown);
        Assert.Equal(FieldType.Extra, unknown.Type);
        Assert.Equal("proxy_upstream_name", unknown.ExtraKey);
    }

    [Fact]
    public void TheEmbeddedCatalogHasNoDuplicateNamesOrAliases()
    {
        var names = FieldCatalog.Embedded.Fields.SelectMany(f => f.Aliases.Append(f.Name)).Select(n => n.ToLowerInvariant()).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void ADuplicateAliasIsRefusedAtLoad()
    {
        const string yaml = """
            version: 1
            fields:
              - name: a
                column: host
                type: keyword
                aliases: [x]
              - name: b
                column: program
                type: keyword
                aliases: [X]
            """;
        var e = Assert.Throws<InvalidDataException>(() => FieldCatalog.Parse(yaml));
        Assert.Contains("'X' names both a and b", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-24h", "2026-03-14T12:00:00Z", false)]
    [InlineData("now", "2026-03-15T12:00:00Z", false)]
    [InlineData("-7d@d", "2026-03-08T00:00:00Z", true)]
    [InlineData("-1h@h", "2026-03-15T11:00:00Z", true)]
    [InlineData("@w", "2026-03-15T00:00:00Z", true)] // 2026-03-15 is a Sunday
    [InlineData("2026-03-01T10:00:00+02:00", "2026-03-01T08:00:00Z", false)]
    [InlineData("2026-03-01T10:00:00", "2026-03-01T10:00:00Z", true)]
    [InlineData("2026-03-01", "2026-03-01T00:00:00Z", true)]
    public void TimeExpressionsResolveAgainstTheClockAndTheDisplayZone(string text, string expectedUtc, bool assumed)
    {
        var now = DateTimeOffset.Parse("2026-03-15T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var instant = TimeExpr.Parse(text, now, TimeZoneInfo.Utc, out var assumedZone);
        Assert.Equal(DateTimeOffset.Parse(expectedUtc, System.Globalization.CultureInfo.InvariantCulture), instant);
        Assert.Equal(assumed, assumedZone);
    }

    [Fact]
    public void SnappingUsesTheDisplayZone()
    {
        var now = DateTimeOffset.Parse("2026-03-15T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var plusTwo = TimeZones.Find("+02:00"); // Europe/Bucharest in March, as a fixed offset
        var instant = TimeExpr.Parse("@d", now, plusTwo, out _);
        Assert.Equal(DateTimeOffset.Parse("2026-03-14T22:00:00Z", System.Globalization.CultureInfo.InvariantCulture), instant); // local midnight, UTC+2
    }

    [Theory]
    [InlineData("UTC", 0)]
    [InlineData("+02:00", 120)]
    [InlineData("-0500", -300)]
    [InlineData("UTC+3", 180)]
    [InlineData("GMT-2:30", -150)]
    public void FixedOffsetsAreTimeZones(string id, int minutes) => Assert.Equal(TimeSpan.FromMinutes(minutes), TimeZones.Find(id).BaseUtcOffset);

    [Theory]
    [InlineData("Mars/Olympus")]
    [InlineData("+25:00")]
    public void UnknownZonesAreAnError(string id) => Assert.Throws<ArgumentException>(() => TimeZones.Find(id));

    [Theory]
    [InlineData("hello world", new[] { "hello", "world" })]
    [InlineData("10.0.0.5", new[] { "10", "0", "0", "5" })]
    [InlineData("sqlmap/1.8#dev", new[] { "sqlmap", "1", "8", "dev" })]
    [InlineData("---", new string[0])]
    [InlineData("юрий-x", new[] { "юрий", "x" })]
    public void TermsSplitLikeTheTokenizer(string text, string[] expected) => Assert.Equal(expected, Transpiler.Tokens(text));
}
