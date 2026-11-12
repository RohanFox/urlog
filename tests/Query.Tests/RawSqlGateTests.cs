using Urlog.Query;

namespace Urlog.Query.Tests;

/// <summary>
/// The gate's walk over recorded <c>EXPLAIN AST</c> trees, exactly as ClickHouse 26.8.36.3 dumps them (captured in
/// the isolation spike; tests/Security/RawSqlGateTests.cs re-takes every one of them from the live server). No Docker here: this is
/// what the pre-commit hook runs.
/// </summary>
public sealed class RawSqlGateTests
{
    private static string[] Lines(string tree) => tree.ReplaceLineEndings("\n").Split('\n');

    [Fact]
    public void PlainSelectOverTheViewPasses()
    {
        Assert.Null(RawSqlGate.Inspect(Lines("""
            SelectWithUnionQuery (children 1)
             ExpressionList (children 1)
              SelectQuery (children 2)
               ExpressionList (children 1)
                Asterisk
               TablesInSelectQuery (children 1)
                TablesInSelectQueryElement (children 1)
                 TableExpression (children 1)
                  TableIdentifier events_v
            """)));
    }

    [Fact]
    public void TranspilerShapePasses()
    {
        // SELECT ts, host FROM events_v WHERE case_id = {case:UInt64} AND ts >= {from:DateTime64(3)} ORDER BY ts DESC LIMIT {limit:UInt64}, parameters substituted.
        Assert.Null(RawSqlGate.Inspect(Lines("""
            SelectWithUnionQuery (children 1)
             ExpressionList (children 1)
              SelectQuery (children 5)
               ExpressionList (children 2)
                Identifier ts
                Identifier host
               TablesInSelectQuery (children 1)
                TablesInSelectQueryElement (children 1)
                 TableExpression (children 1)
                  TableIdentifier events_v
               Function and (children 1)
                ExpressionList (children 2)
                 Function equals (children 1)
                  ExpressionList (children 2)
                   Identifier case_id
                   Literal UInt64_1
                 Function greaterOrEquals (children 1)
                  ExpressionList (children 2)
                   Identifier ts
                   Function _CAST (children 1)
                    ExpressionList (children 2)
                     Literal Decimal64_'1767225600.000'
                     Literal 'DateTime64(3)'
               ExpressionList (children 1)
                OrderByElement (children 1)
                 Identifier ts
               Literal UInt64_100
            """)));
    }

    [Fact]
    public void CteJoinSubqueryAndInSubqueryPass()
    {
        // WITH c AS (SELECT host FROM events_v) SELECT * FROM c AS k
        Assert.Null(RawSqlGate.Inspect(Lines("""
            SelectWithUnionQuery (children 1)
             ExpressionList (children 1)
              SelectQuery (children 3)
               ExpressionList (children 1)
                WithElement (children 1)
                 Subquery (children 1)
                  SelectWithUnionQuery (children 1)
                   ExpressionList (children 1)
                    SelectQuery (children 2)
                     ExpressionList (children 1)
                      Identifier host
                     TablesInSelectQuery (children 1)
                      TablesInSelectQueryElement (children 1)
                       TableExpression (children 1)
                        TableIdentifier events_v
               ExpressionList (children 1)
                Asterisk
               TablesInSelectQuery (children 1)
                TablesInSelectQueryElement (children 1)
                 TableExpression (children 1)
                  TableIdentifier c (alias k)
            """)));

        // SELECT * FROM events_v e JOIN field_stats f ON e.case_id = f.case_id
        Assert.Null(RawSqlGate.Inspect(Lines("""
            SelectWithUnionQuery (children 1)
             ExpressionList (children 1)
              SelectQuery (children 2)
               ExpressionList (children 1)
                Asterisk
               TablesInSelectQuery (children 2)
                TablesInSelectQueryElement (children 1)
                 TableExpression (children 1)
                  TableIdentifier events_v (alias e)
                TablesInSelectQueryElement (children 2)
                 TableExpression (children 1)
                  TableIdentifier field_stats (alias f)
                 TableJoin (children 1)
                  Function equals (children 1)
                   ExpressionList (children 2)
                    Identifier e.case_id
                    Identifier f.case_id
            """)));

        // SELECT * FROM events_v WHERE host IN (SELECT host FROM events_v)
        Assert.Null(RawSqlGate.Inspect(Lines("""
            SelectWithUnionQuery (children 1)
             ExpressionList (children 1)
              SelectQuery (children 3)
               ExpressionList (children 1)
                Asterisk
               TablesInSelectQuery (children 1)
                TablesInSelectQueryElement (children 1)
                 TableExpression (children 1)
                  TableIdentifier events_v
               Function in (children 1)
                ExpressionList (children 2)
                 Identifier host
                 Subquery (children 1)
                  SelectWithUnionQuery (children 1)
                   ExpressionList (children 1)
                    SelectQuery (children 2)
                     ExpressionList (children 1)
                      Identifier host
                     TablesInSelectQuery (children 1)
                      TablesInSelectQueryElement (children 1)
                       TableExpression (children 1)
                        TableIdentifier events_v
            """)));
    }

