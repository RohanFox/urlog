using System.Text.Json;
using ClickHouse.Driver;
using Urlog.Query;

namespace Urlog.Security.Tests;

/// <summary>
/// The raw-SQL gate against the live parser of the pinned LTS. The hostile corpus must be refused before
/// anything is executed; the benign corpus and every transpiler vector must pass. The refusal reasons are the
/// gate's (<see cref="RawSqlRejectedException"/>), except where the server itself refuses the text first (a
/// SETTINGS clause that violates a constraint, a multi-statement): those are rejections too, and recorded as such.
/// </summary>
public sealed class RawSqlGateTests(ClickHouseFixture ch, ITestOutputHelper output) : IClassFixture<ClickHouseFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string, string> Hostile() => new()
    {
        { "settings override", "SELECT count() FROM events_v SETTINGS SQL_allowed_cases = '[1,2,3]'", "SETTINGS" },
        { "settings in subquery", "SELECT * FROM (SELECT * FROM events_v SETTINGS SQL_allowed_cases = '[1,2,3]')", "SETTINGS" },
        { "settings in scalar", "SELECT * FROM events_v WHERE 1 = (SELECT 1 SETTINGS SQL_allowed_cases = '[1,2,3]')", "SETTINGS" },
        { "settings after union", "(SELECT 1) UNION ALL (SELECT 2) SETTINGS max_threads = 1", "SETTINGS" },
        { "settings lowercase", "select 1 settings max_threads = 1", "SETTINGS" },
        { "settings after comment", "SELECT * FROM events_v /* x */ SETTINGS SQL_allowed_cases = '[9]'", "SETTINGS" },
        { "settings on new lines", "SELECT 1\nSETTINGS\nmax_threads = 1", "SETTINGS" },
        { "settings violating a constraint", "SELECT 1 SETTINGS max_result_rows = 5000000", "did not accept" },
        { "SET", "SET max_threads = 1", "only SELECT" },
        { "system.tables", "SELECT name FROM system.tables", "qualified" },
        { "system.one", "SELECT * FROM system.one", "qualified" },
        { "url()", "SELECT * FROM url('http://127.0.0.1:8123/?query=SELECT%201', CSV, 'a String')", "table function url()" },
        { "file()", "SELECT * FROM file('x.csv')", "table function file()" },
        { "remote()", "SELECT * FROM remote('127.0.0.1', 'default', 'events')", "table function remote()" },
        { "numbers()", "SELECT * FROM numbers(10)", "table function numbers()" },
        { "view()", "SELECT * FROM view(SELECT 1)", "table function view()" },
        { "events directly", "SELECT * FROM events", "events is not readable" },
        { "events in subquery", "SELECT * FROM (SELECT * FROM events)", "events is not readable" },
        { "events in join", "SELECT * FROM events_v e JOIN events x ON e.host = x.host", "events is not readable" },
        { "events in IN", "SELECT * FROM events_v WHERE host IN events", "events is not readable" },
        { "events behind a CTE name", "WITH events_v AS (SELECT 1) SELECT * FROM events", "events is not readable" },
        { "file_generation", "SELECT * FROM file_generation", "file_generation is not readable" },
        { "schema_migrations", "SELECT * FROM schema_migrations", "schema_migrations is not readable" },
        { "store_identity", "SELECT * FROM store_identity", "store_identity is not readable" },
        { "qualified view", "SELECT * FROM default.events_v", "qualified" },
        { "INTO OUTFILE", "SELECT * FROM events_v INTO OUTFILE '/tmp/x'", "" },
        { "FORMAT", "SELECT * FROM events_v FORMAT JSON", "" },
        { "deeply nested", NestedSelects(RawSqlGate.MaxSelectDepth + 1), "nest deeper" },
        { "1 MB text", "SELECT 1 /*" + new string('x', 1024 * 1024) + "*/", "limit is 16,384" },
        { "comment-split keyword", "SEL/**/ECT 1", "did not accept" },
        { "comment-split SETTINGS", "SELECT * FROM events_v SET/**/TINGS SQL_allowed_cases = '[9]'", "did not accept" },
        { "EXPLAIN", "EXPLAIN SELECT 1", "only SELECT" },
        { "EXPLAIN AST", "EXPLAIN AST SELECT 1", "only SELECT" },
        { "EXPLAIN indexes", "EXPLAIN indexes = 1 SELECT count() FROM events_v", "only SELECT" },
        { "multi-statement", "SELECT 1; SELECT 2", "did not accept" },
        { "INSERT", "INSERT INTO events_v SELECT 1", "only SELECT" },
        { "CREATE", "CREATE TABLE t (x UInt8) ENGINE = Memory", "only SELECT" },
        { "DROP", "DROP TABLE events", "only SELECT" },
        { "ALTER", "ALTER TABLE events DELETE WHERE 1", "only SELECT" },
        { "SYSTEM", "SYSTEM RELOAD DICTIONARY file_generation_dict", "only SELECT" },
        { "KILL", "KILL QUERY WHERE 1", "only SELECT" },
        { "SHOW", "SHOW TABLES", "only SELECT" },
        { "DESCRIBE", "DESCRIBE events", "only SELECT" },
        { "USE", "USE system", "only SELECT" },
        { "empty", "", "empty" },
        { "whitespace", "  \n\t ", "empty" },
        { "only a comment", "-- nothing", "did not accept" },
        { "homoglyph identifier, bare", "SELECT * FROM еvents_v", "did not accept" },
    };

    [Theory]
    [MemberData(nameof(Hostile))]
    public async Task HostileTextIsRefusedBeforeExecution(string what, string sql, string reasonContains)
    {
        var client = ch.QueryClient();
        var rejected = await Assert.ThrowsAsync<RawSqlRejectedException>(() => client.RunSqlAsync(sql, [1, 2, 3], Ct));
        output.WriteLine($"{what,-36} -> {rejected.Message}");
        Assert.Contains(reasonContains, rejected.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HomoglyphOfTheViewNameQuotedReachesNoTable()
    {
        // `еvents_v` with a Cyrillic е: the dump prints it as an unknown unqualified name, which the gate takes for a
        // CTE (the tree does not carry CTE names, ADR 0001); the server then finds no such table. Zero rows either way.
        var client = ch.QueryClient();
        var e = await Assert.ThrowsAsync<QueryFailedException>(() => client.RunSqlAsync("SELECT * FROM `еvents_v`", [1, 2, 3], Ct));
        output.WriteLine($"{e.ErrorCode}: {e.Message.Split('\n')[0]}");
        Assert.Equal(60, e.ErrorCode); // UNKNOWN_TABLE
    }

    public static TheoryData<string, string> Benign() => new()
    {
        { "select star", "SELECT * FROM events_v" },
        { "count with filter", "SELECT count() FROM events_v WHERE host = 'web-01'" },
        { "trailing semicolon and whitespace", "  SELECT count() FROM events_v ;\n" },
        { "settings in a comment", "SELECT count() FROM events_v -- SETTINGS SQL_allowed_cases = '[9]'" },
        { "settings in a string", "SELECT 'SETTINGS SQL_allowed_cases=[9]' AS s, count() FROM events_v" },
        { "settings as an alias", "SELECT count() AS \"SETTINGS\" FROM events_v" },
        { "CTE", "WITH c AS (SELECT host, count() AS n FROM events_v GROUP BY host) SELECT * FROM c ORDER BY n DESC" },
        { "scalar CTE", "WITH (SELECT max(ts) FROM events_v) AS last SELECT count() FROM events_v WHERE ts = last" },
        { "subquery in FROM", "SELECT host, count() FROM (SELECT host FROM events_v WHERE program = 'nginx') GROUP BY host" },
        { "IN subquery", "SELECT count() FROM events_v WHERE host IN (SELECT host FROM events_v WHERE host = 'web-01')" },
        { "join to field_stats", "SELECT e.host, f.value FROM events_v e LEFT JOIN field_stats f ON e.case_id = f.case_id LIMIT 5" },
        { "union", "SELECT host FROM events_v WHERE host = 'web-01' UNION ALL SELECT host FROM events_v WHERE host = 'web-02'" },
        { "window", "SELECT host, row_number() OVER (PARTITION BY host ORDER BY ts) AS rn FROM events_v LIMIT 5" },
        { "array join", "SELECT k FROM events_v ARRAY JOIN mapKeys(extras) AS k LIMIT 5" },
        { "message alias", "SELECT message, raw FROM events_v WHERE message LIKE '%index%' LIMIT 1" },
        { "eight nested selects", NestedSelects(RawSqlGate.MaxSelectDepth) },
        { "16 KB comment", "SELECT count() FROM events_v /*" + new string('x', RawSqlGate.MaxBytes - 40) + "*/" },
    };

    [Theory]
    [MemberData(nameof(Benign))]
    public async Task BenignTextPassesAndIsStillCaseFiltered(string what, string sql)
    {
        var client = ch.QueryClient();
        var table = await client.RunSqlAsync(sql, [1], Ct);
        output.WriteLine($"{what,-36} -> {table.Rows.Count} rows, {table.Columns.Count} columns");
        var caseColumn = table.Columns.ToList().IndexOf("case_id");
        if (caseColumn >= 0)
        {
            Assert.All(table.Rows, r => Assert.Equal("1", r[caseColumn]));
        }
    }

    [Fact]
    public async Task EveryTranspilerVectorPassesTheGate()
    {
        // The recorded SQL and parameters of src/Query/tests/vectors/*.json (the SPL slice's contract) through the
        // same EXPLAIN AST walk the client runs: the transpiled path is gated too, and nothing it emits trips it.
        var client = ch.QueryClient();
        var dir = Path.Combine(ClickHouseFixture.RepoRoot, "src", "Query", "tests", "vectors");
        var checkedCount = 0;
        foreach (var file in Directory.GetFiles(dir, "*.json").Order(StringComparer.Ordinal))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var vector in doc.RootElement.EnumerateArray())
            {
                if (!vector.TryGetProperty("expect", out var expect))
                {
                    continue;
                }

                var sql = expect.GetProperty("sql").GetString()!;
                var parameters = expect.GetProperty("params").EnumerateObject()
                    .Select(p => (Name: p.Name, Spec: p.Value.GetString()!))
                    .Select(p => new QueryParameter(p.Name, p.Spec[..p.Spec.IndexOf(':', StringComparison.Ordinal)], p.Spec[(p.Spec.IndexOf(':', StringComparison.Ordinal) + 1)..]))
                    .ToList();
                var ast = await client.ExplainAstAsync(sql, parameters, [1], Ct);
                var reason = RawSqlGate.Inspect(ast);
                Assert.True(reason is null, $"{Path.GetFileName(file)} / {vector.GetProperty("name").GetString()}: {reason}\n{sql}");
                checkedCount++;
            }
        }

        output.WriteLine($"{checkedCount} vectors passed the gate");
        Assert.True(checkedCount >= 180, $"{checkedCount} vectors");
    }

    [Fact]
    public async Task LiveTreesMatchTheRecordedShapes()
    {
        // The shapes tests/Query.Tests/RawSqlGateTests.cs pins were captured from this server version; if the dump
        // format moves, this is the test that says so.
        var client = ch.QueryClient();
        var tree = await client.ExplainAstAsync("SELECT count() FROM events_v WHERE case_id = 1 SETTINGS SQL_allowed_cases = '[1,2]'", [], [1], Ct);
        Assert.Equal(
            [
                "SelectWithUnionQuery (children 1)",
                " ExpressionList (children 1)",
                "  SelectQuery (children 4)",
                "   ExpressionList (children 1)",
                "    Function count (children 1)",
                "     ExpressionList",
                "   TablesInSelectQuery (children 1)",
                "    TablesInSelectQueryElement (children 1)",
                "     TableExpression (children 1)",
                "      TableIdentifier events_v",
                "   Function equals (children 1)",
                "    ExpressionList (children 2)",
                "     Identifier case_id",
                "     Literal UInt64_1",
                "   Set",
            ],
            tree);

        IReadOnlyList<string> format;
        try
        {
            format = await client.ExplainAstAsync("SELECT 1 INTO OUTFILE '/tmp/x' FORMAT TSV", [], [1], Ct);
        }
        catch (ClickHouseServerException e)
        {
            format = [$"<refused {e.ErrorCode}>"];
        }

        output.WriteLine(string.Join("\n", format));
        Assert.True(format[0].StartsWith("<refused", StringComparison.Ordinal) || format.Contains(" Identifier TSV"), "INTO OUTFILE over HTTP is refused by the server, or the FORMAT shows as a root child");
    }

    private static string NestedSelects(int selects)
    {
        var sql = "SELECT * FROM events_v";
        for (var i = 1; i < selects; i++)
        {
            sql = $"SELECT * FROM ({sql})";
        }

        return sql;
    }
}
