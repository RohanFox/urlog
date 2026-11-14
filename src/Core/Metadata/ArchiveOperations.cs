using Urlog.Core.Audit;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Core.Metadata;

/// <summary>A stored file whose bytes were not what its record says when the archive read it, or that was not there.</summary>
/// <param name="ActualSha256">The SHA-256 of the bytes that went into the archive, or <c>missing</c>.</param>
public sealed record ArchiveFlag(long FileId, string OriginalName, string ExpectedSha256, long ExpectedSize, string ActualSha256, long? ActualSize)
{
    /// <summary>The flag as an audit detail.</summary>
    public Dictionary<string, object?> ToDetails() => new(StringComparer.Ordinal)
    {
        ["file_id"] = FileId,
        ["original_name"] = OriginalName,
        ["expected_sha256"] = ExpectedSha256,
        ["expected_size"] = ExpectedSize,
        ["actual_sha256"] = ActualSha256,
        ["actual_size"] = ActualSize,
    };
}

/// <summary>What a written and verified archive is, for the commit that makes the case <c>archived</c>.</summary>
public sealed record ArchiveCommit(string Directory, string TarSha256, long TarSize, string ManifestSha256, long ManifestSize, int Files, long Bytes, IReadOnlyList<ArchiveFlag> Flagged);

/// <summary>Why a re-import was refused, and for which file when one can be named.</summary>
public sealed record ReimportRefusal(string Reason, long? FileId = null, string? Expected = null, string? Actual = null);

/// <summary>What a re-import found when its last job had ended.</summary>
public sealed record ReimportResult(int Files, int Parsed, IReadOnlyList<long> FailedFiles, IReadOnlyList<Dictionary<string, object?>> RowDifferences);

/// <summary>
/// The metadata side of archive and re-import (design record, "Case lifecycle", Archive). An archive is requested
/// for a closed case and leaves a task; the executor writes and verifies the archive, and only then
/// <see cref="CommitAsync"/> makes the case <c>archived</c>. A re-import is requested for an archived case; the
/// case stays <c>archived</c> while its files are verified, put back and parsed again, and becomes <c>closed</c>
/// when the last job has ended. File records are kept through both.
/// </summary>
public static class ArchiveOperations
{
    public const string StepBuild = "build";
    public const string StepVerify = "verify";
    public const string StepStore = "store";
    public const string StepFiles = "files";
    public const string StepPlace = "place";
    public const string StepParse = "parse";

    /// <summary>The value of <c>check</c> on integrity rows an archive or a re-import writes.</summary>
    public const string CheckArchive = "archive";
    public const string CheckReimport = "reimport";

    /// <summary>Asks for the archive of a closed case. No audit row: <c>case_archive</c> at the commit names who asked.</summary>
    public static Task<CaseTask> RequestArchiveAsync(MetadataDb db, AuditActor actor, long caseId, TimeProvider clock, CancellationToken cancellationToken = default) =>
        RequestAsync(db, actor, caseId, CaseTaskKind.Archive, clock, cancellationToken);

    /// <summary>
    /// Asks for the re-import of an archived case from its archive. The task carries the directory and the two
    /// hashes the archive was committed with.
    /// </summary>
    public static Task<CaseTask> RequestReimportAsync(MetadataDb db, AuditActor actor, long caseId, TimeProvider clock, CancellationToken cancellationToken = default) =>
        RequestAsync(db, actor, caseId, CaseTaskKind.Reimport, clock, cancellationToken);

