using Microsoft.EntityFrameworkCore;

namespace Urlog.Core.Metadata;

/// <summary>A user's membership in a case: the case row and the role the member holds.</summary>
public sealed record CaseMembership(CaseRecord Case, MemberRole Role);

/// <summary>
/// The case ACL's reads (design record, "Security"): what a user may see and query, derived from <c>case_members</c> on
/// every call and <b>never cached</b> across users or requests. The API's access middleware asks
/// <see cref="MembershipAsync"/> before any case handler runs; the search endpoint asks <see cref="AllowedCaseIdsAsync"/> inside the
/// request that runs a query and sends the list as <c>SQL_allowed_cases</c>.
/// </summary>
public static class CaseAcl
{
    /// <summary>States a member can see: the rest (<c>soft_deleted</c>, <c>purging</c>, <c>purged</c>) are hidden, so a request answers 404 like an unknown id.</summary>
    public static IReadOnlyList<CaseState> VisibleStates { get; } = [CaseState.Active, CaseState.Closed, CaseState.Archived];

    /// <summary>States whose partitions exist: what goes into <c>SQL_allowed_cases</c>. An archived case has none.</summary>
    public static IReadOnlyList<CaseState> QueryableStates { get; } = [CaseState.Active, CaseState.Closed];

    /// <summary>Owner includes rw, rw includes ro: true when <paramref name="actual"/> grants at least <paramref name="minimum"/>.</summary>
    public static bool Satisfies(MemberRole actual, MemberRole minimum) => Rank(actual) >= Rank(minimum);

    /// <summary>The visible states and <c>soft_deleted</c>: what the restore of a case reaches. Never <c>purging</c>.</summary>
    public static IReadOnlyList<CaseState> RestorableStates { get; } = [CaseState.Active, CaseState.Closed, CaseState.Archived, CaseState.SoftDeleted];

    /// <summary>The membership of <paramref name="userId"/> in <paramref name="caseId"/>, or null when there is none or the case is hidden.</summary>
    public static Task<CaseMembership?> MembershipAsync(MetadataDb db, long userId, long caseId, CancellationToken cancellationToken = default) =>
        MembershipAsync(db, userId, caseId, VisibleStates, cancellationToken);

    /// <summary>As the overload without states, for a case in one of <paramref name="states"/>.</summary>
    public static async Task<CaseMembership?> MembershipAsync(MetadataDb db, long userId, long caseId, IReadOnlyList<CaseState> states, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var visible = states;
        var row = await db.CaseMembers.AsNoTracking()
            .Where(m => m.UserId == userId && m.CaseId == caseId)
            .Join(db.Cases.AsNoTracking().Where(c => visible.Contains(c.State)), m => m.CaseId, c => c.Id, (m, c) => new { Case = c, m.Role })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return row is null ? null : new CaseMembership(row.Case, row.Role);
    }

    /// <summary>Every visible case the user is a member of, with the role, by case id.</summary>
    public static async Task<IReadOnlyList<CaseMembership>> ListForUserAsync(MetadataDb db, long userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var visible = VisibleStates;
        var rows = await db.CaseMembers.AsNoTracking()
            .Where(m => m.UserId == userId)
            .Join(db.Cases.AsNoTracking().Where(c => visible.Contains(c.State)), m => m.CaseId, c => c.Id, (m, c) => new { Case = c, m.Role })
            .OrderBy(x => x.Case.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(r => new CaseMembership(r.Case, r.Role)).ToList();
    }

    /// <summary>The soft-deleted cases the user owns, by case id: what the user can still restore.</summary>
    public static async Task<IReadOnlyList<CaseRecord>> DeletedOwnedAsync(MetadataDb db, long userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.CaseMembers.AsNoTracking()
            .Where(m => m.UserId == userId && m.Role == MemberRole.Owner)
            .Join(db.Cases.AsNoTracking().Where(c => c.State == CaseState.SoftDeleted), m => m.CaseId, c => c.Id, (m, c) => c)
            .OrderBy(c => c.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The ids that go into <c>SQL_allowed_cases</c> for this user: memberships in a queryable state, ascending.</summary>
    public static async Task<IReadOnlyList<long>> AllowedCaseIdsAsync(MetadataDb db, long userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var queryable = QueryableStates;
        return await db.CaseMembers.AsNoTracking()
            .Where(m => m.UserId == userId)
            .Join(db.Cases.AsNoTracking().Where(c => queryable.Contains(c.State)), m => m.CaseId, c => c.Id, (m, c) => c.Id)
            .OrderBy(id => id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private static int Rank(MemberRole role) => role switch
    {
        MemberRole.Owner => 3,
        MemberRole.Rw => 2,
        MemberRole.Ro => 1,
        _ => 0,
    };
}
