using System.Diagnostics;
using Urlog.Core.Audit;
using Urlog.Core.Jobs;
using Urlog.Core.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Urlog.Core.Lifecycle;

/// <summary>Where the lifecycle's work finds and puts things on this machine.</summary>
/// <param name="EvidenceRoot">The evidence root; null when files stay where they were found and nothing of them is removed.</param>
/// <param name="CacheRoot">The root of the seekable copies of containers; null for none.</param>
/// <param name="BackupDirectory">Where the backup before a purge goes.</param>
/// <param name="ArchiveRoot">Where archives are written, one directory per case; null when this process archives nothing.</param>
public sealed record LifecyclePaths(string? EvidenceRoot, string? CacheRoot, string BackupDirectory, string? ArchiveRoot = null)
{
    /// <summary>
    /// The archive root as an absolute path, or null for none.
    /// </summary>
    /// <exception cref="ArgumentException">It is the evidence root or the cache root, lies inside one, or contains one: an archive must outlive the removal of what it was made from.</exception>
    public string? CheckedArchiveRoot()
    {
        if (string.IsNullOrWhiteSpace(ArchiveRoot))
        {
            return null;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var archive = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ArchiveRoot));
        foreach (var (name, other) in new[] { ("evidence root", EvidenceRoot), ("cache root", CacheRoot) })
        {
            if (string.IsNullOrWhiteSpace(other))
            {
                continue;
            }

            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(other));
            if (string.Equals(archive, root, comparison) || archive.StartsWith(root + Path.DirectorySeparatorChar, comparison) || root.StartsWith(archive + Path.DirectorySeparatorChar, comparison))
            {
                throw new ArgumentException($"the archive root '{archive}' and the {name} '{root}' must be apart: neither may be the other or lie inside it", nameof(ArchiveRoot));
            }
        }

        return archive;
    }
}

