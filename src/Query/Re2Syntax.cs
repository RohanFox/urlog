namespace Urlog.Query;

/// <summary>A pattern <see cref="Re2Syntax"/> refuses, with the position of the construct inside the pattern.</summary>
public sealed class Re2SyntaxException : Exception
{
    public Re2SyntaxException()
    {
    }

    public Re2SyntaxException(string message)
        : base(message)
    {
    }

    public Re2SyntaxException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public Re2SyntaxException(int offset, int length, string message)
        : base(message)
    {
        Offset = offset;
        Length = length;
    }

    public int Offset { get; }

    public int Length { get; }
}

/// <summary>A capture group of a checked pattern; <see cref="Name"/> is null for an unnamed one.</summary>
public sealed record Re2Group(string? Name, int Index, int NameOffset, int NameLength);

/// <summary>
/// Checks a <c>rex</c> pattern against the RE2 syntax the event store compiles, before any SQL exists (design
/// record, "Query language"). It accepts what it knows and refuses the rest with the offset of the construct:
/// lookahead and lookbehind, backreferences, atomic groups, possessive quantifiers, conditionals, comments and
/// recursion are not RE2. The limits keep the compiled program small, since the cost of a match is rows x bytes x
/// program size: pattern length, capture groups, the bound of a counted repetition, and no counted repetition
/// inside another. The server's own compile error stays the last word for anything this lets through.
/// </summary>
public static class Re2Syntax
{
    public const int MaxPatternBytes = 1000;
    public const int MaxGroups = 16;
    public const int MaxRepeat = 100;
    public const int MaxNesting = 10;

    private static readonly HashSet<string> PosixClasses = new(StringComparer.Ordinal)
    {
        "alnum", "alpha", "ascii", "blank", "cntrl", "digit", "graph", "lower", "print", "punct", "space", "upper", "word", "xdigit",
    };

    /// <summary>The capture groups of a valid pattern, in order; throws <see cref="Re2SyntaxException"/> otherwise.</summary>
    public static IReadOnlyList<Re2Group> Check(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (pattern.Length == 0)
        {
            throw new Re2SyntaxException(0, 0, "the pattern is empty");
        }

        if (System.Text.Encoding.UTF8.GetByteCount(pattern) > MaxPatternBytes)
        {
            throw new Re2SyntaxException(0, pattern.Length, $"the pattern is longer than {MaxPatternBytes} bytes");
        }

        var state = new State(pattern);
        state.Alternation(0);
        if (state.Pos < pattern.Length)
        {
            throw new Re2SyntaxException(state.Pos, 1, "unmatched ')'");
        }

        return state.Groups;
    }

    private sealed class State(string p)
    {
        public int Pos;

        public List<Re2Group> Groups { get; } = [];

        private bool End => Pos >= p.Length;

        /// <summary>Returns true when the alternation holds a counted repetition.</summary>
        public bool Alternation(int depth)
        {
            var counted = Concatenation(depth);
            while (!End && p[Pos] == '|')
            {
                Pos++;
                counted |= Concatenation(depth);
            }

            return counted;
        }

        private bool Concatenation(int depth)
        {
            var counted = false;
            while (!End && p[Pos] != '|' && p[Pos] != ')')
            {
                counted |= Repeated(depth);
            }

            return counted;
        }

        private bool Repeated(int depth)
        {
            var start = Pos;
            var counted = Atom(depth);
            var quantified = false;
            while (!End)
            {
                var c = p[Pos];
                var at = Pos;
                if (c is '*' or '+' or '?')
                {
                    Pos++;
                }
                else if (c == '{' && Bounds(out var low, out var high))
                {
                    if (low > MaxRepeat || high > MaxRepeat)
                    {
                        throw new Re2SyntaxException(at, Pos - at, $"a repetition count is at most {MaxRepeat}");
                    }

                    if (high >= 0 && high < low)
                    {
                        throw new Re2SyntaxException(at, Pos - at, "the repetition's upper bound is below its lower bound");
                    }

                    if (counted)
                    {
                        throw new Re2SyntaxException(start, Pos - start, "a counted repetition inside another counted repetition is not allowed");
                    }

                    counted = true;
                }
                else
                {
                    break;
                }

                if (quantified)
                {
                    throw new Re2SyntaxException(at, Pos - at, "a second repetition operator: put the first in a group");
                }

                quantified = true;
                if (!End && p[Pos] == '?')
                {
                    Pos++; // lazy
                }
                else if (!End && p[Pos] == '+')
                {
                    throw new Re2SyntaxException(at, Pos + 1 - at, "possessive quantifiers are not RE2 syntax");
                }
            }

            return counted;
        }

