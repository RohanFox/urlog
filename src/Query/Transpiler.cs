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

    /// <summary>
    /// True: the rows of an aggregation come in an order that is the same on every run. After the keys of
    /// <c>sort</c>, if any, the order goes on through the by fields and then the aggregate columns, so two groups
    /// with equal counts never change places. The search API asks for it; a result that is paged, cut at a
    /// limit or exported has to be the same result twice.
    /// </summary>
    public bool TotalOrder { get; init; }
}

/// <summary>One bound parameter: the <c>{name:Type}</c> placeholder in the SQL and its value as the HTTP interface takes it.</summary>
public sealed record QueryParameter(string Name, string Type, string Value);

/// <summary>A non-fatal note for the UI: <c>unindexed</c>, <c>assumed_timezone</c>, <c>unknown_field</c>.</summary>
public sealed record QueryWarning(string Code, TextSpan Span, string Message);

/// <summary>What the rows of a result are.</summary>
public enum ResultKind
{
    /// <summary>Events, one per row.</summary>
    Events,

    /// <summary>One row per group of a <c>stats</c>.</summary>
    Stats,

    /// <summary>One row per time bucket and series of a <c>timechart</c>, to be pivoted by the reader.</summary>
    Timechart,
}

/// <summary>
/// Where a timechart's columns are. The SQL returns long rows because the series are data, not columns; the
/// reader pivots. A row whose <see cref="InTop"/> cell is 0 is the rest (every series outside the top ten), and
/// its series cell is the type's empty value, never a real one.
/// </summary>
public sealed record TimechartShape(int Time, int Value, int? InTop, int? Series);

/// <summary>One key of a listing's <c>sort</c>: SQL the transpiler wrote, its direction, and the type its values compare as.</summary>
public sealed record ListingSortKey(string Name, string Expression, bool Descending, FieldType Type);

/// <summary>A rex group of a listing: its name and the SQL that extracts it.</summary>
public sealed record ListingColumn(string Name, string Expression);

/// <summary>
/// What an event listing is made of, for the statements a search builds beside the query's own (a page behind a
/// cursor, the histogram, the counts): the filter with the case and the range first, the sort keys, the rex
/// groups, and the <c>head</c> when the query wrote one. Every text is SQL the transpiler wrote, and its
/// parameters are among the result's.
/// </summary>
/// <param name="Select">The columns the query's own statement selects, without the keyword.</param>
/// <param name="Where">The filter, without the keyword.</param>
/// <param name="Sort">The keys of <c>sort</c>; empty for the default order, newest first.</param>
/// <param name="Head">The count of <c>head</c>, null when the query has none.</param>
/// <param name="Rex">The groups the query's rex verbs define, in order.</param>
/// <param name="Filtered">False when the query narrows nothing but the range.</param>
public sealed record ListingParts(string Select, string Where, ImmutableArray<ListingSortKey> Sort, int? Head, ImmutableArray<ListingColumn> Rex, bool Filtered);

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

    public ResultKind Kind { get; init; }

    /// <summary>
    /// Columns that hold <c>values(f)</c>. The SQL asks for one value more than the cap
    /// (<see cref="Transpiler.ValuesCap"/>), so the reader can tell a group that reached it: it trims the cell to
    /// the cap and marks the row. A cell holds the smallest values of its group in ascending order.
    /// </summary>
    public ImmutableArray<int> ValuesColumns { get; init; } = [];

    public TimechartShape? Timechart { get; init; }

    /// <summary>The parts of an event listing; null for an aggregation.</summary>
    public ListingParts? Listing { get; init; }
}

/// <summary>
/// AST -> parameterised ClickHouse SQL over <c>events_v</c> (design record, "Query language"): every value is a
/// <c>{name:Type}</c> parameter, never concatenated; the case filter and the time range come first so the partition
/// and the sort key prune; bare terms use the text index on <c>lower(message)</c> (and only <c>message</c>: an OR with
/// <c>raw</c> would defeat the index); nothing is ever appended as SETTINGS (the raw-SQL gate rejects SETTINGS).
/// </summary>
public sealed class Transpiler
{
    /// <summary>The most values <c>values(f)</c> shows for one group.</summary>
    public const int ValuesCap = 100;

    /// <summary>Series a timechart keeps; the rest is one series.</summary>
    public const int TopSeries = 10;

    /// <summary>The most buckets a timechart may ask for over its range.</summary>
    public const int MaxBuckets = 5000;

    /// <summary>The most <c>rex</c> verbs in one query.</summary>
    public const int MaxRex = 4;

    private const string DefaultColumns = "ts, host, program, severity, source_ip, message";
    private static readonly string[] DefaultColumnNames = ["ts", "host", "program", "severity", "source_ip", "message"];
    private static readonly SearchValues<char> Wildcards = SearchValues.Create("*?");

