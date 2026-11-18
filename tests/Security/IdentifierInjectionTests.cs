using System.Globalization;
using Urlog.Query;

namespace Urlog.Security.Tests;

/// <summary>
/// A name the analyst writes never reaches the SQL text. Before generated aliases, a by field ending in a backslash
/// escaped the backquote that closed it, and the alias after it ran as SQL: the query lost its own case and time
/// predicates and read whatever the caller's allowed cases held. These tests keep both halves: the transpiler
/// refuses those inputs or binds them as parameters, and the statement such an input once produced, sent as it was,
/// still reads nothing outside the allowed cases, because the view and the gate never depended on the transpiler.
/// </summary>
[Collection(SeedReaders.Name)]
public sealed class IdentifierInjectionTests(ClickHouseFixture ch, ITestOutputHelper output)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TranspileOptions Options = new(
        1,
        new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2020, 1, 1, 0, 1, 0, TimeSpan.Zero),
        TimeZoneInfo.Utc,
        new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));

    private const string Scope = " FROM events_v WHERE case_id = {case:UInt64} AND ts >= {from:DateTime64(3)} AND ts < {to:DateTime64(3)} GROUP BY extras[{p0:String}]";

    [Theory]
    [InlineData(@"| stats count as /**/FROM/**/events_v/**/-- by u\")]
    [InlineData(@"| stats count as /**/FROM/**/events_v/**/UNION/**/ALL/**/SELECT/**/raw/**/FROM/**/events_v/**/-- by u\")]
    public void TheDemonstratedInputsAreAQueryError(string spl)
    {
        var e = Assert.Throws<QueryException>(() => Transpiler.Transpile(spl, Options));
        output.WriteLine($"{e.Offset}+{e.Length}: {e.Message}");
        Assert.Contains("is not a name", e.Message, StringComparison.Ordinal);
        Assert.Equal(17, e.Offset);
    }

    [Fact]
    public async Task AByFieldEndingInABackslashIsAParameterAndTheQueryKeepsItsScope()
    {
        // The range is one minute of 2020 and the seed is 2026: with its scope intact the query sees nothing.
        var query = Transpiler.Transpile(@"| stats count as n by u\ | sort -n", Options);
        Assert.DoesNotContain('\\', query.Sql);
        Assert.Contains(query.Parameters, p => p.Value == @"u\");
        Assert.Equal([@"u\", "n"], query.Columns);
        var table = await ch.QueryClient().RunAsync(query, [1, 2, 3], Ct);
        Assert.Empty(table.Rows);
        Assert.Equal([@"u\", "n"], table.Columns);
    }

    [Fact]
    public async Task TheStatementThatInputOnceProducedStillReadsOnlyTheAllowedCases()
    {
        var client = ch.QueryClient();

        // What the transpiler wrote for the first demonstrated input before the fix, byte for byte: the scope after
        // the comment marker is dead text.
        var escaped = Edited(@"SELECT extras[{p0:String}] AS `u\`, count() AS `/**/FROM/**/events_v/**/--`" + Scope);
        var own = await client.RunAsync(escaped, [1], Ct);
        output.WriteLine($"allowed [1]: {own.Rows.Count} rows for a range that holds none");
        Assert.Equal(ClickHouseFixture.RowsPerCase, own.Rows.Count); // the defect: the query's range no longer applies

        // The view does not depend on that text. No allowed case, no rows; a case named outright but not allowed, no rows.
        Assert.Empty((await client.RunAsync(escaped, [], Ct)).Rows);
        var named = Edited(@"SELECT extras[{p0:String}] AS `u\`, count() AS `/**/FROM/**/events_v/**/WHERE/**/case_id/**/IN/**/[2]/**/--`" + Scope);
        Assert.Empty((await client.RunAsync(named, [1], Ct)).Rows);
        Assert.Empty((await client.RunAsync(named, [1, 3], Ct)).Rows);
        Assert.Equal(ClickHouseFixture.RowsPerCase, (await client.RunAsync(named, [2], Ct)).Rows.Count); // the control: the owner of case 2

        var union = Edited(@"SELECT extras[{p0:String}] AS `u\`, count() AS `/**/FROM/**/events_v/**/UNION/**/ALL/**/SELECT/**/toString(case_id)/**/FROM/**/events_v/**/--`" + Scope);
        var cases = (await client.RunAsync(union, [1], Ct)).Rows.Select(r => r[0]).Where(c => c.Length > 0).Distinct().ToList();
        Assert.Equal(["1"], cases);

        // The gate does not depend on it either: the table behind the view stays out of reach.
        var events = Edited(@"SELECT extras[{p0:String}] AS `u\`, count() AS `/**/FROM/**/events/**/--`" + Scope);
        var rejected = await Assert.ThrowsAsync<RawSqlRejectedException>(() => client.RunAsync(events, [1, 2, 3], Ct));
        Assert.Contains("events", rejected.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStatementTheServerCannotParseIsARefusalNotAServerException()
    {
        // A lone escaped backquote leaves the identifier open to the end of the text.
        var open = Edited(@"SELECT count() AS `a\`" + Scope.Replace(" GROUP BY extras[{p0:String}]", "", StringComparison.Ordinal));
        var rejected = await Assert.ThrowsAsync<RawSqlRejectedException>(() => ch.QueryClient().RunAsync(open, [1], Ct));
        output.WriteLine(rejected.Message);
        Assert.Contains("the server did not accept the query", rejected.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStatementTheServerRefusesWhileRunningIsAQueryError()
    {
        var failed = await Assert.ThrowsAsync<QueryFailedException>(() => ch.QueryClient().RunSqlAsync("SELECT count() FROM no_such_relation", [1], Ct));
        output.WriteLine($"{failed.ErrorCode.ToString(CultureInfo.InvariantCulture)}: {failed.Message}");
        Assert.Equal(60, failed.ErrorCode); // UNKNOWN_TABLE
    }

    /// <summary>A transpiled query with its SQL replaced: a stand-in for the transpiler as it was.</summary>
    private static TranspileResult Edited(string sql)
    {
        var query = Transpiler.Transpile("| stats count by host", Options);
        return query with { Sql = sql, Parameters = [.. query.Parameters, new QueryParameter("p0", "String", @"u\")], Columns = [] };
    }
}