    /// <summary>Archive and re-import tasks the executor has to carry on with, oldest first.</summary>
    public static async Task<List<CaseTask>> PendingAsync(MetadataDb db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.CaseTasks.AsNoTracking()
            .Where(t => (t.Kind == CaseTaskKind.Archive || t.Kind == CaseTaskKind.Reimport) && (t.State == CaseTaskState.Running || t.State == CaseTaskState.Committed))
            .OrderBy(t => t.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The archive a case was last committed with: its task, <c>done</c>. Null when there is none.</summary>
    public static Task<CaseTask?> LatestArchiveAsync(MetadataDb db, long caseId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.CaseTasks.AsNoTracking().Where(t => t.CaseId == caseId && t.Kind == CaseTaskKind.Archive && (t.State == CaseTaskState.Done || t.State == CaseTaskState.Failed) && t.TarSha256 != null)
            .OrderByDescending(t => t.Id).FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// The commit of an archive, one transaction: the case becomes <c>archived</c>, the jobs held since the close
    /// are cancelled with a <c>job_cancelled</c> row each (a re-import parses every file again), the task takes the
    /// archive's directory and hashes and can only go forward from here, and <c>case_archive</c> lists what was
    /// written and every file that was missing or changed. Called only after the archive was read back from disk
    /// and compared with its manifest.
    /// </summary>
    /// <exception cref="LifecycleRefusedException">The case is no longer closed, or a job of it runs.</exception>
    public static async Task CommitAsync(MetadataDb db, IAuditWriter audit, long taskId, ArchiveCommit archive, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(clock);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            var nowUtc = clock.GetUtcNow().UtcDateTime;
            var task = await TaskAsync(db, taskId, CaseTaskKind.Archive, CaseTaskState.Running, cancellationToken).ConfigureAwait(false);
            var @case = await CaseAsync(db, task.CaseId, cancellationToken).ConfigureAwait(false);
            if (@case.State != CaseState.Closed)
            {
                throw new LifecycleRefusedException($"case {@case.Id} is {Names.Of(@case.State)}; an archive is committed for a closed case");
            }

            if (await db.Jobs.AnyAsync(j => j.CaseId == @case.Id && j.State == JobState.Parsing, cancellationToken).ConfigureAwait(false))
            {
                throw new LifecycleRefusedException($"case {@case.Id} is not archived: a job of it is recorded as running");
            }

            // The jobs held since the close, one row each.
            var held = await db.Jobs.Where(j => j.CaseId == @case.Id && j.State == JobState.Queued).OrderBy(j => j.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var job in held)
            {
                await db.Entry(job).ReloadAsync(cancellationToken).ConfigureAwait(false);
                job.State = JobState.Cancelled;
                job.FinishedAt = nowUtc;
                job.Message = "cancelled: the case was archived; a re-import parses every file again";
                var file = await db.Files.AsNoTracking().FirstAsync(f => f.Id == job.FileId, cancellationToken).ConfigureAwait(false);
                var cancelled = new Dictionary<string, object?> { ["job_id"] = job.Id, ["kind"] = Names.Of(job.Kind), ["reason"] = "case_archive" };
                await audit.AppendAsync(db, AuditEntry.ForFile(AuditActions.SystemActor, null, AuditActions.JobCancelled, file, @case.Name, cancelled), cancellationToken).ConfigureAwait(false);
            }

            var from = @case.State;
            CaseLifecycle.Apply(@case, CaseState.Archived, nowUtc);
            task.State = CaseTaskState.Committed;
            task.Step = StepStore;
            task.ArchiveDirectory = archive.Directory;
            task.TarSha256 = archive.TarSha256;
            task.ManifestSha256 = archive.ManifestSha256;
            task.UpdatedAt = nowUtc;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            var details = new Dictionary<string, object?>
            {
                ["from"] = Names.Of(from),
                ["to"] = Names.Of(CaseState.Archived),
                ["requested_by"] = task.RequestedBy,
                ["archive"] = Path.GetFileName(archive.Directory),
                ["tar_sha256"] = archive.TarSha256,
                ["tar_size"] = archive.TarSize,
                ["manifest_sha256"] = archive.ManifestSha256,
                ["manifest_size"] = archive.ManifestSize,
                ["files"] = archive.Files,
                ["bytes"] = archive.Bytes,
                ["jobs_cancelled"] = held.Count,
                ["flagged"] = archive.Flagged.Select(f => (object?)f.ToDetails()).ToList(),
                ["task_id"] = task.Id,
            };
            await audit.AppendAsync(db, AuditEntry.ForCase(task.RequestedBy, task.RequestedById, AuditActions.CaseArchive, @case, details), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>The end of an archive: the partitions are dropped and the files removed. The task is <c>done</c> and <c>case_archive_complete</c> says what went.</summary>
    public static async Task CompleteArchiveAsync(MetadataDb db, IAuditWriter audit, long taskId, int partitionsDropped, int filesRemoved, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(clock);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            var nowUtc = clock.GetUtcNow().UtcDateTime;
            var task = await TaskAsync(db, taskId, CaseTaskKind.Archive, CaseTaskState.Committed, cancellationToken).ConfigureAwait(false);
            var @case = await CaseAsync(db, task.CaseId, cancellationToken).ConfigureAwait(false);
            task.State = CaseTaskState.Done;
            task.Step = null;
            task.Message = null;
            task.UpdatedAt = nowUtc;
            task.FinishedAt = nowUtc;
            task.VerifiedAt = nowUtc;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var details = new Dictionary<string, object?> { ["archive"] = Path.GetFileName(task.ArchiveDirectory), ["tar_sha256"] = task.TarSha256, ["partitions_dropped"] = partitionsDropped, ["files_removed"] = filesRemoved, ["task_id"] = task.Id };
            await audit.AppendAsync(db, AuditEntry.ForCase(AuditActions.SystemActor, null, AuditActions.CaseArchiveComplete, @case, details), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// An archive that is given up: its attempts are used, or its written archive no longer matches what was
    /// committed. The task is <c>failed</c> with the reason and <c>case_archive_failed</c> is in the chain. The case
    /// stays in the state it has. False when the task is not the executor's any more.
    /// </summary>
    public static async Task<bool> FailArchiveAsync(MetadataDb db, IAuditWriter audit, long taskId, string reason, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(clock);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            var nowUtc = clock.GetUtcNow().UtcDateTime;
            var task = await db.CaseTasks.FirstOrDefaultAsync(t => t.Id == taskId && t.Kind == CaseTaskKind.Archive, cancellationToken).ConfigureAwait(false);
            if (task is null)
            {
                return false;
            }

            await db.Entry(task).ReloadAsync(cancellationToken).ConfigureAwait(false);
            if (task.State is CaseTaskState.Failed or CaseTaskState.Withdrawn)
            {
                return false;
            }

            var was = task.State;
            var @case = await CaseAsync(db, task.CaseId, cancellationToken).ConfigureAwait(false);
            task.State = CaseTaskState.Failed;
            task.Message = Trim(reason);
            task.UpdatedAt = nowUtc;
            task.FinishedAt = nowUtc;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var details = new Dictionary<string, object?>
            {
                ["reason"] = Trim(reason),
                ["task_state"] = Names.Of(was),
                ["step"] = task.Step,
                ["attempts"] = task.Attempt,
                ["case_state"] = Names.Of(@case.State),
                ["archive"] = task.ArchiveDirectory is null ? null : Path.GetFileName(task.ArchiveDirectory),
                ["requested_by"] = task.RequestedBy,
                ["task_id"] = task.Id,
            };
            await audit.AppendAsync(db, AuditEntry.ForCase(AuditActions.SystemActor, null, AuditActions.CaseArchiveFailed, @case, details), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// A re-import that is refused: the task is <c>failed</c>, <c>case_reimport_refused</c> says why, and when the
    /// refusal is about one file an <c>integrity_mismatch</c> row (<c>check: reimport</c>) names it with what was
    /// expected and what the archive holds. The case stays <c>archived</c>; no file record changes.
    /// </summary>
    public static async Task RefuseReimportAsync(MetadataDb db, IAuditWriter audit, long taskId, ReimportRefusal refusal, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(refusal);
        ArgumentNullException.ThrowIfNull(clock);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            var nowUtc = clock.GetUtcNow().UtcDateTime;
            var task = await TaskAsync(db, taskId, CaseTaskKind.Reimport, CaseTaskState.Running, cancellationToken).ConfigureAwait(false);
            var @case = await CaseAsync(db, task.CaseId, cancellationToken).ConfigureAwait(false);
            task.State = CaseTaskState.Failed;
            task.Message = Trim(refusal.Reason);
            task.UpdatedAt = nowUtc;
            task.FinishedAt = nowUtc;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            var file = refusal.FileId is { } id ? await db.Files.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id && f.CaseId == @case.Id, cancellationToken).ConfigureAwait(false) : null;
            if (file is not null)
            {
                var mismatch = new Dictionary<string, object?> { ["check"] = CheckReimport, ["expected"] = refusal.Expected, ["actual"] = refusal.Actual, ["reason"] = Trim(refusal.Reason), ["archive"] = Path.GetFileName(task.ArchiveDirectory) };
                await audit.AppendAsync(db, AuditEntry.ForFile(AuditActions.SystemActor, null, AuditActions.IntegrityMismatch, file, @case.Name, mismatch), cancellationToken).ConfigureAwait(false);
            }

            var details = new Dictionary<string, object?>
            {
                ["reason"] = Trim(refusal.Reason),
                ["file_id"] = refusal.FileId,
                ["original_name"] = file?.OriginalName,
                ["expected"] = refusal.Expected,
                ["actual"] = refusal.Actual,
                ["archive"] = Path.GetFileName(task.ArchiveDirectory),
                ["requested_by"] = task.RequestedBy,
                ["task_id"] = task.Id,
            };
            await audit.AppendAsync(db, AuditEntry.ForCase(task.RequestedBy, task.RequestedById, AuditActions.CaseReimportRefused, @case, details), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// The files are verified and back in place: one transaction queues a parse for every record that had a
    /// committed parse, writes the new place of a file that could not go back where it was
    /// (<paramref name="moved"/>: file id to its path under the case's directory), and moves the task on. The case
    /// stays <c>archived</c>; the runner claims these jobs because the task is under way. Returns the jobs queued.
    /// </summary>
    public static async Task<int> QueueReimportAsync(MetadataDb db, long taskId, IReadOnlyDictionary<long, string> moved, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(moved);
        ArgumentNullException.ThrowIfNull(clock);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            var nowUtc = clock.GetUtcNow().UtcDateTime;
            var task = await TaskAsync(db, taskId, CaseTaskKind.Reimport, CaseTaskState.Running, cancellationToken).ConfigureAwait(false);
            foreach (var (fileId, path) in moved)
            {
                await db.Files.Where(f => f.Id == fileId && f.CaseId == task.CaseId).ExecuteUpdateAsync(s => s.SetProperty(f => f.StoredPath, path), cancellationToken).ConfigureAwait(false);
                // Members of a container name its stored file as theirs.
                await db.Files.Where(f => f.RootFileId == fileId && f.CaseId == task.CaseId).ExecuteUpdateAsync(s => s.SetProperty(f => f.StoredPath, path), cancellationToken).ConfigureAwait(false);
            }

            var parsed = await db.Files.AsNoTracking().Where(f => f.CaseId == task.CaseId && f.State == FileState.Parsed).OrderBy(f => f.Id).Select(f => f.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var fileId in parsed)
            {
                if (!await db.Jobs.AnyAsync(j => j.FileId == fileId && (j.State == JobState.Queued || j.State == JobState.Parsing), cancellationToken).ConfigureAwait(false))
                {
                    db.Jobs.Add(new Job { CaseId = task.CaseId, FileId = fileId, Kind = JobKind.Reparse, CreatedAt = nowUtc });
                }
            }

            task.State = CaseTaskState.Committed;
            task.Step = StepParse;
            task.UpdatedAt = nowUtc;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return parsed.Count;
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// The end of a re-import, once no job of the case waits or runs: the case goes from <c>archived</c> to
    /// <c>closed</c> and <c>case_reimport</c> names the files whose parse failed and the files whose row count
    /// differs from the one in the manifest (<paramref name="rowsBefore"/>; a newer parser may count differently).
    /// Null while a job is still open.
    /// </summary>
    public static async Task<ReimportResult?> CompleteReimportAsync(MetadataDb db, IAuditWriter audit, long taskId, IReadOnlyDictionary<long, long>? rowsBefore, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(clock);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            var nowUtc = clock.GetUtcNow().UtcDateTime;
            var task = await TaskAsync(db, taskId, CaseTaskKind.Reimport, CaseTaskState.Committed, cancellationToken).ConfigureAwait(false);
            if (await db.Jobs.AnyAsync(j => j.CaseId == task.CaseId && (j.State == JobState.Queued || j.State == JobState.Parsing), cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var @case = await CaseAsync(db, task.CaseId, cancellationToken).ConfigureAwait(false);
            var files = await db.Files.AsNoTracking().Where(f => f.CaseId == task.CaseId).OrderBy(f => f.Id).Select(f => new { f.Id, f.RowCount, f.State }).ToListAsync(cancellationToken).ConfigureAwait(false);
            // The newest job of each file since the task was asked for says how its parse ended.
            var jobs = await db.Jobs.AsNoTracking().Where(j => j.CaseId == task.CaseId && j.CreatedAt >= task.CreatedAt && j.Kind != JobKind.Expand).GroupBy(j => j.FileId)
                .Select(g => new { FileId = g.Key, Last = g.Max(j => j.Id) }).ToListAsync(cancellationToken).ConfigureAwait(false);
            var last = jobs.Select(j => j.Last).ToList();
            var states = await db.Jobs.AsNoTracking().Where(j => last.Contains(j.Id)).Select(j => new { j.FileId, j.State }).ToListAsync(cancellationToken).ConfigureAwait(false);
            var failed = states.Where(s => s.State != JobState.Done).Select(s => s.FileId).Order().ToList();
            var differences = new List<Dictionary<string, object?>>();
            foreach (var file in files)
            {
                if (rowsBefore is not null && rowsBefore.TryGetValue(file.Id, out var before) && before != file.RowCount && !failed.Contains(file.Id))
                {
                    differences.Add(new Dictionary<string, object?>(StringComparer.Ordinal) { ["file_id"] = file.Id, ["rows_before"] = before, ["rows_after"] = file.RowCount });
                }
            }

            var from = @case.State;
            CaseLifecycle.Apply(@case, CaseState.Closed, nowUtc);
            task.State = CaseTaskState.Done;
            task.Step = null;
            task.Message = null;
            task.UpdatedAt = nowUtc;
            task.FinishedAt = nowUtc;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var result = new ReimportResult(files.Count, states.Count(s => s.State == JobState.Done), failed, differences);
            var details = new Dictionary<string, object?>
            {
                ["from"] = Names.Of(from),
                ["to"] = Names.Of(CaseState.Closed),
                ["archive"] = Path.GetFileName(task.ArchiveDirectory),
                ["tar_sha256"] = task.TarSha256,
                ["manifest_sha256"] = task.ManifestSha256,
                ["files"] = result.Files,
                ["parsed"] = result.Parsed,
                ["failed_files"] = failed.Select(id => (object?)id).ToList(),
                ["row_differences"] = differences.Select(d => (object?)d).ToList(),
                ["rows_compared"] = rowsBefore is not null,
                ["requested_by"] = task.RequestedBy,
                ["task_id"] = task.Id,
            };
            await audit.AppendAsync(db, AuditEntry.ForCase(task.RequestedBy, task.RequestedById, AuditActions.CaseReimport, @case, details), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// The archives a sweep has to read: the latest committed archive of every case that is <c>archived</c> and
    /// has no other work under way, last verified before <paramref name="before"/>, least recently verified first.
    /// </summary>
    public static async Task<List<CaseTask>> DueArchivesAsync(MetadataDb db, DateTime before, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var tasks = await (
            from t in db.CaseTasks.AsNoTracking()
            join c in db.Cases.AsNoTracking() on t.CaseId equals c.Id
            where t.Kind == CaseTaskKind.Archive && t.State == CaseTaskState.Done && c.State == CaseState.Archived
                && (t.VerifiedAt == null || t.VerifiedAt < before)
                && !db.CaseTasks.Any(o => o.CaseId == c.Id && (o.State == CaseTaskState.Running || o.State == CaseTaskState.Committed))
                && !db.CaseTasks.Any(o => o.CaseId == c.Id && o.Kind == CaseTaskKind.Archive && o.Id > t.Id)
            orderby t.VerifiedAt, t.Id
            select t).ToListAsync(cancellationToken).ConfigureAwait(false);
        return tasks;
    }

    /// <summary>
    /// The result of a sweep's read of one archive, with its audit row on the case (<c>check: archive</c>).
    /// A match stamps <c>verified_at</c>. A mismatch (<paramref name="actual"/>: what was found, per file of the
    /// archive) also marks the archive's task <c>failed</c>, so a re-import is refused before it reads anything.
    /// </summary>
    public static async Task RecordArchiveCheckAsync(MetadataDb db, IAuditWriter audit, long taskId, IReadOnlyDictionary<string, object?>? actual, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(clock);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            var nowUtc = clock.GetUtcNow().UtcDateTime;
            var task = await db.CaseTasks.FirstOrDefaultAsync(t => t.Id == taskId && t.Kind == CaseTaskKind.Archive && t.State == CaseTaskState.Done, cancellationToken).ConfigureAwait(false);
            if (task is null)
            {
                return;
            }

            await db.Entry(task).ReloadAsync(cancellationToken).ConfigureAwait(false);
            var @case = await CaseAsync(db, task.CaseId, cancellationToken).ConfigureAwait(false);
            task.VerifiedAt = nowUtc;
            var details = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["check"] = CheckArchive,
                ["archive"] = Path.GetFileName(task.ArchiveDirectory),
                ["tar_sha256"] = task.TarSha256,
                ["manifest_sha256"] = task.ManifestSha256,
                ["task_id"] = task.Id,
            };
            if (actual is not null)
            {
                foreach (var (key, value) in actual)
                {
                    details[key] = value;
                }

                task.State = CaseTaskState.Failed;
                task.Message = "the integrity sweep found the archive changed or gone; it cannot be re-imported";
                task.UpdatedAt = nowUtc;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await audit.AppendAsync(db, AuditEntry.ForCase(AuditActions.SystemActor, null, actual is null ? AuditActions.IntegrityOk : AuditActions.IntegrityMismatch, @case, details), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private static async Task<CaseTask> RequestAsync(MetadataDb db, AuditActor actor, long caseId, CaseTaskKind kind, TimeProvider clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(clock);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            var nowUtc = clock.GetUtcNow().UtcDateTime;
            var @case = await CaseAsync(db, caseId, cancellationToken).ConfigureAwait(false);
            var (needs, operation) = kind == CaseTaskKind.Archive ? (CaseState.Closed, CaseLifecycle.Archive) : (CaseState.Archived, CaseLifecycle.Reimport);
            if (@case.State != needs)
            {
                throw new LifecycleRefusedException($"case {caseId} is {Names.Of(@case.State)}; {(kind == CaseTaskKind.Archive ? "only a closed case is archived" : "only an archived case is re-imported")}");
            }

            await LifecycleOperations.AuthoriseAsync(db, actor, @case, operation, cancellationToken).ConfigureAwait(false);
            var unfinished = await db.CaseTasks.AsNoTracking().Where(t => t.CaseId == caseId && (t.State == CaseTaskState.Requested || t.State == CaseTaskState.Running || t.State == CaseTaskState.Committed))
                .OrderBy(t => t.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (unfinished is not null)
            {
                throw new LifecycleRefusedException($"case {caseId} has unfinished work ({Names.Of(unfinished.Kind)}, {Names.Of(unfinished.State)}, asked for by {unfinished.RequestedBy}); nothing else changes its state until that has ended");
            }

            var running = await db.Jobs.AsNoTracking().Where(j => j.CaseId == caseId && j.State == JobState.Parsing).Select(j => (long?)j.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (running is { } jobId)
            {
                throw new LifecycleRefusedException($"case {caseId}: job {jobId} is running; try again when it has ended");
            }

            var task = new CaseTask { CaseId = caseId, Kind = kind, State = CaseTaskState.Running, RequestedBy = actor.Name, RequestedById = actor.UserId, CreatedAt = nowUtc, UpdatedAt = nowUtc };
            if (kind == CaseTaskKind.Reimport)
            {
                var archive = await LatestArchiveAsync(db, caseId, cancellationToken).ConfigureAwait(false);
                if (archive is null)
                {
                    throw new LifecycleRefusedException($"case {caseId} has no committed archive on record; there is nothing to re-import from");
                }

                if (archive.State != CaseTaskState.Done)
                {
                    throw new LifecycleRefusedException($"case {caseId}: its archive is {Names.Of(archive.State)} ({archive.Message}); it is not re-imported from");
                }

                task.ArchiveDirectory = archive.ArchiveDirectory;
                task.TarSha256 = archive.TarSha256;
                task.ManifestSha256 = archive.ManifestSha256;
            }

            db.CaseTasks.Add(task);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return task;
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private static async Task<CaseTask> TaskAsync(MetadataDb db, long taskId, CaseTaskKind kind, CaseTaskState state, CancellationToken cancellationToken)
    {
        var task = await db.CaseTasks.FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken).ConfigureAwait(false) ?? throw new LifecycleRefusedException($"task {taskId} does not exist");
        await db.Entry(task).ReloadAsync(cancellationToken).ConfigureAwait(false);
        if (task.Kind != kind || task.State != state)
        {
            throw new LifecycleRefusedException($"task {taskId} is a {Names.Of(task.Kind)} in {Names.Of(task.State)}, not a {Names.Of(kind)} in {Names.Of(state)}");
        }

        return task;
    }

    private static async Task<CaseRecord> CaseAsync(MetadataDb db, long caseId, CancellationToken cancellationToken)
    {
        var @case = await db.Cases.FindAsync([caseId], cancellationToken).ConfigureAwait(false) ?? throw new CaseNotFoundException(caseId);
        await db.Entry(@case).ReloadAsync(cancellationToken).ConfigureAwait(false);
        return @case;
    }

    private static string Trim(string text) => text.Length <= 2000 ? text : text[..2000];
}
