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

/// <summary>Pipeline commands (verbs) after the search.</summary>
public abstract record CommandNode(TextSpan Span);

/// <summary><c>stats count [as alias] [by f1, f2, ...]</c>: the only stats form in this slice.</summary>
public sealed record StatsCountNode(string Alias, ImmutableArray<(string Field, TextSpan Span)> By, TextSpan Span) : CommandNode(Span);

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
    (string Value, TextSpan Span)? Latest);
