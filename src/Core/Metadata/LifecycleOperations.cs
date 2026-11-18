using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Urlog.Core.Audit;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Core.Metadata;

/// <summary>What a purge removed from the metadata file, as the <c>case_purge_complete</c> row states it.</summary>
/// <param name="EvidenceSha256">SHA-256 over the SHA-256 of every file record of the case, sorted, one per line (<see cref="LifecycleOperations.EvidenceDigest"/>).</param>
public sealed record PurgeSummary(long CaseId, string CaseName, long OwnerId, string? OwnerName, int FileCount, int ArrivedFileCount, long TotalBytes, string EvidenceSha256, string? BackupSha256);

/// <summary>What the mark of a purge needs to know about the backup taken just before it.</summary>
public sealed record PurgeBackup(string Sha256, string FileName, long Size);

/// <summary>
/// The lifecycle operations a person asks for (design record, "Case lifecycle"), each with its audit row in the
/// same transaction, and the metadata side of the purge the executor carries out. <see cref="CaseLifecycle"/>
/// says which transitions exist; these add what the case must hold at that moment and who may ask.
/// An actor with a user id is held to <see cref="CaseLifecycle.RequiredRole"/>; an actor without one is the
/// operator on the box or the tool itself.
/// <para>
/// Every time these write (<c>closed_at</c>, <c>deleted_at</c>, which starts the purge window, <c>purging_at</c>,
/// the times of a task) is read from the clock after the operation holds the write lock, like the times of a job.
/// </para>
/// </summary>
public static class LifecycleOperations
{
    /// <summary>The steps of a purge after its mark, as <c>case_tasks.step</c> names them.</summary>
    public const string StepStore = "store";
    public const string StepFiles = "files";
    public const string StepMetadata = "metadata";

    /// <summary>States of a task that still has work or a decision ahead of it.</summary>
    public static IReadOnlyList<CaseTaskState> Unfinished { get; } = [CaseTaskState.Requested, CaseTaskState.Running, CaseTaskState.Committed];