    [Fact]
    public void WordsThatLookLikeKeywordsInAliasesAndLiteralsPass()
    {
        // SELECT 1 AS "SETTINGS"
        Assert.Null(RawSqlGate.Inspect(Lines("""
            SelectWithUnionQuery (children 1)
             ExpressionList (children 1)
              SelectQuery (children 1)
               ExpressionList (children 1)
                Literal UInt64_1 (alias SETTINGS)
            """)));

        // SELECT 'SETTINGS SQL_allowed_cases=1'
        Assert.Null(RawSqlGate.Inspect(Lines("""
            SelectWithUnionQuery (children 1)
             ExpressionList (children 1)
              SelectQuery (children 1)
               ExpressionList (children 1)
                Literal 'SETTINGS SQL_allowed_cases=1'
            """)));
    }

    [Theory]
    [InlineData("top level", """
        SelectWithUnionQuery (children 1)
         ExpressionList (children 1)
          SelectQuery (children 3)
           ExpressionList (children 1)
            Asterisk
           TablesInSelectQuery (children 1)
            TablesInSelectQueryElement (children 1)
             TableExpression (children 1)
              TableIdentifier events_v
           Set
        """)]
    [InlineData("in a FROM subquery", """
        SelectWithUnionQuery (children 1)
         ExpressionList (children 1)
          SelectQuery (children 2)
           ExpressionList (children 1)
            Asterisk
           TablesInSelectQuery (children 1)
            TablesInSelectQueryElement (children 1)
             TableExpression (children 1)
              Subquery (children 1)
               SelectWithUnionQuery (children 1)
                ExpressionList (children 1)
                 SelectQuery (children 3)
                  ExpressionList (children 1)
                   Asterisk
                  TablesInSelectQuery (children 1)
                   TablesInSelectQueryElement (children 1)
                    TableExpression (children 1)
                     TableIdentifier events_v
                  Set
        """)]
    [InlineData("in a scalar subquery", """
        SelectWithUnionQuery (children 1)
         ExpressionList (children 1)
          SelectQuery (children 3)
           ExpressionList (children 1)
            Asterisk
           TablesInSelectQuery (children 1)
            TablesInSelectQueryElement (children 1)
             TableExpression (children 1)
              TableIdentifier events_v
           Function equals (children 1)
            ExpressionList (children 2)
             Literal UInt64_1
             Subquery (children 1)
              SelectWithUnionQuery (children 1)
               ExpressionList (children 1)
                SelectQuery (children 2)
                 ExpressionList (children 1)
                  Literal UInt64_1
                 Set
        """)]
    [InlineData("in a WITH scalar", """
        SelectWithUnionQuery (children 1)
         ExpressionList (children 1)
          SelectQuery (children 2)
           ExpressionList (children 1)
            Subquery (alias v) (children 1)
             SelectWithUnionQuery (children 1)
              ExpressionList (children 1)
               SelectQuery (children 2)
                ExpressionList (children 1)
                 Literal UInt64_1
                Set
           ExpressionList (children 1)
            Identifier v
        """)]
    [InlineData("after a UNION", """
        SelectWithUnionQuery (children 2)
         ExpressionList (children 2)
          SelectQuery (children 1)
           ExpressionList (children 1)
            Literal UInt64_1
          SelectQuery (children 1)
           ExpressionList (children 1)
            Literal UInt64_2
         Set
        """)]
    [InlineData("with FORMAT", """
        SelectWithUnionQuery (children 3)
         ExpressionList (children 1)
          SelectQuery (children 1)
           ExpressionList (children 1)
            Literal UInt64_1
         Set
         Identifier JSON
        """)]
    public void SettingsClauseIsRejectedWhereverItSits(string where, string tree)
    {
        var reason = RawSqlGate.Inspect(Lines(tree));
        Assert.NotNull(reason);
        Assert.Contains("SETTINGS", reason, StringComparison.Ordinal);
        Assert.NotEmpty(where);
    }

