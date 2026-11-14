using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Urlog.Core.Audit;
using Urlog.Core.Intake;
using Urlog.Core.Intake.Archives;
using Urlog.Core.Jobs;
using Urlog.Core.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Urlog.Core.Lifecycle;

/// <summary>
/// Archive and re-import (design record, "Consistency", Archive and Re-import).
/// <para>
/// An archive is six steps. Build: the tar, the trail and the manifest are written into a <c>.partial</c>
/// directory. Verify: the tar is opened again from disk and every entry is held to the manifest; the directory
/// gets its final name. Commit: one transaction makes the case <c>archived</c>. Store: the archive is hashed
/// again against the commit, then the case's partitions are dropped and its field statistics deleted. Files: the
/// case's directory under the evidence root is emptied and its cache removed. Complete: the task is done.
/// Before the commit nothing of the case is touched, and a stop is answered by building again from the start, three
/// times at most. From the commit on it only goes forward, and nothing is dropped or removed before the commit.
/// </para>
/// <para>
/// A re-import verifies the archive into a staging folder, puts the files back where their records say, queues a
/// parse for every file that had one, and closes the case when the last job has ended. Any doubt about the
/// archive refuses the whole re-import and leaves nothing outside the staging folder, which is removed.
/// </para>
/// </summary>
public sealed partial class LifecycleExecutor
{
    /// <summary>The staging folder of a re-import inside the case's directory.</summary>
    public const string StagingFolder = ".reimport";

    /// <summary>Where a file goes that was recorded outside the evidence root and is no longer there.</summary>
    public const string RestoredFolder = "restored";

    /// <summary>Times an archive is built before it is given up.</summary>
    public const int MaxArchiveAttempts = 3;

    private readonly string? _archiveRoot = paths.CheckedArchiveRoot();

    /// <summary>Called with the path of each stored file just before the archive reads it; tests stop a build part-way with it.</summary>
    public Action<string>? BeforeArchiveRead { get; init; }

    /// <summary>
    /// What stands in the way of an archive of this case on disk: entries in its directory that are not recorded
    /// evidence. An archive removes the directory, so it is refused while anything unaccounted for is in it.
    /// </summary>
    public async Task<IReadOnlyList<string>> StrayEntriesAsync(long caseId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(paths.EvidenceRoot))
        {
            return [];
        }

        var directory = CaseFolders.CaseDirectory(paths.EvidenceRoot, caseId);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        HashSet<string> recorded;
        await using (var db = store.CreateDbContext())
        {
            var stored = await db.Files.AsNoTracking().Where(f => f.CaseId == caseId && f.ParentFileId == null).Select(f => f.StoredPath).ToListAsync(cancellationToken).ConfigureAwait(false);
            recorded = stored.Where(p => !Path.IsPathRooted(p)).Select(p => Path.GetFullPath(Path.Combine(directory, p))).ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        }

        var stray = new List<string>();
        Walk(new DirectoryInfo(directory));
        return stray;

