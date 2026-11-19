using Urlog.Api.Auth;
using Urlog.Api.Contracts;
using Urlog.Core.Metadata;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Urlog.Api.Endpoints;

/// <summary>
/// <c>/api/v1/admin/cases</c>, admin only: the admin's explicit, audited grant path. The list is metadata and the member
/// list of every case, never events or field statistics; the member routes write the same <c>case_share</c> /
/// <c>case_role_change</c> / <c>case_unshare</c> rows as the owner's, with <c>by_admin: true</c>, targeting the case so
/// they appear in its own trail. An admin reads a case's content only after a membership exists.
/// </summary>
public static class AdminCaseEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var group = api.MapGroup("/admin/cases").RequireAuthorization(ApiHost.AdminPolicy);
        group.MapGet("", ListAsync).WithName("AdminListCases");
        group.MapPost("/{id:long}/members", AddMemberAsync).WithName("AdminAddCaseMember");
        group.MapPut("/{id:long}/members/{userId:long}", ChangeMemberRoleAsync).WithName("AdminChangeCaseMemberRole");
        group.MapDelete("/{id:long}/members/{userId:long}", RemoveMemberAsync).WithName("AdminRemoveCaseMember");
        group.MapPost("/{id:long}/delete", DeleteAsync).WithName("AdminSoftDeleteCase");
        group.MapPost("/{id:long}/restore", RestoreAsync).WithName("AdminRestoreCase");
        group.MapPost("/{id:long}/purge", PurgeAsync).WithName("AdminPurgeCase");
        group.MapPost("/{id:long}/purge/confirm", ConfirmPurgeAsync).WithName("AdminConfirmCasePurge");
    }

    /// <summary>An administrator soft-deletes a case, typing its name like an owner does; the row says <c>by_admin</c>.</summary>
    private static async Task<NoContent> DeleteAsync(long id, DeleteCaseRequest request, CurrentUser me, MetadataDb db, MetadataStore store, IOptions<UrlogOptions> options, TimeProvider clock, CancellationToken cancellationToken)
    {
        await LifecycleOperations.SoftDeleteAsync(db, store.Audit, me.Actor, id, request.Name, options.Value.Lifecycle.PurgeAfter, clock, cancellationToken).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> RestoreAsync(long id, CurrentUser me, MetadataDb db, MetadataStore store, TimeProvider clock, CancellationToken cancellationToken)
    {
        await LifecycleOperations.RestoreAsync(db, store.Audit, me.Actor, id, clock, cancellationToken).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// Asks for the purge of a soft-deleted case. With two-person approval off (the default) this releases it and
    /// the runner carries it out; with it on, this leaves a request that another administrator confirms. It never
    /// confirms a request that waits. 202 with the task either way.
    /// </summary>
    private static async Task<Accepted<CaseTaskResponse>> PurgeAsync(long id, CurrentUser me, MetadataDb db, MetadataStore store, IOptions<UrlogOptions> options, TimeProvider clock, CancellationToken cancellationToken)
    {
        var task = await LifecycleOperations.RequestPurgeAsync(db, store.Audit, me.Actor, id, options.Value.Lifecycle.TwoPersonPurge, PurgeIntent.Request, clock, cancellationToken).ConfigureAwait(false);
        return TypedResults.Accepted("/api/v1/admin/cases", CaseViews.Task(task)!);
    }

    /// <summary>The second administrator's confirmation of a purge another one asked for. Refused for the one who asked, and while two-person approval is off.</summary>
    private static async Task<Accepted<CaseTaskResponse>> ConfirmPurgeAsync(long id, CurrentUser me, MetadataDb db, MetadataStore store, IOptions<UrlogOptions> options, TimeProvider clock, CancellationToken cancellationToken)
    {
        var task = await LifecycleOperations.RequestPurgeAsync(db, store.Audit, me.Actor, id, options.Value.Lifecycle.TwoPersonPurge, PurgeIntent.Confirm, clock, cancellationToken).ConfigureAwait(false);
        return TypedResults.Accepted("/api/v1/admin/cases", CaseViews.Task(task)!);
    }

    private static async Task<Ok<List<AdminCaseResponse>>> ListAsync(MetadataDb db, CancellationToken cancellationToken)
    {
        var cases = await db.Cases.AsNoTracking().OrderBy(c => c.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var owners = await CaseEndpoints.OwnerNamesAsync(db, cases.Select(c => c.OwnerId), cancellationToken).ConfigureAwait(false);
        var members = await db.CaseMembers.AsNoTracking()
            .Join(db.Users.AsNoTracking(), m => m.UserId, u => u.Id, (m, u) => new { m.CaseId, m.UserId, u.Name, m.Role, m.GrantedAt })
            .OrderBy(x => x.UserId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var byCase = members.ToLookup(m => m.CaseId);
        var tasks = (await db.CaseTasks.AsNoTracking()
            .Where(t => t.State == CaseTaskState.Requested || t.State == CaseTaskState.Running || t.State == CaseTaskState.Committed)
            .OrderBy(t => t.Id).ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(t => t.CaseId).ToDictionary(g => g.Key, g => g.First());
        return TypedResults.Ok(cases.Select(c => new AdminCaseResponse(
            c.Id, c.Name, c.State, owners.GetValueOrDefault(c.OwnerId, "?"),
            UserResponse.Utc(c.CreatedAt)!.Value, UserResponse.Utc(c.ClosedAt), UserResponse.Utc(c.ArchivedAt), UserResponse.Utc(c.DeletedAt),
            byCase[c.Id].Select(m => new MemberResponse(m.UserId, m.Name, m.Role, UserResponse.Utc(m.GrantedAt)!.Value)).ToList(),
            CaseViews.Task(tasks.GetValueOrDefault(c.Id)))).ToList());
    }

    private static Task<Results<Created<MemberResponse>, ProblemHttpResult>> AddMemberAsync(long id, AddMemberRequest request, CurrentUser me, MetadataDb db, MetadataStore store, TimeProvider clock, CancellationToken cancellationToken) =>
        CaseEndpoints.AddMemberCoreAsync(id, request, me, byAdmin: true, db, store, clock, cancellationToken);

    private static async Task<Ok<MemberResponse>> ChangeMemberRoleAsync(long id, long userId, ChangeMemberRoleRequest request, CurrentUser me, MetadataDb db, MetadataStore store, TimeProvider clock, CancellationToken cancellationToken) =>
        TypedResults.Ok(await CaseEndpoints.ChangeMemberRoleCoreAsync(id, userId, request.Role, me, byAdmin: true, db, store, clock, cancellationToken).ConfigureAwait(false));

    private static async Task<NoContent> RemoveMemberAsync(long id, long userId, CurrentUser me, MetadataDb db, MetadataStore store, TimeProvider clock, CancellationToken cancellationToken)
    {
        await MemberOperations.UnshareAsync(db, store.Audit, me.Actor, id, userId, byAdmin: true, clock.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
        return TypedResults.NoContent();
    }
}
