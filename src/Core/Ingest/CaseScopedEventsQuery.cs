using System.Globalization;

namespace Urlog.Core.Ingest;

/// <summary>
/// The only place a <c>SELECT ... FROM events</c> text is composed (design record "Write path"). The query
/// path never reads <c>events</c>: it goes through <c>events_v</c>, which filters on the caller's case list. The
/// write path's <c>urlog_ingest</c> account holds <c>SELECT</c> on the table for the per-case field statistics
///, and a grant cannot say "one case at a time", so the text does: every query built here carries
/// <c>case_id = &lt;n&gt;</c> as its first predicate (the partition key, so it also prunes), and
/// <c>tests/Core.Tests/CaseScopedEventsQueryTests</c> scans <c>src/</c> for any other read of the table.
/// <para>
/// The projection, filter and tail are code-owned fragments (column names from the field catalog, aggregate
/// expressions, <c>GROUP BY</c>/<c>ORDER BY</c>/<c>LIMIT</c>), never analyst text.
/// </para>
/// </summary>
public static class CaseScopedEventsQuery
{
    private static readonly char[] WordBreaks = [' ', '\t', '\r', '\n', '(', ')', ','];

    private static readonly string[] RefusedWords = ["FROM", "JOIN", "UNION", "INTERSECT", "EXCEPT", "WHERE", "PREWHERE", "SETTINGS", "FORMAT", "INTO"];

    /// <summary><c>SELECT projection FROM events WHERE case_id = caseId [AND (filter)] [tail]</c>.</summary>
    public static string Build(ulong caseId, string projection, string? filter = null, string? tail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projection);
        Check(projection, nameof(projection));
        Check(filter, nameof(filter));
        Check(tail, nameof(tail));
        var sql = $"SELECT {projection} FROM events WHERE {Predicate(caseId)}";
        if (!string.IsNullOrWhiteSpace(filter))
        {
            sql += $" AND ({filter})";
        }

        if (!string.IsNullOrWhiteSpace(tail))
        {
            sql += " " + tail;
        }

        return sql;
    }

    /// <summary>
    /// The rows <c>events_v</c> shows: the committed generation of each file, from the newest row per file of
    /// <c>file_generation</c>. The same text as the view's own filter (migration 0006; a test compares the two), so
    /// what the statistics count is what an analyst's query counts. Without it a read of the table would include
    /// the rows of a parse that has not been committed and of one a re-parse has replaced, until their delete has
    /// been applied.
    /// </summary>
    public const string CommittedGenerations =
        "(file_id, parse_generation) IN (SELECT file_id, argMax(current, updated_at) AS visible FROM file_generation GROUP BY file_id HAVING visible > 0)";

    /// <summary>One row per pair of the extras map, as <c>k</c> and <c>v</c>.</summary>
    private const string ExtrasPairs = " ARRAY JOIN mapKeys(extras) AS k, mapValues(extras) AS v";

    /// <summary>
    /// <see cref="Build"/> over the visible rows only, with the statement's own limits:
    /// <c>SELECT projection FROM events [pairs of extras] WHERE case_id = caseId AND (committed generations) [AND (filter)] [tail] [SETTINGS ...]</c>.
    /// The generation filter, the array join and the settings are written here; a fragment still carries no
    /// <c>FROM</c> and no <c>SETTINGS</c> of its own.
    /// </summary>
    public static string BuildVisible(ulong caseId, string projection, string? filter = null, string? tail = null, StatementLimits? limits = null, bool extrasPairs = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projection);
        Check(projection, nameof(projection));
        Check(filter, nameof(filter));
        Check(tail, nameof(tail));
        var sql = $"SELECT {projection} FROM events{(extrasPairs ? ExtrasPairs : string.Empty)} WHERE {Predicate(caseId)} AND {CommittedGenerations}";
        if (!string.IsNullOrWhiteSpace(filter))
        {
            sql += $" AND ({filter})";
        }

        if (!string.IsNullOrWhiteSpace(tail))
        {
            sql += " " + tail;
        }

        if (limits is not null)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaxExecutionSeconds, 1, nameof(limits));
            ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaxThreads, 1, nameof(limits));
            ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaxMemoryBytes, 1, nameof(limits));
            sql += string.Create(CultureInfo.InvariantCulture, $" SETTINGS max_execution_time = {limits.MaxExecutionSeconds}, max_threads = {limits.MaxThreads}, max_memory_usage = {limits.MaxMemoryBytes}");
        }

        return sql;
    }

    /// <summary>The first predicate of every text <see cref="Build"/> emits.</summary>
    public static string Predicate(ulong caseId)
    {
        ArgumentOutOfRangeException.ThrowIfZero(caseId);
        return "case_id = " + caseId.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A fragment brings no second statement, comment, relation of its own (<c>FROM</c>, <c>JOIN</c>, a set
    /// operation), second <c>WHERE</c>, <c>SETTINGS</c>, <c>FORMAT</c> or <c>INTO</c>: each could read rows the case
    /// predicate does not cover, or end the predicate early. An unbalanced parenthesis could close the filter's own.
    /// </summary>
    private static void Check(string? fragment, string name)
    {
        if (string.IsNullOrWhiteSpace(fragment))
        {
            return;
        }

        if (fragment.Contains(';', StringComparison.Ordinal) || fragment.Contains("--", StringComparison.Ordinal) ||
            fragment.Contains("/*", StringComparison.Ordinal) || fragment.Contains('#', StringComparison.Ordinal))
        {
            throw new ArgumentException("a fragment carries no statement separator and no comment", name);
        }

        var depth = 0;
        foreach (var c in fragment)
        {
            depth += c == '(' ? 1 : c == ')' ? -1 : 0;
            if (depth < 0)
            {
                break;
            }
        }

        if (depth != 0)
        {
            throw new ArgumentException("a fragment's parentheses balance", name);
        }

        foreach (var word in fragment.Split(WordBreaks, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var refused in RefusedWords)
            {
                if (word.Equals(refused, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException($"a fragment carries no {refused}", name);
                }
            }
        }
    }
}

/// <summary>What one statement of a maintenance read may use on the server; sent as the statement's own settings.</summary>
public sealed record StatementLimits(int MaxExecutionSeconds, int MaxThreads, long MaxMemoryBytes);
