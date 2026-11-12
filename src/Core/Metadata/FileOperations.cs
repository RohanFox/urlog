using Urlog.Core.Audit;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Core.Metadata;

/// <summary>The outcome of <see cref="FileOperations.AddAsync"/>: the row, and whether it already existed (evidence rule 3).</summary>
public sealed record FileIntakeResult(EvidenceFile File, bool Duplicate);

/// <summary>
/// The intake record (evidence rule 6) as a domain rule: every <c>files</c> insert needs its case in <c>active</c>, and
/// the same bytes in the same case are one row. Intake calls this after the hash exists and the bytes are in place.
/// Audited like <see cref="CaseOperations"/>: one BEGIN IMMEDIATE transaction holds the check, the insert and the
/// <c>file_intake</c> row (or, for bytes the case already holds, the <c>file_duplicate</c> row alone).
/// </summary>
public static class FileOperations
{
    /// <summary>
    /// Adds <paramref name="file"/> unless the case already holds its hash, in which case the existing row comes back
    /// flagged <see cref="FileIntakeResult.Duplicate"/> and the second arrival is audited as <c>file_duplicate</c>. The
    /// transaction takes the write lock at BEGIN, so the active check and the insert cannot straddle a close.
    /// </summary>
    /// <exception cref="CaseNotFoundException">No such case.</exception>
    /// <exception cref="CaseNotActiveException">The case is not <c>active</c>.</exception>
    public static async Task<FileIntakeResult> AddAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, EvidenceFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(file);
        if (!IsSha256(file.Sha256))
        {
            throw new ArgumentException("sha256 must be 64 lowercase hex characters", nameof(file));
        }

        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            var @case = await db.Cases.FindAsync([file.CaseId], cancellationToken).ConfigureAwait(false) ?? throw new CaseNotFoundException(file.CaseId);
            await db.Entry(@case).ReloadAsync(cancellationToken).ConfigureAwait(false);
            if (@case.State != CaseState.Active)
            {
                throw new CaseNotActiveException(@case.Id, @case.State);
            }

            var existing = await FindAsync(db, file.CaseId, file.Sha256, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                db.Files.Add(file);
                try
                {
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (DbUpdateException)
                {
                    // A provider without the immediate lock (Postgres) can lose the race on the unique index: take the winner's row.
                    db.Entry(file).State = EntityState.Detached;
                    existing = await FindAsync(db, file.CaseId, file.Sha256, cancellationToken).ConfigureAwait(false);
                    if (existing is null)
                    {
                        throw;
                    }
                }
            }

            if (existing is not null)
            {
                // The arrival is the event; its own name and source are what the existing record does not already say.
                var arrival = new Dictionary<string, object?> { ["arrival_name"] = file.OriginalName, ["arrival_source"] = Names.Of(file.Source) };
                await audit.AppendAsync(db, AuditEntry.ForFile(actor.Name, actor.UserId, AuditActions.FileDuplicate, existing, @case.Name, arrival), cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new FileIntakeResult(existing, Duplicate: true);
            }

            var details = new Dictionary<string, object?> { ["source"] = Names.Of(file.Source), ["archive_path"] = file.ArchivePath };
            await audit.AppendAsync(db, AuditEntry.ForFile(actor.Name, actor.UserId, AuditActions.FileIntake, file, @case.Name, details), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new FileIntakeResult(file, Duplicate: false);
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>64 lowercase hex characters, the form <c>urlog ingest</c> prints and the intake record stores.</summary>
    public static bool IsSha256(string? text)
    {
        if (text is null || text.Length != 64)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    private static Task<EvidenceFile?> FindAsync(MetadataDb db, long caseId, string sha256, CancellationToken cancellationToken) =>
        db.Files.FirstOrDefaultAsync(f => f.CaseId == caseId && f.Sha256 == sha256, cancellationToken);
}
