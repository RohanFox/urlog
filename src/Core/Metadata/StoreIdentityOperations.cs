using Urlog.Core.Audit;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Core.Metadata;

/// <summary>
/// The two writes of the metadata file's store identity, each in one transaction with its audit row
/// (the <see cref="CaseOperations"/> pattern): the first identity of a new pair, and a pairing that takes over the
/// identity of a store the file did not carry before.
/// </summary>
public static class StoreIdentityOperations
{
    public static Task<StoreIdentityRow?> FindAsync(MetadataDb db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.StoreIdentity.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Writes the identity of a new pair. Refused when the file already has one.</summary>
    /// <param name="server">The event store the identity is shared with, as <c>host:port</c>, for the audit row.</param>
    public static async Task<StoreIdentityRow> CreateAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, string storeId, string server, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(actor);
        Require(storeId);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            if (await db.StoreIdentity.AsNoTracking().FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) is { } existing)
            {
                throw new MetadataException($"the metadata file already has store identity {existing.StoreId}; a different store is joined with 'urlog pair'");
            }

            var row = new StoreIdentityRow { StoreId = storeId, CreatedAt = nowUtc, Origin = StoreIdentityOrigin.Created };
            db.StoreIdentity.Add(row);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var details = new Dictionary<string, object?>(StringComparer.Ordinal) { ["store_id"] = storeId, ["server"] = server };
            await audit.AppendAsync(db, new AuditEntry(actor.Name, actor.UserId, AuditActions.StoreIdentityCreated, AuditTargets.Store, null, details), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return row;
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// Sets the identity to <paramref name="storeId"/>, whatever the file held, and records what it held and what
    /// the operator was shown (<paramref name="report"/>: integers and strings) in the <c>store_paired</c> row.
    /// </summary>
    public static async Task<StoreIdentityRow> PairAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, string storeId, string server, IReadOnlyDictionary<string, object?> report, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(report);
        Require(storeId);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            var row = await db.StoreIdentity.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var previous = row?.StoreId;
            if (row is null)
            {
                row = new StoreIdentityRow { StoreId = storeId, CreatedAt = nowUtc, Origin = StoreIdentityOrigin.Paired };
                db.StoreIdentity.Add(row);
            }
            else
            {
                row.StoreId = storeId;
                row.CreatedAt = nowUtc;
                row.Origin = StoreIdentityOrigin.Paired;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var details = new Dictionary<string, object?>(report, StringComparer.Ordinal)
            {
                ["store_id"] = storeId,
                ["previous_store_id"] = previous,
                ["server"] = server,
            };
            await audit.AppendAsync(db, new AuditEntry(actor.Name, actor.UserId, AuditActions.StorePaired, AuditTargets.Store, null, details), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return row;
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private static void Require(string storeId)
    {
        if (!StoreIdentity.IsValid(storeId))
        {
            throw new ArgumentException("a store identity is 32 lowercase hex characters", nameof(storeId));
        }
    }
}
