using Urlog.Api.Auth;
using Urlog.Api.Contracts;
using Urlog.Core.Metadata;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace Urlog.Api.Endpoints;

/// <summary>
/// The state of a case, changed by its owner (design record, "Case lifecycle"): close, reopen, archive, re-import,
/// soft-delete with the typed name, restore. Every handler calls <see cref="LifecycleOperations"/> or
/// <see cref="ArchiveOperations"/> as the session's user and does nothing else: the operation checks the role, the
/// state and what the case holds under the write lock and writes the audit row in the same transaction. A refusal
/// is 409 with the operation's own message. Purge is an administrator's and lives under <c>/admin/cases</c>.
/// </summary>
public static class LifecycleEndpoints
{
    public static void Map(RouteGroupBuilder oneCase)
    {
        ArgumentNullException.ThrowIfNull(oneCase);
        oneCase.MapPost("/close", CloseAsync).RequireCaseRole(MemberRole.Owner).WithName("CloseCase");
        oneCase.MapPost("/reopen", ReopenAsync).RequireCaseRole(MemberRole.Owner).WithName("ReopenCase");
        oneCase.MapPost("/archive", ArchiveAsync).RequireCaseRole(MemberRole.Owner).WithName("ArchiveCase");
        oneCase.MapPost("/reimport", ReimportAsync).RequireCaseRole(MemberRole.Owner).WithName("ReimportCase");
        oneCase.MapPost("/delete", DeleteAsync).RequireCaseRole(MemberRole.Owner).WithName("SoftDeleteCase");
        // The one case route that reaches a soft-deleted case, and only for an owner of it.
        oneCase.MapPost("/restore", RestoreAsync).RequireCaseRoleOnDeleted(MemberRole.Owner).WithName("RestoreCase");
    }

    private static async Task<Ok<CaseDetailResponse>> CloseAsync(long caseId, HttpContext http, CurrentUser me, MetadataDb db, MetadataStore store, TimeProvider clock, CancellationToken cancellationToken)
    {
        var @case = await LifecycleOperations.CloseAsync(db, store.Audit, me.Actor, caseId, clock, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(await CaseViews.CaseAsync(db, @case, Role(http), cancellationToken).ConfigureAwait(false));
    }

    private static async Task<Ok<CaseDetailResponse>> ReopenAsync(long caseId, HttpContext http, CurrentUser me, MetadataDb db, MetadataStore store, TimeProvider clock, CancellationToken cancellationToken)
    {
        var @case = await LifecycleOperations.ReopenAsync(db, store.Audit, me.Actor, caseId, clock, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(await CaseViews.CaseAsync(db, @case, Role(http), cancellationToken).ConfigureAwait(false));
    }

    /// <summary>202: the archive is asked for and the runner writes it. The audit row is the commit's, which names who asked.</summary>
    private static async Task<Results<Accepted<CaseTaskResponse>, ProblemHttpResult>> ArchiveAsync(long caseId, CurrentUser me, MetadataDb db, IOptions<UrlogOptions> options, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.Lifecycle.ArchiveRoot))
        {
            return TypedResults.Problem(Problems.Conflict("this deployment archives nothing: no archive root is configured (Urlog:Lifecycle:ArchiveRoot)"));
        }

        var task = await ArchiveOperations.RequestArchiveAsync(db, me.Actor, caseId, clock, cancellationToken).ConfigureAwait(false);
        return TypedResults.Accepted($"/api/v1/cases/{caseId}", CaseViews.Task(task)!);
    }

    private static async Task<Results<Accepted<CaseTaskResponse>, ProblemHttpResult>> ReimportAsync(long caseId, CurrentUser me, MetadataDb db, IOptions<UrlogOptions> options, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.Lifecycle.ArchiveRoot))
        {
            return TypedResults.Problem(Problems.Conflict("this deployment has no archive root to re-import from (Urlog:Lifecycle:ArchiveRoot)"));
        }

        var task = await ArchiveOperations.RequestReimportAsync(db, me.Actor, caseId, clock, cancellationToken).ConfigureAwait(false);
        return TypedResults.Accepted($"/api/v1/cases/{caseId}", CaseViews.Task(task)!);
    }

    /// <summary>The case is hidden from here on; its owner finds it under <c>GET /cases?state=deleted</c> until it is purged.</summary>
    private static async Task<NoContent> DeleteAsync(long caseId, DeleteCaseRequest request, CurrentUser me, MetadataDb db, MetadataStore store, IOptions<UrlogOptions> options, TimeProvider clock, CancellationToken cancellationToken)
    {
        await LifecycleOperations.SoftDeleteAsync(db, store.Audit, me.Actor, caseId, request.Name, options.Value.Lifecycle.PurgeAfter, clock, cancellationToken).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<CaseDetailResponse>> RestoreAsync(long caseId, HttpContext http, CurrentUser me, MetadataDb db, MetadataStore store, TimeProvider clock, CancellationToken cancellationToken)
    {
        var @case = await LifecycleOperations.RestoreAsync(db, store.Audit, me.Actor, caseId, clock, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(await CaseViews.CaseAsync(db, @case, Role(http), cancellationToken).ConfigureAwait(false));
    }

    private static MemberRole Role(HttpContext http) => http.Features.GetRequiredFeature<CaseContext>().Role;
}