        /// <summary>Reads <c>{n}</c>, <c>{n,}</c> or <c>{n,m}</c> at the position; anything else leaves the brace a literal.</summary>
        private bool Bounds(out int low, out int high)
        {
            low = 0;
            high = -1;
            var i = Pos + 1;
            if (!Number(ref i, out low))
            {
                return false;
            }

            if (i < p.Length && p[i] == '}')
            {
                high = low;
                Pos = i + 1;
                return true;
            }

            if (i >= p.Length || p[i] != ',')
            {
                return false;
            }

            i++;
            if (i < p.Length && p[i] == '}')
            {
                Pos = i + 1;
                return true;
            }

            if (!Number(ref i, out high) || i >= p.Length || p[i] != '}')
            {
                high = -1;
                return false;
            }

            Pos = i + 1;
            return true;
        }

        private bool Number(ref int i, out int value)
        {
            value = 0;
            var start = i;
            while (i < p.Length && char.IsAsciiDigit(p[i]))
            {
                value = Math.Min(100_000, (value * 10) + (p[i] - '0'));
                i++;
            }

            return i > start;
        }

        private bool Atom(int depth)
        {
            var c = p[Pos];
            switch (c)
            {
                case '(':
                    return Group(depth);
                case '[':
                    Class();
                    return false;
                case '\\':
                    Escape(inClass: false);
                    return false;
                case '*' or '+' or '?':
                    throw new Re2SyntaxException(Pos, 1, $"'{c}' has nothing to repeat");
                default:
                    Pos++;
                    return false;
            }
        }

        private bool Group(int depth)
        {
            var open = Pos;
            if (depth + 1 > MaxNesting)
            {
                throw new Re2SyntaxException(open, 1, $"groups are nested more than {MaxNesting} deep");
            }

            Pos++;
            if (!End && p[Pos] == '?')
            {
                if (GroupPrefix(open))
                {
                    return false; // a flag group such as (?i) has no body
                }
            }
            else
            {
                Capture(open, null, 0, 0);
            }

            var counted = Alternation(depth + 1);
            if (End || p[Pos] != ')')
            {
                throw new Re2SyntaxException(open, 1, "missing ')'");
            }

            Pos++;
            return counted;
        }