    [Theory]
    [InlineData("FORMAT clause", "FORMAT", """
        SelectWithUnionQuery (children 2)
         ExpressionList (children 1)
          SelectQuery (children 1)
           ExpressionList (children 1)
            Literal UInt64_1
         Identifier JSON
        """)]
    [InlineData("INTO OUTFILE", "INTO OUTFILE", """
        SelectWithUnionQuery (children 2)
         ExpressionList (children 1)
          SelectQuery (children 1)
           ExpressionList (children 1)
            Literal UInt64_1
         Literal '/tmp/x'
        """)]
    [InlineData("url()", "table function url()", """
        SelectWithUnionQuery (children 1)
         ExpressionList (children 1)
          SelectQuery (children 2)
           ExpressionList (children 1)
            Asterisk
           TablesInSelectQuery (children 1)
            TablesInSelectQueryElement (children 1)
             TableExpression (children 1)
              Function url (children 1)
               ExpressionList (children 3)
                Literal 'http://x/'
                Identifier CSV
                Literal 'a String'
        """)]
    [InlineData("file()", "table function file()", """
        SelectWithUnionQuery (children 1)
         ExpressionList (children 1)
          SelectQuery (children 2)
           ExpressionList (children 1)
            Asterisk
           TablesInSelectQuery (children 1)
            TablesInSelectQueryElement (children 1)
             TableExpression (children 1)
              Function file (children 1)
               ExpressionList (children 1)
                Literal 'x.csv'
        """)]
    [InlineData("numbers()", "table function numbers()", """
        SelectWithUnionQuery (children 1)
         ExpressionList (children 1)
          SelectQuery (children 2)
           ExpressionList (children 1)
            Asterisk
           TablesInSelectQuery (children 1)
            TablesInSelectQueryElement (children 1)
             TableExpression (children 1)
              Function numbers (children 1)
               ExpressionList (children 1)
                Literal UInt64_10
        """)]
    [InlineData("system.tables", "qualified", """
        SelectWithUnionQuery (children 1)
         ExpressionList (children 1)
          SelectQuery (children 2)
           ExpressionList (children 1)
            Identifier name
           TablesInSelectQuery (children 1)
            TablesInSelectQueryElement (children 1)
             TableExpression (children 1)
              TableIdentifier system.tables
        """)]
    [InlineData("default.events_v", "qualified", """
        SelectWithUnionQuery (children 1)
         ExpressionList (children 1)
          SelectQuery (children 2)
           ExpressionList (children 1)
            Asterisk
           TablesInSelectQuery (children 1)
            TablesInSelectQueryElement (children 1)
             TableExpression (children 1)
              TableIdentifier default.events_v
        """)]
    [InlineData("events directly", "events is not readable", """
        SelectWithUnionQuery (children 1)
         ExpressionList (children 1)
          SelectQuery (children 2)
           ExpressionList (children 1)
            Asterisk
           TablesInSelectQuery (children 1)
            TablesInSelectQueryElement (children 1)
             TableExpression (children 1)
              TableIdentifier events
        """)]
    [InlineData("events in a JOIN", "events is not readable", """
        SelectWithUnionQuery (children 1)
         ExpressionList (children 1)
          SelectQuery (children 2)
           ExpressionList (children 1)
            Asterisk
           TablesInSelectQuery (children 2)
            TablesInSelectQueryElement (children 1)
             TableExpression (children 1)
              TableIdentifier events_v (alias e)
            TablesInSelectQueryElement (children 2)
             TableExpression (children 1)
              TableIdentifier events (alias x)
             TableJoin (children 1)
              Function equals (children 1)
               ExpressionList (children 2)
                Identifier e.host
                Identifier x.host
        """)]
    [InlineData("events in a joined subquery", "events is not readable", """
        SelectWithUnionQuery (children 1)
         ExpressionList (children 1)
          SelectQuery (children 2)
           ExpressionList (children 1)
            Asterisk
           TablesInSelectQuery (children 2)
            TablesInSelectQueryElement (children 1)
             TableExpression (children 1)
              TableIdentifier events_v (alias e)
            TablesInSelectQueryElement (children 2)
             TableExpression (children 1)
              Subquery (alias x) (children 1)
               SelectWithUnionQuery (children 1)
                ExpressionList (children 1)
                 SelectQuery (children 2)
                  ExpressionList (children 1)
                   Asterisk
                  TablesInSelectQuery (children 1)
                   TablesInSelectQueryElement (children 1)
                    TableExpression (children 1)
                     TableIdentifier events
             TableJoin (children 1)
              Function equals (children 1)
               ExpressionList (children 2)
                Identifier e.host
                Identifier x.host
        """)]
    [InlineData("IN events", "events is not readable", """
        SelectWithUnionQuery (children 1)
         ExpressionList (children 1)
          SelectQuery (children 3)
           ExpressionList (children 1)
            Asterisk
           TablesInSelectQuery (children 1)
            TablesInSelectQueryElement (children 1)
             TableExpression (children 1)
              TableIdentifier events_v
           Function in (children 1)
            ExpressionList (children 2)
             Identifier host
             Identifier events
        """)]
    [InlineData("IN system.tables", "qualified", """
        SelectWithUnionQuery (children 1)
         ExpressionList (children 1)
          SelectQuery (children 3)
           ExpressionList (children 1)
            Asterisk
           TablesInSelectQuery (children 1)
            TablesInSelectQueryElement (children 1)
             TableExpression (children 1)
              TableIdentifier events_v
           Function globalNotIn (children 1)
            ExpressionList (children 2)
             Identifier host
             Identifier system.tables
        """)]
    [InlineData("file_generation", "file_generation is not readable", """
        SelectWithUnionQuery (children 1)
         ExpressionList (children 1)
          SelectQuery (children 2)
           ExpressionList (children 1)
            Asterisk
           TablesInSelectQuery (children 1)
            TablesInSelectQueryElement (children 1)
             TableExpression (children 1)
              TableIdentifier file_generation
        """)]
    [InlineData("EXPLAIN", "only SELECT", """
        Explain EXPLAIN (children 1)
         SelectWithUnionQuery (children 1)
          ExpressionList (children 1)
           SelectQuery (children 1)
            ExpressionList (children 1)
             Literal UInt64_1
        """)]
    [InlineData("INSERT", "only SELECT", """
        InsertQuery   (children 2)
         Identifier events_v
         SelectWithUnionQuery (children 1)
          ExpressionList (children 1)
           SelectQuery (children 1)
            ExpressionList (children 1)
             Literal UInt64_1
        """)]
    [InlineData("SET", "only SELECT", "Set")]
    [InlineData("SHOW TABLES", "only SELECT", "ShowTables")]
    [InlineData("DESCRIBE", "only SELECT", """
        DescribeQuery (children 1)
         TableExpression (children 1)
          TableIdentifier events
        """)]
    [InlineData("CREATE", "only SELECT", """
        CreateQuery t (children 3)
         Identifier t
         Columns definition (children 1)
          ExpressionList (children 1)
           ColumnDeclaration x (children 1)
            DataType UInt8
         Storage definition (children 1)
          Function Memory
        """)]
    [InlineData("KILL", "only SELECT", """
        KillQueryQuery Literal_UInt64_1 ASYNC (children 1)
         Literal UInt64_1
        """)]
    [InlineData("empty tree", "no syntax tree", "")]
    public void HostileShapesAreRejected(string what, string expectedReason, string tree)
    {
        var reason = RawSqlGate.Inspect(Lines(tree));
        Assert.NotNull(reason);
        Assert.Contains(expectedReason, reason, StringComparison.Ordinal);
        Assert.NotEmpty(what);
    }