    private readonly FieldCatalog _catalog;
    private readonly List<QueryParameter> _parameters = [];
    private readonly List<QueryWarning> _warnings = [];
    private readonly Dictionary<string, FieldDef> _fields = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Operand> _rex = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Operand> _rexOrder = [];
    private List<Output>? _outputs;
    private int _next;
    private int _generated;
    private int _rexCount;
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
        _rex.Clear();
        _rexOrder.Clear();
        _outputs = null;
        _next = 0;
        _generated = 0;
        _rexCount = 0;
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

        var pipeline = Arrange(query.Commands);
        var where = new StringBuilder("case_id = {case:UInt64} AND ts >= {from:DateTime64(3)} AND ts < {to:DateTime64(3)}");
        if (query.Search is not null && query.Search is not AndNode { Terms.Length: 0 })
        {
            where.Append(" AND (").Append(Expression(query.Search)).Append(')');
        }

        // rex and where before the aggregation, in the order written: a where sees the groups of the rex before it.
        foreach (var command in pipeline.Before)
        {
            if (command is RexNode rex)
            {
                DefineRex(rex);
            }
            else
            {
                where.Append(" AND (").Append(Expression(((WhereNode)command).Expression)).Append(')');
            }
        }

        var built = pipeline.Timechart is not null ? BuildTimechart(pipeline, where, from, to)
            : pipeline.Stats is not null ? BuildStats(pipeline, where)
            : BuildListing(pipeline, where);
        ListingParts? listing = null;
        if (built.Kind == ResultKind.Events)
        {
            var filtered = pipeline.Before.Count > 0 || (query.Search is not null && query.Search is not AndNode { Terms.Length: 0 });
            listing = new ListingParts(built.Select, where.ToString(), [.. built.Sort], pipeline.Head?.Count, [.. _rexOrder.Select(g => new ListingColumn(g.Name, g.Expression))], filtered);
        }

