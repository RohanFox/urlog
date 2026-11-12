using Urlog.Api.Auth;
using Urlog.Api.Contracts;
using Urlog.Core.Metadata;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

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
        return TypedResults.Ok(cases.Select(c => new AdminCaseResponse(
            c.Id, c.Name, c.State, owners.GetValueOrDefault(c.OwnerId, "?"),
            UserResponse.Utc(c.CreatedAt)!.Value, UserResponse.Utc(c.ClosedAt), UserResponse.Utc(c.ArchivedAt), UserResponse.Utc(c.DeletedAt),
            byCase[c.Id].Select(m => new MemberResponse(m.UserId, m.Name, m.Role, UserResponse.Utc(m.GrantedAt)!.Value)).ToList())).ToList());
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
