using System.Globalization;

namespace Urlog.Core.Intake;

/// <summary>A host proposed for a file, and the pattern that produced it (kept on the file row so a wrong rule can be found).</summary>
public sealed record HostProposal(string Host, string Rule);

/// <summary>
/// Proposes a host from where a file sits: its archive path, or its name when it arrived alone (design record,
/// "Intake"). A proposal is a default for formats that carry no host of their own; it is flagged as inferred and
/// never required, so a path that matches nothing leaves the file unassigned.
/// <para>
/// Patterns are path templates, deliberately not regular expressions: they come from configuration and run on
/// names an uploader chose. <c>{host}</c> captures (once per pattern), <c>*</c> stays inside one segment,
/// <c>**</c> as a whole segment crosses any number of them, <c>{a,b}</c> is alternation. <c>/</c> separates
/// folders and <c>!</c> ends an archive name, and the two are different: <c>{host}/</c> never captures an archive
/// name, which would otherwise pass for a host with a domain (<c>bundle.zip</c>). Matching ignores case; the
/// first pattern whose capture is host-shaped wins.
/// </para>
/// </summary>
public sealed class HostInference
{
    private const string Placeholder = "{host}";

    private static readonly string[] DefaultPatterns =
    [
        "**/{host}/var/log/**",
        "**/{host}/var/lib/docker/**",
        "**/{host}/{nginx,apache2,httpd,iis,syslog,journal,audit}/**",
        "**/{host}/inetpub/logs/**",
        "**/{host}/W3SVC*/**",
        "**/{host}/LogFiles/**",
        "**/{host}/winevt/**",
        "**/{host}/C/Windows/**",
        "**/{host}/Windows/System32/**",
        "{host}{-,_}logs.*!**",
        "{host}.{zip,tar,tar.*,tgz}!**",
        "**/{host}{_,-}{access,error,auth,syslog,messages,secure}.log*",
    ];

    // Folder and archive names that sit where a host folder would and are not one.
    private static readonly string[] DefaultStopWords =
    [
        "var", "log", "logs", "tmp", "temp", "home", "etc", "opt", "usr", "srv", "root", "data", "backup", "backups", "export", "exports",
        "archive", "archives", "bundle", "collect", "collection", "evidence", "upload", "uploads", "files", "case", "triage",
        "windows", "system32", "inetpub", "logfiles", "users", "c", "d", "e",
        "nginx", "apache", "apache2", "httpd", "iis", "syslog", "journal", "audit", "docker", "containers", "access", "error",
    ];

    private readonly Pattern[] _patterns;
    private readonly HashSet<string> _stopWords;

    /// <param name="patterns">In order of precedence.</param>
    /// <param name="stopWords">Names that are never a host; null uses the built-in list.</param>
    /// <exception cref="ArgumentException">A pattern is empty, has no <c>{host}</c> or more than one, or has an unclosed brace.</exception>
    public HostInference(IEnumerable<string> patterns, IEnumerable<string>? stopWords = null)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        var compiled = new List<Pattern>();
        foreach (var text in patterns)
        {
            foreach (var plain in Expand(text))
            {
                compiled.Add(Pattern.Compile(plain, text));
            }
        }

