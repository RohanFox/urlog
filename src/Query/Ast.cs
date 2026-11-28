using System.Collections.Immutable;

namespace Urlog.Query;

/// <summary>A span of the query text: where an error or a warning points.</summary>
public readonly record struct TextSpan(int Offset, int Length);

/// <summary>A parse or transpile error with its position in the query text (the UI underlines it).</summary>
public sealed class QueryException(TextSpan span, string message) : Exception(message)
{
    public TextSpan Span { get; } = span;

    public int Offset => Span.Offset;

    public int Length => Span.Length;
}

public enum CompareOp
{
    Eq,
    Ne,
    Lt,
    Gt,
    Le,
    Ge,
}

/// <summary>Search expression nodes (the filter grammar).</summary>
public abstract record SearchNode(TextSpan Span);

public sealed record AndNode(ImmutableArray<SearchNode> Terms, TextSpan Span) : SearchNode(Span);

public sealed record OrNode(ImmutableArray<SearchNode> Terms, TextSpan Span) : SearchNode(Span);

public sealed record NotNode(SearchNode Operand, TextSpan Span) : SearchNode(Span);

/// <summary><c>field op value</c>. <see cref="Quoted"/> is true when the value was a quoted string (no wildcards then).</summary>
public sealed record CompareNode(string Field, TextSpan FieldSpan, CompareOp Op, string Value, TextSpan ValueSpan, bool Quoted, TextSpan Span) : SearchNode(Span);

/// <summary><c>field IN (a, b, ...)</c>.</summary>
public sealed record InNode(string Field, TextSpan FieldSpan, ImmutableArray<(string Value, bool Quoted, TextSpan Span)> Values, TextSpan Span) : SearchNode(Span);

/// <summary>A bare term: token search on <c>message</c>.</summary>
public sealed record TermNode(string Text, TextSpan Span) : SearchNode(Span);

/// <summary>A quoted phrase: substring search on <c>message</c>.</summary>
public sealed record PhraseNode(string Text, TextSpan Span) : SearchNode(Span);

/// <summary>
/// <c>word LIKE pattern</c>, also with <c>ILIKE</c> and <c>NOT LIKE</c>: the SQL habit, which the dialect does not
/// have. The parser cannot tell a field from a word, so it keeps the three pieces as the terms they are without
/// the rule: the transpiler refuses the form when <see cref="Left"/> names a field and searches the three terms
/// when it does not.
/// </summary>
/// <param name="Left">The word before the keyword, a <see cref="TermNode"/>, under the <c>NOT</c>s written before it.</param>
/// <param name="Keyword">The keyword as a <see cref="TermNode"/>, under a <see cref="NotNode"/> for <c>NOT LIKE</c>.</param>
/// <param name="Pattern">A <see cref="TermNode"/> or a <see cref="PhraseNode"/>.</param>
public sealed record LikeNode(SearchNode Left, SearchNode Keyword, SearchNode Pattern, TextSpan Span) : SearchNode(Span)
{
    public TermNode Word => (TermNode)Unwrap(Left);

    public TermNode KeywordWord => (TermNode)Unwrap(Keyword);

    /// <summary>True when the NOTs before the word and before the keyword leave a negation.</summary>
    public bool Negated => (Depth(Left) + Depth(Keyword)) % 2 == 1;

    private static SearchNode Unwrap(SearchNode node) => node is NotNode not ? Unwrap(not.Operand) : node;

    private static int Depth(SearchNode node) => node is NotNode not ? 1 + Depth(not.Operand) : 0;
}

/// <summary>Pipeline commands (verbs) after the search.</summary>
public abstract record CommandNode(TextSpan Span);

public enum AggregateFunction
{
    Count,
    Dc,
    Sum,
    Avg,
    Min,
    Max,
    Values,
    Earliest,
    Latest,
}

/// <summary>
/// One aggregate of <c>stats</c> or <c>timechart</c>: <c>count</c>, or <c>fn(field)</c>, with the name after
/// <c>as</c> when there is one. <see cref="Name"/> is the result column's header: the alias, <c>count</c>, or the
/// call as written (<c>dc(user.name)</c>), which later verbs cannot refer to.
/// </summary>
public sealed record AggregateCall(AggregateFunction Function, string? Field, TextSpan FieldSpan, string? Alias, TextSpan Span)
{
    public string Name => Alias ?? (Function == AggregateFunction.Count ? "count" : $"{FunctionName(Function)}({Field})");

    /// <summary>True when a later <c>where</c>, <c>sort</c> or <c>table</c> can name this aggregate.</summary>
    public bool CanBeNamed => Alias is not null || Function == AggregateFunction.Count;

    public static string FunctionName(AggregateFunction function) => function switch
    {
        AggregateFunction.Count => "count",
        AggregateFunction.Dc => "dc",
        AggregateFunction.Sum => "sum",
        AggregateFunction.Avg => "avg",
        AggregateFunction.Min => "min",
        AggregateFunction.Max => "max",
        AggregateFunction.Values => "values",
        AggregateFunction.Earliest => "earliest",
        _ => "latest",
    };
}

/// <summary><c>stats agg [as a], ... [by f1, f2, ...]</c>.</summary>
public sealed record StatsNode(ImmutableArray<AggregateCall> Aggregates, ImmutableArray<(string Field, TextSpan Span)> By, TextSpan Span) : CommandNode(Span);

/// <summary><c>where expr</c>: the filter grammar; after an aggregate it compares the result's columns.</summary>
public sealed record WhereNode(SearchNode Expression, TextSpan Span) : CommandNode(Span);

/// <summary><c>table f1 f2 ...</c>: the columns of the result, in this order.</summary>
public sealed record TableNode(ImmutableArray<(string Field, TextSpan Span)> Fields, TextSpan Span) : CommandNode(Span);

/// <summary>A named group of a <c>rex</c> pattern: its name, its capture index (from 1) and where the name stands in the query.</summary>
public sealed record RexGroup(string Name, int Index, TextSpan Span);

/// <summary><c>rex field=f "pattern"</c>: each named group is a column for the rest of the query.</summary>
public sealed record RexNode(string Field, TextSpan FieldSpan, string Pattern, TextSpan PatternSpan, ImmutableArray<RexGroup> Groups, TextSpan Span) : CommandNode(Span);

/// <summary><c>timechart span=N(s|m|h|d|w) agg [by f]</c>, the span before the aggregate or after the by clause.</summary>
public sealed record TimechartNode(int SpanCount, char SpanUnit, TextSpan SpanSpan, AggregateCall Aggregate, (string Field, TextSpan Span)? By, TextSpan Span) : CommandNode(Span);

public sealed record SortNode(ImmutableArray<(string Field, bool Descending, TextSpan Span)> Keys, TextSpan Span) : CommandNode(Span);

public sealed record HeadNode(int Count, TextSpan Span) : CommandNode(Span);

/// <summary>
/// A parsed query: the search (null when empty: every event in range), the commands in order, and the
/// scope words the search carried (<c>index=</c>, <c>earliest=</c>, <c>latest=</c>), each with its position.
/// </summary>
public sealed record Query(
    SearchNode? Search,
    ImmutableArray<CommandNode> Commands,
    (string Value, TextSpan Span)? Index,
    (string Value, TextSpan Span)? Earliest,
    (string Value, TextSpan Span)? Latest)
{
    /// <summary>A rex group name the query does not use: what a refusal that suggests a rex calls its group.</summary>
    public string FreeGroupName { get; init; } = "m";
}