/// <summary>
/// Carries out the lifecycle work that outlasts a transaction (design record, "Consistency", Purge). It runs in
/// the process that holds the runner lock: the job runner's lifecycle loop, the startup reconciler, or a command
/// that took the lock for itself.
/// <para>
/// A purge is five steps. Backup: <c>VACUUM INTO</c> with its <c>backup</c> row. Mark: one transaction makes the
/// case <c>purging</c>, cancels its queued jobs and writes <c>case_purge</c> with the backup's hash. Store: the
/// case's partitions are dropped, its field statistics and the generation rows of its files deleted, and both are
/// read back. Files: the case's directory under the evidence root (stored files, unfinished and finished
/// uploads) and its cache directory are removed. Metadata: one transaction writes <c>case_purge_complete</c> and
/// deletes every row of the case.
/// </para>
/// <para>
/// From the mark on a purge only goes forward. Every step leaves nothing behind when it is repeated, so a resume
/// starts again at the store step whatever the last run reached. There is no undo and no clean-up on failure in
/// here: an exception and a killed process leave the same state, and the same resume finishes both.
/// </para>
/// </summary>
public sealed partial class LifecycleExecutor(MetadataStore store, IJobEventStore events, LifecycleOptions options, LifecyclePaths paths, ILogger logger)
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);
    private readonly Dictionary<long, (int Failures, long NotBefore)> _backoff = [];

    /// <summary>Called with each path just before it is removed; tests stop a purge part-way with it.</summary>
    public Action<string>? BeforeRemoval { get; init; }

    /// <summary>Called with the name of each step just before it starts (<c>backup</c>, <c>mark</c>, <c>store</c>, <c>files</c>, <c>metadata</c>).</summary>
    public Action<string>? OnStep { get; init; }

    /// <summary>How long the store's delete of generation rows may take to show as applied before the step fails.</summary>
    public TimeSpan DeleteWait { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Releases the scheduled purges that are due, then carries out every purge that is released or was
    /// interrupted. A purge that fails keeps its task with the reason and is tried again later, each time after a
    /// longer wait; the others still run. Returns the cases purged.
    /// </summary>
    /// <exception cref="StoreIdentityException">The event store is not the one this metadata file belongs to. Nothing was dropped.</exception>
    public async Task<int> RunPendingAsync(CancellationToken cancellationToken)
    {
        await ReleaseDueAsync(cancellationToken).ConfigureAwait(false);
        List<CaseTask> tasks;
        List<long> unowned;
        await using (var db = store.CreateDbContext())
        {
            tasks = await LifecycleOperations.PendingPurgesAsync(db, cancellationToken).ConfigureAwait(false);
            // A case marked purging with no task row (marked by hand, or by a build without tasks) is finished all the same.
            var owned = tasks.Select(t => t.CaseId).ToHashSet();
            unowned = (await db.Cases.AsNoTracking().Where(c => c.State == CaseState.Purging).OrderBy(c => c.Id).Select(c => c.Id).ToListAsync(cancellationToken).ConfigureAwait(false))
                .Where(id => !owned.Contains(id)).ToList();
        }

        var purged = 0;
        foreach (var task in tasks)
        {
            purged += await AttemptAsync(task.CaseId, task, cancellationToken).ConfigureAwait(false) ? 1 : 0;
        }

        foreach (var caseId in unowned)
        {
            purged += await AttemptAsync(caseId, null, cancellationToken).ConfigureAwait(false) ? 1 : 0;
        }

        await RunArchivesAsync(cancellationToken).ConfigureAwait(false);
        return purged;
    }

    /// <summary>True when a purge is released or interrupted and waits for this executor.</summary>
    public async Task<bool> HasPendingAsync(CancellationToken cancellationToken)
    {
        await using var db = store.CreateDbContext();
        // A re-import whose jobs are running waits for them, not for this executor.
        return await db.CaseTasks.AnyAsync(t => (t.State == CaseTaskState.Running || t.State == CaseTaskState.Committed) && !(t.Kind == CaseTaskKind.Reimport && t.State == CaseTaskState.Committed), cancellationToken).ConfigureAwait(false)
            || await db.Cases.AnyAsync(c => c.State == CaseState.Purging, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One purge from wherever it stands to the end. False when it no longer stands (the case was restored before
    /// the mark). Throws on anything that stops it; the task and the case stay as the last finished step left them.
    /// </summary>
    public async Task<bool> PurgeAsync(long caseId, CaseTask? task, CancellationToken cancellationToken)
    {
        if (task is { State: CaseTaskState.Running })
        {
            // Before the mark. A run that stopped between the backup and the mark takes a new backup: the mark
            // names the backup taken immediately before it.
            await StepAsync(task, "backup", newAttempt: true, cancellationToken).ConfigureAwait(false);
            // Backups are named by the second and never overwritten: an attempt that follows another within the
            // same second waits for the next one.
            for (var i = 0; i < 12 && File.Exists(SqliteBackup.PathFor(store, paths.BackupDirectory)); i++)
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }

            var backup = await SqliteBackup.TakeAsync(store, paths.BackupDirectory, SqliteBackup.ReasonPrePurge, task.ConfirmedBy ?? task.RequestedBy, task.ConfirmedById ?? task.RequestedById, cancellationToken).ConfigureAwait(false);
            var mark = new PurgeBackup(backup.Sha256, Path.GetFileName(backup.Path), backup.Size);
            OnStep?.Invoke("mark");
            var marked = await BusyRetry.Default.RunAsync(
                async () =>
                {
                    await using var db = store.CreateDbContext();
                    return await LifecycleOperations.MarkPurgingAsync(db, store.Audit, task.Id, mark, store.Clock, cancellationToken).ConfigureAwait(false);
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!marked)
            {
                LifecycleLog.PurgeWithdrawn(logger, caseId);
                return false;
            }

            LifecycleLog.PurgeMarked(logger, caseId, backup.Sha256);
            // The mark wrote the step with itself.
            OnStep?.Invoke(LifecycleOperations.StepStore);
        }
        else
        {
            await StepAsync(task, LifecycleOperations.StepStore, newAttempt: true, cancellationToken).ConfigureAwait(false);
        }

        // Store. The identity is read again before each group of statements that remove something.
        await StoreIdentityGuard.RequireAsync(store.Path, events, cancellationToken).ConfigureAwait(false);
        var dropped = await DropPartitionsAsync((ulong)caseId, cancellationToken).ConfigureAwait(false);
        List<long> fileIds;
        await using (var db = store.CreateDbContext())
        {
            fileIds = await LifecycleOperations.FileIdsAsync(db, caseId, cancellationToken).ConfigureAwait(false);
        }

        await StoreIdentityGuard.RequireAsync(store.Path, events, cancellationToken).ConfigureAwait(false);
        await events.DeleteCaseFieldStatsAsync((ulong)caseId, cancellationToken).ConfigureAwait(false);
        await ClearGenerationsAsync(caseId, fileIds, cancellationToken).ConfigureAwait(false);

        // Files.
        await StepAsync(task, LifecycleOperations.StepFiles, newAttempt: false, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(paths.EvidenceRoot))
        {
            await using var db = store.CreateDbContext();
            var stored = await db.Files.AsNoTracking().Where(f => f.CaseId == caseId && f.ParentFileId == null).Select(f => f.StoredPath).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (stored.Any(p => !Path.IsPathRooted(p)))
            {
                // The records are the only way to the files: they are not deleted while the files cannot be reached.
                throw new InvalidOperationException($"case {caseId} has files stored under an evidence root and this process was given none; start it with the evidence root so the files can be removed");
            }
        }

        var removed = RemoveCaseDirectories(caseId);

        // Metadata.
        await StepAsync(task, LifecycleOperations.StepMetadata, newAttempt: false, cancellationToken).ConfigureAwait(false);
        var summary = await BusyRetry.Default.RunAsync(
            async () =>
            {
                await using var db = store.CreateDbContext();
                return await LifecycleOperations.CompletePurgeAsync(db, store.Audit, caseId, store.Clock, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (summary is not null)
        {
            LifecycleLog.Purged(logger, caseId, dropped, removed, summary.FileCount, summary.EvidenceSha256);
        }

        return true;
    }

    private async Task<bool> AttemptAsync(long caseId, CaseTask? task, CancellationToken cancellationToken)
    {
        if (_backoff.TryGetValue(caseId, out var wait) && Stopwatch.GetTimestamp() < wait.NotBefore)
        {
            return false;
        }

        try
        {
            var purged = await PurgeAsync(caseId, task, cancellationToken).ConfigureAwait(false);
            _backoff.Remove(caseId);
            return purged;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            var failures = wait.Failures + 1;
            var delay = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, 30 * Math.Pow(2, Math.Min(failures - 1, 10))));
            _backoff[caseId] = (failures, Stopwatch.GetTimestamp() + (long)(delay.TotalSeconds * Stopwatch.Frequency));
            LifecycleLog.PurgeStopped(logger, caseId, e.Message, delay.TotalSeconds);
            if (task is not null)
            {
                try
                {
                    await using var db = store.CreateDbContext();
                    await LifecycleOperations.NoteAsync(db, task.Id, e.Message, store.Clock, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception note) when (note is not OperationCanceledException)
                {
                    // The reason is in the log; the task row keeps its last one.
                }
            }

            if (e is StoreIdentityException)
            {
                throw;
            }

            return false;
        }
    }

    private async Task ReleaseDueAsync(CancellationToken cancellationToken)
    {
        if (!options.ScheduledPurge || options.TwoPersonPurge)
        {
            return;
        }

        await using var db = store.CreateDbContext();
        foreach (var caseId in await LifecycleOperations.DueForPurgeAsync(db, options.PurgeAfter, Now(), cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await LifecycleOperations.RequestPurgeAsync(db, store.Audit, AuditActor.System, caseId, twoPerson: false, store.Clock, cancellationToken).ConfigureAwait(false);
                LifecycleLog.PurgeDue(logger, caseId, options.PurgeAfter.TotalDays);
            }
            catch (MetadataException e)
            {
                // Restored or purged between the read and the request.
                LifecycleLog.PurgeStopped(logger, caseId, e.Message, 0);
            }
        }
    }

    private async Task<int> DropPartitionsAsync(ulong caseId, CancellationToken cancellationToken)
    {
        var dropped = 0;
        for (var round = 0; round < 3; round++)
        {
            var partitions = await events.CasePartitionsAsync(caseId, cancellationToken).ConfigureAwait(false);
            if (partitions.Count == 0)
            {
                return dropped;
            }

            foreach (var partition in partitions)
            {
                await events.DropCasePartitionAsync(caseId, partition.Month, cancellationToken).ConfigureAwait(false);
                dropped++;
            }
        }

        var left = await events.CasePartitionsAsync(caseId, cancellationToken).ConfigureAwait(false);
        if (left.Count > 0)
        {
            throw new InvalidOperationException($"case {caseId} still has {left.Count} partition(s) with active parts after three rounds of drops (first: {left[0].Month}); something is writing to it");
        }

        return dropped;
    }

    private async Task ClearGenerationsAsync(long caseId, List<long> fileIds, CancellationToken cancellationToken)
    {
        if (fileIds.Count == 0)
        {
            return;
        }

        var ids = fileIds.ConvertAll(id => (ulong)id);
        await events.ClearFileGenerationsAsync(ids, cancellationToken).ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            var left = await events.CountFileGenerationsAsync(ids, cancellationToken).ConfigureAwait(false);
            if (left == 0)
            {
                return;
            }

            if (Stopwatch.GetElapsedTime(started) > DeleteWait)
            {
                throw new InvalidOperationException($"the event store still holds {left} generation row(s) of case {caseId}'s files {DeleteWait.TotalSeconds:F0} s after the delete; the purge stops before any file is removed");
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
    }

    private int RemoveCaseDirectories(long caseId)
    {
        var guard = new GuardedDelete([paths.EvidenceRoot, paths.CacheRoot, _archiveRoot], BeforeRemoval);
        var removed = 0;
        if (_archiveRoot is not null)
        {
            // The case's archives go with it: after a purge nothing of the case is left on this machine.
            removed += guard.RemoveTree(CaseFolders.CaseDirectory(_archiveRoot, caseId));
        }

        if (!string.IsNullOrWhiteSpace(paths.EvidenceRoot))
        {
            // Everything of the case under the evidence root: stored files, .uploads/ and web/. A file recorded
            // with a path outside the root was never copied here and is not removed.
            removed += guard.RemoveTree(CaseFolders.CaseDirectory(paths.EvidenceRoot, caseId));
        }

        if (!string.IsNullOrWhiteSpace(paths.CacheRoot))
        {
            removed += guard.RemoveTree(CaseFolders.CaseDirectory(paths.CacheRoot, caseId));
        }

        return removed;
    }

    private async Task StepAsync(CaseTask? task, string step, bool newAttempt, CancellationToken cancellationToken)
    {
        OnStep?.Invoke(step);
        if (task is null)
        {
            return;
        }

        await BusyRetry.Default.RunAsync(
            async () =>
            {
                await using var db = store.CreateDbContext();
                await LifecycleOperations.SetStepAsync(db, task.Id, step, newAttempt, store.Clock, cancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private DateTime Now() => store.Clock.GetUtcNow().UtcDateTime;
}

/// <summary>The lifecycle's log lines. None carries evidence bytes or a full evidence path.</summary>
internal static partial class LifecycleLog
{
    [LoggerMessage(EventId = 200, Level = LogLevel.Information, Message = "case {CaseId}: soft-deleted for more than {Days:F0} days; its purge is released")]
    public static partial void PurgeDue(ILogger logger, long caseId, double days);

    [LoggerMessage(EventId = 201, Level = LogLevel.Information, Message = "case {CaseId}: marked purging; metadata backup sha256 {BackupSha256}")]
    public static partial void PurgeMarked(ILogger logger, long caseId, string backupSha256);

    [LoggerMessage(EventId = 202, Level = LogLevel.Information, Message = "case {CaseId}: purged; {Partitions} partition(s) dropped, {Removed} file(s) removed, {Files} file record(s) deleted, evidence digest {EvidenceSha256}")]
    public static partial void Purged(ILogger logger, long caseId, int partitions, int removed, int files, string evidenceSha256);

    [LoggerMessage(EventId = 203, Level = LogLevel.Warning, Message = "case {CaseId}: the purge stopped ({Reason}); it is taken up again, not before {Seconds:F0} s from now")]
    public static partial void PurgeStopped(ILogger logger, long caseId, string reason, double seconds);

    [LoggerMessage(EventId = 204, Level = LogLevel.Information, Message = "case {CaseId}: the purge was withdrawn before its mark; nothing was removed")]
    public static partial void PurgeWithdrawn(ILogger logger, long caseId);

    [LoggerMessage(EventId = 210, Level = LogLevel.Information, Message = "integrity sweep: {Ok} file(s) match, {Mismatched} changed, {Missing} missing, {Unreadable} could not be read, {Megabytes:F1} MB in {Seconds:F1} s; {State}")]
    public static partial void SweepEnded(ILogger logger, int ok, int mismatched, int missing, int unreadable, double megabytes, double seconds, string state);

    [LoggerMessage(EventId = 211, Level = LogLevel.Error, Message = "integrity sweep: file {FileId} of case {CaseId} is quarantined: the record says sha256 {Expected}, the stored bytes say {Actual}")]
    public static partial void SweepMismatch(ILogger logger, long fileId, long caseId, string expected, string actual);

    [LoggerMessage(EventId = 212, Level = LogLevel.Warning, Message = "integrity sweep: file {FileId} of case {CaseId} could not be read ({Reason}); no result is recorded and the next sweep tries again")]
    public static partial void SweepUnreadable(ILogger logger, long fileId, long caseId, string reason);

    [LoggerMessage(EventId = 214, Level = LogLevel.Information, Message = "integrity sweep: holds for a running job; {Verified} file(s) recorded as matching and {Quarantined} quarantined so far, {Bytes} bytes read")]
    public static partial void SweepHolds(ILogger logger, int verified, int quarantined, long bytes);

    [LoggerMessage(EventId = 215, Level = LogLevel.Information, Message = "integrity sweep: goes on after holding {Seconds:F1} s; {Bytes} bytes read")]
    public static partial void SweepGoesOn(ILogger logger, double seconds, long bytes);

    [LoggerMessage(EventId = 216, Level = LogLevel.Error, Message = "integrity sweep: the archive {Archive} of case {CaseId} is changed or gone; it cannot be re-imported")]
    public static partial void SweepArchiveMismatch(ILogger logger, long caseId, string archive);

    [LoggerMessage(EventId = 213, Level = LogLevel.Warning, Message = "lifecycle work failed: {Reason}")]
    public static partial void Failed(ILogger logger, string reason);
}
