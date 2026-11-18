using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Urlog.Query;

namespace Urlog.Query.Tests;

/// <summary>
/// Nothing the analyst writes is ever part of the SQL text (design record, "Query language"): a value is a bound
/// parameter, an extras key is a parameter, and a name the analyst invents labels the result beside the SQL, which
/// carries a generated alias in its place. Two properties say so, over every recorded vector and over a seeded run
/// of inputs built from the characters that end an identifier or start a comment:
/// every backquoted identifier in the SQL is a catalog field name or a generated alias, and the SQL text does not
/// change when each name and value of the analyst's is replaced by another of the same shape.
/// </summary>
public sealed partial class SqlTextInvariantTests(ITestOutputHelper output)
{
    private const int FuzzSeed = 20261114;
    private const int FuzzInputs = 4000;

    private static readonly TranspileOptions Options = new(
        1,
        new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero),
        TimeZoneInfo.Utc,
        new DateTimeOffset(2026, 3, 15, 12, 0, 0, TimeSpan.Zero))
    {
        AllowedCases = new HashSet<ulong> { 1, 7 },
    };

    /// <summary>Every character the transpiler's own text uses. A quote, a backslash, a comment or a semicolon can only come from input.</summary>
    private static readonly SearchValues<char> SqlCharacters =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 _.,(){}[]:=<>!`");

    private static readonly HashSet<string> CatalogNames = [.. FieldCatalog.Embedded.Fields.Select(f => f.Name)];

    /// <summary>Words the grammar reads as something other than a name or a value.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "OR", "AND", "NOT", "IN", "as", "by", "count", "stats", "sort", "head", "index", "earliest", "latest",
        "where", "table", "rex", "timechart", "field", "span", "dc", "sum", "avg", "min", "max", "values",
    };

    [GeneratedRegex("[(][?]P?<([A-Za-z_][A-Za-z0-9_]*)>")]
    private static partial Regex RexGroupName();

    [GeneratedRegex("`([^`]*)`")]
    private static partial Regex Backquoted();

    [GeneratedRegex(@"\{([a-z0-9]+):[A-Za-z0-9()]+\}")]
    private static partial Regex Placeholder();

    [Fact]
    public void EveryVectorKeepsTheAnalystsTextOutOfTheSql()
    {
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var checkedSql = 0;
        var replaced = 0;
        var failures = new List<string>();
        foreach (var file in Directory.GetFiles(VectorTests.VectorDirectory, "*.json").Order(StringComparer.Ordinal))
        {
            foreach (var v in JsonSerializer.Deserialize<List<VectorTests.Vector>>(File.ReadAllText(file), json) ?? [])
            {
                if (v.Expect is null)
                {
                    continue;
                }

                var options = OptionsOf(v);
                var original = Transpiler.Transpile(v.Spl, options);
                if (SqlTextProblem(original) is { } problem)
                {
                    failures.Add($"{v.Name}: {problem}\n  {original.Sql}");
                    continue;
                }

                checkedSql++;
                var (other, count) = ReplaceNamesAndValues(v.Spl, options);
                replaced += count;
                var twin = Transpiler.Transpile(other, options);
                if (twin.Sql != original.Sql)
                {
                    failures.Add($"{v.Name}: the SQL changed with the analyst's text\n  {v.Spl}\n  {original.Sql}\n  {other}\n  {twin.Sql}");
                }
                else if (!twin.Warnings.Select(w => w.Code).SequenceEqual(original.Warnings.Select(w => w.Code)))
                {
                    failures.Add($"{v.Name}: the warnings changed with the analyst's text");
                }
            }
        }

        output.WriteLine($"{checkedSql} vectors with SQL, {replaced} names and values replaced");
        Assert.True(failures.Count == 0, string.Join("\n\n", failures));
        Assert.True(checkedSql >= 60, $"{checkedSql} vectors with SQL");
        Assert.True(replaced >= 80, $"{replaced} replacements: the check would prove nothing with fewer");
    }

    [Fact]
    public void SeededHostileInputsNeverReachTheSqlText()
    {
        // The fills use only the letters q, x and z, so no fill and no part of one is a keyword, a verb or a
        // catalog name, and its twin (q -> x -> z -> q) has the same shape. The catalog must keep that true.
        foreach (var word in Reserved.Concat(CatalogWords()))
        {
            Assert.True(word.Any(c => char.IsAsciiLetter(c) && !"qxzQXZ".Contains(c, StringComparison.Ordinal)), $"'{word}' could be built by the generator");
        }

        var random = new Random(FuzzSeed);
        var accepted = 0;
        var refused = 0;
        var withHostileNames = 0;
        var failures = new List<string>();
        for (var i = 0; i < FuzzInputs && failures.Count < 5; i++)
        {
            var template = Templates[random.Next(Templates.Length)];
            var fills = new string[8];
            for (var f = 0; f < fills.Length; f++)
            {
                fills[f] = Fill(random);
            }

            var spl = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, fills);
            var twinSpl = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, [.. fills.Select(Twin)]);
            var a = Try(spl);
            var b = Try(twinSpl);
            if (a.Unexpected is not null || b.Unexpected is not null)
            {
                failures.Add($"{Show(spl)}: {(a.Unexpected ?? b.Unexpected)!.GetType().Name}: {(a.Unexpected ?? b.Unexpected)!.Message}");
                continue;
            }

            if (a.Result is null != b.Result is null)
            {
                failures.Add($"{Show(spl)} and its twin {Show(twinSpl)}: one is accepted and one refused ({a.Error?.Message ?? b.Error?.Message})");
                continue;
            }

            if (a.Result is null)
            {
                refused++;
                if (a.Error!.Offset != b.Error!.Offset || a.Error.Length != b.Error.Length)
                {
                    failures.Add($"{Show(spl)}: refused at {a.Error.Offset}+{a.Error.Length}, its twin at {b.Error.Offset}+{b.Error.Length}");
                }

                continue;
            }

            accepted++;
            withHostileNames += spl.AsSpan().IndexOfAny(NeverInATemplate) >= 0 ? 1 : 0;
            if (SqlTextProblem(a.Result) is { } problem)
            {
                failures.Add($"{Show(spl)}: {problem}\n  {a.Result.Sql}");
            }
            else if (a.Result.Sql != b.Result!.Sql)
            {
                failures.Add($"{Show(spl)}: the SQL changed with the analyst's text\n  {a.Result.Sql}\n  {b.Result.Sql}");
            }
        }

        output.WriteLine($"seed {FuzzSeed}: {FuzzInputs} inputs, {accepted} accepted ({withHostileNames} of them with a hostile character), {refused} refused");
        Assert.True(failures.Count == 0, string.Join("\n\n", failures));
        Assert.True(accepted >= 1000, $"{accepted} accepted");
        Assert.True(withHostileNames >= 300, $"{withHostileNames} accepted inputs with a hostile character");
        Assert.True(refused >= 300, $"{refused} refused");
    }

    [Theory]
    [InlineData("SELECT 1 AS `a\\`", "character")]
    [InlineData("SELECT 1 AS `n`", "identifier")]
    [InlineData("SELECT 1 AS `c0` -- x", "character")]
    [InlineData("SELECT 1 AS `c0` /**/ FROM events_v", "character")]
    [InlineData("SELECT 'x'", "character")]
    [InlineData("SELECT {p9:String}", "parameter")]
    [InlineData("SELECT 1 AS `c0", "backquote")]
    [InlineData("SELECT count() AS `c0` FROM events", "FROM events")]
    [InlineData("SELECT count() AS `c0` FROM events_v WHERE host IN (SELECT host FROM file_generation)", "FROM file_generation")]
    [InlineData("SELECT count() AS `c0`", "no FROM events_v")]
    [InlineData("SELECT count() AS `c0` FROM events_v SETTINGS max_threads = 0", "SETTINGS")]
    [InlineData("SELECT count() AS `c0` FROM events_v AS a JOIN events_v AS b ON a.host = b.host", "JOIN")]
    [InlineData("SELECT count() AS `c0` FROM events_v WHERE http_status = 404", "literal 404")]
    [InlineData("SELECT count() AS `c0` FROM events_v LIMIT 10", "literal 10")]
    [InlineData("SELECT extractGroups(raw, raw)[17] AS `c0` FROM events_v", "literal 17")]
    [InlineData("SELECT groupArraySortedDistinct(100)(host) AS `c0` FROM events_v", "literal 100")]
    public void TheCheckItselfRefusesWhatItShould(string sql, string expected)
    {
        var problem = SqlTextProblem(new TranspileResult(sql, [], [], [], false, 1, Options.RangeFrom, Options.RangeTo));
        Assert.NotNull(problem);
        Assert.Contains(expected, problem, StringComparison.Ordinal);
    }

    /// <summary>What is wrong with a result's SQL text, or null: characters, backquoted identifiers, placeholders.</summary>
    private static string? SqlTextProblem(TranspileResult result)
    {
        var sql = result.Sql;
        var bad = sql.AsSpan().IndexOfAnyExcept(SqlCharacters);
        if (bad >= 0)
        {
            return $"character U+{(int)sql[bad]:X4} at {bad} is not one the transpiler writes";
        }

        if (sql.Count(c => c == '`') % 2 != 0)
        {
            return "an odd number of backquote characters";
        }

        foreach (Match m in Backquoted().Matches(sql))
        {
            var name = m.Groups[1].Value;
            if (!CatalogNames.Contains(name) && !Names.IsGenerated(name))
            {
                return $"identifier `{name}` is neither a catalog field nor a generated alias";
            }
        }

        var bound = result.Parameters.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (Match m in Placeholder().Matches(sql))
        {
            if (!bound.Contains(m.Groups[1].Value))
            {
                return $"placeholder {m.Value} has no parameter";
            }
        }

        var used = Placeholder().Matches(sql).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        if (bound.FirstOrDefault(p => !used.Contains(p)) is { } unused)
        {
            return $"parameter {unused} has no placeholder";
        }

        var text = Backquoted().Replace(Placeholder().Replace(sql, "?"), "?");
        if (text.AsSpan().IndexOfAny('{', '}', '`') >= 0)
        {
            return "a brace or a backquote outside a parameter and an identifier";
        }

        // Every read is of the view, and nothing sets a setting, joins, or names an output format.
        foreach (Match m in AfterFrom().Matches(text))
        {
            if (m.Groups[1].Value != "events_v")
            {
                return $"FROM {m.Groups[1].Value}: every query reads events_v and nothing else";
            }
        }

        if (!text.Contains(" FROM events_v ", StringComparison.Ordinal) && !text.EndsWith(" FROM events_v", StringComparison.Ordinal))
        {
            return "no FROM events_v";
        }

        if (ForbiddenWord().Match(text) is { Success: true } word)
        {
            return $"the word {word.Value}: the transpiler never writes it";
        }

        // A number that stands alone is one of the transpiler's constants: 0 (position(...) > 0, a column whose 0
        // means absent), the values cap plus one, or the index of a rex group right after extractGroups(...).
        foreach (Match m in Number().Matches(text))
        {
            var isIndex = m.Index >= 2 && text[m.Index - 1] == '[' && text[m.Index - 2] == ')' && m.Index + m.Length < text.Length && text[m.Index + m.Length] == ']'
                && int.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture) is >= 1 and <= Re2Syntax.MaxGroups;
            if (m.Value != "0" && m.Value != (Transpiler.ValuesCap + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) && !isIndex)
            {
                return $"the literal {m.Value} at {m.Index}: a value must be a parameter";
            }
        }

        return null;
    }

    [GeneratedRegex(@"\bFROM (\S+)")]
    private static partial Regex AfterFrom();

    [GeneratedRegex(@"\b(SETTINGS|SET|JOIN|FORMAT|INTO|UNION|INSERT|ALTER|DROP|events)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ForbiddenWord();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])[0-9]+(?![A-Za-z0-9_])")]
    private static partial Regex Number();

    /// <summary>
    /// The query with every name and value of the analyst's replaced by another of the same shape: letters moved
    /// one along the alphabet and digits one up, everything else kept, so the lengths, the wildcards and the token boundaries stay.
    /// Keywords, verbs and catalog names are not the analyst's and stay; so does a word whose replacement the
    /// grammar refuses (a relative time, a case id).
    /// </summary>
    private static (string Spl, int Replaced) ReplaceNamesAndValues(string spl, TranspileOptions options)
    {
        // A name can stand in several places (a by field and its sort key, with or without a sign; a rex group
        // and the where that reads it): every occurrence is replaced together, and the lengths never change, so
        // the offsets hold throughout. Of a rex pattern only the group names are names; the pattern is a value
        // whose letters carry meaning to the regex, so it stays.
        var places = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        void Place(string key, int offset)
        {
            if (!places.TryGetValue(key, out var list))
            {
                places[key] = list = [];
            }

            list.Add(offset);
        }

        foreach (var token in Lexer.Tokenize(spl))
        {
            var raw = spl.Substring(token.Span.Offset, token.Span.Length);
            if (token.Kind == TokenKind.Word)
            {
                var bare = raw.TrimStart('-', '+');
                if (bare.Length > 0)
                {
                    Place(bare, token.Span.Offset + raw.Length - bare.Length);
                }
            }
            else if (token.Kind == TokenKind.Quoted)
            {
                var groups = RexGroupName().Matches(raw);
                if (groups.Count == 0)
                {
                    Place(raw, token.Span.Offset);
                }

                foreach (Match m in groups)
                {
                    Place(m.Groups[1].Value, token.Span.Offset + m.Groups[1].Index);
                }
            }
        }

        var replaced = 0;
        var text = spl;
        foreach (var (key, offsets) in places)
        {
            if (IsFixedWord(key))
            {
                continue;
            }

            for (var shift = 1; shift <= 3; shift++)
            {
                var other = Shift(key, shift);
                if (other == key || IsFixedWord(other) || places.ContainsKey(other))
                {
                    continue;
                }

                var candidate = new StringBuilder(text);
                foreach (var start in offsets)
                {
                    candidate.Remove(start, other.Length).Insert(start, other);
                }

                if (Try(candidate.ToString(), options).Result is not null)
                {
                    text = candidate.ToString();
                    replaced++;
                    break;
                }
            }
        }

        return (text, replaced);
    }

    private static bool IsFixedWord(string word)
    {
        var bare = word.TrimStart('-', '+');
        return Reserved.Contains(word) || Reserved.Contains(bare) || FieldCatalog.Embedded.Contains(word) || (bare.Length > 0 && FieldCatalog.Embedded.Contains(bare));
    }

    private static string Shift(string text, int by) => string.Create(text.Length, (text, by), static (span, state) =>
    {
        for (var i = 0; i < span.Length; i++)
        {
            var c = state.text[i];
            span[i] = c switch
            {
                >= 'a' and <= 'z' => (char)('a' + ((c - 'a' + state.by) % 26)),
                >= 'A' and <= 'Z' => (char)('A' + ((c - 'A' + state.by) % 26)),
                >= '0' and <= '9' => (char)('0' + ((c - '0' + state.by) % 10)),
                _ => c,
            };
        }
    });

    private static readonly string[] Templates =
    [
        "{0} | stats count as {1} by {2}",
        "{2}={3} | stats count as {1} by {4}, host | sort -{1}",
        "| stats count by {2} | sort {2}",
        "{2} IN ({3}, {5}) {0} | sort -{2} | head 5",
        "| stats count as {1} by {2}, {4} | sort {4}, -{1} | head 3",
        "\"{6}\" {2}!={3}*",
        "{0} {7} NOT {2}={3} | sort {4}",
        "| stats count as {1}",
        "| rex field={2} \"(?<{1}>x{6})\" | table {1}, {4} | sort {1}",
        "| rex field=raw \"(?<grp>\\w+)\" | where grp={3} | stats count as {1} by grp, {2}",
        "| timechart span=1h count as {1} by {2}",
        "{0} | timechart dc({2}) by {4} span=1d",
        "| stats dc({2}) as {1}, values({4}), avg({2}) by {2} | where {1}>{3} | table {2} {1}",
        "{2}=* | table {2}, {4} | sort {4}",
        "| stats count by {2} | where {2}={3}* | sort -count",
        "| where {2}={3} | stats earliest({4}) as {1} by {2} | where {1}={5}",
    ];

    /// <summary>Characters no template holds: an accepted input with one of them took it from a fill.</summary>
    private static readonly SearchValues<char> NeverInATemplate = SearchValues.Create("\\`'\0\u00e9\u044f{};$%[]:/");

    private static readonly string[] HostilePieces =
    [
        "\\", "`", "'", "\"", "\n", "\0", "é", "я", "/**/", "--", "{", "}", "{p0:String}", ":", ";", "%", "$", "[", "]",
        ".", "-", "*", " ", "|", "(", ")", ",", "=", "\t", "\r", "\\`", "``",
    ];

    private static string Fill(Random random)
    {
        var hostile = random.Next(3) != 0;
        var sb = new StringBuilder();
        var pieces = random.Next(1, 7);
        for (var i = 0; i < pieces; i++)
        {
            var roll = random.Next(10);
            if (hostile && roll < 3)
            {
                sb.Append(HostilePieces[random.Next(HostilePieces.Length)]);
            }
            else if (roll == 9)
            {
                sb.Append((char)('0' + random.Next(10)));
            }
            else
            {
                sb.Append("qxzQXZ_"[random.Next(7)]);
            }
        }

        return sb.ToString();
    }

    private static string Twin(string fill) => string.Create(fill.Length, fill, static (span, text) =>
    {
        for (var i = 0; i < span.Length; i++)
        {
            span[i] = text[i] switch
            {
                'q' => 'x',
                'x' => 'z',
                'z' => 'q',
                'Q' => 'X',
                'X' => 'Z',
                'Z' => 'Q',
                var c => c,
            };
        }
    });

    private static IEnumerable<string> CatalogWords() =>
        FieldCatalog.Embedded.Fields.SelectMany(f => f.Aliases.Append(f.Name));

    private static (TranspileResult? Result, QueryException? Error, Exception? Unexpected) Try(string spl, TranspileOptions? options = null)
    {
        try
        {
            return (Transpiler.Transpile(spl, options ?? Options), null, null);
        }
        catch (QueryException e)
        {
            return (null, e, null);
        }
#pragma warning disable CA1031 // the property under test: nothing but a query error ever leaves the transpiler
        catch (Exception e)
#pragma warning restore CA1031
        {
            return (null, null, e);
        }
    }

    private static TranspileOptions OptionsOf(VectorTests.Vector v)
    {
        var o = v.Options ?? new VectorTests.VectorOptions();
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        return new TranspileOptions(o.Case, DateTimeOffset.Parse(o.From, culture), DateTimeOffset.Parse(o.To, culture), TimeZones.Find(o.Tz), DateTimeOffset.Parse(o.Now, culture))
        {
            AllowedCases = o.Allowed.ToHashSet(),
        };
    }

    private static string Show(string spl) => JsonSerializer.Serialize(spl);
}
