using System.Globalization;
using ClickHouse.Driver.ADO;
using Urlog.Query;

namespace Urlog.Security.Tests;

/// <summary>
/// ADR 0003 (a) and (b): a user-written <c>SETTINGS</c> clause is refused by the gate on both paths, and
/// with the gate bypassed (the driver straight, as a harness) an inline <c>SETTINGS SQL_allowed_cases = ...</c>
/// <b>does</b> override the per-request parameter on the pinned LTS. (b) is why the gate is mandatory: with
/// <c>readonly = 2</c> the server does not stop it. Both outcomes are recorded in docs/adr/0001-case-isolation.md.
/// </summary>
public sealed class SettingsOverrideTests(ClickHouseFixture ch, ITestOutputHelper output) : IClassFixture<ClickHouseFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Override = "SELECT count() FROM events_v SETTINGS SQL_allowed_cases = '[1,2,3]'";

    [Fact]
    public async Task GateRefusesTheRawTab()
    {
        var client = ch.QueryClient();
        var rejected = await Assert.ThrowsAsync<RawSqlRejectedException>(() => client.RunSqlAsync(Override, [1], Ct));
        Assert.Contains("SETTINGS", rejected.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GateRefusesTheTranspiledPathToo()
    {
        var client = ch.QueryClient();
        var options = new TranspileOptions(1, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc, new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));

        // The transpiler never emits SETTINGS; a result edited to carry one stands in for a transpiler bug, and the
        // gate in RunAsync and ExplainAsync catches it before the server sees it.
        var query = Transpiler.Transpile("| stats count", options);
        var broken = query with { Sql = query.Sql + " SETTINGS SQL_allowed_cases = '[1,2,3]'" };
        var rejected = await Assert.ThrowsAsync<RawSqlRejectedException>(() => client.RunAsync(broken, [1], Ct));
        Assert.Contains("SETTINGS", rejected.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<RawSqlRejectedException>(() => client.ExplainAsync(broken, [1], Ct));

        // The analyst's text can only reach SQL as a parameter value, which the parser never reads as a clause.
        var viaValue = Transpiler.Transpile("message=\"x SETTINGS SQL_allowed_cases='[1,2,3]'\" | stats count", options);
        Assert.DoesNotContain("SETTINGS", viaValue.Sql, StringComparison.Ordinal);
        Assert.Contains(viaValue.Parameters, p => p.Value.Contains("SETTINGS", StringComparison.Ordinal));
        var table = await client.RunAsync(viaValue, [1], Ct);
        Assert.Equal("0", table.Rows[0][0]); // no such message, and no widening

        var hostValue = Transpiler.Transpile("host=\"web-01' SETTINGS SQL_allowed_cases='[1,2,3]\" | stats count", options);
        var hostTable = await client.RunAsync(hostValue, [1], Ct);
        Assert.Equal("0", hostTable.Rows[0][0]);
    }

    [Fact]
    public async Task WithTheGateBypassedAnInlineSettingsClauseOverridesTheRequestParameter()
    {
        // The harness: the driver straight, as the backend would send it, minus the gate.
        await using var connection = new ClickHouseConnection(ch.HttpConnectionString(ClickHouseFixture.QueryUser, ClickHouseFixture.QueryPassword));
        await connection.OpenAsync(Ct);

        // Control: the request parameter alone shows case 1.
        var control = await CountAsync(connection, "SELECT count() FROM events_v", "[1]");
        Assert.Equal(ClickHouseFixture.RowsPerCase, control);

        // The hole: the same request parameter, plus the clause. The clause wins and every case is visible.
        var overridden = await CountAsync(connection, Override, "[1]");
        var effective = await ScalarAsync(connection, "SELECT getSetting('SQL_allowed_cases') SETTINGS SQL_allowed_cases = '[1,2,3]'", "[1]");
        output.WriteLine($"request parameter [1] + inline [1,2,3]: getSetting = {effective}, count = {overridden} (case 1 alone would be {control})");
        Assert.Equal("[1,2,3]", effective);
        Assert.Equal(ClickHouseFixture.Cases.Length * ClickHouseFixture.RowsPerCase, overridden);

        // And the other way round, the clause narrows what the parameter allowed: the clause is simply the last word.
        var narrowed = await CountAsync(connection, "SELECT count() FROM events_v SETTINGS SQL_allowed_cases = '[2]'", "[1,2,3]");
        Assert.Equal(ClickHouseFixture.RowsPerCase, narrowed);
    }

    [Fact]
    public async Task ReadonlyConstraintOnTheSettingBlocksTheRequestParameterToo()
    {
        // The row-policy fallback's precondition (ADR 0001, "The fallback, measured and not taken"): the setting marked readonly in the user's
        // constraints. Measured: a READONLY constraint refuses the inline clause and the per-request parameter
        // alike (452), so it cannot coexist with per-request delivery; the fallback would need a ClickHouse user or
        // profile per analyst. Recorded in ADR 0001 as the reason the fallback was not taken.
        await using var admin = ch.Admin();
        await admin.ExecuteAsync("DROP USER IF EXISTS locked_probe", cancellationToken: Ct);
        await admin.ExecuteAsync("CREATE USER locked_probe IDENTIFIED WITH plaintext_password BY 'probe' SETTINGS PROFILE 'urlog_query'", cancellationToken: Ct);
        await admin.ExecuteAsync("GRANT SELECT ON default.events_v TO locked_probe", cancellationToken: Ct);
        await admin.ExecuteAsync("DROP SETTINGS PROFILE IF EXISTS locked_probe_profile", cancellationToken: Ct);
        await admin.ExecuteAsync("CREATE SETTINGS PROFILE locked_probe_profile SETTINGS SQL_allowed_cases = '[2]' READONLY TO locked_probe", cancellationToken: Ct);
        try
        {
            await using var connection = new ClickHouseConnection(ch.HttpConnectionString("locked_probe", "probe"));
            await connection.OpenAsync(Ct);
            Assert.Equal(ClickHouseFixture.RowsPerCase, await CountAsync(connection, "SELECT count() FROM events_v", null)); // the profile's value, case 2
            var viaParameter = await Assert.ThrowsAsync<ClickHouse.Driver.ClickHouseServerException>(() => CountAsync(connection, "SELECT count() FROM events_v", "[1]"));
            var viaClause = await Assert.ThrowsAsync<ClickHouse.Driver.ClickHouseServerException>(() => CountAsync(connection, Override, null));
            output.WriteLine($"readonly SQL_allowed_cases: parameter -> {viaParameter.ErrorCode}, clause -> {viaClause.ErrorCode}");
            Assert.Equal(452, viaParameter.ErrorCode); // SETTING_CONSTRAINT_VIOLATION
            Assert.Equal(452, viaClause.ErrorCode);
        }
        finally
        {
            await admin.ExecuteAsync("DROP SETTINGS PROFILE IF EXISTS locked_probe_profile", cancellationToken: Ct);
            await admin.ExecuteAsync("DROP USER IF EXISTS locked_probe", cancellationToken: Ct);
        }
    }

    private static async Task<int> CountAsync(ClickHouseConnection connection, string sql, string? allowedCases) =>
        int.Parse(await ScalarAsync(connection, sql, allowedCases), CultureInfo.InvariantCulture);

    private static async Task<string> ScalarAsync(ClickHouseConnection connection, string sql, string? allowedCases)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (allowedCases is not null)
        {
            command.CustomSettings["SQL_allowed_cases"] = allowedCases;
        }

        return Convert.ToString(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture) ?? "";
    }
}
