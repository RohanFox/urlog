using System.Globalization;
using Urlog.Core.Ingest;

namespace Urlog.Query;

/// <summary>One field of a case's statistics: how many events have it, about how many distinct values, the most frequent values.</summary>
/// <param name="DistinctEstimate">An estimate (<c>uniqCombined</c>); null where none was computed.</param>
/// <param name="Top">Most frequent first, with exact counts; empty when the field has no top values.</param>
public sealed record FieldStat(string Field, ulong Events, ulong? DistinctEstimate, IReadOnlyList<KeyValuePair<string, ulong>> Top);

/// <summary>The newest set of one case; <see cref="ComputedAt"/> is null when the store holds none.</summary>
public sealed record CaseFieldStats(ulong CaseId, DateTime? ComputedAt, ulong Events, IReadOnlyDictionary<string, FieldStat> Fields);

/// <summary>
/// The field statistics as the query account reads them (design record, "Security", query path): through
/// <c>field_stats_v</c>, which shows the cases the request's <c>SQL_allowed_cases</c> lists and nothing else, and
/// with the case named in the text as well. <see cref="Read"/> refuses a result that holds a row of another case,
/// so a change that loses either filter fails here and not in an analyst's browser.
/// </summary>
public static class FieldStats
{
    /// <summary>The one text that reads the statistics. The case is a bound parameter.</summary>
    public const string Sql =
        "SELECT case_id, field, value, count, distinct_estimate, toString(computed_at) FROM field_stats_v WHERE case_id = {case:UInt64} ORDER BY field ASC, count DESC, value ASC";

    /// <summary>
    /// The fields the statistics task covers, from the catalog: keyword, int and ip columns with top values,
    /// string columns for presence only, extras keys. Time and text fields are left out: every event has them.
    /// </summary>
    public static IReadOnlyList<StatsField> TaskFields(FieldCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var fields = new List<StatsField>();
        foreach (var f in catalog.Fields)
        {
            switch (f.Type)
            {
                case FieldType.Keyword:
                    fields.Add(Column(f, f.Column!, f.TopValues));
                    break;
                case FieldType.Number:
                    fields.Add(Column(f, $"toString({f.Column})", f.TopValues));
                    break;
                case FieldType.Ip:
                    // The text an analyst types and the query output shows: a mapped address as IPv4.
                    fields.Add(Column(f, $"replaceRegexpOne(toString({f.Column}), '^::ffff:', '')", f.TopValues));
                    break;
                case FieldType.Verbatim:
                    fields.Add(Column(f, null, false));
                    break;
                case FieldType.Extra:
                    fields.Add(new StatsField(f.Name, StatsFieldKind.Extra, f.ExtraKey!, null, null, f.TopValues).Checked());
                    break;
                default:
                    break;
            }
        }

        return fields;

        static StatsField Column(FieldDef f, string? valueText, bool top) =>
            new StatsField(f.Name, StatsFieldKind.Column, f.Column!, valueText, FieldDef.HasValue(f.Type, f.ZeroIsAbsent, f.Column!, "'::'"), top).Checked();
    }

    /// <summary>Rows of <see cref="Sql"/> as one case's statistics.</summary>
    /// <exception cref="InvalidOperationException">A row belongs to another case, or the rows carry two <c>computed_at</c> values.</exception>
    public static CaseFieldStats Read(ulong caseId, IReadOnlyList<string[]> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        DateTime? computedAt = null;
        ulong events = 0;
        var presence = new Dictionary<string, (ulong Events, ulong Distinct)>(StringComparer.Ordinal);
        var top = new Dictionary<string, List<KeyValuePair<string, ulong>>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (ulong.Parse(row[0], CultureInfo.InvariantCulture) != caseId)
            {
                throw new InvalidOperationException($"the field statistics of case {caseId} came back with a row of case {row[0]}");
            }

            var at = DateTime.SpecifyKind(DateTime.ParseExact(row[5], "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture), DateTimeKind.Utc);
            if (computedAt is { } seen && seen != at)
            {
                throw new InvalidOperationException($"the field statistics of case {caseId} came back as two sets ({seen:O} and {at:O})");
            }

            computedAt = at;
            var (field, value, count) = (row[1], row[2], ulong.Parse(row[3], CultureInfo.InvariantCulture));
            if (field.Length == 0)
            {
                events = count;
            }
            else if (value.Length == 0)
            {
                presence[field] = (count, ulong.Parse(row[4], CultureInfo.InvariantCulture));
            }
            else
            {
                if (!top.TryGetValue(field, out var list))
                {
                    top[field] = list = [];
                }

                list.Add(new(value, count));
            }
        }

        var fields = new Dictionary<string, FieldStat>(StringComparer.Ordinal);
        foreach (var (field, p) in presence)
        {
            fields[field] = new FieldStat(field, p.Events, p.Distinct == 0 ? null : p.Distinct, top.TryGetValue(field, out var list) ? list : []);
        }

        return new CaseFieldStats(caseId, computedAt, events, fields);
    }
}
