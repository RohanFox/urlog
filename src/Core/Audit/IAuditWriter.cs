using Urlog.Core.Metadata;

namespace Urlog.Core.Audit;

/// <summary>
/// Appends to the hash chain. Every audited state change (design record, "Audit log") writes its row through this, <b>in the same transaction
/// as the change</b>: open it with <see cref="AuditChain.BeginTransactionAsync"/>, make the change, call
/// <see cref="AppendAsync"/>, commit. A rollback discards the row with the change, so <c>seq</c> never has a gap.
/// </summary>
public interface IAuditWriter
{
    /// <summary>
    /// Appends one row inside the caller's open transaction on <paramref name="db"/> and returns it as stored. Flushes the
    /// context's pending changes in the same transaction (call it after the change, before the commit).
    /// </summary>
    /// <exception cref="AuditTransactionException">No open transaction, or one that does not serialise writers (see <see cref="AuditChain"/>).</exception>
    /// <exception cref="AuditChainException">No genesis row: the store was not migrated.</exception>
    Task<AuditRow> AppendAsync(MetadataDb db, AuditEntry entry, CancellationToken cancellationToken = default);
}
