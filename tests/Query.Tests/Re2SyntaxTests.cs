using Urlog.Query;

namespace Urlog.Query.Tests;

/// <summary>
/// The pattern checker on its own. The vectors in <c>rex.json</c> cover it through the parser, with offsets into the
/// query; these pin the offsets inside the pattern, the group list and the limits.
/// </summary>
public sealed class Re2SyntaxTests
{
    [Theory]
    [InlineData(@"(?<user>\w+) from (?<ip>\S+)", "user:1,ip:2")]
    [InlineData(@"(?P<a>x)(y)(?:z)(?<b>w)", "a:1,:2,b:3")]
    [InlineData(@"((?<inner>a)|b)+", ":1,inner:2")]
    [InlineData(@"^(?i:get|post)\s+(?<path>[^\s?]+)(?:\?(?<query>\S*))?$", "path:1,query:2")]
    [InlineData(@"(?<a>[]x-z\]\d[:alpha:][:^digit:]]+)", "a:1")]
    [InlineData(@"(?<a>\Qa.b(c\E)", "a:1")]
    [InlineData(@"(?<a>x{2}y{2,}z{2,5}?)", "a:1")]
    [InlineData(@"(?<a>x{,5}|y{z}|{)", "a:1")] // a brace that is not a repetition is a literal, as in RE2
    [InlineData(@"(?s-m)(?<a>.)\A\z\b\B", "a:1")]
    public void AcceptedPatternsAndTheirGroups(string pattern, string groups)
    {
        var found = Re2Syntax.Check(pattern);
        Assert.Equal(groups, string.Join(",", found.Select(g => $"{g.Name}:{g.Index}")));
        foreach (var g in found.Where(g => g.Name is not null))
        {
            Assert.Equal(g.Name, pattern.Substring(g.NameOffset, g.NameLength));
        }
    }

    [Theory]
    [InlineData(@"(?<a>x)(?=y)", 7, 3, "lookahead")]
    [InlineData(@"(?<a>x)(?!y)", 7, 3, "lookahead")]
    [InlineData(@"(?<=y)(?<a>x)", 0, 4, "lookbehind")]
    [InlineData(@"(?<a>x)\1", 7, 2, "backreferences")]
    [InlineData(@"(?<a>x)(?P=a)", 7, 4, "backreferences")]
    [InlineData(@"(?<a>x)\k<a>", 7, 2, "backreferences")]
    [InlineData(@"(?>x)", 0, 3, "atomic")]
    [InlineData(@"x++", 1, 2, "possessive")]
    [InlineData(@"x?+", 1, 2, "possessive")]
    [InlineData(@"(?(1)a|b)", 0, 3, "conditionals")]
    [InlineData(@"(?#c)x", 0, 3, "comments")]
    [InlineData(@"(?x)a", 0, 3, "group syntax")]
    [InlineData(@"(?R)", 0, 3, "group syntax")]
    [InlineData(@"a\Zb", 1, 2, "not an RE2 escape")]
    [InlineData(@"a\Cb", 1, 2, "not an RE2 escape")]
    [InlineData(@"[a\b]", 2, 2, "not an RE2 escape")]
    [InlineData(@"\xZZ", 0, 4, "hex")]
    [InlineData(@"\x{110000}", 0, 10, "hex")]
    [InlineData(@"\p{}", 0, 4, "class")]
    [InlineData(@"x{101}", 1, 5, "at most 100")]
    [InlineData(@"x{2,101}", 1, 7, "at most 100")]
    [InlineData(@"(x{2}){2}", 0, 9, "inside another")]
    [InlineData(@"(?:(?:x{2})+y){3}", 0, 17, "inside another")]
    [InlineData(@"x**", 2, 1, "second repetition")]
    [InlineData(@"+x", 0, 1, "nothing to repeat")]
    [InlineData(@"(|*)", 2, 1, "nothing to repeat")]
    [InlineData(@"x)", 1, 1, "unmatched")]
    [InlineData(@"(x", 0, 1, "missing ')'")]
    [InlineData(@"[x", 0, 1, "missing ']'")]
    [InlineData(@"(?<a>x)(?<A>y)", 10, 1, "used twice")]
    [InlineData(@"(?<a-b>x)", 3, 3, "not a group name")]
    [InlineData(@"x\", 1, 1, "end of the pattern")]
    [InlineData("", 0, 0, "empty")]
    public void RefusedPatternsPointAtTheConstruct(string pattern, int offset, int length, string message)
    {
        var e = Assert.Throws<Re2SyntaxException>(() => Re2Syntax.Check(pattern));
        Assert.Contains(message, e.Message, StringComparison.Ordinal);
        Assert.Equal((offset, length), (e.Offset, e.Length));
    }

    [Fact]
    public void TheLimits()
    {
        Assert.Equal(16, Re2Syntax.Check(string.Concat(Enumerable.Repeat("(x)", Re2Syntax.MaxGroups))).Count);
        Assert.Contains("capture groups", Assert.Throws<Re2SyntaxException>(() => Re2Syntax.Check(string.Concat(Enumerable.Repeat("(x)", Re2Syntax.MaxGroups + 1)))).Message, StringComparison.Ordinal);

        Re2Syntax.Check(new string('x', Re2Syntax.MaxPatternBytes));
        Assert.Contains("longer than", Assert.Throws<Re2SyntaxException>(() => Re2Syntax.Check(new string('x', Re2Syntax.MaxPatternBytes + 1))).Message, StringComparison.Ordinal);
        Assert.Contains("longer than", Assert.Throws<Re2SyntaxException>(() => Re2Syntax.Check(new string('é', (Re2Syntax.MaxPatternBytes / 2) + 1))).Message, StringComparison.Ordinal);

        Re2Syntax.Check(new string('(', Re2Syntax.MaxNesting) + "x" + new string(')', Re2Syntax.MaxNesting));
        Assert.Contains("nested", Assert.Throws<Re2SyntaxException>(() => Re2Syntax.Check(new string('(', Re2Syntax.MaxNesting + 1) + "x" + new string(')', Re2Syntax.MaxNesting + 1))).Message, StringComparison.Ordinal);
    }
}
