using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Urlog.Query;

/// <summary>What the caller knows that the query text does not: the case, the default range, the display timezone and the clock.</summary>
public sealed record TranspileOptions(ulong CaseId, DateTimeOffset RangeFrom, DateTimeOffset RangeTo, TimeZoneInfo DisplayZone, DateTimeOffset Now)
{
    /// <summary>Cases the user may name with <c>index=</c>; <see cref="CaseId"/> is always allowed.</summary>
    public IReadOnlySet<ulong>? AllowedCases { get; init; }
}

/// <summary>One bound parameter: the <c>{name:Type}</c> placeholder in the SQL and its value as the HTTP interface takes it.</summary>
public sealed record QueryParameter(string Name, string Type, string Value);

/// <summary>A non-fatal note for the UI: <c>unindexed</c>, <c>assumed_timezone</c>, <c>unknown_field</c>.</summary>
public sealed record QueryWarning(string Code, TextSpan Span, string Message);

public sealed record TranspileResult(
    string Sql,
    ImmutableArray<QueryParameter> Parameters,
    ImmutableArray<QueryWarning> Warnings,
    ImmutableArray<FieldDef> Fields,
    bool IsAggregate,
    ulong CaseId,
    DateTimeOffset RangeFrom,
    DateTimeOffset RangeTo)
{
    /// <summary>
    /// The name of each output column, in SELECT order: what the analyst wrote (an alias, a field the catalog does
    /// not list) or the catalog name. The SQL carries a generated alias for the former, so the reader of the result
    /// takes its headers from here.
    /// </summary>
    public ImmutableArray<string> Columns { get; init; } = [];
}

/// <summary>
/// AST -> parameterised ClickHouse SQL over <c>events_v</c> (design record, "Query language"): every value is a
/// <c>{name:Type}</c> parameter, never concatenated; the case filter and the time range come first so the partition
/// and the sort key prune; bare terms use the text index on <c>lower(message)</c> (and only <c>message</c>: an OR with
/// <c>raw</c> would defeat the index); nothing is ever appended as SETTINGS (the raw-SQL gate rejects SETTINGS).
/// </summary>
public sealed class Transpiler
{
    private const string DefaultColumns = "ts, host, program, severity, source_ip, message";
    private static readonly string[] DefaultColumnNames = ["ts", "host", "program", "severity", "source_ip", "message"];
    private static readonly SearchValues<char> Wildcards = SearchValues.Create("*?");

    private readonly FieldCatalog _catalog;
    private readonly List<QueryParameter> _parameters = [];
    private readonly List<QueryWarning> _warnings = [];
    private readonly Dictionary<string, FieldDef> _fields = new(StringComparer.Ordinal);
    private int _next;
    private TranspileOptions? _current;

    public Transpiler(FieldCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
    }

    /// <summary>Parses and transpiles in one call.</summary>
    public static TranspileResult Transpile(string spl, TranspileOptions options, FieldCatalog? catalog = null) =>
        new Transpiler(catalog ?? FieldCatalog.Embedded).Transpile(Parser.Parse(spl), options);

    public TranspileResult Transpile(Query query, TranspileOptions options)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(options);
        _parameters.Clear();
        _warnings.Clear();
        _fields.Clear();
        _next = 0;
        _current = options;

        var caseId = options.CaseId;
        if (query.Index is { } index)
        {
            if (!ulong.TryParse(index.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var requested))
            {
                throw new QueryException(index.Span, $"index '{index.Value}' is not a case id");
            }

            if (requested != options.CaseId && options.AllowedCases?.Contains(requested) != true)
            {
                throw new QueryException(index.Span, $"index {requested} is not an allowed case");
            }

            caseId = requested;
        }

        var from = options.RangeFrom.ToUniversalTime();
        var to = options.RangeTo.ToUniversalTime();
        if (query.Earliest is { } earliest)
        {
            from = ParseTime(earliest.Value, earliest.Span, options);
        }

        if (query.Latest is { } latest)
        {
            to = ParseTime(latest.Value, latest.Span, options);
        }

        if (to <= from)
        {
            var span = query.Latest?.Span ?? query.Earliest?.Span ?? new TextSpan(0, 0);
            throw new QueryException(span, $"the time range is empty: {Fmt(from)} to {Fmt(to)}");
        }