        /// <summary>Reads what follows <c>(?</c>. Returns true for a flag group that closes itself.</summary>
        private bool GroupPrefix(int open)
        {
            var rest = p.AsSpan(Pos + 1);
            if (rest.StartsWith(":"))
            {
                Pos += 2;
                return false;
            }

            if (rest.StartsWith("=") || rest.StartsWith("!"))
            {
                throw new Re2SyntaxException(open, 3, "lookahead is not RE2 syntax");
            }

            if (rest.StartsWith("<=") || rest.StartsWith("<!"))
            {
                throw new Re2SyntaxException(open, 4, "lookbehind is not RE2 syntax");
            }

            if (rest.StartsWith(">"))
            {
                throw new Re2SyntaxException(open, 3, "atomic groups are not RE2 syntax");
            }

            if (rest.StartsWith("#"))
            {
                throw new Re2SyntaxException(open, 3, "comments are not RE2 syntax");
            }

            if (rest.StartsWith("("))
            {
                throw new Re2SyntaxException(open, 3, "conditionals are not RE2 syntax");
            }

            if (rest.StartsWith("P=") || rest.StartsWith("P>"))
            {
                throw new Re2SyntaxException(open, 4, "backreferences and recursion are not RE2 syntax");
            }

            var nameStart = rest.StartsWith("P<") ? Pos + 3 : rest.StartsWith("<") ? Pos + 2 : -1;
            if (nameStart >= 0)
            {
                var close = p.IndexOf('>', nameStart);
                if (close < 0)
                {
                    throw new Re2SyntaxException(open, p.Length - open, "a group name needs a closing '>'");
                }

                var name = p[nameStart..close];
                if (!Names.IsStrict(name))
                {
                    throw new Re2SyntaxException(nameStart, Math.Max(1, close - nameStart), $"'{name}' is not a group name: use letters, digits and '_', starting with a letter or '_', at most {Names.MaxLength} characters");
                }

                if (Groups.Any(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new Re2SyntaxException(nameStart, name.Length, $"the group name '{name}' is used twice");
                }

                Capture(open, name, nameStart, name.Length);
                Pos = close + 1;
                return false;
            }

            // Flags: (?i), (?i-s), (?i:...). Only i, m, s and U exist.
            var i = Pos + 1;
            var any = false;
            while (i < p.Length && (p[i] is 'i' or 'm' or 's' or 'U' or '-'))
            {
                any |= p[i] != '-';
                i++;
            }

            if (any && i < p.Length && p[i] == ')')
            {
                Pos = i + 1;
                return true;
            }

            if (any && i < p.Length && p[i] == ':')
            {
                Pos = i + 1;
                return false;
            }

            throw new Re2SyntaxException(open, Math.Min(p.Length, i + 1) - open, "this group syntax is not RE2: use (...), (?:...), (?<name>...) or the flags i, m, s, U");
        }

        private void Capture(int open, string? name, int nameOffset, int nameLength)
        {
            if (Groups.Count == MaxGroups)
            {
                throw new Re2SyntaxException(open, 1, $"more than {MaxGroups} capture groups: make the ones you do not need non-capturing, (?:...)");
            }

            Groups.Add(new Re2Group(name, Groups.Count + 1, nameOffset, nameLength));
        }

        private void Class()
        {
            var open = Pos;
            Pos++;
            if (!End && p[Pos] == '^')
            {
                Pos++;
            }

            var first = true;
            while (true)
            {
                if (End)
                {
                    throw new Re2SyntaxException(open, 1, "missing ']'");
                }

                var c = p[Pos];
                if (c == ']' && !first)
                {
                    Pos++;
                    return;
                }

                first = false;
                if (c == '[' && Pos + 1 < p.Length && p[Pos + 1] == ':')
                {
                    var close = p.IndexOf(":]", Pos + 2, StringComparison.Ordinal);
                    var name = close < 0 ? "" : p[(Pos + 2)..close].TrimStart('^');
                    if (close < 0 || !PosixClasses.Contains(name))
                    {
                        throw new Re2SyntaxException(Pos, close < 0 ? 2 : close + 2 - Pos, "this is not a character class name: [[:alpha:]], [[:digit:]], ...");
                    }

                    Pos = close + 2;
                    continue;
                }

                var low = ClassMember(out var single);
                if (single && !End && p[Pos] == '-' && Pos + 1 < p.Length && p[Pos + 1] != ']')
                {
                    var at = Pos;
                    Pos++;
                    var high = ClassMember(out var highSingle);
                    if (!highSingle || high < low)
                    {
                        throw new Re2SyntaxException(at - 1, Pos - at + 1, "this character range is not valid: the first character must not come after the second");
                    }
                }
            }
        }

        /// <summary>One member of a class. <paramref name="single"/> is false for a shorthand such as <c>\d</c>, which cannot bound a range.</summary>
        private int ClassMember(out bool single)
        {
            if (p[Pos] == '\\')
            {
                return Escape(inClass: true, out single);
            }

            single = true;
            var c = char.ConvertToUtf32(p, Pos);
            Pos += char.IsSurrogatePair(p, Pos) ? 2 : 1;
            return c;
        }

        private void Escape(bool inClass) => Escape(inClass, out _);

        private int Escape(bool inClass, out bool single)
        {
            var at = Pos;
            single = false;
            if (Pos + 1 >= p.Length)
            {
                throw new Re2SyntaxException(at, 1, "a backslash at the end of the pattern escapes nothing");
            }

            var c = p[Pos + 1];
            Pos += 2;
            switch (c)
            {
                case 'd' or 'D' or 'w' or 'W' or 's' or 'S':
                    return 0;
                case 'b' or 'B' or 'A' or 'z' when !inClass:
                    return 0;
                case 'n':
                    single = true;
                    return '\n';
                case 't':
                    single = true;
                    return '\t';
                case 'r':
                    single = true;
                    return '\r';
                case 'f':
                    single = true;
                    return '\f';
                case 'v':
                    single = true;
                    return '\v';
                case 'a':
                    single = true;
                    return '\a';
                case >= '1' and <= '9':
                    throw new Re2SyntaxException(at, 2, "backreferences are not RE2 syntax");
                case '0':
                    throw new Re2SyntaxException(at, 2, "octal escapes are not accepted: write \\x00");
                case 'k':
                    throw new Re2SyntaxException(at, 2, "backreferences are not RE2 syntax");
                case 'p' or 'P':
                    UnicodeClass(at);
                    return 0;
                case 'x':
                    single = true;
                    return Hex(at);
                case 'Q' when !inClass:
                {
                    var end = p.IndexOf("\\E", Pos, StringComparison.Ordinal);
                    Pos = end < 0 ? p.Length : end + 2;
                    return 0;
                }

                default:
                    if (char.IsAscii(c) && !char.IsAsciiLetterOrDigit(c))
                    {
                        single = true;
                        return c; // an escaped punctuation character is itself
                    }

                    throw new Re2SyntaxException(at, 2, $"'\\{c}' is not an RE2 escape");
            }
        }

        private void UnicodeClass(int at)
        {
            if (End)
            {
                throw new Re2SyntaxException(at, 2, "\\p needs a class: \\pL or \\p{Greek}");
            }

            if (p[Pos] == '{')
            {
                var close = p.IndexOf('}', Pos);
                var name = close < 0 ? "" : p[(Pos + 1)..close].TrimStart('^');
                if (name.Length == 0 || !name.All(ch => char.IsAsciiLetter(ch) || ch == '_'))
                {
                    throw new Re2SyntaxException(at, (close < 0 ? p.Length : close + 1) - at, "\\p needs a class: \\pL or \\p{Greek}");
                }

                Pos = close + 1;
                return;
            }

            if (!char.IsAsciiLetter(p[Pos]))
            {
                throw new Re2SyntaxException(at, 3, "\\p needs a class: \\pL or \\p{Greek}");
            }

            Pos++;
        }

        private int Hex(int at)
        {
            if (!End && p[Pos] == '{')
            {
                var close = p.IndexOf('}', Pos);
                var digits = close < 0 ? "" : p[(Pos + 1)..close];
                if (digits.Length is 0 or > 6 || !digits.All(char.IsAsciiHexDigit) || Convert.ToInt32(digits, 16) > 0x10FFFF)
                {
                    throw new Re2SyntaxException(at, (close < 0 ? p.Length : close + 1) - at, "\\x{...} takes one to six hex digits, at most 10FFFF");
                }

                Pos = close + 1;
                return Convert.ToInt32(digits, 16);
            }

            if (Pos + 1 >= p.Length || !char.IsAsciiHexDigit(p[Pos]) || !char.IsAsciiHexDigit(p[Pos + 1]))
            {
                throw new Re2SyntaxException(at, Math.Min(4, p.Length - at), "\\x takes two hex digits, or \\x{...}");
            }

            Pos += 2;
            return Convert.ToInt32(p.Substring(Pos - 2, 2), 16);
        }
    }
}
