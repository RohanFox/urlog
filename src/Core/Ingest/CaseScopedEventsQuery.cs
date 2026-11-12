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