        _parameters.Add(new QueryParameter("case", "UInt64", caseId.ToString(CultureInfo.InvariantCulture)));
        _parameters.Add(new QueryParameter("from", "DateTime64(3)", Fmt(from)));
        _parameters.Add(new QueryParameter("to", "DateTime64(3)", Fmt(to)));

        var where = new StringBuilder("case_id = {case:UInt64} AND ts >= {from:DateTime64(3)} AND ts < {to:DateTime64(3)}");
        if (query.Search is not null && query.Search is not AndNode { Terms.Length: 0 })
        {
            where.Append(" AND (").Append(Expression(query.Search)).Append(')');
        }

        StatsCountNode? stats = null;
        SortNode? sort = null;
        HeadNode? head = null;
        foreach (var command in query.Commands)
        {
            switch (command)
            {
                case StatsCountNode s:
                    if (stats is not null)
                    {
                        throw new QueryException(s.Span, "a second stats is not supported in v1");
                    }

                    if (sort is not null || head is not null)
                    {
                        throw new QueryException(s.Span, "stats must come before sort and head");
                    }

                    stats = s;
                    break;
                case SortNode s:
                    if (head is not null)
                    {
                        throw new QueryException(s.Span, "sort must come before head");
                    }

                    sort = s;
                    break;
                case HeadNode h:
                    head = h;
                    break;
                default:
                    throw new QueryException(command.Span, "not supported in v1");
            }
        }

        var sql = new StringBuilder();
        var columns = new List<string>();
        if (stats is null)
        {
            columns.AddRange(DefaultColumnNames);
            sql.Append("SELECT ").Append(DefaultColumns).Append(" FROM events_v WHERE ").Append(where);
            sql.Append(" ORDER BY ");
            if (sort is null)
            {
                sql.Append("ts DESC");
            }
            else
            {
                var keys = new List<string>();
                foreach (var (name, descending, span) in sort.Keys)
                {
                    var field = Resolve(name, span);
                    keys.Add(field.Expression(field.Column is null ? KeyParameter(field.ExtraKey!) : null) + (descending ? " DESC" : " ASC"));
                }

                sql.Append(string.Join(", ", keys));
            }

            _parameters.Add(new QueryParameter("limit", "UInt64", (head?.Count ?? Parser.DefaultHead).ToString(CultureInfo.InvariantCulture)));
            sql.Append(" LIMIT {limit:UInt64}");
        }
        else
        {
            // An output column is named in the SQL by a catalog field name or by a generated alias, never by text
            // from the query: the analyst's names (an alias, a field the catalog does not list) label the result
            // beside it, in Columns.
            var generated = 0;
            var groups = new List<(string Name, string Alias, string Expression)>();
            foreach (var (name, span) in stats.By)
            {
                var field = Resolve(name, span);
                var alias = field.IsUnknown ? Names.Generated(generated++) : field.Name;
                groups.Add((field.Name, Quote(alias), field.Expression(field.Column is null ? KeyParameter(field.ExtraKey!) : null)));
                columns.Add(field.Name);
            }

            var countAlias = Quote(Names.Generated(generated));
            columns.Add(stats.Alias);
            sql.Append("SELECT ");
            foreach (var (_, alias, expression) in groups)
            {
                sql.Append(expression).Append(" AS ").Append(alias).Append(", ");
            }

            sql.Append("count() AS ").Append(countAlias).Append(" FROM events_v WHERE ").Append(where);
            if (groups.Count > 0)
            {
                sql.Append(" GROUP BY ").Append(string.Join(", ", groups.Select(g => g.Expression)));
            }

            if (sort is not null)
            {
                var keys = new List<string>();
                foreach (var (name, descending, span) in sort.Keys)
                {
                    string alias;
                    if (name.Equals(stats.Alias, StringComparison.OrdinalIgnoreCase) || name.Equals("count", StringComparison.OrdinalIgnoreCase))
                    {
                        alias = countAlias;
                    }
                    else
                    {
                        var field = Resolve(name, span);
                        var match = groups.FindIndex(g => g.Name == field.Name);
                        if (match < 0)
                        {
                            throw new QueryException(span, $"sort after stats: '{name}' is neither {stats.Alias} nor a by field");
                        }

                        alias = groups[match].Alias;
                    }

                    keys.Add(alias + (descending ? " DESC" : " ASC"));
                }

                sql.Append(" ORDER BY ").Append(string.Join(", ", keys));
            }

            if (head is not null)
            {
                _parameters.Add(new QueryParameter("limit", "UInt64", head.Count.ToString(CultureInfo.InvariantCulture)));
                sql.Append(" LIMIT {limit:UInt64}");
            }
        }

