using Urlog.Core.Ingest;

namespace Urlog.Query.Tests;

/// <summary>
/// The field statistics on the query side: which fields the task covers and with what rule, which fields the
/// catalog marks sensitive, and what the reader makes of the rows of <c>field_stats_v</c>, including the rows it
/// must refuse.
/// </summary>
public sealed class FieldStatsTests
{
    private static readonly string[] Sensitive =
    [
        "user.name", "source.ip", "destination.ip", "url.query", "http.request.referrer", "message", "raw",
        "url.original", "network.forwarded_ip", "source.address", "ident",
    ];

    [Fact]
    public void TheOneTextThatReadsTheStatisticsNamesTheViewAndTheCase()
    {
        Assert.Equal(
            "SELECT case_id, field, value, count, distinct_estimate, toString(computed_at) FROM field_stats_v WHERE case_id = {case:UInt64} ORDER BY field ASC, count DESC, value ASC",
            FieldStats.Sql);
        Assert.Equal(["case_histogram_v", "events_v", "field_stats_v"], RawSqlGate.AllowedRelations.Order(StringComparer.Ordinal));
        Assert.Contains("field_stats", RawSqlGate.DeniedRelations.AsEnumerable());
    }

    [Fact]
    public void TheCatalogMarksTheseFieldsSensitiveAndNoOthers() =>
        Assert.Equal(Sensitive.Order(StringComparer.Ordinal), FieldCatalog.Embedded.Fields.Where(f => f.Sensitive).Select(f => f.Name).Order(StringComparer.Ordinal));

    [Fact]
    public void TopValuesAreStoredForKeywordIntIpAndExtrasFieldsExceptTheRequestTarget()
    {
        var fields = FieldStats.TaskFields(FieldCatalog.Embedded).ToDictionary(f => f.Name);

        // Time and text fields are in every event and are not counted at all.
        Assert.DoesNotContain("_time", fields.Keys);
        Assert.DoesNotContain("message", fields.Keys);
        Assert.DoesNotContain("raw", fields.Keys);

        // String columns: how many events have them, never their values.
        foreach (var name in new[] { "user.name", "url.path", "url.query", "http.request.referrer", "user_agent.original" })
        {
            Assert.False(fields[name].TopValues, name);
        }

        // The sensitive fields whose top values are stored: members of the case read the same values in its events.
        foreach (var name in new[] { "source.ip", "destination.ip", "network.forwarded_ip", "source.address", "ident" })
        {
            Assert.True(fields[name].TopValues, name);
        }

        // A request target can carry a token.
        Assert.False(fields["url.original"].TopValues);

        Assert.Equal(
            new StatsField("http.response.status_code", StatsFieldKind.Column, "http_status", "toString(http_status)", "http_status != 0", true),
            fields["http.response.status_code"]);
        Assert.Equal(
            new StatsField("http.response.body.bytes", StatsFieldKind.Column, "http_response_body_bytes", "toString(http_response_body_bytes)", null, true),
            fields["http.response.body.bytes"]);
        Assert.Equal(
            new StatsField("source.ip", StatsFieldKind.Column, "source_ip", "replaceRegexpOne(toString(source_ip), '^::ffff:', '')", "source_ip != toIPv6('::')", true),
            fields["source.ip"]);
        Assert.Equal(new StatsField("host.name", StatsFieldKind.Column, "host", "host", "notEmpty(host)", true), fields["host.name"]);
        Assert.Equal(new StatsField("user.name", StatsFieldKind.Column, "user_name", null, "notEmpty(user_name)", false), fields["user.name"]);
        Assert.Equal(new StatsField("reject_reason", StatsFieldKind.Extra, "reject_reason", null, null, true), fields["reject_reason"]);
    }

    [Fact]
    public void TheVerbsOfferedAreTheVerbsTheParserTakes()
    {
        Assert.Equal(["where", "stats", "sort", "head", "table", "rex", "timechart"], Parser.Verbs);
        var options = new TranspileOptions(1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1), TimeZoneInfo.Utc, DateTimeOffset.UnixEpoch);
        var samples = new Dictionary<string, string>
        {
            ["where"] = "where host=a",
            ["stats"] = "stats count",
            ["sort"] = "sort host",
            ["head"] = "head 5",
            ["table"] = "table host",
            ["rex"] = """rex field=message "(?<word>\w+)" """,
            ["timechart"] = "timechart span=1h count",
        };
        foreach (var verb in Parser.Verbs)
        {
            // A verb the parser does not take is refused as "not supported in v1"; each of these transpiles.
            Assert.NotEmpty(Transpiler.Transpile("host=a | " + samples[verb], options).Sql);
        }
    }

    [Fact]
    public void RowsBecomeOneSetWithPresenceEstimatesAndTopValuesInOrder()
    {
        const string at = "2026-11-18 10:02:11.482";
        string[][] rows =
        [
            ["7", "", "", "1000", "0", at],
            ["7", "http.response.status_code", "", "990", "3", at],
            ["7", "http.response.status_code", "200", "900", "0", at],
            ["7", "http.response.status_code", "404", "80", "0", at],
            ["7", "http.response.status_code", "500", "10", "0", at],
            ["7", "user.name", "", "12", "0", at],
            ["7", "x_tenant", "", "1000", "4", at],
        ];

        var stats = FieldStats.Read(7, rows);

        Assert.Equal((7UL, 1000UL, new DateTime(2026, 11, 18, 10, 2, 11, 482, DateTimeKind.Utc)), (stats.CaseId, stats.Events, stats.ComputedAt));
        var status = stats.Fields["http.response.status_code"];
        Assert.Equal((990UL, 3UL), (status.Events, status.DistinctEstimate));
        Assert.Equal([new("200", 900UL), new("404", 80UL), new("500", 10UL)], status.Top);
        Assert.Equal((12UL, null), (stats.Fields["user.name"].Events, stats.Fields["user.name"].DistinctEstimate));
        Assert.Empty(stats.Fields["user.name"].Top);
        Assert.Equal(3, stats.Fields.Count);
    }

    [Fact]
    public void NoRowsIsACaseWithoutASet()
    {
        var stats = FieldStats.Read(7, []);
        Assert.Null(stats.ComputedAt);
        Assert.Empty(stats.Fields);
    }

    [Fact]
    public void ARowOfAnotherCaseIsRefused()
    {
        const string at = "2026-11-18 10:02:11.482";
        string[][] rows = [["7", "", "", "10", "0", at], ["8", "host.name", "web-01", "10", "0", at]];
        var error = Assert.Throws<InvalidOperationException>(() => FieldStats.Read(7, rows));
        Assert.Contains("a row of case 8", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RowsOfTwoSetsAreRefused()
    {
        string[][] rows = [["7", "", "", "10", "0", "2026-11-18 10:02:11.482"], ["7", "host.name", "web-01", "10", "0", "2026-11-18 10:05:00.000"]];
        Assert.Contains("two sets", Assert.Throws<InvalidOperationException>(() => FieldStats.Read(7, rows)).Message, StringComparison.Ordinal);
    }
}
