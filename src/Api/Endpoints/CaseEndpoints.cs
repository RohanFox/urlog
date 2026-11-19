using Urlog.Api.Auth;
using Urlog.Api.Contracts;
using Urlog.Core.Metadata;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Urlog.Api.Endpoints;

/// <summary>
/// <c>/api/v1/cases</c>: the caller's list and create; <c>/api/v1/cases/{caseId}</c>: every route carries
/// <c>RequireCaseRole</c>, enforced by <see cref="CaseAccessMiddleware"/> before the handler (404 for a non-member or a hidden
/// case, 403 for a role below the requirement). Members are managed by owners; the admin's path is <see cref="AdminCaseEndpoints"/>.
/// </summary>
public static class CaseEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var cases = api.MapGroup("/cases");
        cases.MapGet("", ListAsync).WithName("ListCases");
        cases.MapPost("", CreateAsync).WithName("CreateCase");

        var one = cases.MapGroup("/{caseId:long}");
        one.MapGet("", GetAsync).RequireCaseRole(MemberRole.Ro).WithName("GetCase");
        one.MapGet("/members", MembersAsync).RequireCaseRole(MemberRole.Ro).WithName("ListCaseMembers");
        one.MapPost("/members", AddMemberAsync).RequireCaseRole(MemberRole.Owner).WithName("AddCaseMember");
        one.MapPut("/members/{userId:long}", ChangeMemberRoleAsync).RequireCaseRole(MemberRole.Owner).WithName("ChangeCaseMemberRole");
        one.MapDelete("/members/{userId:long}", RemoveMemberAsync).RequireCaseRole(MemberRole.Owner).WithName("RemoveCaseMember");
        FieldEndpoints.Map(one);
        SearchEndpoints.Map(one);
        Uploads.UploadEndpoints.Map(one);
        LifecycleEndpoints.Map(one);
        DashboardEndpoints.Map(one);
        HostEndpoints.Map(one);
        FileEndpoints.Map(one);
        JobEndpoints.Map(one);
    }

    /// <summary>
    /// The caller's cases. With <c>?state=deleted</c>: the soft-deleted cases the caller owns, which no other route
    /// lists and which the owner can still restore, each with the day its purge falls due.
    /// </summary>
    private static async Task<IResult> ListAsync(HttpRequest request, CurrentUser me, MetadataDb db, IOptions<UrlogOptions> options, CancellationToken cancellationToken)
    {
        if (request.Query.TryGetValue("state", out var state) && state.ToString() is { Length: > 0 } wanted)
        {
            if (!string.Equals(wanted, "deleted", StringComparison.Ordinal))
            {
                throw new ArgumentException("state: 'deleted' lists your soft-deleted cases; without it the list is your visible ones");
            }

            var deleted = await CaseAcl.DeletedOwnedAsync(db, me.Id, cancellationToken).ConfigureAwait(false);
            var answer = new List<DeletedCaseResponse>(deleted.Count);
            foreach (var @case in deleted)
            {
                var due = @case.DeletedAt is { } at ? at + options.Value.Lifecycle.PurgeAfter : (DateTime?)null;
                answer.Add(new DeletedCaseResponse(@case.Id, @case.Name, @case.PriorState, UserResponse.Utc(@case.DeletedAt), UserResponse.Utc(due), await CaseViews.UnfinishedTaskAsync(db, @case.Id, cancellationToken).ConfigureAwait(false)));
            }

            return TypedResults.Ok(answer);
        }

        return await ListVisibleAsync(me, db, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Ok<List<CaseSummaryResponse>>> ListVisibleAsync(CurrentUser me, MetadataDb db, CancellationToken cancellationToken)
    {
        var memberships = await CaseAcl.ListForUserAsync(db, me.Id, cancellationToken).ConfigureAwait(false);
        var owners = await OwnerNamesAsync(db, memberships.Select(m => m.Case.OwnerId), cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(memberships
            .Select(m => new CaseSummaryResponse(m.Case.Id, m.Case.Name, m.Case.State, m.Role, owners.GetValueOrDefault(m.Case.OwnerId, "?"), UserResponse.Utc(m.Case.CreatedAt)!.Value))
            .ToList());
    }

    private static async Task<Created<CaseResponse>> CreateAsync(CreateCaseRequest request, CurrentUser me, MetadataDb db, MetadataStore store, TimeProvider clock, CancellationToken cancellationToken)
    {
        var @case = await CaseOperations.CreateAsync(db, store.Audit, me.Actor, request.Name, me.Id, clock.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
        return TypedResults.Created($"/api/v1/cases/{@case.Id}", Response(@case, me.Name, 1, MemberRole.Owner));
    }

    private static async Task<Ok<CaseDetailResponse>> GetAsync(HttpContext http, MetadataDb db, CancellationToken cancellationToken)
    {
        var context = http.Features.GetRequiredFeature<CaseContext>();
        return TypedResults.Ok(await CaseViews.CaseAsync(db, context.Case, context.Role, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<Ok<List<MemberResponse>>> MembersAsync(HttpContext http, MetadataDb db, CancellationToken cancellationToken)
    {
        var context = http.Features.GetRequiredFeature<CaseContext>();
        return TypedResults.Ok(await MemberListAsync(db, context.Case.Id, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<Results<Created<MemberResponse>, ProblemHttpResult>> AddMemberAsync(long caseId, AddMemberRequest request, CurrentUser me, MetadataDb db, MetadataStore store, TimeProvider clock, CancellationToken cancellationToken)
    {
        var result = await AddMemberCoreAsync(caseId, request, me, byAdmin: false, db, store, clock, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static async Task<Ok<MemberResponse>> ChangeMemberRoleAsync(long caseId, long userId, ChangeMemberRoleRequest request, CurrentUser me, MetadataDb db, MetadataStore store, TimeProvider clock, CancellationToken cancellationToken) =>
        TypedResults.Ok(await ChangeMemberRoleCoreAsync(caseId, userId, request.Role, me, byAdmin: false, db, store, clock, cancellationToken).ConfigureAwait(false));

    private static async Task<NoContent> RemoveMemberAsync(long caseId, long userId, CurrentUser me, MetadataDb db, MetadataStore store, TimeProvider clock, CancellationToken cancellationToken)
    {
        await MemberOperations.UnshareAsync(db, store.Audit, me.Actor, caseId, userId, byAdmin: false, clock.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    /// <summary>Shared with the admin routes: the user by name (400 when unknown), then <see cref="MemberOperations.ShareAsync"/>.</summary>
    internal static async Task<Results<Created<MemberResponse>, ProblemHttpResult>> AddMemberCoreAsync(long caseId, AddMemberRequest request, CurrentUser me, bool byAdmin, MetadataDb db, MetadataStore store, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!UserOperations.TryNormalizeName(request.User, out var name))
        {
            return TypedResults.Problem(Problems.BadRequest($"no user named '{request.User}'"));
        }

        var user = await UserOperations.FindByNameAsync(db, name, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return TypedResults.Problem(Problems.BadRequest($"no user named '{request.User}'"));
        }

        var member = await MemberOperations.ShareAsync(db, store.Audit, me.Actor, caseId, user.Id, request.Role, byAdmin, clock.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
        var prefix = byAdmin ? $"/api/v1/admin/cases/{caseId}" : $"/api/v1/cases/{caseId}";
        return TypedResults.Created($"{prefix}/members/{user.Id}", new MemberResponse(user.Id, user.Name, member.Role, UserResponse.Utc(member.GrantedAt)!.Value));
    }

    internal static async Task<MemberResponse> ChangeMemberRoleCoreAsync(long caseId, long userId, MemberRole role, CurrentUser me, bool byAdmin, MetadataDb db, MetadataStore store, TimeProvider clock, CancellationToken cancellationToken)
    {
        var member = await MemberOperations.ChangeRoleAsync(db, store.Audit, me.Actor, caseId, userId, role, byAdmin, clock.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false);
        return new MemberResponse(user.Id, user.Name, member.Role, UserResponse.Utc(member.GrantedAt)!.Value);
    }

    internal static async Task<List<MemberResponse>> MemberListAsync(MetadataDb db, long caseId, CancellationToken cancellationToken)
    {
        var rows = await db.CaseMembers.AsNoTracking()
            .Where(m => m.CaseId == caseId)
            .Join(db.Users.AsNoTracking(), m => m.UserId, u => u.Id, (m, u) => new { m.UserId, u.Name, m.Role, m.GrantedAt })
            .OrderBy(x => x.UserId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(r => new MemberResponse(r.UserId, r.Name, r.Role, UserResponse.Utc(r.GrantedAt)!.Value)).ToList();
    }

    internal static async Task<Dictionary<long, string>> OwnerNamesAsync(MetadataDb db, IEnumerable<long> ids, CancellationToken cancellationToken)
    {
        var set = ids.Distinct().ToList();
        return await db.Users.AsNoTracking().Where(u => set.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name, cancellationToken).ConfigureAwait(false);
    }

    internal static CaseResponse Response(CaseRecord @case, string owner, int members, MemberRole myRole) =>
        new(@case.Id, @case.Name, @case.State, owner, UserResponse.Utc(@case.CreatedAt)!.Value, UserResponse.Utc(@case.ClosedAt), UserResponse.Utc(@case.ArchivedAt), members, myRole, SpanResponse.Of(@case));
}
