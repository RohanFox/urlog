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

    [Theory]
    [MemberData(nameof(Shapes))]
    public void AReadOfTheVisibleRowsCarriesTheCaseThenTheCommittedGenerations(string projection, string? filter, string? tail)
    {
        var limits = new StatementLimits(37, 4, 2_000_000_000);
        foreach (var caseId in new ulong[] { 1, 7, 18_446_744_073_709_551_615 })
        {
            var sql = CaseScopedEventsQuery.BuildVisible(caseId, projection, filter, tail, limits);
            var head = $"SELECT {projection} FROM events WHERE case_id = {caseId} AND {CaseScopedEventsQuery.CommittedGenerations}";
            Assert.StartsWith(head, sql, StringComparison.Ordinal);
            const string settings = " SETTINGS max_execution_time = 37, max_threads = 4, max_memory_usage = 2000000000";
            Assert.EndsWith(settings, sql, StringComparison.Ordinal);
            var rest = sql[head.Length..^settings.Length];
            Assert.True(rest.Length == 0 || rest.StartsWith(" AND (", StringComparison.Ordinal) || rest == " " + tail, rest);
            Assert.Single(FromEvents().Matches(sql));
            Assert.Single(Where().Matches(sql));

            var pairs = CaseScopedEventsQuery.BuildVisible(caseId, "k, count() AS c", "notEmpty(v)", "GROUP BY k", extrasPairs: true);
            Assert.StartsWith($"SELECT k, count() AS c FROM events ARRAY JOIN mapKeys(extras) AS k, mapValues(extras) AS v WHERE case_id = {caseId} AND {CaseScopedEventsQuery.CommittedGenerations} AND (notEmpty(v))", pairs, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("count()", "1) OR (1", null)]
    [InlineData("count()", "1 OR case_id IN (SELECT case_id FROM events)", null)]
    [InlineData("count()", null, "UNION ALL SELECT count() FROM events")]
    [InlineData("count()", null, "SETTINGS max_execution_time = 0")]
    [InlineData("count()", "1 -- x", null)]
    public void AFragmentCannotWidenAReadOfTheVisibleRows(string projection, string? filter, string? tail) =>
        Assert.ThrowsAny<ArgumentException>(() => CaseScopedEventsQuery.BuildVisible(1, projection, filter, tail, new StatementLimits(1, 1, 1)));

    [Fact]
    public void TheGenerationFilterIsTheViewsOwnText()
    {
        // events_v is defined by migration 0006. The statistics count the rows that view shows, so the filter the
        // builder writes is the one in that file, compared with all whitespace runs taken as one space.
        var migration = File.ReadAllText(Path.Combine(RepoRoot, "migrations", "clickhouse", "0006_events_v_hide_uncommitted.sql"));
        Assert.Contains(Squeeze(CaseScopedEventsQuery.CommittedGenerations), Squeeze(migration), StringComparison.Ordinal);

        static string Squeeze(string text) => Spaces().Replace(text, " ").Replace("( ", "(", StringComparison.Ordinal).Replace(" )", ")", StringComparison.Ordinal);
    }

    [Fact]
    public void AStatisticsFieldNamesNothingButAColumnOrAKey()
    {
        Assert.Throws<ArgumentException>(() => new StatsField("host.name", StatsFieldKind.Column, "host FROM events", "host", null, true).Checked());
        Assert.Throws<ArgumentException>(() => new StatsField("x", StatsFieldKind.Extra, "k') OR ('1", null, null, true).Checked());
        Assert.Throws<ArgumentException>(() => new StatsField("bad name", StatsFieldKind.Column, "host", "host", null, true).Checked());
        Assert.Throws<ArgumentException>(() => new StatsField("host.name", StatsFieldKind.Column, "host", null, null, true).Checked());
        Assert.NotNull(new StatsField("network.forwarded_ip", StatsFieldKind.Extra, "network.forwarded_ip", null, null, true).Checked());
    }

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

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\b(FROM|JOIN)\s+(default\.)?`?events`?(?![\w.])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReadsEvents();
}
