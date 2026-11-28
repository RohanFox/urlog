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

        return new Query(search, commands.ToImmutable(), _index, _earliest, _latest) { FreeGroupName = FreeGroupName() };
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

        // How many of the last terms stand side by side with no AND written between them.
        var adjacent = 0;
        while (!AtExpressionEnd() && !Current.IsKeyword("OR"))
        {
            if (Current.IsKeyword("AND"))
            {
                var and = Advance();
                if (AtExpressionEnd() || Current.IsKeyword("OR"))
                {
                    throw new QueryException(and.Span, "expected an expression after AND");
                }

                adjacent = 0;
                continue;
            }

            var node = ParseUnary(allowScope);
            if (node is null)
            {
                adjacent = 0;
                continue;
            }

            terms.Add(node);
            adjacent++;
            if (adjacent >= 3 && LikeShape(terms[^3], terms[^2], terms[^1]) is { } like)
            {
                terms.RemoveRange(terms.Count - 3, 3);
                terms.Add(like);
                adjacent = 0;
            }
        }

        if (terms.Count == 0)
        {
            return null;
        }

        return terms.Count == 1 ? terms[0] : new AndNode(terms.ToImmutable(), new TextSpan(start, PreviousEnd() - start));
    }

    /// <summary>
    /// The three terms as one <see cref="LikeNode"/> when they are a word, <c>LIKE</c> or <c>ILIKE</c> in upper
    /// case (the convention of <c>OR</c>, <c>AND</c>, <c>NOT</c> and <c>IN</c>) with or without <c>NOT</c> before
    /// it, and a word or a quoted string. A quoted <c>"LIKE"</c> is a phrase and lower case is a word, so neither
    /// is the shape.
    /// </summary>
    private static LikeNode? LikeShape(SearchNode left, SearchNode keyword, SearchNode pattern)
    {
        var word = left;
        while (word is NotNode not)
        {
            word = not.Operand;
        }

        var bare = keyword is NotNode { Operand: TermNode negated } ? negated : keyword as TermNode;
        if (word is not TermNode || bare is not { Text: "LIKE" or "ILIKE" } || pattern is not (TermNode or PhraseNode))
        {
            return null;
        }

        return new LikeNode(left, keyword, pattern, new TextSpan(left.Span.Offset, pattern.Span.Offset + pattern.Span.Length - left.Span.Offset));
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
        if (StartsHere(TokenKind.Quoted, t) && t.Text[^1] == '~')
        {
            var negated = t.Text.EndsWith("!~", StringComparison.Ordinal);
            var name = t.Text[..^(negated ? 2 : 1)];
            if (name.Length > 0)
            {
                throw RegexOperator(name, negated ? "!~" : "~", negated, t, Current, $"; to search for the text {t.Text} put a space after it");
            }
        }

        if (_afterAggregate && Current.Kind == TokenKind.LParen && AggregateFunctions.ContainsKey(t.Text))
        {
            throw NameTheAggregate(t);
        }

        var comparison = Current.Kind == TokenKind.Operator;
        var list = Current.IsKeyword("IN") && _i + 1 < _tokens.Count && _tokens[_i + 1].Kind == TokenKind.LParen;
        if ((comparison || list) && t.Text[0] == '-')
        {
            throw MinusBeforeAField(t, list);
        }

        if (comparison)
        {
            var op = Advance();
            var value = Current;
            if (value.Kind is not (TokenKind.Word or TokenKind.Quoted))
            {
                throw new QueryException(value.Kind == TokenKind.End ? op.Span : value.Span, $"expected a value after '{op.Text}'");
            }

            Advance();
            if (op.Text is "=" or "!=" && value is { Kind: TokenKind.Word, Text: "~" } && StartsHere(TokenKind.Quoted, value))
            {
                throw RegexOperator(t.Text, op.Text + "~", op.Text == "!=", t, Current, string.Empty);
            }

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

        if (list)
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

    /// <summary>
    /// <c>-status=200</c> is how other query languages write "exclude". Here it would compare a field called
    /// <c>-status</c>, which no event has, and answer with nothing. No catalog name starts with a minus, so the
    /// form is refused with what to write. A minus in a value (<c>earliest=-7d</c>), in a bare word (<c>-enc</c>)
    /// and in quotes is untouched.
    /// </summary>
    private QueryException MinusBeforeAField(Token word, bool list)
    {
        var name = word.Text[1..];
        if (name.Length == 0)
        {
            return new QueryException(word.Span, "'-' is not a field name");
        }

        if (list)
        {
            return new QueryException(word.Span, $"'{word.Text}' is not a field name: to exclude write NOT {name} IN (...)");
        }

        // The comparison as written, when it is whole, so the rewrite can be copied.
        var op = _tokens[_i];
        var value = _tokens[_i + 1];
        if (value.Kind is not (TokenKind.Word or TokenKind.Quoted))
        {
            return new QueryException(word.Span, $"'{word.Text}' is not a field name: to exclude write NOT {name}{op.Text}...");
        }

        var written = _text.Substring(value.Span.Offset, value.Span.Length);
        var rewrite = op.Text == "=" ? $"NOT {name}={written} or {name}!={written}" : $"NOT {name}{op.Text}{written}";
        return new QueryException(word.Span, $"'{word.Text}' is not a field name: to exclude write {rewrite}");
    }

    /// <summary>True when the current token is of this kind and starts at the character after <paramref name="previous"/>.</summary>
    private bool StartsHere(TokenKind kind, Token previous) =>
        Current.Kind == kind && Current.Span.Offset == previous.Span.Offset + previous.Span.Length;

    /// <summary>
    /// <c>field~"re"</c>, <c>field!~"re"</c> and <c>field=~"re"</c> are regex matches in other query languages. The
    /// lexer keeps <c>~</c> a word character (<c>PROGRA~1</c>, <c>~/.ssh</c>), so without this rule the form is a
    /// word and a phrase searched in the message, and answers with nothing. The quote must touch the operator:
    /// with a space between them the two are search terms.
    /// </summary>
    private QueryException RegexOperator(string field, string op, bool negated, Token first, Token pattern, string otherwise) => new(
        new TextSpan(first.Span.Offset, pattern.Span.Offset + pattern.Span.Length - first.Span.Offset),
        RegexOperatorMessage(field, op, "\"...\"", negated, FreeGroupName(), _afterAggregate ? _aggregateVerb : null, otherwise));

    /// <summary>
    /// The refusal of a regex operator, with the rex that does the same. After an aggregation there is no event
    /// left to match, and a rex there is refused, so the message gives the form with the rex before the verb.
    /// </summary>
    /// <param name="group">A group name the query does not use, so the rewrite can be pasted as it is.</param>
    /// <param name="aggregate">The aggregating verb the operator stands after, or null before any.</param>
    /// <param name="otherwise">How to search for the text itself; left out after an aggregation, where no term is searched.</param>
    internal static string RegexOperatorMessage(string field, string op, string pattern, bool negated, string group, string? aggregate, string otherwise)
    {
        var rewrite = $"| rex field={field} \"(?<{group}>...)\" | where {group}{(negated ? "!=" : "=")}*";
        return aggregate is null
            ? $"not supported in v1: {field}{op}{pattern} (use {rewrite}{otherwise})"
            : $"not supported in v1: {field}{op}{pattern} (a pattern is matched before {aggregate}: {rewrite} | {aggregate} ...)";
    }

    /// <summary>
    /// <c>m</c>, or the first of <c>m2</c>, <c>m3</c>, ... that the query does not use as a word or as the name of
    /// a group. A rex refuses a group name that an earlier rex took or that the query read as a field.
    /// </summary>
    private string FreeGroupName()
    {
        for (var n = 1; ; n++)
        {
            var name = n == 1 ? "m" : "m" + n.ToString(CultureInfo.InvariantCulture);
            var used = false;
            foreach (var token in _tokens)
            {
                if (Uses(token, name))
                {
                    used = true;
                    break;
                }
            }

            if (!used)
            {
                return name;
            }
        }
    }

    private static bool Uses(Token token, string name)
    {
        if (token.Kind == TokenKind.Quoted)
        {
            return token.Text.Contains("<" + name + ">", StringComparison.OrdinalIgnoreCase);
        }

        if (token.Kind != TokenKind.Word)
        {
            return false;
        }

        // The word itself, or the part before a regex operator: m~x reads m.
        var word = token.Text.AsSpan();
        var tilde = word.IndexOf('~');
        if (tilde > 0)
        {
            word = word[..tilde].TrimEnd('!');
        }

        return word.Equals(name, StringComparison.OrdinalIgnoreCase);
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
