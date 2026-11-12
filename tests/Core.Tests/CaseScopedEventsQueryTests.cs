using System.Text.RegularExpressions;
using Urlog.Core.Ingest;

namespace Urlog.Core.Tests;

/// <summary>
/// <c>urlog_ingest</c> holds <c>SELECT</c> on <c>events</c> for the per-case field statistics and the
/// grant cannot say "one case". <see cref="CaseScopedEventsQuery"/> is the one place such a text is composed; these
/// tests pin that every text it can emit starts its <c>WHERE</c> with the case, and that nothing else under
/// <c>src/</c> reads the table.
/// </summary>
public sealed partial class CaseScopedEventsQueryTests
{
    public static TheoryData<string, string?, string?> Shapes => new()
    {
        { "count()", null, null },
        { "host, count() AS c", null, "GROUP BY host ORDER BY c DESC LIMIT 100" },
        { "toString(http_status) AS v, count() AS c", "http_status != 0", "GROUP BY v ORDER BY c DESC LIMIT 100" },
        { "arrayJoin(mapKeys(extras)) AS k, count() AS c", "notEmpty(extras)", "GROUP BY k" },
        { "min(ts), max(ts)", "file_id = 7 AND (parse_generation = 2 OR parse_generation = 3)", null },
        { "uniq(user_name)", "user_name != '' OR 1", "LIMIT 1" },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void EveryTextCarriesTheCaseAsItsFirstPredicate(string projection, string? filter, string? tail)
    {
        foreach (var caseId in new ulong[] { 1, 7, 18_446_744_073_709_551_615 })
        {
            var sql = CaseScopedEventsQuery.Build(caseId, projection, filter, tail);
            var head = $"SELECT {projection} FROM events WHERE case_id = {caseId}";
            Assert.StartsWith(head, sql, StringComparison.Ordinal);

            // What follows the case predicate is ANDed with it as one parenthesised term, or is the tail: an OR in
            // the filter cannot escape the case.
            var rest = sql[head.Length..];
            Assert.True(rest.Length == 0 || rest.StartsWith(" AND (", StringComparison.Ordinal) || rest == " " + tail, rest);
            Assert.Single(FromEvents().Matches(sql));
            Assert.Single(Where().Matches(sql));
        }
    }

    [Theory]
    [InlineData("count()", "1) OR (1", null)]
    [InlineData("count()", "1 OR case_id IN (SELECT case_id FROM events)", null)]
    [InlineData("count()", null, "UNION ALL SELECT count() FROM events")]
    [InlineData("count()", null, "SETTINGS max_threads = 1")]
    [InlineData("count()", null, "; DROP TABLE events")]
    [InlineData("count()", "1 -- x", null)]
    [InlineData("count()", "1 /* x */", null)]
    [InlineData("count() FROM events WHERE 1 OR 1", null, null)]
    [InlineData("count()", null, "WHERE 1")]
    [InlineData("count()", null, "INTO OUTFILE 'x'")]
    [InlineData("count()", null, "FORMAT Native")]
    [InlineData(" ", null, null)]
    public void AFragmentCannotWidenTheRead(string projection, string? filter, string? tail) =>
        Assert.ThrowsAny<ArgumentException>(() => CaseScopedEventsQuery.Build(1, projection, filter, tail));

    [Fact]
    public void CaseZeroIsRefused() => Assert.Throws<ArgumentOutOfRangeException>(() => CaseScopedEventsQuery.Build(0, "count()"));

    [Fact]
    public void NothingElseUnderSrcReadsTheEventsTable()
    {
        // A read of the table is "FROM events" or "JOIN events" as a whole word (events_v is another word) in a
        // code line. The allowed ones: the builder itself, and the lightweight DELETE FROM events of the re-parse.
        var src = Path.Combine(RepoRoot, "src");
        var offenders = new List<string>();
        var files = 0;
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(src, file).Replace('\\', '/');
            if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal))
            {
                continue;
            }

            files++;
            var number = 0;
            foreach (var line in File.ReadLines(file))
            {
                number++;
                var code = line.TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal))
                {
                    continue; // comments (and doc comments) describe; they do not query
                }

                foreach (Match m in ReadsEvents().Matches(line))
                {
                    var isDelete = m.Index >= 7 && line.AsSpan(m.Index - 7, 7).Equals("DELETE ", StringComparison.OrdinalIgnoreCase);
                    if (relative != "Core/Ingest/CaseScopedEventsQuery.cs" && !isDelete)
                    {
                        offenders.Add($"{relative}:{number}: {code}");
                    }
                }
            }
        }

        Assert.True(files > 50, $"scanned only {files} files under {src}");
        Assert.True(offenders.Count == 0, "SELECT ... FROM events outside CaseScopedEventsQuery:\n" + string.Join("\n", offenders));
    }

    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "urlog.sln")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? throw new InvalidOperationException("urlog.sln not found above the test binary");
        }
    }

    [GeneratedRegex(@"\bFROM events\b(?!_)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FromEvents();

    [GeneratedRegex(@"\bWHERE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Where();

    [GeneratedRegex(@"\b(FROM|JOIN)\s+(default\.)?`?events`?(?![\w.])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReadsEvents();
}