    [Fact]
    public void NestingBeyondEightSelectsIsRejectedAndEightPasses()
    {
        Assert.Null(RawSqlGate.Inspect(Lines(Nested(RawSqlGate.MaxSelectDepth))));
        var reason = RawSqlGate.Inspect(Lines(Nested(RawSqlGate.MaxSelectDepth + 1)));
        Assert.NotNull(reason);
        Assert.Contains("nest deeper", reason, StringComparison.Ordinal);

        // SELECT * FROM (SELECT * FROM (... events_v ...)) with the given number of SELECTs, as the server dumps it.
        static string Nested(int selects)
        {
            var lines = new List<string>();
            var indent = 0;
            for (var i = 0; i < selects; i++)
            {
                var pad = new string(' ', indent);
                lines.Add(pad + "SelectWithUnionQuery (children 1)");
                lines.Add(pad + " ExpressionList (children 1)");
                lines.Add(pad + "  SelectQuery (children 2)");
                lines.Add(pad + "   ExpressionList (children 1)");
                lines.Add(pad + "    Asterisk");
                lines.Add(pad + "   TablesInSelectQuery (children 1)");
                lines.Add(pad + "    TablesInSelectQueryElement (children 1)");
                lines.Add(pad + "     TableExpression (children 1)");
                if (i == selects - 1)
                {
                    lines.Add(pad + "      TableIdentifier events_v");
                }
                else
                {
                    lines.Add(pad + "      Subquery (children 1)");
                    indent += 7;
                }
            }

            return string.Join('\n', lines);
        }
    }

