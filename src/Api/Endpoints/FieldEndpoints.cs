using Urlog.Api.Auth;
using Urlog.Api.Contracts;
using Urlog.Core.Metadata;
using Urlog.Query;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Names = Urlog.Core.Metadata.Names;

namespace Urlog.Api.Endpoints;

/// <summary>Where the fields endpoint gets a case's statistics: the event store as the query account, or a test's stand-in.</summary>
public interface IFieldStatsSource
{
    Task<CaseFieldStats> ReadAsync(ulong caseId, CancellationToken cancellationToken);
}

/// <summary>The statistics through <c>field_stats_v</c> as <c>urlog_query</c>, with the allowed-case list set to the one case asked for.</summary>
public sealed class QueryFieldStatsSource(ClickHouseQueryClient client) : IFieldStatsSource
{
    public Task<CaseFieldStats> ReadAsync(ulong caseId, CancellationToken cancellationToken) => client.FieldStatsAsync(caseId, cancellationToken);
}

/// <summary>
/// <c>GET /api/v1/cases/{caseId}/fields</c>: what autocomplete needs for one case (design record, "Query
/// language", Autocomplete). The catalog's fields with type, description, aliases and the sensitive mark; for each,
/// how many events of the case have it and its most frequent values; the extras keys seen that the catalog does
/// not list; the verbs; the case's span. A member with any role reads it, top values of sensitive fields included:
/// the same member reads those values in the events. The statistics are read for the route's case only, after the
/// membership check, and only while the case is active or closed.
/// </summary>
public static class FieldEndpoints
{
    public static void Map(RouteGroupBuilder oneCase)
    {
        ArgumentNullException.ThrowIfNull(oneCase);
        oneCase.MapGet("/fields", GetAsync).RequireCaseRole(MemberRole.Ro).WithName("GetCaseFields");
    }

    private static async Task<Ok<FieldsResponse>> GetAsync(HttpContext http, MetadataDb db, IFieldStatsSource source, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var @case = http.Features.GetRequiredFeature<CaseContext>().Case;
        var state = await CaseStatsOperations.GetAsync(db, @case.Id, cancellationToken).ConfigureAwait(false);
        CaseFieldStats? stats = null;
        var unavailable = false;
        if (@case.State is CaseState.Active or CaseState.Closed)
        {
            try
            {
                stats = await source.ReadAsync((ulong)@case.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The catalog half of the answer needs no event store; autocomplete works without the counts.
                unavailable = true;
                Log.FieldStatsUnavailable(loggers.CreateLogger(typeof(FieldEndpoints)), @case.Id, e.Message);
            }
        }

        return TypedResults.Ok(Build(FieldCatalog.Embedded, @case, state, stats, unavailable));
    }

    /// <summary>The response from its four sources. Pure, so the shape is tested without a store.</summary>
    internal static FieldsResponse Build(FieldCatalog catalog, CaseRecord @case, CaseStats? state, CaseFieldStats? stats, bool unavailable)
    {
        var have = stats?.ComputedAt is not null;
        var fields = new List<FieldResponse>();
        foreach (var f in catalog.Fields)
        {
            FieldStat? stat = null;
            if (have)
            {
                stats!.Fields.TryGetValue(f.Name, out stat);
            }

            var hasTop = f.TopValues && f.Type is FieldType.Keyword or FieldType.Number or FieldType.Ip or FieldType.Extra;
            // Time and text fields are in every event and are not counted; a counted field the set does not hold is in no event.
            var counted = f.Type is not (FieldType.Time or FieldType.Text);
            fields.Add(new FieldResponse(
                f.Name,
                TypeName(f.Type),
                f.Description,
                f.Aliases,
                f.Sensitive,
                have && counted ? (long)(stat?.Events ?? 0) : null,
                stat?.DistinctEstimate is { } d ? (long)d : null,
                hasTop && f.TopValues ? [.. (stat?.Top ?? []).Select(t => new TopValueResponse(t.Key, (long)t.Value))] : null));
        }

        var keys = have
            ? stats!.Fields.Values.Where(s => !catalog.Contains(s.Field)).OrderByDescending(s => s.Events).ThenBy(s => s.Field, StringComparer.Ordinal).Select(s => new ExtrasKeyResponse(s.Field, (long)s.Events)).ToList()
            : [];

        var stateName = unavailable ? "unavailable"
            : state?.State is CaseStatsState.Skipped or CaseStatsState.TimedOut or CaseStatsState.Failed ? Names.Of(state.State)
            : have ? Names.Of(CaseStatsState.Ok)
            : Names.Of(CaseStatsState.None);
        var statistics = new StatisticsResponse(
            stateName,
            state?.DirtySince is not null,
            have ? new DateTimeOffset(stats!.ComputedAt!.Value, TimeSpan.Zero) : null,
            have ? (long)stats!.Events : null,
            unavailable ? "the event store did not answer" : stateName == Names.Of(CaseStatsState.Ok) ? null : state?.Message);
        return new FieldsResponse(@case.Id, SpanResponse.Of(@case), statistics, fields, keys, Parser.Verbs);
    }

    private static string TypeName(FieldType type) => type switch
    {
        FieldType.Time => "time",
        FieldType.Keyword => "keyword",
        FieldType.Verbatim => "string",
        FieldType.Number => "int",
        FieldType.Ip => "ip",
        FieldType.Text => "text",
        _ => "extra",
    };
}
