using Urlog.Core.Audit;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Core.Metadata;

/// <summary>One stored file the integrity sweep has to read: its record and where the bytes should be.</summary>
public sealed record SweepCandidate(EvidenceFile File, string CaseName);

/// <summary>
/// The integrity sweep's side of the metadata file (design record, "Evidence handling", rule 5): which stored
/// files are due, and the result of each read, written with its audit row in the same transaction.
/// </summary>
public static class IntegrityOperations
{
    /// <summary>The value of <c>check</c> on the sweep's audit rows.</summary>
    public const string CheckSweep = "sweep";

    /// <summary>What <c>actual_sha256</c> says for a file that is not where its record puts it.</summary>
    public const string Missing = "missing";

    /// <summary>
    /// The ids of the files a sweep has to read, least recently verified first: files that arrived (not archive
    /// members, whose bytes are inside their container) and were last verified before <paramref name="before"/>.
    /// Only cases whose files are in place: active, closed and soft-deleted, and none with lifecycle work under
    /// way. A quarantined file is not read again: its mismatch is on record. A run takes this list once, so it
    /// reads no file twice whatever the clock does meanwhile.
    /// </summary>
    public static async Task<List<long>> DueIdsAsync(MetadataDb db, DateTime before, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await Eligible(db).Where(x => x.File.VerifiedAt == null || x.File.VerifiedAt < before)
            .OrderBy(x => x.File.VerifiedAt).ThenBy(x => x.File.Id).Select(x => x.File.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The records of <paramref name="ids"/> that are still to be read (a case may have been closed, deleted or purged since the list was taken), in the order given.</summary>
    public static async Task<List<SweepCandidate>> LoadAsync(MetadataDb db, IReadOnlyList<long> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(ids);
        var rows = await Eligible(db).Where(x => ids.Contains(x.File.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var byId = rows.ToDictionary(r => r.File.Id, r => new SweepCandidate(r.File, r.CaseName));
        return [.. ids.Where(byId.ContainsKey).Select(id => byId[id])];
    }

    private static IQueryable<EligibleRow> Eligible(MetadataDb db) =>
        from f in db.Files.AsNoTracking()
        join c in db.Cases.AsNoTracking() on f.CaseId equals c.Id
        where f.ParentFileId == null && f.State != FileState.Quarantined
            && (c.State == CaseState.Active || c.State == CaseState.Closed || c.State == CaseState.SoftDeleted)
            && !db.CaseTasks.Any(t => t.CaseId == c.Id && (t.State == CaseTaskState.Running || t.State == CaseTaskState.Committed))
        select new EligibleRow { File = f, CaseName = c.Name };

    private sealed class EligibleRow
    {
        public required EvidenceFile File { get; init; }

        public required string CaseName { get; init; }
    }

    /// <summary>
    /// The files whose bytes matched their records: <c>verified_at</c> and one <c>integrity_ok</c> row each, all in
    /// one transaction. A file whose record is gone meanwhile (its case was purged) is left out. Returns the rows written.
    /// </summary>
    public static Task<int> RecordVerifiedAsync(MetadataDb db, IAuditWriter audit, IReadOnlyList<long> fileIds, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return RecordVerifiedCoreAsync(db, audit, fileIds, () => clock.GetUtcNow().UtcDateTime, cancellationToken);
    }

    /// <summary>As the overload with a clock, at a time the caller fixes. For tests: a caller that reads a clock itself reads it before the write lock.</summary>
    internal static Task<int> RecordVerifiedAsync(MetadataDb db, IAuditWriter audit, IReadOnlyList<long> fileIds, DateTime nowUtc, CancellationToken cancellationToken = default) =>
        RecordVerifiedCoreAsync(db, audit, fileIds, () => nowUtc, cancellationToken);

    private static async Task<int> RecordVerifiedCoreAsync(MetadataDb db, IAuditWriter audit, IReadOnlyList<long> fileIds, Func<DateTime> now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(fileIds);
        if (fileIds.Count == 0)
        {
            return 0;
        }

        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            // After the lock: the order of the times written is the order of the transactions.
            var nowUtc = now();
            var files = await db.Files.Where(f => fileIds.Contains(f.Id)).OrderBy(f => f.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            var names = await db.Cases.AsNoTracking().Where(c => files.Select(f => f.CaseId).Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken).ConfigureAwait(false);
            var written = 0;
            foreach (var file in files)
            {
                await db.Entry(file).ReloadAsync(cancellationToken).ConfigureAwait(false);
                file.VerifiedAt = nowUtc;
                var details = new Dictionary<string, object?> { ["check"] = CheckSweep };
                await audit.AppendAsync(db, AuditEntry.ForFile(AuditActions.SystemActor, null, AuditActions.IntegrityOk, file, names[file.CaseId], details), cancellationToken).ConfigureAwait(false);
                written++;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return written;
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// The stored bytes do not match the record, or are gone: the file is <c>quarantined</c> (never queued
    /// again), its queued jobs are cancelled and <c>integrity_mismatch</c> carries the expected and the actual hash
    /// and size, in one transaction. Rows parsed from the file earlier stay as they are: they were hashed in the
    /// read that produced them. The bytes on disk are not touched. False when the record is gone.
    /// </summary>
    public static Task<bool> RecordMismatchAsync(MetadataDb db, IAuditWriter audit, long fileId, IntegrityMismatch? actual, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return RecordMismatchCoreAsync(db, audit, fileId, actual, () => clock.GetUtcNow().UtcDateTime, cancellationToken);
    }

    /// <summary>As the overload with a clock, at a time the caller fixes. For tests: a caller that reads a clock itself reads it before the write lock.</summary>
    internal static Task<bool> RecordMismatchAsync(MetadataDb db, IAuditWriter audit, long fileId, IntegrityMismatch? actual, DateTime nowUtc, CancellationToken cancellationToken = default) =>
        RecordMismatchCoreAsync(db, audit, fileId, actual, () => nowUtc, cancellationToken);

    private static async Task<bool> RecordMismatchCoreAsync(MetadataDb db, IAuditWriter audit, long fileId, IntegrityMismatch? actual, Func<DateTime> now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            // After the lock: the order of the times written is the order of the transactions.
            var nowUtc = now();
            var file = await db.Files.FirstOrDefaultAsync(f => f.Id == fileId, cancellationToken).ConfigureAwait(false);
            if (file is null)
            {
                return false;
            }

            await db.Entry(file).ReloadAsync(cancellationToken).ConfigureAwait(false);
            var was = file.State;
            file.State = FileState.Quarantined;
            file.VerifiedAt = nowUtc;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var cancelled = await LifecycleOperations.CancelQueuedAsync(db, j => j.FileId == fileId, "cancelled: the integrity sweep quarantined the file", nowUtc, cancellationToken).ConfigureAwait(false);
            var caseName = await db.Cases.Where(c => c.Id == file.CaseId).Select(c => c.Name).FirstAsync(cancellationToken).ConfigureAwait(false);
            var details = new Dictionary<string, object?>
            {
                ["actual_sha256"] = actual?.ActualSha256 ?? Missing,
                ["actual_size"] = actual?.ActualSize,
                ["check"] = CheckSweep,
                ["state_before"] = Names.Of(was),
                ["jobs_cancelled"] = cancelled,
            };
            await audit.AppendAsync(db, AuditEntry.ForFile(AuditActions.SystemActor, null, AuditActions.IntegrityMismatch, file, caseName, details), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }
}
