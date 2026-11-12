using Urlog.Core.Audit;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Core.Metadata;

/// <summary>
/// The case ACL's writes: share, change a member's role, unshare. Every row targets the case
/// (<see cref="AuditEntry.ForCase"/>) with <c>user_id</c>, <c>user_name</c> and <c>by_admin</c> in the details, so a grant
/// an admin gives anyone, themselves included, appears in that case's own trail where the owner sees it. A case always
/// keeps at least one owner.
/// </summary>
public static class MemberOperations
{
    /// <summary>Adds a member and writes <c>case_share</c>.</summary>
    /// <exception cref="CaseNotFoundException">No such case.</exception>
    /// <exception cref="UserNotFoundException">No such user.</exception>
    /// <exception cref="AlreadyMemberException">The user is already a member (change the role instead).</exception>
    public static async Task<CaseMember> ShareAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, long caseId, long userId, MemberRole role, bool byAdmin, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(actor);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            var @case = await LoadCaseAsync(db, caseId, cancellationToken).ConfigureAwait(false);
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false) ?? throw new UserNotFoundException(userId);
            if (await db.CaseMembers.AnyAsync(m => m.CaseId == caseId && m.UserId == userId, cancellationToken).ConfigureAwait(false))
            {
                throw new AlreadyMemberException(caseId, user.Name);
            }

            var member = new CaseMember { CaseId = caseId, UserId = userId, Role = role, GrantedAt = nowUtc };
            db.CaseMembers.Add(member);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var details = Details(user, byAdmin);
            details["role"] = Names.Of(role);
            await audit.AppendAsync(db, AuditEntry.ForCase(actor.Name, actor.UserId, AuditActions.CaseShare, @case, details), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return member;
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>Changes a member's role and writes <c>case_role_change</c> (<c>from</c>, <c>to</c>).</summary>
    /// <exception cref="CaseNotFoundException">No such case.</exception>
    /// <exception cref="NotAMemberException">The user is not a member.</exception>
    /// <exception cref="LastOwnerException">The user is the only owner and <paramref name="role"/> is not owner.</exception>
    public static async Task<CaseMember> ChangeRoleAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, long caseId, long userId, MemberRole role, bool byAdmin, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(actor);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            var @case = await LoadCaseAsync(db, caseId, cancellationToken).ConfigureAwait(false);
            var (member, user) = await LoadMemberAsync(db, caseId, userId, cancellationToken).ConfigureAwait(false);
            var from = member.Role;
            if (from == MemberRole.Owner && role != MemberRole.Owner)
            {
                await RequireAnotherOwnerAsync(db, caseId, userId, @case.Name, cancellationToken).ConfigureAwait(false);
            }

            member.Role = role;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var details = Details(user, byAdmin);
            details["from"] = Names.Of(from);
            details["to"] = Names.Of(role);
            await audit.AppendAsync(db, AuditEntry.ForCase(actor.Name, actor.UserId, AuditActions.CaseRoleChange, @case, details), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return member;
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>Removes a member and writes <c>case_unshare</c> (<c>role</c> the member had).</summary>
    /// <exception cref="CaseNotFoundException">No such case.</exception>
    /// <exception cref="NotAMemberException">The user is not a member.</exception>
    /// <exception cref="LastOwnerException">The user is the only owner.</exception>
    public static async Task UnshareAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, long caseId, long userId, bool byAdmin, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(actor);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            var @case = await LoadCaseAsync(db, caseId, cancellationToken).ConfigureAwait(false);
            var (member, user) = await LoadMemberAsync(db, caseId, userId, cancellationToken).ConfigureAwait(false);
            if (member.Role == MemberRole.Owner)
            {
                await RequireAnotherOwnerAsync(db, caseId, userId, @case.Name, cancellationToken).ConfigureAwait(false);
            }

            db.CaseMembers.Remove(member);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var details = Details(user, byAdmin);
            details["role"] = Names.Of(member.Role);
            await audit.AppendAsync(db, AuditEntry.ForCase(actor.Name, actor.UserId, AuditActions.CaseUnshare, @case, details), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private static async Task<CaseRecord> LoadCaseAsync(MetadataDb db, long caseId, CancellationToken cancellationToken)
    {
        var @case = await db.Cases.FindAsync([caseId], cancellationToken).ConfigureAwait(false) ?? throw new CaseNotFoundException(caseId);
        await db.Entry(@case).ReloadAsync(cancellationToken).ConfigureAwait(false);
        return @case;
    }

    private static async Task<(CaseMember Member, User User)> LoadMemberAsync(MetadataDb db, long caseId, long userId, CancellationToken cancellationToken)
    {
        var member = await db.CaseMembers.FirstOrDefaultAsync(m => m.CaseId == caseId && m.UserId == userId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotAMemberException(caseId, userId);
        await db.Entry(member).ReloadAsync(cancellationToken).ConfigureAwait(false);
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false);
        return (member, user);
    }

    private static async Task RequireAnotherOwnerAsync(MetadataDb db, long caseId, long userId, string caseName, CancellationToken cancellationToken)
    {
        if (!await db.CaseMembers.AnyAsync(m => m.CaseId == caseId && m.UserId != userId && m.Role == MemberRole.Owner, cancellationToken).ConfigureAwait(false))
        {
            throw new LastOwnerException(caseName);
        }
    }

    private static Dictionary<string, object?> Details(User user, bool byAdmin) => new()
    {
        ["user_id"] = user.Id,
        ["user_name"] = user.Name,
        ["by_admin"] = byAdmin,
    };
}
