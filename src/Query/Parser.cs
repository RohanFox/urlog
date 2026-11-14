using System.Collections.Immutable;
using System.Globalization;

namespace Urlog.Query;

/// <summary>
/// Recursive-descent parser for the v1 dialect (design record, "Query language"): the filter grammar here, the
/// verbs in the other half of the class. Precedence: <c>NOT</c> binds tighter than implicit/explicit <c>AND</c>,
/// which binds tighter than <c>OR</c>. Any other verb is an error "not supported in v1: ...". Ambiguity is an
/// error with a position, never a guess.
/// </summary>
public sealed partial class Parser
{
    public const int DefaultHead = 100;
    public const int MaxHead = 1_000_000;

    private static readonly HashSet<string> ScopeWords = new(StringComparer.OrdinalIgnoreCase) { "index", "earliest", "latest" };

    private readonly string _text;
    private readonly List<Token> _tokens;
    private int _i;
    private (string Value, TextSpan Span)? _index;
    private (string Value, TextSpan Span)? _earliest;
    private (string Value, TextSpan Span)? _latest;

    private Parser(string text)
    {
        _text = text;
        _tokens = Lexer.Tokenize(text);
    }

    public static Query Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var p = new Parser(text);
        return p.ParseQuery();
    }

    private Token Current => _tokens[_i];

    private Token Advance() => _tokens[_i++];

    private Query ParseQuery()
    {
        SearchNode? search = null;
        if (Current.Kind is not (TokenKind.Pipe or TokenKind.End))
        {
            search = ParseOr(allowScope: true);
        }

        if (Current.Kind == TokenKind.RParen)
        {
            throw new QueryException(Current.Span, "unmatched ')'");
        }

        var commands = ImmutableArray.CreateBuilder<CommandNode>();
        while (Current.Kind == TokenKind.Pipe)
        {
            var pipe = Advance();
            if (Current.Kind != TokenKind.Word)
            {
                throw new QueryException(Current.Kind == TokenKind.End ? pipe.Span : Current.Span, "expected a command after '|'");
            }

            commands.Add(ParseCommand());
        }

        if (Current.Kind != TokenKind.End)
        {
            throw new QueryException(Current.Span, $"unexpected '{Current.Text}'");
        }

        return new Query(search, commands.ToImmutable(), _index, _earliest, _latest);
    }

    private SearchNode ParseOr(bool allowScope)
    {
        var start = Current.Span.Offset;
        var terms = ImmutableArray.CreateBuilder<SearchNode>();
        var first = ParseAnd(allowScope);
        if (first is not null)
        {
            terms.Add(first);
        }

        var sawOr = false;
        while (Current.IsKeyword("OR"))
        {
            var or = Advance();
            sawOr = true;
            if (AtExpressionEnd())
            {
                throw new QueryException(or.Span, "expected an expression after OR");
            }

            var next = ParseAnd(allowScope: false);
            if (next is not null)
            {
                terms.Add(next);
            }
        }

        if (terms.Count == 0)
        {
            // Only scope words (index=, earliest=, latest=) were here: no filter.
            return new AndNode([], new TextSpan(start, Math.Max(0, PreviousEnd() - start)));
        }

        if (terms.Count == 1 && !sawOr)
        {
            return terms[0];
        }

        return new OrNode(terms.ToImmutable(), new TextSpan(start, PreviousEnd() - start));
    }

    private SearchNode? ParseAnd(bool allowScope)
    {
        var start = Current.Span.Offset;
        var terms = ImmutableArray.CreateBuilder<SearchNode>();
        while (!AtExpressionEnd() && !Current.IsKeyword("OR"))
        {
            if (Current.IsKeyword("AND"))
            {
                var and = Advance();
                if (AtExpressionEnd() || Current.IsKeyword("OR"))
                {
                    throw new QueryException(and.Span, "expected an expression after AND");
                }

                continue;
            }

            var node = ParseUnary(allowScope);
            if (node is not null)
            {
                terms.Add(node);
            }
        }

        if (terms.Count == 0)
        {
            return null;
        }

        return terms.Count == 1 ? terms[0] : new AndNode(terms.ToImmutable(), new TextSpan(start, PreviousEnd() - start));
    }

    private SearchNode? ParseUnary(bool allowScope)
    {
        if (Current.IsKeyword("NOT"))
        {
            var not = Advance();
            if (AtExpressionEnd() || Current.IsKeyword("OR") || Current.IsKeyword("AND"))
            {
                throw new QueryException(not.Span, "expected an expression after NOT");
            }

            var operand = ParseUnary(allowScope: false) ?? throw new QueryException(not.Span, "expected an expression after NOT");
            return new NotNode(operand, new TextSpan(not.Span.Offset, PreviousEnd() - not.Span.Offset));
        }

        return ParsePrimary(allowScope);
    }

    private SearchNode? ParsePrimary(bool allowScope)
    {
        var t = Current;
        switch (t.Kind)
        {
            case TokenKind.LParen:
            {
                Advance();
                if (Current.Kind == TokenKind.RParen)
                {
                    throw new QueryException(new TextSpan(t.Span.Offset, 2), "empty parentheses");
                }

                var inner = ParseOr(allowScope: false);
                if (Current.Kind != TokenKind.RParen)
                {
                    throw new QueryException(t.Span, "missing ')'");
                }

                Advance();
                return inner;
            }

            case TokenKind.Quoted:
                Advance();
                return new PhraseNode(t.Text, t.Span);
            case TokenKind.Word:
                break;
            case TokenKind.Operator:
                throw new QueryException(t.Span, $"unexpected '{t.Text}': a comparison needs a field name before it");
            case TokenKind.Comma:
                throw new QueryException(t.Span, "unexpected ','");
            default:
                throw new QueryException(t.Span, $"unexpected '{t.Text}'");
        }

        Advance();
        if (_afterAggregate && Current.Kind == TokenKind.LParen && AggregateFunctions.ContainsKey(t.Text))
        {
            throw NameTheAggregate(t);
        }

        if (Current.Kind == TokenKind.Operator)
        {
            var op = Advance();
            var value = Current;
            if (value.Kind is not (TokenKind.Word or TokenKind.Quoted))
            {
                throw new QueryException(value.Kind == TokenKind.End ? op.Span : value.Span, $"expected a value after '{op.Text}'");
            }

            Advance();
            if (Current.Kind == TokenKind.Operator)
            {
                throw new QueryException(Current.Span, $"unexpected '{Current.Text}': quote a value that contains operators");
            }

            var compareOp = op.Text switch
            {
                "=" => CompareOp.Eq,
                "!=" => CompareOp.Ne,
                "<" => CompareOp.Lt,
                ">" => CompareOp.Gt,
                "<=" => CompareOp.Le,
                _ => CompareOp.Ge,
            };
            var span = new TextSpan(t.Span.Offset, value.Span.Offset + value.Span.Length - t.Span.Offset);
            if (ScopeWords.Contains(t.Text) && !t.Text.Contains('.'))
            {
                if (compareOp != CompareOp.Eq)
                {
                    throw new QueryException(op.Span, $"{t.Text.ToLowerInvariant()} takes '=' only");
                }

                if (_inWhere)
                {
                    throw new QueryException(span, $"{t.Text.ToLowerInvariant()}= sets the scope of the whole search: write it before the first '|', not in where");
                }

                if (!allowScope)
                {
                    throw new QueryException(span, $"{t.Text.ToLowerInvariant()}= sets the scope of the whole search and cannot sit inside OR, NOT or parentheses");
                }

                SetScope(t.Text, value.Text, span);
                return null;
            }

            return new CompareNode(t.Text, t.Span, compareOp, value.Text, value.Span, value.Kind == TokenKind.Quoted, span);
        }

        if (Current.IsKeyword("IN") && _i + 1 < _tokens.Count && _tokens[_i + 1].Kind == TokenKind.LParen)
        {
            Advance();
            var open = Advance();
            var values = ImmutableArray.CreateBuilder<(string, bool, TextSpan)>();
            while (true)
            {
                var v = Current;
                if (v.Kind is not (TokenKind.Word or TokenKind.Quoted))
                {
                    throw new QueryException(v.Kind == TokenKind.End ? open.Span : v.Span, "IN needs a list of values: IN (a, b)");
                }

                Advance();
                values.Add((v.Text, v.Kind == TokenKind.Quoted, v.Span));
                if (Current.Kind == TokenKind.Comma)
                {
                    Advance();
                    continue;
                }

                if (Current.Kind == TokenKind.RParen)
                {
                    Advance();
                    break;
                }

                throw new QueryException(Current.Kind == TokenKind.End ? open.Span : Current.Span, "IN list: expected ',' or ')'");
            }

            return new InNode(t.Text, t.Span, values.ToImmutable(), new TextSpan(t.Span.Offset, PreviousEnd() - t.Span.Offset));
        }

        return new TermNode(t.Text, t.Span);
    }

    private void SetScope(string word, string value, TextSpan span)
    {
        switch (word.ToLowerInvariant())
        {
            case "index":
                if (_index is not null)
                {
                    throw new QueryException(span, "index= given twice");
                }

                _index = (value, span);
                break;
            case "earliest":
                if (_earliest is not null)
                {
                    throw new QueryException(span, "earliest= given twice");
                }

                _earliest = (value, span);
                break;
            default:
                if (_latest is not null)
                {
                    throw new QueryException(span, "latest= given twice");
                }

                _latest = (value, span);
                break;
        }
    }

    private static void ValidateName(string name, TextSpan span)
    {
        if (name.Length == 0 || name.Contains('`') || name.Any(char.IsControl))
        {
            throw new QueryException(span, $"'{name}' is not a field name");
        }
    }

    private bool AtExpressionEnd() => Current.Kind is TokenKind.End or TokenKind.Pipe or TokenKind.RParen;

    private int PreviousEnd() => _i == 0 ? 0 : _tokens[_i - 1].Span.Offset + _tokens[_i - 1].Span.Length;

    public override string ToString() => _text;
}