    /// <summary>
    /// <c>active -> closed</c>. Ingest is frozen from the commit on; jobs still queued stay queued and are held
    /// (the runner claims for active cases only), so a reopen lets them run. Refused while a job of the case runs.
    /// </summary>
    public static Task<CaseRecord> CloseAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, long caseId, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return CloseCoreAsync(db, audit, actor, caseId, () => clock.GetUtcNow().UtcDateTime, cancellationToken);
    }

    /// <summary>As the overload with a clock, at a time the caller fixes. For tests: a caller that reads a clock itself reads it before the write lock.</summary>
    internal static Task<CaseRecord> CloseAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, long caseId, DateTime nowUtc, CancellationToken cancellationToken = default) =>
        CloseCoreAsync(db, audit, actor, caseId, () => nowUtc, cancellationToken);

    private static Task<CaseRecord> CloseCoreAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, long caseId, Func<DateTime> now, CancellationToken cancellationToken) =>
        MoveAsync(db, audit, actor, caseId, CaseState.Closed, now, async (@case, details, _) =>
        {
            await RequireNoRunningJobAsync(db, @case, "closed", cancellationToken).ConfigureAwait(false);
            details["queued_jobs_held"] = await db.Jobs.CountAsync(j => j.CaseId == caseId && j.State == JobState.Queued, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary><c>closed -> active</c>.</summary>
    public static Task<CaseRecord> ReopenAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, long caseId, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return ReopenCoreAsync(db, audit, actor, caseId, () => clock.GetUtcNow().UtcDateTime, cancellationToken);
    }

    /// <summary>As the overload with a clock, at a time the caller fixes. For tests: a caller that reads a clock itself reads it before the write lock.</summary>
    internal static Task<CaseRecord> ReopenAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, long caseId, DateTime nowUtc, CancellationToken cancellationToken = default) =>
        ReopenCoreAsync(db, audit, actor, caseId, () => nowUtc, cancellationToken);

    private static Task<CaseRecord> ReopenCoreAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, long caseId, Func<DateTime> now, CancellationToken cancellationToken) =>
        MoveAsync(db, audit, actor, caseId, CaseState.Active, now, (_, _, _) => Task.CompletedTask, cancellationToken);

    /// <summary>
    /// To <c>soft_deleted</c> from active, closed or archived. <paramref name="typedName"/> is what the person
    /// typed to confirm and must be the case's name exactly. The state left is kept in <c>prior_state</c> for the
    /// restore. Queued jobs stay queued and are held; the purge cancels them.
    /// </summary>
    public static Task<CaseRecord> SoftDeleteAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, long caseId, string typedName, TimeSpan purgeAfter, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return SoftDeleteCoreAsync(db, audit, actor, caseId, typedName, purgeAfter, () => clock.GetUtcNow().UtcDateTime, cancellationToken);
    }

    /// <summary>As the overload with a clock, at a time the caller fixes. For tests: a caller that reads a clock itself reads it before the write lock.</summary>
    internal static Task<CaseRecord> SoftDeleteAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, long caseId, string typedName, TimeSpan purgeAfter, DateTime nowUtc, CancellationToken cancellationToken = default) =>
        SoftDeleteCoreAsync(db, audit, actor, caseId, typedName, purgeAfter, () => nowUtc, cancellationToken);

    private static Task<CaseRecord> SoftDeleteCoreAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, long caseId, string typedName, TimeSpan purgeAfter, Func<DateTime> now, CancellationToken cancellationToken) =>
        MoveAsync(db, audit, actor, caseId, CaseState.SoftDeleted, now, async (@case, details, nowUtc) =>
        {
            if (!string.Equals(typedName, @case.Name, StringComparison.Ordinal))
            {
                throw new LifecycleRefusedException($"case {caseId} is not deleted: the name typed to confirm is not the case's name");
            }

            await RequireNoRunningJobAsync(db, @case, "deleted", cancellationToken).ConfigureAwait(false);
            details["by_admin"] = actor.UserId is { } id && !await IsOwnerAsync(db, caseId, id, cancellationToken).ConfigureAwait(false);
            details["purge_due"] = (nowUtc + purgeAfter).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        }, cancellationToken);

    /// <summary>
    /// <c>soft_deleted</c> back to the state in <c>prior_state</c>. A purge that was asked for and has not marked
    /// the case yet is withdrawn in the same transaction; once the case is <c>purging</c> there is no restore.
    /// </summary>
    public static Task<CaseRecord> RestoreAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, long caseId, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return RestoreCoreAsync(db, audit, actor, caseId, () => clock.GetUtcNow().UtcDateTime, cancellationToken);
    }

    /// <summary>As the overload with a clock, at a time the caller fixes. For tests: a caller that reads a clock itself reads it before the write lock.</summary>
    internal static Task<CaseRecord> RestoreAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, long caseId, DateTime nowUtc, CancellationToken cancellationToken = default) =>
        RestoreCoreAsync(db, audit, actor, caseId, () => nowUtc, cancellationToken);

    private static async Task<CaseRecord> RestoreCoreAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, long caseId, Func<DateTime> now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(actor);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            // After the lock: the order of the times written is the order of the transactions.
            var nowUtc = now();
            var @case = await LoadAsync(db, caseId, cancellationToken).ConfigureAwait(false);
            if (@case.State != CaseState.SoftDeleted || @case.PriorState is not { } prior)
            {
                throw new InvalidCaseTransitionException(@case.State, @case.PriorState ?? CaseState.Active, @case.PriorState);
            }

            await AuthoriseAsync(db, actor, @case, CaseLifecycle.Restore, cancellationToken).ConfigureAwait(false);
            var withdrawn = 0;
            foreach (var task in await db.CaseTasks.Where(t => t.CaseId == caseId && t.Kind == CaseTaskKind.Purge && (t.State == CaseTaskState.Requested || t.State == CaseTaskState.Running)).ToListAsync(cancellationToken).ConfigureAwait(false))
            {
                task.State = CaseTaskState.Withdrawn;
                task.Message = $"withdrawn: the case was restored by {actor.Name}";
                task.UpdatedAt = nowUtc;
                task.FinishedAt = nowUtc;
                withdrawn++;
            }

            var operation = CaseLifecycle.Apply(@case, prior, nowUtc);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var details = actor.Stamp(new Dictionary<string, object?> { ["from"] = Names.Of(CaseState.SoftDeleted), ["to"] = Names.Of(prior), ["purge_requests_withdrawn"] = withdrawn });
            await audit.AppendAsync(db, AuditEntry.ForCase(actor.Name, actor.UserId, AuditActions.ForCaseOperation(operation), @case, details), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return @case;
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// Asks for the purge of a soft-deleted case, or confirms a request another administrator made. Without
    /// <paramref name="twoPerson"/> the task is the executor's at once. With it the first call leaves a request and
    /// the call of a second, different administrator account releases it. Nothing is removed here: the executor
    /// takes the backup, marks the case and carries out the rest.
    /// </summary>
    /// <exception cref="LifecycleRefusedException">The case is not soft-deleted, a purge is under way, or the same administrator confirms their own request.</exception>
    public static Task<CaseTask> RequestPurgeAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, long caseId, bool twoPerson, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return RequestPurgeCoreAsync(db, audit, actor, caseId, twoPerson, () => clock.GetUtcNow().UtcDateTime, cancellationToken);
    }

    /// <summary>As the overload with a clock, at a time the caller fixes. For tests: a caller that reads a clock itself reads it before the write lock.</summary>
    internal static Task<CaseTask> RequestPurgeAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, long caseId, bool twoPerson, DateTime nowUtc, CancellationToken cancellationToken = default) =>
        RequestPurgeCoreAsync(db, audit, actor, caseId, twoPerson, () => nowUtc, cancellationToken);

    private static async Task<CaseTask> RequestPurgeCoreAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, long caseId, bool twoPerson, Func<DateTime> now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(actor);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            // After the lock: the order of the times written is the order of the transactions.
            var nowUtc = now();
            var @case = await LoadAsync(db, caseId, cancellationToken).ConfigureAwait(false);
            if (@case.State != CaseState.SoftDeleted)
            {
                throw new LifecycleRefusedException($"case {caseId} is {Names.Of(@case.State)}; only a soft-deleted case can be purged");
            }

            await AuthoriseAsync(db, actor, @case, CaseLifecycle.Purge, cancellationToken).ConfigureAwait(false);
            if (twoPerson && actor.UserId is null)
            {
                throw new LifecycleRoleException("two-person approval is on: a purge is asked for and confirmed by two administrator accounts, and this request names none");
            }

            var task = await db.CaseTasks.Where(t => t.CaseId == caseId && t.Kind == CaseTaskKind.Purge && (t.State == CaseTaskState.Requested || t.State == CaseTaskState.Running || t.State == CaseTaskState.Committed))
                .OrderBy(t => t.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var details = actor.Stamp(new Dictionary<string, object?> { ["two_person"] = twoPerson });
            if (task is null)
            {
                task = new CaseTask
                {
                    CaseId = caseId,
                    Kind = CaseTaskKind.Purge,
                    State = twoPerson ? CaseTaskState.Requested : CaseTaskState.Running,
                    RequestedBy = actor.Name,
                    RequestedById = actor.UserId,
                    CreatedAt = nowUtc,
                    UpdatedAt = nowUtc,
                };
                db.CaseTasks.Add(task);
                details["step"] = twoPerson ? "requested" : "released";
            }
            else if (task.State != CaseTaskState.Requested)
            {
                throw new LifecycleRefusedException($"case {caseId} already has a purge under way (asked for by {task.RequestedBy})");
            }
            else
            {
                await db.Entry(task).ReloadAsync(cancellationToken).ConfigureAwait(false);
                if (twoPerson && task.RequestedById == actor.UserId)
                {
                    throw new LifecycleRefusedException($"the purge of case {caseId} was asked for by {task.RequestedBy}; a second administrator has to confirm it");
                }

                task.State = CaseTaskState.Running;
                task.ConfirmedBy = actor.Name;
                task.ConfirmedById = actor.UserId;
                task.UpdatedAt = nowUtc;
                details["step"] = "confirmed";
                details["requested_by"] = task.RequestedBy;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            details["task_id"] = task.Id;
            await audit.AppendAsync(db, AuditEntry.ForCase(actor.Name, actor.UserId, AuditActions.CasePurgeRequest, @case, details), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return task;
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// Soft-deleted cases whose window has passed and that have no purge asked for: what the scheduled purge
    /// releases as <c>system</c> when two-person approval is off.
    /// </summary>
    public static async Task<List<long>> DueForPurgeAsync(MetadataDb db, TimeSpan purgeAfter, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var before = nowUtc - purgeAfter;
        return await db.Cases.AsNoTracking()
            .Where(c => c.State == CaseState.SoftDeleted && c.DeletedAt != null && c.DeletedAt <= before)
            .Where(c => !db.CaseTasks.Any(t => t.CaseId == c.Id && t.Kind == CaseTaskKind.Purge && (t.State == CaseTaskState.Requested || t.State == CaseTaskState.Running || t.State == CaseTaskState.Committed)))
            .OrderBy(c => c.Id).Select(c => c.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Purge tasks the executor has to carry out or finish, oldest first.</summary>
    public static async Task<List<CaseTask>> PendingPurgesAsync(MetadataDb db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.CaseTasks.AsNoTracking()
            .Where(t => t.Kind == CaseTaskKind.Purge && (t.State == CaseTaskState.Running || t.State == CaseTaskState.Committed))
            .OrderBy(t => t.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The mark of a purge, one transaction: the case becomes <c>purging</c>, its queued jobs are cancelled, the
    /// task can only go forward from here, and <c>case_purge</c> names who asked, who confirmed and the backup taken
    /// just before. False when the purge no longer stands (the case was restored meanwhile).
    /// </summary>
    public static Task<bool> MarkPurgingAsync(MetadataDb db, IAuditWriter audit, long taskId, PurgeBackup backup, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return MarkPurgingCoreAsync(db, audit, taskId, backup, () => clock.GetUtcNow().UtcDateTime, cancellationToken);
    }

    /// <summary>As the overload with a clock, at a time the caller fixes. For tests: a caller that reads a clock itself reads it before the write lock.</summary>
    internal static Task<bool> MarkPurgingAsync(MetadataDb db, IAuditWriter audit, long taskId, PurgeBackup backup, DateTime nowUtc, CancellationToken cancellationToken = default) =>
        MarkPurgingCoreAsync(db, audit, taskId, backup, () => nowUtc, cancellationToken);

    private static async Task<bool> MarkPurgingCoreAsync(MetadataDb db, IAuditWriter audit, long taskId, PurgeBackup backup, Func<DateTime> now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(backup);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            // After the lock: the order of the times written is the order of the transactions.
            var nowUtc = now();
            var task = await db.CaseTasks.FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken).ConfigureAwait(false);
            if (task is not null)
            {
                await db.Entry(task).ReloadAsync(cancellationToken).ConfigureAwait(false);
            }

            if (task is null || task.State != CaseTaskState.Running)
            {
                return false;
            }

            var @case = await LoadAsync(db, task.CaseId, cancellationToken).ConfigureAwait(false);
            if (@case.State != CaseState.SoftDeleted)
            {
                return false;
            }

            var running = await db.Jobs.Where(j => j.CaseId == @case.Id && j.State == JobState.Parsing).Select(j => (long?)j.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (running is { } jobId)
            {
                throw new LifecycleRefusedException($"case {@case.Id} is not purged yet: job {jobId} is still recorded as running; the next start of the runner settles it");
            }

            var cancelled = await CancelQueuedAsync(db, j => j.CaseId == @case.Id, "cancelled: the case is being purged", nowUtc, cancellationToken).ConfigureAwait(false);
            var from = @case.State;
            var operation = CaseLifecycle.Apply(@case, CaseState.Purging, nowUtc);
            task.State = CaseTaskState.Committed;
            task.Step = StepStore;
            task.BackupSha256 = backup.Sha256;
            task.UpdatedAt = nowUtc;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            var details = new Dictionary<string, object?>
            {
                ["from"] = Names.Of(from),
                ["to"] = Names.Of(CaseState.Purging),
                ["requested_by"] = task.RequestedBy,
                ["confirmed_by"] = task.ConfirmedBy,
                ["backup_sha256"] = backup.Sha256,
                ["backup_file"] = backup.FileName,
                ["backup_size"] = backup.Size,
                ["files"] = await db.Files.CountAsync(f => f.CaseId == @case.Id, cancellationToken).ConfigureAwait(false),
                ["jobs_cancelled"] = cancelled,
                ["task_id"] = task.Id,
            };
            await audit.AppendAsync(db, AuditEntry.ForCase(task.ConfirmedBy ?? task.RequestedBy, task.ConfirmedById ?? task.RequestedById, AuditActions.ForCaseOperation(operation), @case, details), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>Writes the step a task is about to start, so a stop names where it was. No audit row: nothing has changed for the case.</summary>
    public static async Task SetStepAsync(MetadataDb db, long taskId, string step, bool newAttempt, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
        var nowUtc = clock.GetUtcNow().UtcDateTime;
        await db.CaseTasks.Where(t => t.Id == taskId).ExecuteUpdateAsync(
            s => s.SetProperty(t => t.Step, step).SetProperty(t => t.UpdatedAt, nowUtc).SetProperty(t => t.Attempt, t => t.Attempt + (newAttempt ? 1 : 0)),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What the last attempt on a task stopped on; the task stays the executor's.</summary>
    public static async Task NoteAsync(MetadataDb db, long taskId, string message, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        var text = message.Length <= 4000 ? message : message[..4000];
        await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
        var nowUtc = clock.GetUtcNow().UtcDateTime;
        await db.CaseTasks.Where(t => t.Id == taskId).ExecuteUpdateAsync(s => s.SetProperty(t => t.Message, text).SetProperty(t => t.UpdatedAt, nowUtc), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The file ids of a case, for the event store's generation rows.</summary>
    public static async Task<List<long>> FileIdsAsync(MetadataDb db, long caseId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.Files.AsNoTracking().Where(f => f.CaseId == caseId).OrderBy(f => f.Id).Select(f => f.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The last step of a purge, one transaction: <c>case_purge_complete</c> with what the case was (name, owner,
    /// file count, bytes, the digest over its evidence hashes, the hash of the backup that still holds its
    /// records), then every row of the case in every table but the audit chain. The case id is free again after
    /// the commit. Null when the case is already gone.
    /// </summary>
    public static Task<PurgeSummary?> CompletePurgeAsync(MetadataDb db, IAuditWriter audit, long caseId, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return CompletePurgeCoreAsync(db, audit, caseId, () => clock.GetUtcNow().UtcDateTime, cancellationToken);
    }

    /// <summary>As the overload with a clock, at a time the caller fixes. For tests: a caller that reads a clock itself reads it before the write lock.</summary>
    internal static Task<PurgeSummary?> CompletePurgeAsync(MetadataDb db, IAuditWriter audit, long caseId, DateTime nowUtc, CancellationToken cancellationToken = default) =>
        CompletePurgeCoreAsync(db, audit, caseId, () => nowUtc, cancellationToken);

    private static async Task<PurgeSummary?> CompletePurgeCoreAsync(MetadataDb db, IAuditWriter audit, long caseId, Func<DateTime> now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            // After the lock: the order of the times written is the order of the transactions.
            var nowUtc = now();
            var @case = await db.Cases.FirstOrDefaultAsync(c => c.Id == caseId, cancellationToken).ConfigureAwait(false);
            if (@case is null)
            {
                return null;
            }

            await db.Entry(@case).ReloadAsync(cancellationToken).ConfigureAwait(false);
            if (@case.State != CaseState.Purging)
            {
                throw new InvalidCaseTransitionException(@case.State, CaseState.Purged, @case.PriorState);
            }

            var task = await db.CaseTasks.AsNoTracking().Where(t => t.CaseId == caseId && t.Kind == CaseTaskKind.Purge && t.State == CaseTaskState.Committed).OrderByDescending(t => t.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var files = await db.Files.AsNoTracking().Where(f => f.CaseId == caseId).Select(f => new { f.Sha256, f.Size, f.ParentFileId }).ToListAsync(cancellationToken).ConfigureAwait(false);
            var ownerName = await db.Users.AsNoTracking().Where(u => u.Id == @case.OwnerId).Select(u => u.Name).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var summary = new PurgeSummary(
                caseId,
                @case.Name,
                @case.OwnerId,
                ownerName,
                files.Count,
                files.Count(f => f.ParentFileId is null),
                files.Where(f => f.ParentFileId is null).Sum(f => f.Size),
                EvidenceDigest(files.Select(f => f.Sha256)),
                task?.BackupSha256);
            var details = new Dictionary<string, object?>
            {
                ["from"] = Names.Of(CaseState.Purging),
                ["to"] = Names.Of(CaseState.Purged),
                ["owner_id"] = summary.OwnerId,
                ["owner_name"] = summary.OwnerName,
                ["file_count"] = summary.FileCount,
                ["arrived_file_count"] = summary.ArrivedFileCount,
                ["total_bytes"] = summary.TotalBytes,
                ["evidence_sha256"] = summary.EvidenceSha256,
                ["backup_sha256"] = summary.BackupSha256,
                ["requested_by"] = task?.RequestedBy,
                ["confirmed_by"] = task?.ConfirmedBy,
                ["created_at"] = @case.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            };
            await audit.AppendAsync(db, AuditEntry.ForCase(AuditActions.SystemActor, null, AuditActions.CasePurgeComplete, @case, details), cancellationToken).ConfigureAwait(false);

            // Children before parents, each in one statement: the member-to-container reference is checked at the end of the statement.
            await db.Jobs.Where(j => j.CaseId == caseId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await db.Files.Where(f => f.CaseId == caseId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await db.Hosts.Where(h => h.CaseId == caseId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await db.CaseMembers.Where(m => m.CaseId == caseId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await db.CaseTasks.Where(t => t.CaseId == caseId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await db.CaseStats.Where(s => s.CaseId == caseId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await db.Cases.Where(c => c.Id == caseId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            db.ChangeTracker.Clear();
            return summary;
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// The digest a purge leaves of a case's evidence: SHA-256 over the UTF-8 text of every file record's SHA-256
    /// (64 lowercase hex characters), sorted by ordinal comparison, each followed by one line feed. It can be
    /// computed again from the backup taken before the purge.
    /// </summary>
    public static string EvidenceDigest(IEnumerable<string> sha256s)
    {
        ArgumentNullException.ThrowIfNull(sha256s);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var value in sha256s.Order(StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(value));
            hash.AppendData("\n"u8);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Inside the caller's transaction: every queued job the predicate takes becomes <c>cancelled</c> with the reason. Returns the count.</summary>
    internal static Task<int> CancelQueuedAsync(MetadataDb db, System.Linq.Expressions.Expression<Func<Job, bool>> which, string reason, DateTime nowUtc, CancellationToken cancellationToken) =>
        db.Jobs.Where(j => j.State == JobState.Queued).Where(which).ExecuteUpdateAsync(
            s => s.SetProperty(j => j.State, JobState.Cancelled).SetProperty(j => j.FinishedAt, nowUtc).SetProperty(j => j.Message, reason),
            cancellationToken);

    private static async Task<CaseRecord> MoveAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, long caseId, CaseState to, Func<DateTime> now, Func<CaseRecord, Dictionary<string, object?>, DateTime, Task> check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(actor);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            // After the lock: the order of the times written is the order of the transactions.
            var nowUtc = now();
            var @case = await LoadAsync(db, caseId, cancellationToken).ConfigureAwait(false);
            if (!CaseLifecycle.CanTransition(@case, to, out var operation))
            {
                throw new InvalidCaseTransitionException(@case.State, to, @case.PriorState);
            }

            await AuthoriseAsync(db, actor, @case, operation!, cancellationToken).ConfigureAwait(false);
            var unfinished = await db.CaseTasks.AsNoTracking().Where(t => t.CaseId == caseId && (t.State == CaseTaskState.Requested || t.State == CaseTaskState.Running || t.State == CaseTaskState.Committed))
                .OrderBy(t => t.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (unfinished is not null)
            {
                throw new LifecycleRefusedException($"case {caseId} has unfinished work ({Names.Of(unfinished.Kind)}, {Names.Of(unfinished.State)}, asked for by {unfinished.RequestedBy}); nothing else changes its state until that has ended");
            }

            var from = @case.State;
            var details = actor.Stamp(new Dictionary<string, object?> { ["from"] = Names.Of(from), ["to"] = Names.Of(to) });
            await check(@case, details, nowUtc).ConfigureAwait(false);
            CaseLifecycle.Apply(@case, to, nowUtc);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await audit.AppendAsync(db, AuditEntry.ForCase(actor.Name, actor.UserId, AuditActions.ForCaseOperation(operation!), @case, details), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return @case;
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private static async Task<CaseRecord> LoadAsync(MetadataDb db, long caseId, CancellationToken cancellationToken)
    {
        var @case = await db.Cases.FindAsync([caseId], cancellationToken).ConfigureAwait(false) ?? throw new CaseNotFoundException(caseId);
        // Read again under the write lock: a context that tracked the case earlier must not act on a stale state.
        await db.Entry(@case).ReloadAsync(cancellationToken).ConfigureAwait(false);
        return @case;
    }

    private static async Task RequireNoRunningJobAsync(MetadataDb db, CaseRecord @case, string verb, CancellationToken cancellationToken)
    {
        var running = await db.Jobs.AsNoTracking().Where(j => j.CaseId == @case.Id && j.State == JobState.Parsing).OrderBy(j => j.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (running is not null)
        {
            throw new LifecycleRefusedException($"case {@case.Id} is not {verb}: job {running.Id} is parsing file {running.FileId}; try again when it has ended");
        }
    }

    private static Task<bool> IsOwnerAsync(MetadataDb db, long caseId, long userId, CancellationToken cancellationToken) =>
        db.CaseMembers.AnyAsync(m => m.CaseId == caseId && m.UserId == userId && m.Role == MemberRole.Owner, cancellationToken);

    internal static async Task AuthoriseAsync(MetadataDb db, AuditActor actor, CaseRecord @case, string operation, CancellationToken cancellationToken)
    {
        if (actor.UserId is not { } userId)
        {
            return;
        }

        var role = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => (UserRole?)u.Role).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? throw new UserNotFoundException(userId);
        var admin = role == UserRole.Admin;
        var required = CaseLifecycle.RequiredRole(operation);
        var allowed = required switch
        {
            LifecycleRole.Admin => admin,
            LifecycleRole.OwnerOrAdmin => admin || await IsOwnerAsync(db, @case.Id, userId, cancellationToken).ConfigureAwait(false),
            _ => await IsOwnerAsync(db, @case.Id, userId, cancellationToken).ConfigureAwait(false),
        };
        if (!allowed)
        {
            var needs = required switch
            {
                LifecycleRole.Admin => "an administrator",
                LifecycleRole.OwnerOrAdmin => "an owner of the case or an administrator",
                _ => "an owner of the case",
            };
            throw new LifecycleRoleException($"{actor.Name} may not {operation.Replace('_', ' ')} case {@case.Id}: it takes {needs}");
        }
    }
}