        return new TranspileResult(sql.ToString(), [.. _parameters], [.. _warnings], [.. _fields.Values], stats is not null, caseId, from, to) { Columns = [.. columns] };
    }

    private string Expression(SearchNode node)
    {
        switch (node)
        {
            case AndNode and:
                return and.Terms.Length == 1 ? Expression(and.Terms[0]) : "(" + string.Join(" AND ", and.Terms.Select(Expression)) + ")";
            case OrNode or:
                return "(" + string.Join(" OR ", or.Terms.Select(Expression)) + ")";
            case NotNode not:
                return "NOT (" + Expression(not.Operand) + ")";
            case CompareNode c:
                return Compare(c);
            case InNode @in:
                return In(@in);
            case TermNode term:
                return Term(term.Text, term.Span, isPhrase: false);
            case PhraseNode phrase:
                return Term(phrase.Text, phrase.Span, isPhrase: true);
            default:
                throw new InvalidOperationException(node.GetType().Name);
        }
    }

    private string Compare(CompareNode c)
    {
        var field = Resolve(c.Field, c.FieldSpan);
        var value = c.Value;
        var hasWildcard = !c.Quoted && value.AsSpan().ContainsAny(Wildcards);
        var ordering = c.Op is CompareOp.Lt or CompareOp.Gt or CompareOp.Le or CompareOp.Ge;
        switch (field.Type)
        {
            case FieldType.Number:
            {
                if (ordering || !hasWildcard)
                {
                    if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                    {
                        throw new QueryException(c.ValueSpan, $"{field.Name} expects an unsigned integer, got '{value}'");
                    }

                    return $"{field.Column} {Sql(c.Op)} {{{Add("UInt64", value)}:UInt64}}";
                }

                throw new QueryException(c.ValueSpan, $"{field.Name} is numeric: wildcards do not apply");
            }

            case FieldType.Time:
            {
                var instant = ParseTimeValue(value, c.ValueSpan);
                return $"{field.Column} {Sql(c.Op)} {{{Add("DateTime64(3)", Fmt(instant))}:DateTime64(3)}}";
            }

            case FieldType.Ip:
            {
                if (ordering)
                {
                    throw new QueryException(c.Span, $"{field.Name} is an address: use =, != or a CIDR like 10.0.0.0/8");
                }

                if (value.Contains('/'))
                {
                    var cidr = Cidr(value, c.ValueSpan);
                    var test = $"isIPAddressInRange(IPv6NumToString({field.Column}), {{{Add("String", cidr)}:String}})";
                    return c.Op == CompareOp.Ne ? "NOT " + test : test;
                }

                if (hasWildcard)
                {
                    throw new QueryException(c.ValueSpan, $"{field.Name} is an address: use a CIDR instead of a wildcard");
                }

                if (!IPAddress.TryParse(value, out var address) || address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
                {
                    throw new QueryException(c.ValueSpan, $"'{value}' is not an IP address");
                }

                return $"{field.Column} {Sql(c.Op)} toIPv6({{{Add("String", value)}:String}})";
            }

            case FieldType.Keyword:
            case FieldType.Verbatim:
            case FieldType.Text:
            case FieldType.Extra:
            {
                if (ordering)
                {
                    throw new QueryException(c.Span, $"{field.Name} is a string: <, >, <= and >= need a numeric or time field");
                }

                var column = field.Expression(field.Column is null ? KeyParameter(field.ExtraKey!) : null);
                var text = field.Type == FieldType.Keyword ? AsciiLower(value) : value;
                if (field.Type is FieldType.Text or FieldType.Extra)
                {
                    Warn("unindexed", c.Span, field.Type == FieldType.Text ? $"{field.Name}= compares the whole field; a bare term uses the index" : $"{field.Name} lives in extras: compared without an index");
                }

                if (!hasWildcard)
                {
                    return $"{column} {Sql(c.Op)} {{{Add("String", text)}:String}}";
                }

                var pattern = LikePattern(text);
                var prefixOnly = text.IndexOfAny(['*', '?']) == text.Length - 1 && text[^1] == '*' && text.Length > 1;
                var like = field.Type is FieldType.Verbatim or FieldType.Text or FieldType.Extra && !prefixOnly ? "ILIKE" : "LIKE";
                if (!prefixOnly && field.Type is FieldType.Keyword or FieldType.Verbatim)
                {
                    Warn("unindexed", c.Span, $"a leading wildcard on {field.Name} scans every row");
                }

                var test = $"{column} {like} {{{Add("String", pattern)}:String}}";
                return c.Op == CompareOp.Ne ? "NOT " + test : test;
            }

            default:
                throw new InvalidOperationException(field.Type.ToString());
        }
    }

    private string In(InNode node)
    {
        var field = Resolve(node.Field, node.FieldSpan);
        var items = new List<string>();
        foreach (var (value, quoted, span) in node.Values)
        {
            if (!quoted && value.AsSpan().ContainsAny(Wildcards))
            {
                throw new QueryException(span, "IN takes exact values; use field=val* OR field=other* for wildcards");
            }

            switch (field.Type)
            {
                case FieldType.Number:
                    if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                    {
                        throw new QueryException(span, $"{field.Name} expects an unsigned integer, got '{value}'");
                    }

                    items.Add($"{{{Add("UInt64", value)}:UInt64}}");
                    break;
                case FieldType.Time:
                    items.Add($"{{{Add("DateTime64(3)", Fmt(ParseTimeValue(value, span)))}:DateTime64(3)}}");
                    break;
                case FieldType.Ip:
                    if (value.Contains('/') || !IPAddress.TryParse(value, out _))
                    {
                        throw new QueryException(span, $"'{value}' is not an IP address (IN takes addresses, not CIDRs)");
                    }

                    items.Add($"toIPv6({{{Add("String", value)}:String}})");
                    break;
                default:
                    items.Add($"{{{Add("String", field.Type == FieldType.Keyword ? AsciiLower(value) : value)}:String}}");
                    break;
            }
        }

        if (field.Type is FieldType.Text or FieldType.Extra)
        {
            Warn("unindexed", node.Span, $"{field.Name} IN (...) is compared without an index");
        }

        var column = field.Expression(field.Column is null ? KeyParameter(field.ExtraKey!) : null);
        return $"{column} IN ({string.Join(", ", items)})";
    }

    /// <summary>A bare term or a quoted phrase against <c>message</c>: tokens through the text index, the exact text confirmed by <c>position</c> when the term is more than one token.</summary>
    private string Term(string text, TextSpan span, bool isPhrase)
    {
        var lower = AsciiLower(text);
        if (!isPhrase && lower.AsSpan().ContainsAny(Wildcards))
        {
            Warn("unindexed", span, "a wildcard term scans message without the index");
            return $"lower(message) LIKE {{{Add("String", LikePattern(lower))}:String}}";
        }

        var tokens = Tokens(lower);
        if (tokens.Count == 0)
        {
            Warn("unindexed", span, "a term without letters or digits scans message without the index");
            return $"position(lower(message), {{{Add("String", lower)}:String}}) > 0";
        }

        if (tokens.Count == 1 && tokens[0].Length == lower.Length)
        {
            return $"hasToken(lower(message), {{{Add("String", lower)}:String}})";
        }

        var parts = tokens.Distinct(StringComparer.Ordinal).Select(t => $"hasToken(lower(message), {{{Add("String", t)}:String}})").ToList();
        parts.Add($"position(lower(message), {{{Add("String", lower)}:String}}) > 0");
        return "(" + string.Join(" AND ", parts) + ")";
    }

    /// <summary>ASCII-only lowercasing: what <c>lower()</c> does in ClickHouse (the index is on <c>lower(message)</c>) and what the writer does to LowCardinality columns at ingest.</summary>
    public static string AsciiLower(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return string.Create(text.Length, text, static (span, s) =>
        {
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                span[i] = c is >= 'A' and <= 'Z' ? (char)(c | 0x20) : c;
            }
        });
    }

    /// <summary>The tokens the <c>splitByNonAlpha</c> tokenizer would cut: runs of characters that are not ASCII punctuation or whitespace.</summary>
    public static List<string> Tokens(string text)
    {
        var tokens = new List<string>();
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var separator = i == text.Length || (char.IsAscii(text[i]) && !char.IsAsciiLetterOrDigit(text[i]));
            if (separator)
            {
                if (start >= 0)
                {
                    tokens.Add(text[start..i]);
                    start = -1;
                }
            }
            else if (start < 0)
            {
                start = i;
            }
        }

        return tokens;
    }

    private static string LikePattern(string text)
    {
        var sb = new StringBuilder(text.Length + 4);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '*':
                    sb.Append('%');
                    break;
                case '?':
                    sb.Append('_');
                    break;
                case '%':
                case '_':
                case '\\':
                    sb.Append('\\').Append(ch);
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>A CIDR as <c>isIPAddressInRange</c> needs it against an IPv6 column: an IPv4 range becomes the mapped <c>::ffff:a.b.c.d/(n + 96)</c>.</summary>
    private static string Cidr(string value, TextSpan span)
    {
        var slash = value.IndexOf('/', StringComparison.Ordinal);
        if (!IPAddress.TryParse(value.AsSpan(0, slash), out var address) || !int.TryParse(value.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var bits))
        {
            throw new QueryException(span, $"'{value}' is not a CIDR like 10.0.0.0/8");
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            if (bits > 32)
            {
                throw new QueryException(span, $"'{value}': an IPv4 prefix is at most /32");
            }

            return $"::ffff:{address}/{bits + 96}";
        }

        if (bits > 128)
        {
            throw new QueryException(span, $"'{value}': an IPv6 prefix is at most /128");
        }

        return $"{address}/{bits}";
    }

    private DateTimeOffset ParseTime(string text, TextSpan span, TranspileOptions options)
    {
        try
        {
            var instant = TimeExpr.Parse(text, options.Now, options.DisplayZone, out var assumed);
            if (assumed)
            {
                Warn("assumed_timezone", span, $"read in {options.DisplayZone.Id}");
            }

            return instant;
        }
        catch (FormatException e)
        {
            throw new QueryException(span, e.Message);
        }
    }

    private DateTimeOffset ParseTimeValue(string text, TextSpan span) => ParseTime(text, span, _current ?? throw new InvalidOperationException("Transpile sets the options"));

    private FieldDef Resolve(string name, TextSpan span)
    {
        if (name.Length == 0 || name.Contains('`') || name.Any(char.IsControl))
        {
            throw new QueryException(span, $"'{name}' is not a field name");
        }

        var field = _catalog.Resolve(name);
        if (field.IsUnknown)
        {
            Warn("unknown_field", span, $"'{name}' is not in the catalog: searched as extras['{name}']");
        }

        _fields.TryAdd(field.Name, field);
        return field;
    }

    private string KeyParameter(string key) => Add("String", key);

    private string Add(string type, string value)
    {
        var name = "p" + _next++.ToString(CultureInfo.InvariantCulture);
        _parameters.Add(new QueryParameter(name, type, value));
        return name;
    }

    private void Warn(string code, TextSpan span, string message) => _warnings.Add(new QueryWarning(code, span, message));

    /// <summary>Backquotes a catalog field name or a generated alias: the only two things that are ever an identifier in the SQL.</summary>
    private static string Quote(string identifier) =>
        Names.IsCatalogName(identifier) || Names.IsGenerated(identifier) ? "`" + identifier + "`" : throw new InvalidOperationException("not a catalog name or a generated alias");

    private static string Sql(CompareOp op) => op switch
    {
        CompareOp.Eq => "=",
        CompareOp.Ne => "!=",
        CompareOp.Lt => "<",
        CompareOp.Gt => ">",
        CompareOp.Le => "<=",
        _ => ">=",
    };

    /// <summary>ClickHouse <c>DateTime64(3)</c> text form, UTC.</summary>
    public static string Fmt(DateTimeOffset instant) => instant.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
}
