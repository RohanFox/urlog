using System.Data;
using Urlog.Core.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Urlog.Core.Audit;

/// <summary>
/// The writer of the audit chain. <c>seq</c> is assigned here, never by the database: the last row is read and the
/// new one chained to it inside the caller's transaction. For two callers never to read the same tail, that transaction
/// must hold the write lock before the read. Measured on Microsoft.Data.Sqlite 10 (tests/Metadata.Tests/AuditChainTests):
/// EF's default and <see cref="IsolationLevel.Serializable"/> transactions are both <c>BEGIN IMMEDIATE</c> (the reserved
/// lock at <c>BEGIN</c>; a second writer waits on the 5 s busy timeout); <see cref="IsolationLevel.ReadUncommitted"/> is a
/// deferred <c>BEGIN</c> that under WAL still reports Serializable, and in it an append whose snapshot went stale fails with
/// <c>SQLITE_BUSY_SNAPSHOT</c> rather than forking, so it is safe but it fails instead of waiting. On Postgres
/// Serializable is the level under which a concurrent append fails to commit and the caller retries.
/// <see cref="AppendAsync"/> refuses to run outside a transaction, or in one whose level is not Serializable.
/// </summary>
public sealed class AuditChain(TimeProvider clock) : IAuditWriter
{
    /// <summary>The writer on the system clock.</summary>
    public static AuditChain Default { get; } = new(TimeProvider.System);

    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>
    /// The transaction every audited change runs in: <c>BEGIN IMMEDIATE</c> on SQLite, serializable on Postgres.
    /// <c>await using var tx = await AuditChain.BeginTransactionAsync(db, ct); ... ; await tx.CommitAsync(ct);</c>
    /// </summary>
    public static Task<IDbContextTransaction> BeginTransactionAsync(MetadataDb db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
    }

    /// <summary>The check <see cref="AppendAsync"/> makes; callers that want to fail early can make it too.</summary>
    public static void RequireTransaction(MetadataDb db)
    {
        ArgumentNullException.ThrowIfNull(db);
        var transaction = db.Database.CurrentTransaction
            ?? throw new AuditTransactionException("an audit row is written inside the transaction of the change it records; open one with AuditChain.BeginTransactionAsync");
        var level = transaction.GetDbTransaction().IsolationLevel;
        if (level != IsolationLevel.Serializable)
        {
            throw new AuditTransactionException($"the audit chain needs a transaction that serialises writers (BEGIN IMMEDIATE on SQLite, IsolationLevel.Serializable): this one is {level}; open it with AuditChain.BeginTransactionAsync");
        }
    }

    public async Task<AuditRow> AppendAsync(MetadataDb db, AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        RequireTransaction(db);
        var tail = await TailAsync(db, cancellationToken).ConfigureAwait(false)
            ?? throw new AuditChainException("the audit table has no genesis row: the store was not migrated (MetadataStore.MigrateAsync writes it)");
        var row = Build(tail.Seq + 1, tail.Hash, entry, schemaVersion: null);
        db.Audit.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    /// <summary>
    /// Writes the genesis row when the table is empty: <c>seq 1</c>, actor <c>system</c>, <c>prev_hash</c> all zeros,
    /// <c>schema_version</c> = <see cref="AuditHash.FormatVersion"/>, details naming the applied migration and the tool
    /// version. Called by <see cref="MetadataStore.MigrateAsync"/>; returns the row written, or null when one existed.
    /// </summary>
    public async Task<AuditRow?> EnsureGenesisAsync(MetadataDb db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        await using var transaction = await BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
        if (await TailAsync(db, cancellationToken).ConfigureAwait(false) is not null)
        {
            return null;
        }

        var migrations = await db.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["migration"] = migrations.LastOrDefault(),
            ["tool"] = "urlog " + (typeof(AuditChain).Assembly.GetName().Version?.ToString() ?? "0"),
        };
        var row = Build(1, AuditHash.ZeroHash, new AuditEntry(AuditActions.SystemActor, null, AuditActions.Genesis, null, null, details), AuditHash.FormatVersion);
        db.Audit.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    private AuditRow Build(long seq, string prevHash, AuditEntry entry, int? schemaVersion)
    {
        if (!AuditHash.IsHex64(prevHash))
        {
            throw new AuditChainException($"the hash of seq {seq - 1} is not 64 lowercase hex characters");
        }

        var ts = CanonicalJson.TruncateToMicroseconds(_clock.GetUtcNow().UtcDateTime);
        var detailsJson = CanonicalJson.SerializeToString(entry.Details ?? new Dictionary<string, object?>());
        return new AuditRow
        {
            Seq = seq,
            TsUtc = ts,
            Actor = entry.Actor,
            ActorId = entry.ActorId,
            Action = entry.Action,
            TargetType = entry.TargetType,
            TargetId = entry.TargetId,
            DetailsJson = detailsJson,
            PrevHash = prevHash,
            SchemaVersion = schemaVersion,
            Hash = AuditHash.Compute(seq, ts, entry.Actor, entry.ActorId, entry.Action, entry.TargetType, entry.TargetId, detailsJson, prevHash, schemaVersion),
        };
    }

    private static Task<Tail?> TailAsync(MetadataDb db, CancellationToken cancellationToken) =>
        db.Audit.AsNoTracking().OrderByDescending(a => a.Seq).Select(a => new Tail(a.Seq, a.Hash)).FirstOrDefaultAsync(cancellationToken);

    private sealed record Tail(long Seq, string Hash);
}
