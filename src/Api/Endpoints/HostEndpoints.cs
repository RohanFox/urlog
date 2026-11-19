using System.Text.Json;
using Urlog.Api.Auth;
using Urlog.Api.Contracts;
using Urlog.Core.Metadata;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Api.Endpoints;

/// <summary>
/// <c>/api/v1/cases/{caseId}/hosts</c>: the hosts of a case with what their files add up to; create, change
/// (name, zone, default source options) and merge for owners and read-write members. A change that would have
/// files parsed again answers first with what it costs and changes nothing; the same request with
/// <c>?confirm=true</c> makes it.
/// </summary>
public static class HostEndpoints
{
    public static void Map(RouteGroupBuilder oneCase)
    {
        ArgumentNullException.ThrowIfNull(oneCase);
        oneCase.MapGet("/hosts", ListAsync).RequireCaseRole(MemberRole.Ro).WithName("ListCaseHosts");
        oneCase.MapPost("/hosts", CreateAsync).RequireCaseRole(MemberRole.Rw).WithName("CreateCaseHost");
        oneCase.MapPatch("/hosts/{hostId:long}", UpdateAsync).RequireCaseRole(MemberRole.Rw).WithName("UpdateCaseHost");
        oneCase.MapPost("/hosts/{hostId:long}/merge", MergeAsync).RequireCaseRole(MemberRole.Rw).WithName("MergeCaseHost");
    }

    private static async Task<Ok<List<HostResponse>>> ListAsync(long caseId, MetadataDb db, CancellationToken cancellationToken)
    {
        var totals = await TotalsAsync(db, caseId, cancellationToken).ConfigureAwait(false);
        var hosts = await db.Hosts.AsNoTracking().Where(h => h.CaseId == caseId).OrderBy(h => h.Name).ToListAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(hosts.Select(h => CaseViews.Host(h, totals.GetValueOrDefault(h.Id))).ToList());
    }

    private static async Task<Created<HostResponse>> CreateAsync(long caseId, JsonElement body, CurrentUser me, MetadataDb db, MetadataStore store, SourceRules rules, TimeProvider clock, CancellationToken cancellationToken)
    {
        Body.Object(body);
        var host = await HostOperations.CreateAsync(db, store.Audit, me.Actor, rules, caseId, Body.RequiredText(body, "name"), Body.OptionalText(body, "timezone"), Body.Options(body, "source_options").Value, clock, cancellationToken).ConfigureAwait(false);
        return TypedResults.Created($"/api/v1/cases/{caseId}/hosts/{host.Id}", CaseViews.Host(host, null));
    }

    private static async Task<IResult> UpdateAsync(long caseId, long hostId, JsonElement body, HttpContext http, CurrentUser me, MetadataDb db, MetadataStore store, SourceRules rules, TimeProvider clock, CancellationToken cancellationToken)
    {
        Body.Object(body);
        var version = Preconditions.Version(http, $"host {hostId}");
        var name = Body.Text(body, "name");
        var zone = Body.Text(body, "timezone");
        if ((name.IsSet && name.Value is null) || (zone.IsSet && zone.Value is null))
        {
            throw new ArgumentException("name, timezone: a host has both; leave a member out to keep it");
        }

        var edit = new HostEdit(new Change<string>(name.IsSet, name.Value ?? string.Empty), new Change<string>(zone.IsSet, zone.Value ?? string.Empty), Body.Options(body, "source_options"));
        var change = await HostOperations.UpdateAsync(db, store.Audit, me.Actor, rules, caseId, hostId, version, edit, Preconditions.Confirmed(http), clock, cancellationToken).ConfigureAwait(false);
        var totals = await TotalsAsync(db, caseId, cancellationToken).ConfigureAwait(false);
        var response = new HostChangeResponse(change.Applied, change.Changed, CaseViews.Host(change.Host, totals.GetValueOrDefault(hostId)), new ReparseResponse(change.Cost.Files, change.Cost.Bytes), change.Jobs);
        http.Response.Headers.ETag = Preconditions.Tag(change.Host.Version);
        return change.Applied && change.Jobs > 0 ? TypedResults.Accepted($"/api/v1/cases/{caseId}/jobs", response) : TypedResults.Ok(response);
    }

    private static async Task<Ok<HostMergeResponse>> MergeAsync(long caseId, long hostId, JsonElement body, HttpContext http, CurrentUser me, MetadataDb db, MetadataStore store, TimeProvider clock, CancellationToken cancellationToken)
    {
        Body.Object(body);
        var version = Preconditions.Version(http, $"host {hostId}");
        var into = Body.OptionalLong(body, "into") ?? throw new ArgumentException("into: the id of the host that stays");
        var (survivor, moved) = await HostOperations.MergeAsync(db, store.Audit, me.Actor, caseId, hostId, version, into, clock, cancellationToken).ConfigureAwait(false);
        var totals = await TotalsAsync(db, caseId, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new HostMergeResponse(CaseViews.Host(survivor, totals.GetValueOrDefault(survivor.Id)), moved));
    }

    /// <summary>Per host: its files, and the rows, rejects and span of those whose parse is committed.</summary>
    internal static async Task<Dictionary<long, HostTotals>> TotalsAsync(MetadataDb db, long caseId, CancellationToken cancellationToken)
    {
        var counts = await db.Files.AsNoTracking().Where(f => f.CaseId == caseId && f.HostId != null)
            .GroupBy(f => f.HostId!.Value).Select(g => new { HostId = g.Key, Files = g.Count() }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var parsed = await db.Files.AsNoTracking().Where(f => f.CaseId == caseId && f.HostId != null && f.State == FileState.Parsed)
            .GroupBy(f => f.HostId!.Value).Select(g => new { HostId = g.Key, Rows = g.Sum(f => f.RowCount), Rejects = g.Sum(f => f.RejectCount), Min = g.Min(f => f.TsMin), Max = g.Max(f => f.TsMax) })
            .ToDictionaryAsync(g => g.HostId, cancellationToken).ConfigureAwait(false);
        return counts.ToDictionary(c => c.HostId, c => parsed.TryGetValue(c.HostId, out var p)
            ? new HostTotals(c.HostId, c.Files, p.Rows, p.Rejects, p.Min, p.Max)
            : new HostTotals(c.HostId, c.Files, 0, 0, null, null));
    }
}
