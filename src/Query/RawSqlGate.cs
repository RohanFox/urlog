using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace Urlog.Query;

/// <summary>A query the raw-SQL gate refused, with the reason the UI shows. Nothing reached the event store.</summary>
public sealed class RawSqlRejectedException : Exception
{
    public RawSqlRejectedException()
    {
    }

    public RawSqlRejectedException(string message)
        : base(message)
    {
    }

    public RawSqlRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The raw-SQL gate (design record, "Security"; ADR 0001): every query text, the analyst's raw SQL and the
/// transpiler's output alike, is parsed by ClickHouse itself (<c>EXPLAIN AST</c>, run as the same read-only user)
/// and refused unless the tree is a plain SELECT that names nothing but <c>events_v</c>, <c>field_stats</c> and its
/// own CTEs. The one hole it closes is a <c>SETTINGS</c> clause: with <c>readonly = 2</c> the server lets a query-level
/// <c>SETTINGS SQL_allowed_cases = ...</c> override the per-request value (measured, ADR 0001), so the gate is mandatory,
/// not defence in depth. The walk is over the dumped tree, so there is no SQL parser on this side: the shapes come
/// from the pinned LTS and are pinned by tests/Query.Tests/RawSqlGateTests.cs (recorded trees) and
/// tests/Security/RawSqlGateTests.cs (the live server).
/// </summary>
public static class RawSqlGate
{
    /// <summary>The raw-SQL tab's limit on the text; ClickHouse's own <c>max_query_size</c> (256 KiB) sits far above it.</summary>
    public const int MaxBytes = 16 * 1024;

    /// <summary>How many SELECTs may nest (a subquery inside a subquery ... counts each level).</summary>
    public const int MaxSelectDepth = 8;

    /// <summary>The relations the query path may name.</summary>
    public static readonly FrozenSet<string> AllowedRelations = FrozenSet.ToFrozenSet(["events_v", "field_stats"], StringComparer.Ordinal);

    /// <summary>
    /// Every other relation the migrations create. An unqualified name that is in neither set is taken for a CTE
    /// (<c>WITH c AS (...) SELECT ... FROM c</c>: the dumped tree does not print the CTE's name, so the gate cannot
    /// tell one from a typo; the server then answers UNKNOWN_TABLE, and the user's grants cover any table that is
    /// not in this list). tests/Security asserts this list against <c>system.tables</c>, so a new migration that
    /// adds a relation must add it here too.
    /// </summary>
    public static readonly FrozenSet<string> DeniedRelations = FrozenSet.ToFrozenSet(["events", "file_generation", "schema_migrations", "store_identity"], StringComparer.Ordinal);

    private static readonly FrozenSet<string> InOperators = FrozenSet.ToFrozenSet(
        ["in", "notIn", "nullIn", "notNullIn", "globalIn", "globalNotIn", "globalNullIn", "globalNotNullIn"], StringComparer.Ordinal);

    /// <summary>Trims surrounding whitespace and one trailing semicolon (the HTTP interface refuses multi-statements, and a lone trailing one would read as that).</summary>
    public static string Normalise(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var text = sql.Trim();
        if (text.EndsWith(';'))
        {
            text = text[..^1].TrimEnd();
        }

        return text;
    }

    /// <summary>The checks that need no server: the reason to refuse, or null.</summary>
    public static string? PreCheck(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        if (string.IsNullOrWhiteSpace(sql))
        {
            return "the query is empty";
        }

        var bytes = Encoding.UTF8.GetByteCount(sql);
        if (bytes > MaxBytes)
        {
            return string.Create(CultureInfo.InvariantCulture, $"the query is {bytes:N0} bytes; the limit is {MaxBytes:N0}");
        }

        return null;
    }