        // A column that table left out takes its parameters with it: only what a text names is bound. The rex
        // groups stay bound for a listing, whose page carries every group whatever table lists.
        var texts = listing is null ? built.Sql : built.Sql + " " + string.Join(" ", listing.Rex.Select(g => g.Expression));
        _parameters.RemoveAll(p => !texts.Contains("{" + p.Name + ":", StringComparison.Ordinal));
        return new TranspileResult(built.Sql, [.. _parameters], [.. _warnings], [.. _fields.Values], built.Kind != ResultKind.Events, caseId, from, to)
        {
            Columns = [.. built.Columns],
            Kind = built.Kind,
            ValuesColumns = [.. built.ValuesColumns],
            Timechart = built.Timechart,
            Listing = listing,
        };
    }

    // ---- the pipeline -------------------------------------------------------------------------------------------

    private sealed record Pipeline(
        List<CommandNode> Before,
        StatsNode? Stats,
        TimechartNode? Timechart,
        List<WhereNode> After,
        TableNode? Table,
        SortNode? Sort,
        HeadNode? Head);

    private sealed record Built(string Sql, ResultKind Kind, List<string> Columns, List<int> ValuesColumns, TimechartShape? Timechart = null)
    {
        public List<ListingSortKey> Sort { get; init; } = [];

        public string Select { get; init; } = string.Empty;
    }

    /// <summary>
    /// Which verb may follow which: <c>search, (rex | where)*, [stats | timechart], where*, [table], [sort], [head]</c>,
    /// with <c>where</c>, <c>table</c> and <c>sort</c> free to trade places where the result is the same. Every refusal
    /// names both verbs.
    /// </summary>
    private static Pipeline Arrange(ImmutableArray<CommandNode> commands)
    {
        var before = new List<CommandNode>();
        var after = new List<WhereNode>();
        StatsNode? stats = null;
        TimechartNode? timechart = null;
        TableNode? table = null;
        SortNode? sort = null;
        HeadNode? head = null;
        foreach (var command in commands)
        {
            if (timechart is not null)
            {
                throw new QueryException(command.Span, $"{Verb(command)} after timechart is not supported in v1: timechart is the last verb");
            }

            switch (command)
            {
                case RexNode rex:
                    if (stats is not null || table is not null || head is not null)
                    {
                        throw new QueryException(rex.Span, $"rex must come before {(stats is not null ? "stats" : table is not null ? "table" : "head")}");
                    }

                    before.Add(rex);
                    break;
                case WhereNode w:
                    if (head is not null)
                    {
                        throw new QueryException(w.Span, "where must come before head");
                    }

                    if (stats is not null)
                    {
                        after.Add(w);
                    }
                    else
                    {
                        before.Add(w);
                    }

                    break;
                case StatsNode s:
                    if (stats is not null)
                    {
                        throw new QueryException(s.Span, "a second stats is not supported in v1");
                    }

                    if (table is not null)
                    {
                        throw new QueryException(s.Span, "stats must come before table");
                    }

                    if (sort is not null || head is not null)
                    {
                        throw new QueryException(s.Span, "stats must come before sort and head");
                    }

                    stats = s;
                    break;
                case TimechartNode t:
                    if (stats is not null)
                    {
                        throw new QueryException(t.Span, "timechart after stats is not supported in v1: use one or the other");
                    }

                    if (table is not null || sort is not null || head is not null)
                    {
                        throw new QueryException(t.Span, $"timechart must come before {(table is not null ? "table" : sort is not null ? "sort" : "head")}");
                    }

                    timechart = t;
                    break;
                case TableNode t:
                    if (table is not null)
                    {
                        throw new QueryException(t.Span, "table given twice");
                    }

                    table = t;
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

        return new Pipeline(before, stats, timechart, after, table, sort, head);
    }

    private static string Verb(CommandNode command) => command switch
    {
        RexNode => "rex",
        WhereNode => "where",
        StatsNode => "stats",
        TimechartNode => "timechart",
        TableNode => "table",
        SortNode => "sort",
        _ => "head",
    };

    // ---- operands -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Something a comparison, a by clause or an aggregate reads: a catalog field, an extras key, a rex group, or
    /// after an aggregation a column of its result. <see cref="Expression"/> is SQL the transpiler wrote.
    /// </summary>
    private sealed record Operand(string Name, string Expression, FieldType Type, FieldDef? Field = null, bool IsRex = false, bool IsArray = false);

    /// <summary>A column of an aggregation's result, which later verbs name by <see cref="Name"/>.</summary>
    private sealed class Output(string name, string alias, string expression, FieldType type)
    {
        public string Name { get; } = name;

        public string Alias { get; } = alias;

        public string Expression { get; } = expression;

        public FieldType Type { get; } = type;

        public bool IsArray { get; init; }

        /// <summary>The canonical name of the by field this column is, so an alias of the field finds it.</summary>
        public string? ByField { get; init; }

        /// <summary>False for an aggregate written without <c>as</c> (other than a bare count): it has no name to refer to.</summary>
        public bool CanBeNamed { get; init; } = true;

        public bool IsCount { get; init; }

        public bool Selected { get; set; } = true;

        /// <summary>How HAVING and ORDER BY read the column: by its alias when it is selected, else by its expression.</summary>
        public string Reference => Selected ? Alias : Expression;
    }

    /// <summary>Resolves a name where it stands: a result column after an aggregation, else a rex group, else the catalog.</summary>
    private Operand Resolve(string name, TextSpan span)
    {
        if (name.Length == 0 || name.Contains('`') || name.Any(char.IsControl))
        {
            throw new QueryException(span, $"'{name}' is not a field name");
        }

        if (_outputs is not null)
        {
            var output = FindOutput(name, span);
            return new Operand(output.Name, output.Reference, output.Type, IsArray: output.IsArray);
        }

        if (_rex.TryGetValue(name, out var group))
        {
            return group;
        }

        var field = _catalog.Resolve(name);
        if (field.IsUnknown)
        {
            Warn("unknown_field", span, $"'{name}' is not in the catalog: searched as extras['{name}']");
        }

        _fields.TryAdd(field.Name, field);
        return new Operand(field.Name, field.Expression(field.Column is null ? Add("String", field.ExtraKey!) : null), field.Type, field);
    }

    private Output FindOutput(string name, TextSpan span)
    {
        var outputs = _outputs!;
        var byName = outputs.FirstOrDefault(o => o.CanBeNamed && o.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (byName is not null)
        {
            return byName;
        }

        if (_catalog.Contains(name))
        {
            var canonical = _catalog.Resolve(name).Name;
            if (outputs.FirstOrDefault(o => o.ByField == canonical) is { } byField)
            {
                return byField;
            }
        }

        // A count named with "as" still answers to "count" when nothing else is called that.
        if (name.Equals("count", StringComparison.OrdinalIgnoreCase) && outputs.FirstOrDefault(o => o.IsCount) is { } count)
        {
            return count;
        }

        var unnamed = outputs.Where(o => !o.CanBeNamed).Select(o => o.Name).ToList();
        var hint = unnamed.Count == 0 ? "" : $" ({unnamed[0]} has no name: write it as {unnamed[0]} as n)";
        throw new QueryException(span, $"'{name}' is not a column of the result: {string.Join(", ", outputs.Where(o => o.CanBeNamed).Select(o => o.Name))}{hint}");
    }

    /// <summary>
    /// The test for "this row has a value in the field", or null when every row has one. A string is a value
    /// unless empty, an address unless <c>::</c>, a number unless it is 0 in a column the catalog marks
    /// (<see cref="FieldDef.ZeroIsAbsent"/>); the columns are not nullable, so a source without the field stores
    /// exactly those.
    /// </summary>
    private string? HasValue(Operand operand) => operand.Type == FieldType.Ip
        ? FieldDef.HasValue(operand.Type, false, operand.Expression, $"{{{Add("String", "::")}:String}}")
        : FieldDef.HasValue(operand.Type, operand.Field?.ZeroIsAbsent == true, operand.Expression, string.Empty);

    private string NextAlias() => Quote(Names.Generated(_generated++));

    // ---- rex ----------------------------------------------------------------------------------------------------

    private void DefineRex(RexNode rex)
    {
        if (++_rexCount > MaxRex)
        {
            throw new QueryException(rex.Span, $"more than {MaxRex} rex in one query");
        }

        var source = Resolve(rex.Field, rex.FieldSpan);
        if (source.Type is not (FieldType.Keyword or FieldType.Verbatim or FieldType.Text or FieldType.Extra))
        {
            throw new QueryException(rex.FieldSpan, $"rex reads text: {source.Name} is {Describe(source.Type)}");
        }

        var pattern = Add("String", rex.Pattern);
        foreach (var group in rex.Groups)
        {
            if (_rex.ContainsKey(group.Name))
            {
                throw new QueryException(group.Span, $"the group name '{group.Name}' is used by an earlier rex");
            }

            if (_fields.Values.Any(f => f.IsUnknown && f.Name.Equals(group.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new QueryException(group.Span, $"'{group.Name}' is read earlier in the query, before this rex defines it: put the rex first");
            }

            if (_catalog.Contains(group.Name))
            {
                throw new QueryException(group.Span, $"'{group.Name}' is a field already ({_catalog.Resolve(group.Name).Name}): give the group another name");
            }

            var operand = new Operand(
                group.Name,
                $"extractGroups({source.Expression}, {{{pattern}:String}})[{group.Index.ToString(CultureInfo.InvariantCulture)}]",
                FieldType.Verbatim,
                IsRex: true);
            _rex.Add(group.Name, operand);
            _rexOrder.Add(operand);
        }
    }

    // ---- event listings -----------------------------------------------------------------------------------------

    private Built BuildListing(Pipeline pipeline, StringBuilder where)
    {
        var columns = new List<string>();
        var sql = new StringBuilder("SELECT ");
        if (pipeline.Table is null)
        {
            sql.Append(DefaultColumns);
            columns.AddRange(DefaultColumnNames);
            foreach (var group in _rexOrder)
            {
                sql.Append(", ").Append(group.Expression).Append(" AS ").Append(NextAlias());
                columns.Add(group.Name);
            }
        }
        else
        {
            var items = new List<string>();
            foreach (var (name, span) in pipeline.Table.Fields)
            {
                var operand = Resolve(name, span);
                if (columns.Contains(operand.Name, StringComparer.OrdinalIgnoreCase))
                {
                    throw new QueryException(span, $"table: {operand.Name} is listed twice");
                }

                // A catalog field keeps its name; anything the analyst named gets a generated alias.
                var alias = operand.Field is { IsUnknown: false } ? Quote(operand.Name) : NextAlias();
                items.Add($"{operand.Expression} AS {alias}");
                columns.Add(operand.Name);
            }

            sql.Append(string.Join(", ", items));
        }

        var select = sql.ToString()["SELECT ".Length..];
        sql.Append(" FROM events_v WHERE ").Append(where).Append(" ORDER BY ");
        var sort = new List<ListingSortKey>();
        if (pipeline.Sort is null)
        {
            sql.Append("ts DESC");
        }
        else
        {
            foreach (var key in pipeline.Sort.Keys)
            {
                var operand = Resolve(key.Field, key.Span);
                sort.Add(new ListingSortKey(operand.Name, operand.Expression, key.Descending, operand.Type));
            }

            sql.Append(string.Join(", ", sort.Select(k => k.Expression + (k.Descending ? " DESC" : " ASC"))));
        }

        _parameters.Add(new QueryParameter("limit", "UInt64", (pipeline.Head?.Count ?? Parser.DefaultHead).ToString(CultureInfo.InvariantCulture)));
        sql.Append(" LIMIT {limit:UInt64}");
        return new Built(sql.ToString(), ResultKind.Events, columns, []) { Sort = sort, Select = select };
    }

    // ---- stats --------------------------------------------------------------------------------------------------

    private Built BuildStats(Pipeline pipeline, StringBuilder where)
    {
        var stats = pipeline.Stats!;
        var outputs = new List<Output>();
        var groupBy = new List<string>();

        // Generated aliases are handed out to the by fields first, then to the aggregates, in the order written.
        var byOperands = stats.By.Select(b => (Operand: Resolve(b.Field, b.Span), b.Span)).ToList();
        foreach (var (operand, span) in byOperands)
        {
            if (outputs.Any(o => o.Name.Equals(operand.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new QueryException(span, $"stats: {operand.Name} is listed twice after by");
            }

            // A row without a value in a by field belongs to no group.
            if (HasValue(operand) is { } has)
            {
                where.Append(" AND ").Append(has);
            }

            var alias = operand.Field is { IsUnknown: false } ? Quote(operand.Name) : NextAlias();
            outputs.Add(new Output(operand.Name, alias, operand.Expression, ResultType(operand.Type)) { ByField = operand.Field?.Name });
            groupBy.Add(operand.Expression);
        }

        foreach (var call in stats.Aggregates)
        {
            var (expression, type, isArray) = Aggregate(call, "stats");
            if (call.Alias is not null && _catalog.Contains(call.Alias))
            {
                throw new QueryException(call.Span, $"'{call.Alias}' is a field ({_catalog.Resolve(call.Alias).Name}): give the aggregate another name");
            }

            if (outputs.Any(o => o.Name.Equals(call.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new QueryException(call.Span, $"stats: two columns would be called {call.Name}: name one of them with as");
            }

            outputs.Add(new Output(call.Name, NextAlias(), expression, type)
            {
                IsArray = isArray,
                CanBeNamed = call.CanBeNamed,
                IsCount = call.Function == AggregateFunction.Count,
            });
        }

        // From here on a name is a column of the result.
        _outputs = outputs;
        var selected = outputs;
        if (pipeline.Table is not null)
        {
            selected = [];
            foreach (var (name, span) in pipeline.Table.Fields)
            {
                var output = FindOutput(name, span);
                if (selected.Contains(output))
                {
                    throw new QueryException(span, $"table: {output.Name} is listed twice");
                }

                selected.Add(output);
            }

            foreach (var output in outputs)
            {
                output.Selected = selected.Contains(output);
            }
        }

        var sql = new StringBuilder("SELECT ");
        sql.Append(string.Join(", ", selected.Select(o => $"{o.Expression} AS {o.Alias}")));
        var having = pipeline.After.Select(w => "(" + Expression(w.Expression) + ")").ToList();
        sql.Append(" FROM events_v WHERE ").Append(where);
        if (groupBy.Count > 0)
        {
            sql.Append(" GROUP BY ").Append(string.Join(", ", groupBy));
        }

        if (having.Count > 0)
        {
            sql.Append(" HAVING ").Append(string.Join(" AND ", having));
        }

        var order = new List<string>();
        var ordered = new HashSet<Output>();
        if (pipeline.Sort is not null)
        {
            foreach (var key in pipeline.Sort.Keys)
            {
                var output = FindOutput(key.Field, key.Span);
                order.Add(output.Reference + (key.Descending ? " DESC" : " ASC"));
                ordered.Add(output);
            }
        }

        if (_current!.TotalOrder)
        {
            // The tie-break: every by field, then every aggregate, in the order written. A group is the only one
            // with its by fields, so the order is total; a result without by fields is one row.
            foreach (var output in outputs.Where(ordered.Add))
            {
                order.Add(output.Reference + " ASC");
            }
        }

        if (order.Count > 0)
        {
            sql.Append(" ORDER BY ").Append(string.Join(", ", order));
        }

        if (pipeline.Head is not null)
        {
            _parameters.Add(new QueryParameter("limit", "UInt64", pipeline.Head.Count.ToString(CultureInfo.InvariantCulture)));
            sql.Append(" LIMIT {limit:UInt64}");
        }

        var values = Enumerable.Range(0, selected.Count).Where(i => selected[i].IsArray).ToList();
        return new Built(sql.ToString(), ResultKind.Stats, [.. selected.Select(o => o.Name)], values);
    }

    /// <summary>The SQL of one aggregate and the type of its result. An aggregate over a field reads only the rows that have a value in it.</summary>
    private (string Expression, FieldType Type, bool IsArray) Aggregate(AggregateCall call, string verb)
    {
        if (call.Function == AggregateFunction.Count)
        {
            return ("count()", FieldType.Number, false);
        }

        var fn = AggregateCall.FunctionName(call.Function);
        var operand = Resolve(call.Field!, call.FieldSpan);
        var e = operand.Expression;
        var has = HasValue(operand);
        var stringLike = operand.Type is FieldType.Extra || operand.IsRex;
        string If(string function, string arguments) => has is null ? $"{function}({arguments})" : $"{function}If({arguments}, {has})";
        string Numeric(string function)
        {
            // An extras value and a rex group are text: read as a number where they are one, skipped where not.
            Warn("unindexed", call.FieldSpan, $"{fn}({operand.Name}) reads {operand.Name} as a number; values that are not numbers are skipped");
            return $"{function}(toFloat64OrNull({e}))";
        }

        switch (call.Function)
        {
            case AggregateFunction.Dc:
                return (If("uniqExact", e), FieldType.Number, false);
            case AggregateFunction.Values:
                // The smallest values of the group, ascending: the same cell on every run, and above the cap the
                // same ones. The server keeps every distinct value of a group while it reads, not the cap.
                return (has is null
                    ? $"groupArraySortedDistinct({ValuesRequested})({e})"
                    : $"groupArraySortedDistinctIf({ValuesRequested})({e}, {has})", ResultType(operand.Type), true);
            case AggregateFunction.Earliest or AggregateFunction.Latest:
            {
                var earliest = call.Function == AggregateFunction.Earliest;
                return operand.Type == FieldType.Time
                    ? ($"{(earliest ? "min" : "max")}({e})", FieldType.Time, false)
                    : (If(earliest ? "argMin" : "argMax", $"{e}, {RowKey}"), ResultType(operand.Type), false);
            }

            case AggregateFunction.Sum when operand.Type == FieldType.Number:
                return ($"sum({e})", FieldType.Number, false);
            case AggregateFunction.Avg when operand.Type == FieldType.Number:
                return (If("avg", e), FieldType.Real, false);
            case AggregateFunction.Min or AggregateFunction.Max when operand.Type == FieldType.Number:
                return (If(fn, e), FieldType.Number, false);
            case AggregateFunction.Min or AggregateFunction.Max when operand.Type == FieldType.Time:
                return ($"{fn}({e})", FieldType.Time, false);
            case AggregateFunction.Sum or AggregateFunction.Avg or AggregateFunction.Min or AggregateFunction.Max when stringLike:
                return (Numeric(fn), FieldType.Real, false);
            default:
                throw new QueryException(call.Span, $"{verb}: {fn}({operand.Name}) needs a number: {operand.Name} is {Describe(operand.Type)}");
        }
    }

    private const int ValuesRequested = ValuesCap + 1;

    /// <summary>
    /// What earliest(f) and latest(f) compare rows by: the key of a listing. A visible row is the only one with
    /// its file and offset, so rows on one instant have one order and the same row is chosen on every run.
    /// </summary>
    private const string RowKey = "(ts, file_id, file_offset)";

    /// <summary>How a result column compares: an extras value and a text column are plain strings there.</summary>
    private static FieldType ResultType(FieldType type) => type is FieldType.Extra or FieldType.Text ? FieldType.Verbatim : type;

    private static string Describe(FieldType type) => type switch
    {
        FieldType.Number or FieldType.Real => "a number",
        FieldType.Ip => "an address",
        FieldType.Time => "a time",
        _ => "text",
    };

    // ---- timechart ----------------------------------------------------------------------------------------------

    private Built BuildTimechart(Pipeline pipeline, StringBuilder where, DateTimeOffset from, DateTimeOffset to)
    {
        var chart = pipeline.Timechart!;
        if (chart.Aggregate.Function is AggregateFunction.Values or AggregateFunction.Earliest or AggregateFunction.Latest)
        {
            var name = AggregateCall.FunctionName(chart.Aggregate.Function);
            throw new QueryException(chart.Aggregate.Span, $"timechart: {name}(...) is not a number to chart: use count, dc, sum, avg, min or max");
        }

        var bucket = Bucket(chart, from, to);
        var (aggregate, type, _) = Aggregate(chart.Aggregate, "timechart");
        if (type == FieldType.Time)
        {
            throw new QueryException(chart.Aggregate.Span, "timechart: a time is not a number to chart");
        }

        if (chart.By is not { } by)
        {
            var value = NextAlias();
            return new Built(
                $"SELECT {bucket} AS `_time`, {aggregate} AS {value} FROM events_v WHERE {where} GROUP BY `_time` ORDER BY `_time` ASC",
                ResultKind.Timechart,
                ["_time", chart.Aggregate.Name],
                [],
                new TimechartShape(0, 1, null, null));
        }

        var series = Resolve(by.Field, by.Span);
        if (HasValue(series) is { } has)
        {
            where.Append(" AND ").Append(has);
        }

        // The ten series with the largest aggregate over the whole range keep their rows; every other series is
        // folded into one, whose key is the type's empty value and whose flag is 0. The aliases are generated: a
        // field's own name here would shadow its column in WHERE.
        _parameters.Add(new QueryParameter("top", "UInt64", TopSeries.ToString(CultureInfo.InvariantCulture)));
        var inTop = NextAlias();
        var seriesAlias = NextAlias();
        var valueAlias = NextAlias();
        var sql = $"WITH top AS (SELECT {series.Expression} AS s FROM events_v WHERE {where} GROUP BY s ORDER BY {aggregate} DESC, s ASC LIMIT {{top:UInt64}}) "
            + $"SELECT {bucket} AS `_time`, {series.Expression} IN top AS {inTop}, if({inTop}, {series.Expression}, defaultValueOfArgumentType({series.Expression})) AS {seriesAlias}, {aggregate} AS {valueAlias} "
            + $"FROM events_v WHERE {where} GROUP BY `_time`, {inTop}, {seriesAlias} ORDER BY `_time` ASC, {inTop} DESC, {seriesAlias} ASC";
        return new Built(sql, ResultKind.Timechart, ["_time", "in_top", series.Name, chart.Aggregate.Name], [], new TimechartShape(0, 3, 1, 2));
    }

    /// <summary>
    /// The start of the bucket a row falls in, as a UTC instant. Under a day the buckets are cut in UTC from the
    /// epoch. A day or more is cut on the display zone's calendar: a day there is 23 or 25 hours long when the
    /// clocks change, and starts at the first instant that exists after local midnight.
    /// </summary>
    private string Bucket(TimechartNode chart, DateTimeOffset from, DateTimeOffset to)
    {
        var seconds = chart.SpanCount * (long)(chart.SpanUnit switch { 's' => 1, 'm' => 60, 'h' => 3600, 'd' => 86_400, _ => 604_800 });
        var buckets = ((long)(to - from).TotalSeconds + seconds - 1) / seconds;
        if (buckets > MaxBuckets)
        {
            throw new QueryException(chart.SpanSpan, $"timechart: this span cuts the range into {buckets:N0} buckets, more than {MaxBuckets:N0}: use a longer span or a shorter range");
        }

        var zone = _current!.DisplayZone;
        return BucketExpression(chart.SpanCount, chart.SpanUnit, zone, _parameters, string.Empty)
            ?? throw new QueryException(chart.SpanSpan, $"timechart: a span of a day or more is cut in the display timezone, and '{zone.Id}' is neither an IANA zone (Europe/Bucharest) nor an offset (+02:00)");
    }

    /// <summary>
    /// The bucket expression for a span of <paramref name="count"/> units (<c>s m h d w</c>), with its parameters
    /// added to <paramref name="parameters"/> under names that start with <paramref name="prefix"/>. Null when the
    /// span is a day or more and the zone is neither an IANA zone nor a fixed offset, so the server cannot cut it;
    /// nothing is added then.
    /// </summary>
    /// <param name="column">The time that is cut: <c>ts</c>, or the minute of the stored histogram.</param>
    internal static string? BucketExpression(int count, char unit, TimeZoneInfo zone, ICollection<QueryParameter> parameters, string prefix, string column = "ts")
    {
        var span = "{" + prefix + "span:UInt32}";
        var spanParameter = new QueryParameter(prefix + "span", "UInt32", count.ToString(CultureInfo.InvariantCulture));
        switch (unit)
        {
            case 's':
                parameters.Add(spanParameter);
                return $"toStartOfInterval({column}, toIntervalSecond({span}))";
            case 'm':
                parameters.Add(spanParameter);
                return $"toStartOfInterval({column}, toIntervalMinute({span}))";
            case 'h':
                parameters.Add(spanParameter);
                return $"toStartOfInterval({column}, toIntervalHour({span}))";
        }

        var week = unit == 'w';
        var interval = week ? $"toIntervalWeek({span})" : $"toIntervalDay({span})";
        var utc = "{" + prefix + "utc:String}";
        if (FixedOffset(zone) is { } offset)
        {
            // The server loads IANA zones only: an offset is applied by hand around the cut.
            var shift = "{" + prefix + "shift:Int64}";
            parameters.Add(spanParameter);
            parameters.Add(new QueryParameter(prefix + "utc", "String", "UTC"));
            parameters.Add(new QueryParameter(prefix + "shift", "Int64", ((long)offset.TotalSeconds).ToString(CultureInfo.InvariantCulture)));
            var shifted = $"toStartOfInterval(addSeconds({column}, {shift}), {interval}, {utc})";
            return $"subtractSeconds({(week ? $"toDateTime({shifted}, {utc})" : shifted)}, {shift})";
        }

        if ((zone.Equals(TimeZoneInfo.Utc) ? "UTC" : IanaId(zone)) is not { } iana)
        {
            return null;
        }

        var tz = "{" + prefix + "tz:String}";
        parameters.Add(spanParameter);
        parameters.Add(new QueryParameter(prefix + "utc", "String", "UTC"));
        parameters.Add(new QueryParameter(prefix + "tz", "String", iana));
        var local = $"toStartOfInterval({column}, {interval}, {tz})";
        return $"toTimeZone({(week ? $"toDateTime({local}, {tz})" : local)}, {utc})";
    }

    /// <summary>The offset of a zone <see cref="TimeZones.Find"/> built from one (<c>+02:00</c>); null for UTC and for a real zone.</summary>
    private static TimeSpan? FixedOffset(TimeZoneInfo zone) =>
        !zone.Equals(TimeZoneInfo.Utc) && !zone.SupportsDaylightSavingTime && zone.Id.StartsWith("UTC", StringComparison.Ordinal) && zone.GetAdjustmentRules().Length == 0 && zone.BaseUtcOffset != TimeSpan.Zero
            ? zone.BaseUtcOffset
            : null;

    /// <summary>
    /// The zone's IANA id, which is what the server loads. A runtime without the zone database cannot say whether
    /// an id is one, so an id of the IANA form (Area/Location) is passed on and the server decides.
    /// </summary>
    private static string? IanaId(TimeZoneInfo zone)
    {
        if (zone.HasIanaId)
        {
            return zone.Id;
        }

        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana))
        {
            return iana;
        }

        var id = zone.Id;
        var slash = id.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 && slash < id.Length - 1 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '_' or '-' or '+') ? id : null;
    }

    // ---- the filter grammar -------------------------------------------------------------------------------------

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
        if (field.IsArray)
        {
            if (ordering || hasWildcard)
            {
                throw new QueryException(c.Span, $"{field.Name} is a list of values: use = or != with one exact value");
            }

            var contains = $"has({field.Expression}, {Element(field, value, c.ValueSpan)})";
            return c.Op == CompareOp.Ne ? "NOT " + contains : contains;
        }

        // field=* asks whether the row has a value in the field at all, whatever its type.
        if (!c.Quoted && value == "*" && !ordering)
        {
            var has = HasValue(field) ?? $"isNotNull({field.Expression})";
            return c.Op == CompareOp.Ne ? $"NOT ({has})" : has;
        }

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

                    return $"{field.Expression} {Sql(c.Op)} {{{Add("UInt64", value)}:UInt64}}";
                }

                throw new QueryException(c.ValueSpan, $"{field.Name} is numeric: wildcards do not apply");
            }

            case FieldType.Real:
                return $"{field.Expression} {Sql(c.Op)} {{{Add("Float64", Float(field, value, c.ValueSpan))}:Float64}}";

            case FieldType.Time:
            {
                var instant = ParseTimeValue(value, c.ValueSpan);
                return $"{field.Expression} {Sql(c.Op)} {{{Add("DateTime64(3)", Fmt(instant))}:DateTime64(3)}}";
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
                    var test = $"isIPAddressInRange(IPv6NumToString({field.Expression}), {{{Add("String", cidr)}:String}})";
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

                return $"{field.Expression} {Sql(c.Op)} toIPv6({{{Add("String", value)}:String}})";
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

                var column = field.Expression;
                var text = field.Type == FieldType.Keyword ? AsciiLower(value) : value;
                if (field.Type is FieldType.Text or FieldType.Extra)
                {
                    Warn("unindexed", c.Span, field.Type == FieldType.Text ? $"{field.Name}= compares the whole field; a bare term uses the index" : $"{field.Name} lives in extras: compared without an index");
                }
                else if (field.IsRex)
                {
                    Warn("unindexed", c.Span, $"{field.Name} is extracted by rex for every row the search leaves: compared without an index");
                }

                if (!hasWildcard)
                {
                    return $"{column} {Sql(c.Op)} {{{Add("String", text)}:String}}";
                }

                var pattern = LikePattern(text);
                var prefixOnly = text.IndexOfAny(['*', '?']) == text.Length - 1 && text[^1] == '*' && text.Length > 1;
                var like = field.Type is FieldType.Verbatim or FieldType.Text or FieldType.Extra && !prefixOnly ? "ILIKE" : "LIKE";
                if (!prefixOnly && field.Type is FieldType.Keyword or FieldType.Verbatim && !field.IsRex && _outputs is null)
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

    private static string Float(Operand field, string value, TextSpan span)
    {
        if (!double.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
        {
            throw new QueryException(span, $"{field.Name} expects a number, got '{value}'");
        }

        return number.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>One value as an element of a <c>values(f)</c> list of the field's type.</summary>
    private string Element(Operand field, string value, TextSpan span)
    {
        switch (field.Type)
        {
            case FieldType.Number:
                if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                {
                    throw new QueryException(span, $"{field.Name} expects an unsigned integer, got '{value}'");
                }

                return $"{{{Add("UInt64", value)}:UInt64}}";
            case FieldType.Time:
                return $"{{{Add("DateTime64(3)", Fmt(ParseTimeValue(value, span)))}:DateTime64(3)}}";
            case FieldType.Ip:
                if (value.Contains('/') || !IPAddress.TryParse(value, out _))
                {
                    throw new QueryException(span, $"'{value}' is not an IP address");
                }

                return $"toIPv6({{{Add("String", value)}:String}})";
            default:
                return $"{{{Add("String", field.Type == FieldType.Keyword ? AsciiLower(value) : value)}:String}}";
        }
    }

    private string In(InNode node)
    {
        var field = Resolve(node.Field, node.FieldSpan);
        if (field.IsArray)
        {
            throw new QueryException(node.Span, $"{field.Name} is a list of values: use = with one exact value");
        }

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
                case FieldType.Real:
                    items.Add($"{{{Add("Float64", Float(field, value, span))}:Float64}}");
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

        if (field.Type is FieldType.Text or FieldType.Extra || field.IsRex)
        {
            Warn("unindexed", node.Span, $"{field.Name} IN (...) is compared without an index");
        }

        return $"{field.Expression} IN ({string.Join(", ", items)})";
    }

    /// <summary>A bare term or a quoted phrase against <c>message</c>: tokens through the text index, the exact text confirmed by <c>position</c> when the term is more than one token.</summary>
    private string Term(string text, TextSpan span, bool isPhrase)
    {
        if (_outputs is not null)
        {
            throw new QueryException(span, "after stats, where compares the columns of the result (count>10, host=web-01): a search term has no event to search, so put it before stats");
        }

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
        ArgumentNullException.ThrowIfNull(text);
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