        _patterns = [.. compiled];
        _stopWords = new HashSet<string>(stopWords ?? DefaultStopWords, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The built-in list: a host folder above a well-known log folder, an archive named after its host, a file named after its host.</summary>
    public static HostInference Default { get; } = new(DefaultPatterns);

    /// <summary>The built-in patterns, as configuration would write them.</summary>
    public static IReadOnlyList<string> BuiltInPatterns => DefaultPatterns;

    /// <summary>
    /// The proposal for <paramref name="path"/> (<c>bundle.zip!web-01/nginx/access.log.3</c>, or a bare file name),
    /// lowercased, or null when no pattern yields a host-shaped name.
    /// </summary>
    public HostProposal? Infer(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var segments = Split(path);
        if (segments.Count == 0)
        {
            return null;
        }

        foreach (var pattern in _patterns)
        {
            string? host = null;
            if (Match(pattern.Segments, 0, segments, 0, ref host) && host is not null && IsHostShaped(host))
            {
                return new HostProposal(host.ToLowerInvariant(), pattern.Source);
            }
        }

        return null;
    }

    /// <summary>
    /// A selection of files by where they sat, in the syntax of the host patterns without <c>{host}</c>:
    /// <c>*</c> stays inside one path segment, <c>**</c> crosses segments, <c>{a,b}</c> is alternation, <c>!</c>
    /// follows an archive name. The result says whether a path (an archive path, or a bare file name) matches.
    /// </summary>
    /// <exception cref="ArgumentException">The pattern is empty, has an unclosed brace or holds <c>{host}</c>.</exception>
    public static Func<string, bool> CompileSelector(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var compiled = Expand(pattern.Trim()).Select(plain => Pattern.Compile(plain, pattern, hostCount: 0)).ToArray();
        return path =>
        {
            var segments = Split(path);
            if (segments.Count == 0)
            {
                return false;
            }

            foreach (var one in compiled)
            {
                string? ignored = null;
                if (Match(one.Segments, 0, segments, 0, ref ignored))
                {
                    return true;
                }
            }

            return false;
        };
    }

    /// <summary>
    /// RFC 1123 labels (underscore tolerated, Windows names have it) with a letter somewhere, or an IPv4 address;
    /// 2 to 253 characters; not on the stop list. Dates and plain numbers have no letter and fail.
    /// </summary>
    public bool IsHostShaped(string candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Length is < 2 or > 253 || _stopWords.Contains(candidate))
        {
            return false;
        }

        var labels = candidate.Split('.');
        if (labels.Length == 4 && Array.TrueForAll(labels, l => l.Length is >= 1 and <= 3 && byte.TryParse(l, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            return true;
        }

        var letter = false;
        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-')
            {
                return false;
            }

            foreach (var c in label)
            {
                if (char.IsAsciiLetter(c))
                {
                    letter = true;
                }
                else if (!char.IsAsciiDigit(c) && c != '-' && c != '_')
                {
                    return false;
                }
            }
        }

        return letter;
    }

    private static List<Segment> Split(string path)
    {
        var segments = new List<Segment>();
        var start = 0;
        for (var i = 0; i <= path.Length; i++)
        {
            var end = i == path.Length;
            if (!end && path[i] is not ('/' or '\\' or '!'))
            {
                continue;
            }

            if (i > start)
            {
                var text = path[start..i];
                if (text != ".")
                {
                    segments.Add(new Segment(text, Container: !end && path[i] == '!'));
                }
            }

            start = i + 1;
        }

        return segments;
    }

    private static bool Match(PatternSegment[] pattern, int p, List<Segment> path, int s, ref string? host)
    {
        if (p == pattern.Length)
        {
            return s == path.Count;
        }

        var segment = pattern[p];
        if (segment.AnyDepth)
        {
            for (var skip = s; skip <= path.Count; skip++)
            {
                var captured = host;
                if (Match(pattern, p + 1, path, skip, ref captured))
                {
                    host = captured;
                    return true;
                }
            }

            return false;
        }

        if (s == path.Count || path[s].Container != segment.Container)
        {
            return false;
        }

        var here = host;
        if (!MatchSegment(segment.Tokens, 0, path[s].Text, 0, ref here))
        {
            return false;
        }

        if (!Match(pattern, p + 1, path, s + 1, ref here))
        {
            return false;
        }

        host = here;
        return true;
    }

    private static bool MatchSegment(Token[] tokens, int t, string text, int at, ref string? host)
    {
        if (t == tokens.Length)
        {
            return at == text.Length;
        }

        var token = tokens[t];
        switch (token.Kind)
        {
            case TokenKind.Literal:
                return text.AsSpan(at).StartsWith(token.Text, StringComparison.OrdinalIgnoreCase)
                    && MatchSegment(tokens, t + 1, text, at + token.Text.Length, ref host);
            case TokenKind.Star:
                for (var end = at; end <= text.Length; end++)
                {
                    if (MatchSegment(tokens, t + 1, text, end, ref host))
                    {
                        return true;
                    }
                }

                return false;
            default:
                // The shortest capture that lets the rest match: "web-01-access.log" against "{host}-access.log".
                for (var end = at + 1; end <= text.Length; end++)
                {
                    string? ignored = null;
                    if (MatchSegment(tokens, t + 1, text, end, ref ignored))
                    {
                        host = text[at..end];
                        return true;
                    }
                }

                return false;
        }
    }

    /// <summary>Writes out every alternative of the <c>{a,b}</c> groups; <c>{host}</c> is left as it is.</summary>
    private static List<string> Expand(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            throw new ArgumentException("a host pattern is empty", nameof(pattern));
        }

        var open = -1;
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '{' && !pattern.AsSpan(i).StartsWith(Placeholder, StringComparison.Ordinal))
            {
                open = i;
                break;
            }
        }

        if (open < 0)
        {
            return [pattern];
        }

        var close = pattern.IndexOf('}', open);
        if (close < 0)
        {
            throw new ArgumentException($"host pattern '{pattern}' has an unclosed brace", nameof(pattern));
        }

        var expanded = new List<string>();
        foreach (var alternative in pattern[(open + 1)..close].Split(','))
        {
            expanded.AddRange(Expand(string.Concat(pattern.AsSpan(0, open), alternative, pattern.AsSpan(close + 1))));
        }

        return expanded;
    }

    private enum TokenKind
    {
        Literal,
        Star,
        Host,
    }

    private readonly record struct Segment(string Text, bool Container);

    private readonly record struct Token(TokenKind Kind, string Text);

    private readonly record struct PatternSegment(Token[] Tokens, bool Container, bool AnyDepth);

    private sealed record Pattern(PatternSegment[] Segments, string Source)
    {
        public static Pattern Compile(string plain, string source, int hostCount = 1)
        {
            var segments = new List<PatternSegment>();
            var hosts = 0;
            var start = 0;
            for (var i = 0; i <= plain.Length; i++)
            {
                var end = i == plain.Length;
                if (!end && plain[i] is not ('/' or '!'))
                {
                    continue;
                }

                if (i > start)
                {
                    var text = plain[start..i];
                    var container = !end && plain[i] == '!';
                    if (text == "**")
                    {
                        segments.Add(new PatternSegment([], Container: false, AnyDepth: true));
                    }
                    else
                    {
                        var tokens = Tokenise(text, source);
                        hosts += tokens.Count(t => t.Kind == TokenKind.Host);
                        segments.Add(new PatternSegment(tokens, container, AnyDepth: false));
                    }
                }

                start = i + 1;
            }

            if (hosts != hostCount)
            {
                throw new ArgumentException(hostCount == 1 ? $"host pattern '{source}' must hold {Placeholder} exactly once" : $"pattern '{source}' selects files and may not hold {Placeholder}", nameof(source));
            }

            return new Pattern([.. segments], source);
        }

        private static Token[] Tokenise(string segment, string source)
        {
            var tokens = new List<Token>();
            var literal = 0;
            var i = 0;
            while (i < segment.Length)
            {
                var host = segment.AsSpan(i).StartsWith(Placeholder, StringComparison.Ordinal);
                if (!host && segment[i] != '*')
                {
                    if (segment[i] is '{' or '}')
                    {
                        throw new ArgumentException($"host pattern '{source}' has a brace that is neither {Placeholder} nor an alternation", nameof(source));
                    }

                    i++;
                    continue;
                }

                if (i > literal)
                {
                    tokens.Add(new Token(TokenKind.Literal, segment[literal..i]));
                }

                tokens.Add(new Token(host ? TokenKind.Host : TokenKind.Star, string.Empty));
                i += host ? Placeholder.Length : 1;
                literal = i;
            }

            if (segment.Length > literal)
            {
                tokens.Add(new Token(TokenKind.Literal, segment[literal..]));
            }

            return [.. tokens];
        }
    }
}
