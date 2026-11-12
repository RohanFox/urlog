using System.Buffers;
using System.Text;

namespace Urlog.Query;

public enum TokenKind
{
    End,
    Pipe,
    LParen,
    RParen,
    Comma,

    /// <summary><c>=</c> <c>!=</c> <c>&lt;</c> <c>&gt;</c> <c>&lt;=</c> <c>&gt;=</c>; <see cref="Token.Text"/> holds which.</summary>
    Operator,

    /// <summary>A double-quoted string; <see cref="Token.Text"/> is the unescaped content.</summary>
    Quoted,

    /// <summary>Anything else: a field name, a value, a bare term, a verb, a keyword (<c>OR</c>, <c>AND</c>, <c>NOT</c>, <c>IN</c>, <c>by</c>, <c>as</c>).</summary>
    Word,
}

public readonly record struct Token(TokenKind Kind, string Text, TextSpan Span)
{
    public bool IsWord(string keyword, StringComparison comparison = StringComparison.OrdinalIgnoreCase) => Kind == TokenKind.Word && Text.Equals(keyword, comparison);

    /// <summary>Splunk reads <c>OR</c>, <c>AND</c> and <c>NOT</c> as operators only in upper case; lower case is a term.</summary>
    public bool IsKeyword(string keyword) => Kind == TokenKind.Word && Text.Equals(keyword, StringComparison.Ordinal);
}

/// <summary>
/// Tokens of the SPL dialect (design record, "Query language"). Whitespace separates; <c>|</c>, <c>(</c>, <c>)</c> and <c>,</c> stand
/// alone; <c>=</c>, <c>!=</c>, <c>&lt;</c>, <c>&gt;</c>, <c>&lt;=</c>, <c>&gt;=</c> are operators; <c>"..."</c> is a
/// quoted string with <c>\"</c> and <c>\\</c> escapes; everything else is a word (so <c>10.0.0.0/8</c>, <c>val*</c>,
/// <c>-7d@d</c> and <c>url.path</c> are single words). A value with spaces, operators or parentheses is quoted.
/// Positions are character offsets into the query text, for inline errors.
/// </summary>
public sealed class Lexer
{
    private static readonly SearchValues<char> Specials = SearchValues.Create("|(),\"=<>!");

    private readonly string _text;
    private int _pos;

    public Lexer(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _text = text;
    }

    public static List<Token> Tokenize(string text)
    {
        var lexer = new Lexer(text);
        var tokens = new List<Token>();
        Token t;
        do
        {
            t = lexer.Next();
            tokens.Add(t);
        }
        while (t.Kind != TokenKind.End);
        return tokens;
    }

    public Token Next()
    {
        while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos]))
        {
            _pos++;
        }

        if (_pos >= _text.Length)
        {
            return new Token(TokenKind.End, "", new TextSpan(_text.Length, 0));
        }

        var start = _pos;
        var c = _text[_pos];
        switch (c)
        {
            case '|':
                _pos++;
                return new Token(TokenKind.Pipe, "|", new TextSpan(start, 1));
            case '(':
                _pos++;
                return new Token(TokenKind.LParen, "(", new TextSpan(start, 1));
            case ')':
                _pos++;
                return new Token(TokenKind.RParen, ")", new TextSpan(start, 1));
            case ',':
                _pos++;
                return new Token(TokenKind.Comma, ",", new TextSpan(start, 1));
            case '"':
                return Quoted(start);
            case '=':
                _pos++;
                return new Token(TokenKind.Operator, "=", new TextSpan(start, 1));
            case '<':
            case '>':
                _pos++;
                if (_pos < _text.Length && _text[_pos] == '=')
                {
                    _pos++;
                    return new Token(TokenKind.Operator, c + "=", new TextSpan(start, 2));
                }

                return new Token(TokenKind.Operator, c.ToString(), new TextSpan(start, 1));
            case '!':
                if (_pos + 1 < _text.Length && _text[_pos + 1] == '=')
                {
                    _pos += 2;
                    return new Token(TokenKind.Operator, "!=", new TextSpan(start, 2));
                }

                break; // a lone '!' is part of a word
            default:
                break;
        }

        // A word runs to whitespace or a special; '!' inside a word is only special when followed by '='.
        while (_pos < _text.Length)
        {
            var ch = _text[_pos];
            if (char.IsWhiteSpace(ch))
            {
                break;
            }

            if (Specials.Contains(ch) && !(ch == '!' && (_pos + 1 >= _text.Length || _text[_pos + 1] != '=')))
            {
                break;
            }

            _pos++;
        }

        if (_pos == start)
        {
            _pos++; // unreachable in practice: every special is handled above
        }

        return new Token(TokenKind.Word, _text[start.._pos], new TextSpan(start, _pos - start));
    }

    private Token Quoted(int start)
    {
        var sb = new StringBuilder();
        _pos++; // opening quote
        while (_pos < _text.Length)
        {
            var ch = _text[_pos];
            if (ch == '\\' && _pos + 1 < _text.Length && (_text[_pos + 1] == '"' || _text[_pos + 1] == '\\'))
            {
                sb.Append(_text[_pos + 1]);
                _pos += 2;
                continue;
            }

            if (ch == '"')
            {
                _pos++;
                return new Token(TokenKind.Quoted, sb.ToString(), new TextSpan(start, _pos - start));
            }

            sb.Append(ch);
            _pos++;
        }

        throw new QueryException(new TextSpan(start, _text.Length - start), "unterminated quoted string");
    }
}