    /// <summary>
    /// Walks the lines of <c>EXPLAIN AST</c> (one node per line, one space of indent per level) and returns the
    /// reason to refuse, or null when the query is a plain SELECT over the allowed relations.
    /// </summary>
    public static string? Inspect(IReadOnlyList<string> astLines)
    {
        ArgumentNullException.ThrowIfNull(astLines);
        if (astLines.Count == 0)
        {
            return "the server returned no syntax tree";
        }

        var nodes = new List<Node>(astLines.Count);
        foreach (var line in astLines)
        {
            if (line.Length == 0)
            {
                continue;
            }

            nodes.Add(Node.Parse(line, nodes.Count));
        }

        if (nodes.Count == 0 || nodes[0].Indent != 0)
        {
            return "the server returned no syntax tree";
        }

        if (nodes[0].Kind != "SelectWithUnionQuery")
        {
            return $"only SELECT queries are allowed (got {Describe(nodes[0])})";
        }

        // A FORMAT clause, an INTO OUTFILE and a union-level SETTINGS are the root's children besides the select list.
        for (var i = 1; i < nodes.Count && nodes[i].Indent >= 1; i++)
        {
            if (nodes[i].Indent == 1 && nodes[i].Kind != "ExpressionList")
            {
                return nodes[i].Kind switch
                {
                    "Set" => "a SETTINGS clause is not allowed",
                    "Identifier" => "a FORMAT clause is not allowed",
                    "Literal" => "INTO OUTFILE is not allowed",
                    _ => $"unsupported clause: {Describe(nodes[i])}",
                };
            }
        }

        var selectDepth = new Stack<int>(); // indents of the enclosing SelectQuery nodes
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            while (selectDepth.Count > 0 && selectDepth.Peek() >= node.Indent)
            {
                selectDepth.Pop();
            }

            switch (node.Kind)
            {
                case "Set":
                    return "a SETTINGS clause is not allowed";
                case "SelectQuery":
                    selectDepth.Push(node.Indent);
                    if (selectDepth.Count > MaxSelectDepth)
                    {
                        return string.Create(CultureInfo.InvariantCulture, $"subqueries nest deeper than {MaxSelectDepth} levels");
                    }

                    break;
                case "TableExpression":
                    foreach (var child in Children(nodes, i))
                    {
                        if (child.Kind == "Function")
                        {
                            return $"table function {child.Name}() is not allowed";
                        }

                        if (child.Kind == "TableIdentifier" && Relation(child.Name) is { } reason)
                        {
                            return reason;
                        }
                    }

                    break;
                case "Function" when InOperators.Contains(node.Name):
                    // `x IN t` reads the table t: the right operand is an Identifier, not a TableIdentifier.
                    foreach (var list in Children(nodes, i))
                    {
                        var args = Children(nodes, list.Index).ToList();
                        if (args.Count == 2 && args[1].Kind == "Identifier" && Relation(args[1].Name) is { } reason)
                        {
                            return reason;
                        }
                    }

                    break;
                default:
                    break;
            }
        }

        return null;
    }

    private static string? Relation(string name)
    {
        if (name.Contains('.', StringComparison.Ordinal))
        {
            return $"qualified names are not allowed: {name}";
        }

        if (DeniedRelations.Contains(name))
        {
            return $"{name} is not readable through the query path; query events_v";
        }

        return null;
    }

    private static string Describe(Node node) => node.Name.Length == 0 ? node.Kind : node.Kind + " " + node.Name;

    private static IEnumerable<Node> Children(List<Node> nodes, int parent)
    {
        var indent = nodes[parent].Indent;
        for (var i = parent + 1; i < nodes.Count && nodes[i].Indent > indent; i++)
        {
            if (nodes[i].Indent == indent + 1)
            {
                yield return nodes[i];
            }
        }
    }

    /// <summary>One dumped node: <c>Kind Name (alias a) (children n)</c>, where the name and the parentheses are optional.</summary>
    private readonly record struct Node(int Index, int Indent, string Kind, string Name)
    {
        public static Node Parse(string line, int index)
        {
            var indent = 0;
            while (indent < line.Length && line[indent] == ' ')
            {
                indent++;
            }

            var rest = line.AsSpan(indent);
            var space = rest.IndexOf(' ');
            var kind = space < 0 ? rest.ToString() : rest[..space].ToString();
            var name = "";
            if (space >= 0)
            {
                var tail = rest[(space + 1)..];
                if (tail.Length > 0 && tail[0] != '(')
                {
                    var end = tail.IndexOf(" (", StringComparison.Ordinal);
                    name = (end < 0 ? tail : tail[..end]).ToString();
                }
            }

            return new Node(index, indent, kind, name);
        }
    }
}
