using Urlog.Core.Audit;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Core.Metadata;

/// <summary>
/// Creating a case and moving it through <see cref="CaseLifecycle"/>. These are the model for every audited operation
///: open the transaction with <see cref="AuditChain.BeginTransactionAsync"/> (BEGIN IMMEDIATE), make the change and
/// save it, append the audit row, commit. If anything throws before the commit, the audit append included, the
/// transaction is disposed uncommitted and the change is gone with the row: there is no change without its audit row and
/// no audit row without its change.
/// </summary>
public static class CaseOperations
{
    /// <summary>A new <c>active</c> case owned by <paramref name="ownerId"/>, with the owner's <c>case_members</c> row and a <c>case_create</c> audit row, in one transaction.</summary>
    public static async Task<CaseRecord> CreateAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, string name, long ownerId, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var @case = new CaseRecord { Name = name.Trim(), OwnerId = ownerId, CreatedAt = nowUtc };
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            db.Cases.Add(@case);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            db.CaseMembers.Add(new CaseMember { CaseId = @case.Id, UserId = ownerId, Role = MemberRole.Owner, GrantedAt = nowUtc });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await audit.AppendAsync(db, AuditEntry.ForCase(actor.Name, actor.UserId, AuditActions.CaseCreate, @case, new Dictionary<string, object?> { ["owner_id"] = ownerId }), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return @case;
        }
        catch
        {
            // The rows were rolled back; the context must not go on believing they exist.
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// The transition table alone: no role, no precondition. Internal on purpose. What a person asks for goes
    /// through <see cref="LifecycleOperations"/>, and the API assembly cannot reach this method, so no route can
    /// skip the roles and the preconditions (tests/Api.Tests pins it).
    /// Applies one lifecycle transition, saves it and writes its audit row (<c>case_</c> + the operation, with the states it
    /// moved between), in one transaction. Returns the operation name. A concurrent transition of the same case surfaces
    /// as <see cref="DbUpdateConcurrencyException"/> on a provider without the immediate lock (the state is a concurrency token).
    /// </summary>
    internal static async Task<string> TransitionAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, long caseId, CaseState to, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(actor);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            var @case = await db.Cases.FindAsync([caseId], cancellationToken).ConfigureAwait(false) ?? throw new CaseNotFoundException(caseId);
            // Read again under the write lock: a context that tracked the case earlier must not act on a stale state.
            await db.Entry(@case).ReloadAsync(cancellationToken).ConfigureAwait(false);
            var from = @case.State;
            var operation = CaseLifecycle.Apply(@case, to, nowUtc);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var details = new Dictionary<string, object?> { ["from"] = Names.Of(from), ["to"] = Names.Of(to) };
            await audit.AppendAsync(db, AuditEntry.ForCase(actor.Name, actor.UserId, AuditActions.ForCaseOperation(operation), @case, details), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return operation;
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }
}