    [Fact]
    public void PreCheckRefusesEmptyAndOversizedText()
    {
        Assert.NotNull(RawSqlGate.PreCheck(""));
        Assert.NotNull(RawSqlGate.PreCheck("   \n"));
        Assert.Null(RawSqlGate.PreCheck("SELECT 1"));
        Assert.Null(RawSqlGate.PreCheck("SELECT 1 /*" + new string('x', RawSqlGate.MaxBytes - 20) + "*/"));
        var tooBig = RawSqlGate.PreCheck("SELECT 1 /*" + new string('x', 1024 * 1024) + "*/");
        Assert.NotNull(tooBig);
        Assert.Contains("limit is 16,384", tooBig, StringComparison.Ordinal);
        // Multi-byte text counts in bytes, not chars.
        Assert.NotNull(RawSqlGate.PreCheck("SELECT '" + new string('é', RawSqlGate.MaxBytes / 2) + "'"));
    }

    [Fact]
    public void NormaliseTrimsOneTrailingSemicolon()
    {
        Assert.Equal("SELECT 1", RawSqlGate.Normalise("  SELECT 1 ;\n"));
        Assert.Equal("SELECT 1;", RawSqlGate.Normalise("SELECT 1;;"));
        Assert.Equal("", RawSqlGate.Normalise(";"));
    }

    [Fact]
    public void RelationListsAreDisjointAndNameTheSchema()
    {
        Assert.Empty(RawSqlGate.AllowedRelations.Intersect(RawSqlGate.DeniedRelations, StringComparer.Ordinal));
        Assert.Contains("events_v", RawSqlGate.AllowedRelations.ToArray());
        Assert.Contains("events", RawSqlGate.DeniedRelations.ToArray());
    }
}