        void Walk(DirectoryInfo current)
        {
            foreach (var entry in current.EnumerateFileSystemInfos())
            {
                var relative = Path.GetRelativePath(directory, entry.FullName).Replace('\\', '/');
                // Unfinished uploads expire by themselves and a staging folder is a leftover: both go with the directory.
                if (relative is CaseFolders.Uploads or StagingFolder)
                {
                    continue;
                }

                if (GuardedDelete.IsLink(entry))
                {
                    stray.Add(relative + " (a link)");
                }
                else if (entry is DirectoryInfo sub)
                {
                    Walk(sub);
                }
                else if (!recorded.Contains(entry.FullName))
                {
                    stray.Add(relative);
                }
            }
        }
    }

    /// <summary>
    /// One archive from wherever it stands to its end. Throws on anything that stops it; the task and the case
    /// stay as the last finished step left them. Returns the files that were missing or changed when the archive
    /// read them (empty on a resume after the commit, where the audit row has them).
    /// </summary>
    public async Task<IReadOnlyList<ArchiveFlag>> ArchiveAsync(CaseTask task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        var archiveRoot = _archiveRoot ?? throw new ArchiveRefusedException("no archive root is configured; an archive has nowhere to go");
        var evidenceRoot = string.IsNullOrWhiteSpace(paths.EvidenceRoot) ? throw new ArchiveRefusedException("no evidence root is configured; the stored files of the case cannot be reached") : paths.EvidenceRoot;
        var caseId = task.CaseId;
        var guard = new GuardedDelete([paths.EvidenceRoot, paths.CacheRoot, archiveRoot], BeforeRemoval);
        var archives = CaseFolders.CaseDirectory(archiveRoot, caseId);
        IReadOnlyList<ArchiveFlag> flagged = [];
        if (task.State == CaseTaskState.Running)
        {
            await StepAsync(task, ArchiveOperations.StepBuild, newAttempt: true, cancellationToken).ConfigureAwait(false);
            // What an earlier attempt left, under either name: it was never committed, so it is no archive. The
            // archives of the case are the directories its tasks have on record, and those are left alone.
            if (Directory.Exists(archives))
            {
                HashSet<string> committed;
                await using (var db = store.CreateDbContext())
                {
                    var recorded = await db.CaseTasks.AsNoTracking().Where(t => t.CaseId == caseId && t.Id != task.Id && t.ArchiveDirectory != null).Select(t => t.ArchiveDirectory!).ToListAsync(cancellationToken).ConfigureAwait(false);
                    committed = recorded.Select(Path.GetFullPath).ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                }

                foreach (var leftover in Directory.EnumerateDirectories(archives).Where(d => !committed.Contains(Path.GetFullPath(d))).ToList())
                {
                    guard.RemoveTree(leftover);
                }
            }

            var stray = await StrayEntriesAsync(caseId, cancellationToken).ConfigureAwait(false);
            if (stray.Count > 0)
            {
                throw new ArchiveRefusedException($"the directory of case {caseId} holds {stray.Count} entr{(stray.Count == 1 ? "y" : "ies")} that no file record accounts for (first: {stray[0]}); an archive removes the directory, so it is not made until they are recorded or taken away");
            }

            await RequireArchiveRoomAsync(caseId, archiveRoot, cancellationToken).ConfigureAwait(false);
            var stamp = Now().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            for (var i = 0; i < 12 && Directory.Exists(Path.Combine(archives, stamp)); i++)
            {
                // Archives are named by the second and never overwritten.
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                stamp = Now().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            }

            var final = Path.Combine(archives, stamp);
            var building = final + ".partial";
            var build = await ArchiveWriter.BuildAsync(store, new MemberStore(evidenceRoot, paths.CacheRoot), caseId, building, BeforeArchiveRead, cancellationToken).ConfigureAwait(false);
            flagged = build.Flagged;
            await RecordFlaggedAsync(build.Flagged, cancellationToken).ConfigureAwait(false);

            // Verify: nothing below trusts what the build remembers. The manifest and the tar are read from disk.
            await StepAsync(task, ArchiveOperations.StepVerify, newAttempt: false, cancellationToken).ConfigureAwait(false);
            var manifestBytes = await File.ReadAllBytesAsync(Path.Combine(building, ArchiveManifest.ManifestName), cancellationToken).ConfigureAwait(false);
            var manifestSha = Convert.ToHexStringLower(SHA256.HashData(manifestBytes));
            if (manifestSha != build.ManifestSha256)
            {
                throw new InvalidDataException($"the manifest read back from disk has sha256 {manifestSha}, and {build.ManifestSha256} was written");
            }

            ArchiveManifest manifest;
            try
            {
                manifest = ArchiveManifest.Parse(manifestBytes);
                await ArchiveReader.ReadAsync(Path.Combine(building, ArchiveManifest.TarName), manifest, sink: null, cancellationToken).ConfigureAwait(false);
            }
            catch (ArchiveRefusedException e)
            {
                // What was just written does not read back as written: a fault of this attempt, and the next one writes it again.
                throw new InvalidDataException("the archive does not read back as it was written: " + e.Message, e);
            }

            var trail = ArchiveReader.HashFile(Path.Combine(building, ArchiveManifest.AuditName));
            if (trail.Sha256 != manifest.AuditSha256)
            {
                throw new InvalidDataException($"{ArchiveManifest.AuditName} read back from disk has sha256 {trail.Sha256}, and the manifest says {manifest.AuditSha256}");
            }

            ArchiveWriter.Seal(building);
            Directory.Move(building, final);

            OnStep?.Invoke("commit");
            var commit = new ArchiveCommit(final, manifest.TarSha256, manifest.TarSize, manifestSha, manifestBytes.Length, build.Files, build.Bytes, build.Flagged);
            await BusyRetry.Default.RunAsync(
                async () =>
                {
                    await using var db = store.CreateDbContext();
                    await ArchiveOperations.CommitAsync(db, store.Audit, task.Id, commit, store.Clock, cancellationToken).ConfigureAwait(false);
                    return true;
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var archiveName = Path.GetFileName(final);
            LifecycleLog.ArchiveCommitted(logger, caseId, archiveName, manifest.TarSha256, build.Files, build.Flagged.Count);
            OnStep?.Invoke(ArchiveOperations.StepStore);
        }
        else
        {
            await StepAsync(task, ArchiveOperations.StepStore, newAttempt: true, cancellationToken).ConfigureAwait(false);
        }

        // Store. From here the archive is all there will be of the case: it is held to the commit once more first.
        CaseTask row;
        await using (var db = store.CreateDbContext())
        {
            row = await db.CaseTasks.AsNoTracking().FirstAsync(t => t.Id == task.Id, cancellationToken).ConfigureAwait(false);
        }

        if (ArchiveDifference(row) is { } difference)
        {
            await using var db = store.CreateDbContext();
            await ArchiveOperations.FailArchiveAsync(db, store.Audit, task.Id, difference + "; nothing was dropped or removed after this was found", store.Clock, cancellationToken).ConfigureAwait(false);
            throw new ArchiveRefusedException(difference);
        }

        await StoreIdentityGuard.RequireAsync(store.Path, events, cancellationToken).ConfigureAwait(false);
        var dropped = await DropPartitionsAsync((ulong)caseId, cancellationToken).ConfigureAwait(false);
        await StoreIdentityGuard.RequireAsync(store.Path, events, cancellationToken).ConfigureAwait(false);
        // The generation rows stay: a re-import's parses are numbered above them.
        await events.DeleteCaseFieldStatsAsync((ulong)caseId, cancellationToken).ConfigureAwait(false);

        // Files.
        await StepAsync(task, ArchiveOperations.StepFiles, newAttempt: false, cancellationToken).ConfigureAwait(false);
        var late = await StrayEntriesAsync(caseId, cancellationToken).ConfigureAwait(false);
        if (late.Count > 0)
        {
            throw new InvalidOperationException($"the directory of case {caseId} holds {late.Count} entr{(late.Count == 1 ? "y" : "ies")} that the archive does not account for (first: {late[0]}); the directory is not removed with them in it");
        }

        var directory = CaseFolders.CaseDirectory(evidenceRoot, caseId);
        var removed = guard.RemoveTree(directory);
        Directory.CreateDirectory(directory);
        if (!string.IsNullOrWhiteSpace(paths.CacheRoot))
        {
            removed += guard.RemoveTree(CaseFolders.CaseDirectory(paths.CacheRoot, caseId));
        }

        OnStep?.Invoke("complete");
        await BusyRetry.Default.RunAsync(
            async () =>
            {
                await using var db = store.CreateDbContext();
                await ArchiveOperations.CompleteArchiveAsync(db, store.Audit, task.Id, dropped, removed, store.Clock, cancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        LifecycleLog.Archived(logger, caseId, dropped, removed);
        return flagged;
    }

    /// <summary>
    /// One re-import from wherever it stands. True when the case is <c>closed</c> again; false while its jobs are
    /// still to run, and false after a refusal (the task then says why).
    /// </summary>
    public async Task<bool> ReimportAsync(CaseTask task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        var caseId = task.CaseId;
        if (task.State == CaseTaskState.Running)
        {
            var archiveRoot = _archiveRoot ?? throw new InvalidOperationException("no archive root is configured; the archive of the case cannot be reached");
            var evidenceRoot = string.IsNullOrWhiteSpace(paths.EvidenceRoot) ? throw new InvalidOperationException("no evidence root is configured; the files of the case have nowhere to go") : paths.EvidenceRoot;
            var guard = new GuardedDelete([paths.EvidenceRoot, paths.CacheRoot, archiveRoot], BeforeRemoval);
            var directory = CaseFolders.CaseDirectory(evidenceRoot, caseId);
            var staging = Path.Combine(directory, StagingFolder);
            await StepAsync(task, ArchiveOperations.StepVerify, newAttempt: true, cancellationToken).ConfigureAwait(false);
            // Every attempt starts from an empty directory: an archived case has no file in place.
            guard.RemoveTree(directory);
            Directory.CreateDirectory(staging);

            ArchiveManifest manifest;
            Dictionary<long, EvidenceFile> records;
            try
            {
                (manifest, records) = await VerifyForReimportAsync(task, archiveRoot, directory, staging, cancellationToken).ConfigureAwait(false);
            }
            catch (ArchiveRefusedException refusal)
            {
                guard.RemoveTree(staging);
                await BusyRetry.Default.RunAsync(
                    async () =>
                    {
                        await using var db = store.CreateDbContext();
                        await ArchiveOperations.RefuseReimportAsync(db, store.Audit, task.Id, new ReimportRefusal(refusal.Message, refusal.FileId, refusal.Expected, refusal.Actual), store.Clock, cancellationToken).ConfigureAwait(false);
                        return true;
                    },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                LifecycleLog.ReimportRefused(logger, caseId, refusal.Message);
                return false;
            }

            // Place: every name on disk comes from a file record.
            await StepAsync(task, ArchiveOperations.StepPlace, newAttempt: false, cancellationToken).ConfigureAwait(false);
            var moved = new Dictionary<long, string>();
            foreach (var entry in manifest.Entries.Where(e => !e.Missing))
            {
                var record = records[entry.FileId];
                var staged = Path.Combine(staging, entry.FileId.ToString(CultureInfo.InvariantCulture));
                string target;
                if (Path.IsPathRooted(record.StoredPath))
                {
                    // Recorded where it was found, outside the evidence root. It was never removed; if it is still
                    // there and still the same, it is the file.
                    if (File.Exists(record.StoredPath) && ArchiveReader.HashFile(record.StoredPath) == (entry.TarSha256!, entry.TarSize!.Value))
                    {
                        File.Delete(staged);
                        continue;
                    }

                    var relative = RestoredFolder + "/" + entry.FileId.ToString(CultureInfo.InvariantCulture);
                    moved[entry.FileId] = relative;
                    target = Path.Combine(directory, RestoredFolder, entry.FileId.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    target = Path.GetFullPath(Path.Combine(directory, record.StoredPath));
                }

                BeforeRemoval?.Invoke(target);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(staged, target);
                EvidencePermissions.MakeReadOnly(target);
            }

            guard.RemoveTree(staging);
            OnStep?.Invoke("queue");
            var queued = await BusyRetry.Default.RunAsync(
                async () =>
                {
                    await using var db = store.CreateDbContext();
                    return await ArchiveOperations.QueueReimportAsync(db, task.Id, moved, store.Clock, cancellationToken).ConfigureAwait(false);
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var restored = manifest.Entries.Count(e => !e.Missing);
            LifecycleLog.ReimportQueued(logger, caseId, restored, queued);
        }

        // Committed: the case closes when its last job has ended.
        Dictionary<long, long>? rowsBefore = null;
        try
        {
            if (task.ArchiveDirectory is { } archive)
            {
                var manifest = ArchiveManifest.Parse(await File.ReadAllBytesAsync(Path.Combine(archive, ArchiveManifest.ManifestName), cancellationToken).ConfigureAwait(false));
                rowsBefore = manifest.Files.Where(f => f.State == Names.Of(FileState.Parsed)).ToDictionary(f => f.Id, f => f.RowCount);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArchiveRefusedException)
        {
            // The files are back and parsed; the comparison with the manifest's counts is left out and the row says so.
        }

        var result = await BusyRetry.Default.RunAsync(
            async () =>
            {
                await using var db = store.CreateDbContext();
                return await ArchiveOperations.CompleteReimportAsync(db, store.Audit, task.Id, rowsBefore, store.Clock, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result is not null)
        {
            LifecycleLog.Reimported(logger, caseId, result.Parsed, result.FailedFiles.Count, result.RowDifferences.Count);
        }

        return result is not null;
    }

    /// <summary>Archive and re-import tasks, each carried on from where it stands. One that fails keeps its task and is tried again later; an archive that used its attempts is given up with an audit row.</summary>
    private async Task RunArchivesAsync(CancellationToken cancellationToken)
    {
        List<CaseTask> tasks;
        await using (var db = store.CreateDbContext())
        {
            tasks = await ArchiveOperations.PendingAsync(db, cancellationToken).ConfigureAwait(false);
        }

        foreach (var task in tasks)
        {
            if (_backoff.TryGetValue(task.CaseId, out var wait) && System.Diagnostics.Stopwatch.GetTimestamp() < wait.NotBefore)
            {
                continue;
            }

            try
            {
                if (task.Kind == CaseTaskKind.Archive)
                {
                    await ArchiveAsync(task, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await ReimportAsync(task, cancellationToken).ConfigureAwait(false);
                }

                _backoff.Remove(task.CaseId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                var gaveUp = await AfterArchiveFailureAsync(task, e).ConfigureAwait(false);
                var failures = wait.Failures + 1;
                var delay = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, 30 * Math.Pow(2, Math.Min(failures - 1, 10))));
                if (gaveUp)
                {
                    _backoff.Remove(task.CaseId);
                }
                else
                {
                    _backoff[task.CaseId] = (failures, System.Diagnostics.Stopwatch.GetTimestamp() + (long)(delay.TotalSeconds * System.Diagnostics.Stopwatch.Frequency));
                }

                var kind = task.Kind == CaseTaskKind.Archive ? "archive" : "re-import";
                var next = gaveUp ? "it is given up" : "it is taken up again later";
                LifecycleLog.ArchiveStopped(logger, task.CaseId, kind, e.Message, next);
                if (e is StoreIdentityException)
                {
                    throw;
                }
            }
        }
    }

    /// <summary>
    /// After an archive or a re-import stopped on <paramref name="failure"/>: the reason goes on the task. An
    /// archive that is not committed and cannot be made as things are (no room, files nobody recorded), or that
    /// has used its attempts, is given up with <c>case_archive_failed</c>. True when it was given up.
    /// </summary>
    public async Task<bool> AfterArchiveFailureAsync(CaseTask task, Exception failure)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(failure);
        try
        {
            await using var db = store.CreateDbContext();
            var row = await db.CaseTasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == task.Id).ConfigureAwait(false);
            if (row is null || row.State is not (CaseTaskState.Running or CaseTaskState.Committed))
            {
                return row is { State: CaseTaskState.Failed };
            }

            if (row.Kind == CaseTaskKind.Archive && row.State == CaseTaskState.Running && (failure is ArchiveRefusedException || row.Attempt >= MaxArchiveAttempts))
            {
                var reason = failure is ArchiveRefusedException ? failure.Message : $"given up after {row.Attempt} attempts; the last one stopped on: {failure.Message}";
                return await ArchiveOperations.FailArchiveAsync(db, store.Audit, row.Id, reason, store.Clock).ConfigureAwait(false);
            }

            await LifecycleOperations.NoteAsync(db, row.Id, failure.Message, store.Clock).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The reason is in the log; the task row keeps its last one.
        }

        return false;
    }

    /// <summary>Holds the archive of a re-import to everything on record before a byte of it is used, then extracts it into the staging folder.</summary>
    private async Task<(ArchiveManifest Manifest, Dictionary<long, EvidenceFile> Records)> VerifyForReimportAsync(CaseTask task, string archiveRoot, string caseDirectory, string staging, CancellationToken cancellationToken)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var archives = CaseFolders.CaseDirectory(archiveRoot, task.CaseId) + Path.DirectorySeparatorChar;
        if (task.ArchiveDirectory is not { } archive || !Path.GetFullPath(archive).StartsWith(archives, comparison) || GuardedDelete.IsLink(new DirectoryInfo(archive)))
        {
            throw new ArchiveRefusedException($"the archive on record is not a directory under {archives}; this process reads archives of the case from there only");
        }

        var manifestPath = Path.Combine(archive, ArchiveManifest.ManifestName);
        var tarPath = Path.Combine(archive, ArchiveManifest.TarName);
        if (!File.Exists(manifestPath) || !File.Exists(tarPath))
        {
            throw new ArchiveRefusedException($"the archive {Path.GetFileName(archive)} is not there, or not whole: it needs {ArchiveManifest.ManifestName} and {ArchiveManifest.TarName}", null, task.TarSha256, IntegrityOperations.Missing);
        }

        // The manifest, against the task and against the audit chain.
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var manifestSha = Convert.ToHexStringLower(SHA256.HashData(manifestBytes));
        Dictionary<long, EvidenceFile> records;
        string? chained;
        await using (var db = store.CreateDbContext())
        {
            records = await db.Files.AsNoTracking().Where(f => f.CaseId == task.CaseId).ToDictionaryAsync(f => f.Id, cancellationToken).ConfigureAwait(false);
            var commit = await db.Audit.AsNoTracking().Where(a => a.Action == AuditActions.CaseArchive && a.TargetType == AuditTargets.Case && a.TargetId == task.CaseId).OrderByDescending(a => a.Seq).Select(a => a.DetailsJson).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            chained = commit is null ? null : JsonDocument.Parse(commit).RootElement.TryGetProperty("manifest_sha256", out var value) ? value.GetString() : null;
        }

        if (manifestSha != task.ManifestSha256 || manifestSha != chained)
        {
            throw new ArchiveRefusedException($"{ArchiveManifest.ManifestName} has sha256 {manifestSha}; the archive was committed with {chained ?? task.ManifestSha256}", null, chained ?? task.ManifestSha256, manifestSha);
        }

        var manifest = ArchiveManifest.Parse(manifestBytes);
        var identity = await StoreIdentity.ReadAsync(store.Path, cancellationToken).ConfigureAwait(false);
        if (manifest.Case.Id != task.CaseId || manifest.StoreIdentity != identity || manifest.TarSha256 != task.TarSha256)
        {
            throw new ArchiveRefusedException($"the manifest is of case {manifest.Case.Id} in store {manifest.StoreIdentity} with tar {manifest.TarSha256}; this re-import is of case {task.CaseId} in store {identity} with tar {task.TarSha256}", null, task.TarSha256, manifest.TarSha256);
        }

        // Every entry against its record, and every place a file would go, before anything is extracted.
        var listed = manifest.Files.ToDictionary(f => f.Id);
        foreach (var entry in manifest.Entries)
        {
            if (!records.TryGetValue(entry.FileId, out var record) || record.ParentFileId is not null)
            {
                throw new ArchiveRefusedException($"the manifest lists file {entry.FileId}, and the case has no stored file with that id", entry.FileId, "a file record", "none");
            }

            if (record.Sha256 != entry.RecordSha256 || record.Size != entry.RecordSize)
            {
                throw new ArchiveRefusedException($"the manifest says file {entry.FileId} is {entry.RecordSize} bytes with sha256 {entry.RecordSha256}; its record says {record.Size} bytes with sha256 {record.Sha256}", entry.FileId, $"{record.Sha256} ({record.Size} bytes)", $"{entry.RecordSha256} ({entry.RecordSize} bytes)");
            }

            if (!entry.Missing && entry.Changed != (entry.TarSha256 != entry.RecordSha256 || entry.TarSize != entry.RecordSize))
            {
                throw new ArchiveRefusedException($"the manifest contradicts itself about file {entry.FileId}: it is flagged {(entry.Changed ? "changed" : "unchanged")} and its two hashes say otherwise", entry.FileId, entry.RecordSha256, entry.TarSha256);
            }

            if (!listed.TryGetValue(entry.FileId, out var copy) || copy.StoredPath != record.StoredPath)
            {
                throw new ArchiveRefusedException($"the manifest places file {entry.FileId} elsewhere than its record does", entry.FileId, record.StoredPath, copy?.StoredPath);
            }

            if (!Path.IsPathRooted(record.StoredPath))
            {
                RequirePlaceInside(caseDirectory, record, comparison);
            }
        }

        var absent = records.Values.FirstOrDefault(r => r.ParentFileId is null && manifest.Entries.All(e => e.FileId != r.Id));
        if (absent is not null)
        {
            throw new ArchiveRefusedException($"the case has stored file {absent.Id} on record and the manifest does not list it", absent.Id, absent.Sha256, "not listed");
        }

        // The tar. A name on disk here is the entry's file id as a number, taken from the manifest's entry.
        await ArchiveReader.ReadAsync(
            tarPath,
            manifest,
            async (entry, data, ct) =>
            {
                var staged = Path.Combine(staging, entry.FileId.ToString(CultureInfo.InvariantCulture));
                BeforeRemoval?.Invoke(staged);
                await using var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous);
                await data.CopyToAsync(output, ct).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
        return (manifest, records);
    }

    /// <summary>A record's stored path must lead to a place inside the case's directory, by plain segments, and not into the staging folder.</summary>
    private static void RequirePlaceInside(string caseDirectory, EvidenceFile record, StringComparison comparison)
    {
        var segments = record.StoredPath.Split('/', '\\');
        var target = Path.GetFullPath(Path.Combine(caseDirectory, record.StoredPath));
        if (segments.Any(s => s is "" or "." or "..") || segments[0] is StagingFolder or CaseFolders.Uploads
            || !target.StartsWith(Path.TrimEndingDirectorySeparator(caseDirectory) + Path.DirectorySeparatorChar, comparison))
        {
            throw new ArchiveRefusedException($"the record of file {record.Id} places it at '{record.StoredPath}', which is not a plain path inside the case's directory", record.Id, "a path inside the case's directory", record.StoredPath);
        }
    }

    /// <summary>What differs between the archive on disk and what the task was committed with, or null.</summary>
    private static string? ArchiveDifference(CaseTask row)
    {
        if (row.ArchiveDirectory is not { } archive || row.TarSha256 is null || row.ManifestSha256 is null)
        {
            return $"the task of case {row.CaseId} has no archive on record";
        }

        var tar = Path.Combine(archive, ArchiveManifest.TarName);
        var manifest = Path.Combine(archive, ArchiveManifest.ManifestName);
        if (!File.Exists(tar) || !File.Exists(manifest))
        {
            return $"the archive {Path.GetFileName(archive)} of case {row.CaseId} is not there, or not whole";
        }

        var tarSha = ArchiveReader.HashFile(tar).Sha256;
        var manifestSha = ArchiveReader.HashFile(manifest).Sha256;
        return tarSha == row.TarSha256 && manifestSha == row.ManifestSha256
            ? null
            : $"the archive {Path.GetFileName(archive)} of case {row.CaseId} changed after it was committed: tar {tarSha} (committed {row.TarSha256}), manifest {manifestSha} (committed {row.ManifestSha256})";
    }

    private async Task RequireArchiveRoomAsync(long caseId, string archiveRoot, CancellationToken cancellationToken)
    {
        long needed;
        await using (var db = store.CreateDbContext())
        {
            needed = await db.Files.AsNoTracking().Where(f => f.CaseId == caseId && f.ParentFileId == null).SumAsync(f => (long?)f.Size, cancellationToken).ConfigureAwait(false) ?? 0;
        }

        Directory.CreateDirectory(archiveRoot);
        var free = new DriveDiskProbe().Measure(archiveRoot).FreeBytes;
        // The evidence may not compress at all, so the whole of it has to fit, with the headroom on top.
        if (free - needed < options.ArchiveHeadroomBytes)
        {
            throw new ArchiveRefusedException($"the archive volume has {free} bytes free; the stored files of case {caseId} are {needed} bytes and {options.ArchiveHeadroomBytes} bytes are kept free");
        }
    }

    /// <summary>A stored file that was not what its record says when the archive read it is quarantined, once, with what was found.</summary>
    private async Task RecordFlaggedAsync(IReadOnlyList<ArchiveFlag> flagged, CancellationToken cancellationToken)
    {
        foreach (var flag in flagged)
        {
            await BusyRetry.Default.RunAsync(
                async () =>
                {
                    await using var db = store.CreateDbContext();
                    if (await db.Files.AsNoTracking().AnyAsync(f => f.Id == flag.FileId && f.State == FileState.Quarantined, cancellationToken).ConfigureAwait(false))
                    {
                        return false;
                    }

                    var actual = flag.ActualSize is { } size ? new IntegrityMismatch(flag.ActualSha256, size) : null;
                    return await IntegrityOperations.RecordMismatchAsync(db, store.Audit, flag.FileId, actual, ArchiveOperations.CheckArchive, store.Clock, cancellationToken).ConfigureAwait(false);
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }
}

internal static partial class LifecycleLog
{
    [LoggerMessage(EventId = 220, Level = LogLevel.Information, Message = "case {CaseId}: archive {Archive} committed, tar sha256 {TarSha256}, {Files} file(s), {Flagged} missing or changed")]
    public static partial void ArchiveCommitted(ILogger logger, long caseId, string archive, string tarSha256, int files, int flagged);

    [LoggerMessage(EventId = 221, Level = LogLevel.Information, Message = "case {CaseId}: archived; {Partitions} partition(s) dropped, {Removed} file(s) removed")]
    public static partial void Archived(ILogger logger, long caseId, int partitions, int removed);

    [LoggerMessage(EventId = 222, Level = LogLevel.Warning, Message = "case {CaseId}: the {Kind} stopped ({Reason}); {Next}")]
    public static partial void ArchiveStopped(ILogger logger, long caseId, string kind, string reason, string next);

    [LoggerMessage(EventId = 223, Level = LogLevel.Error, Message = "case {CaseId}: re-import refused, nothing restored: {Reason}")]
    public static partial void ReimportRefused(ILogger logger, long caseId, string reason);

    [LoggerMessage(EventId = 224, Level = LogLevel.Information, Message = "case {CaseId}: {Files} file(s) verified and back in place, {Jobs} parse(s) queued")]
    public static partial void ReimportQueued(ILogger logger, long caseId, int files, int jobs);

    [LoggerMessage(EventId = 225, Level = LogLevel.Information, Message = "case {CaseId}: re-imported and closed; {Parsed} file(s) parsed, {Failed} failed, {Differences} with another row count than before")]
    public static partial void Reimported(ILogger logger, long caseId, int parsed, int failed, int differences);
}
