using System.Text.Json;
using Urlog.Core.Audit;
using Urlog.Core.Lifecycle;
using Urlog.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Core.Tests.Lifecycle;

/// <summary>
/// The archive as the executor carries it out: what it leaves, that nothing of the case goes before the archive
/// was read back and committed, a stop at every call with a second executor finishing each one, and what it
/// does with files that are missing or changed and with things nobody recorded.
/// </summary>
public sealed class ArchiveTests
{
    private static CancellationToken Ct => RunnerHarness.Ct;

    [Fact]
    public async Task AnArchiveHoldsEveryStoredFileAndOnlyThenTheStoreAndTheDirectoryAreEmptied()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var scene = await ArchiveScene.BuildAsync(h);
        var steps = new List<string>();
        var task = await scene.RequestArchiveAsync();
        var flagged = await scene.Executor(onStep: steps.Add).ArchiveAsync(task, Ct);
        Assert.Empty(flagged);
        Assert.Equal(["build", "verify", "commit", "store", "files", "complete"], steps);
        task = await scene.TaskAsync(task.Id);
        await scene.AssertArchivedAsync(task);

        // The archive: three files, read-only, named by the second it was made.
        var directory = task.ArchiveDirectory!;
        Assert.Equal([ArchiveManifest.AuditName, ArchiveManifest.TarName, ArchiveManifest.ManifestName], Directory.EnumerateFiles(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.All(Directory.EnumerateFiles(directory), file => Assert.True(Urlog.Core.Intake.EvidencePermissions.IsReadOnly(file), file));
        if (!OperatingSystem.IsWindows())
        {
            // 0440 on the files (what IsReadOnly checks there) and 0750 on the directory.
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute, File.GetUnixFileMode(directory));
        }

        var manifest = ArchiveManifest.Parse(await File.ReadAllBytesAsync(Path.Combine(directory, ArchiveManifest.ManifestName), Ct));
        Assert.Equal((scene.CaseId, "to archive", h.Events.StoreId), (manifest.Case.Id, manifest.Case.Name, manifest.StoreIdentity));
        Assert.Equal(scene.AllArrived.Select(f => f.Id).Order(), manifest.Entries.Select(e => e.FileId));
        Assert.All(manifest.Entries, entry =>
        {
            var record = scene.AllArrived.Single(f => f.Id == entry.FileId);
            Assert.Equal(($"files/{record.Id}", record.Sha256, record.Size, record.Sha256, (long?)record.Size, false, false), (entry.Name, entry.RecordSha256, entry.RecordSize, entry.TarSha256, entry.TarSize, entry.Missing, entry.Changed));
            Assert.Equal(record.Id == scene.External.Id, entry.External);
        });

        // The tar holds exactly those names and those bytes, and no name or path of the evidence.
        var entries = TarForge.Read(Path.Combine(directory, ArchiveManifest.TarName));
        Assert.Equal(manifest.Entries.Select(e => e.Name), entries.Select(e => e.Name));
        Assert.All(entries, entry => Assert.Equal(manifest.Entries.Single(m => m.Name == entry.Name).TarSha256, ArchiveScene.Sha(entry.Data)));
        Assert.DoesNotContain(entries, e => e.Name.Contains("access", StringComparison.Ordinal) || e.Name.Contains("host-b", StringComparison.Ordinal));

        // The trail in the archive is the case's own, up to the head the manifest names.
        var trail = await File.ReadAllLinesAsync(Path.Combine(directory, ArchiveManifest.AuditName), Ct);
        Assert.Equal(manifest.AuditLines, trail.Length);
        Assert.Contains(trail, line => line.Contains("\"case_close\"", StringComparison.Ordinal));
        Assert.DoesNotContain(trail, line => line.Contains("\"case_name\":\"kept\"", StringComparison.Ordinal));

        // The chain: the commit with both hashes, one row per held job, the completion.
        var audit = await h.AuditAsync();
        var commit = audit.Single(a => a.Action == AuditActions.CaseArchive);
        using var details = JsonDocument.Parse(commit.DetailsJson);
        Assert.Equal(task.TarSha256, details.RootElement.GetProperty("tar_sha256").GetString());
        Assert.Equal(task.ManifestSha256, details.RootElement.GetProperty("manifest_sha256").GetString());
        Assert.Equal((4, 1, 0), (details.RootElement.GetProperty("files").GetInt32(), details.RootElement.GetProperty("jobs_cancelled").GetInt32(), details.RootElement.GetProperty("flagged").GetArrayLength()));
        Assert.Equal("alice", commit.Actor);
        var cancelled = audit.Single(a => a.Action == AuditActions.JobCancelled);
        Assert.Equal((AuditTargets.File, (long?)scene.Stored[0].Id), (cancelled.TargetType, cancelled.TargetId));
        Assert.Contains($"\"job_id\":{scene.HeldJob}", cancelled.DetailsJson, StringComparison.Ordinal);
        var complete = audit.Single(a => a.Action == AuditActions.CaseArchiveComplete);
        Assert.True(cancelled.Seq < commit.Seq && commit.Seq < complete.Seq);
        Assert.Contains("\"partitions_dropped\":3", complete.DetailsJson, StringComparison.Ordinal);
        Assert.True((await AuditVerifier.VerifyAsync(h.Store.Path, Ct)).Ok);

        // An archived case takes no file, no job and no second archive, and the accounting shows what is left of it.
        await using var db = h.Open();
        await Assert.ThrowsAsync<CaseNotActiveException>(() => JobOperations.EnqueueAsync(db, scene.Stored[0].Id, DateTime.UtcNow, Ct));
        var again = await Assert.ThrowsAsync<LifecycleRefusedException>(() => scene.RequestArchiveAsync());
        Assert.Contains("is archived; only a closed case is archived", again.Message, StringComparison.Ordinal);
        var usage = await CaseAccounting.MeasureAsync(db, h.Events, scene.Paths, scene.CaseId, Ct);
        Assert.Equal((0L, 0L, 0L, 0L), (usage!.IntakeBytes, usage.UploadBytes, usage.CacheBytes, usage.StoreRows));
        Assert.Equal(Directory.EnumerateFiles(directory).Sum(f => new FileInfo(f).Length), usage.ArchiveBytes);
        Assert.Equal(0, (await CaseAccounting.MeasureAsync(db, h.Events, scene.Paths, scene.Kept, Ct))!.ArchiveBytes);
    }

    [Fact]
    public async Task AStopAtAnyCallOfAnArchiveIsFinishedByTheNextExecutorAndNothingGoesBeforeTheCommit()
    {
        int total;
        await using (var clean = await RunnerHarness.CreateAsync())
        {
            var scene = await ArchiveScene.BuildAsync(clean);
            var ticks = 0;
            await scene.Executor(_ => ticks++, _ => ticks++, _ => ticks++, _ => ticks++).ArchiveAsync(await scene.RequestArchiveAsync(), Ct);
            total = ticks;
        }

        Assert.InRange(total, 20, 90);
        for (var stopAt = 0; stopAt < total; stopAt++)
        {
            await using var h = await RunnerHarness.CreateAsync();
            var scene = await ArchiveScene.BuildAsync(h);
            var seen = 0;
            string? stoppedOn = null;
            void Tick(string what)
            {
                if (seen++ == stopAt)
                {
                    stoppedOn = what;
                    throw new IOException($"stopped by the test at call {stopAt}");
                }
            }

            var task = await scene.RequestArchiveAsync();
            await Assert.ThrowsAsync<IOException>(() => scene.Executor(Tick, Tick, Tick, Tick).ArchiveAsync(task, Ct));

            // What the stop left. A partition or a stored file is missing only if the verified archive and its commit exist.
            var when = $"after a stop at call {stopAt} ({Path.GetFileName(stoppedOn)})";
            var row = await scene.TaskAsync(task.Id);
            var state = await scene.StateAsync();
            if (state == CaseState.Closed)
            {
                Assert.True(scene.StoreWhole && scene.FilesWhole, $"{when}: the case is not archived, so nothing of it may be gone");
                Assert.Equal(CaseTaskState.Running, row.State);
                Assert.DoesNotContain(await h.AuditAsync(), a => a.Action == AuditActions.CaseArchive);
            }
            else
            {
                Assert.Equal(CaseState.Archived, state);
                Assert.Equal(CaseTaskState.Committed, row.State);
                Assert.True(ArchiveScene.ArchiveVerifies(row), $"{when}: the case is archived and its archive does not verify");
                Assert.Single(await h.AuditAsync(), a => a.Action == AuditActions.CaseArchive);
                Assert.True(scene.FilesWhole || scene.StoreEmpty, $"{when}: a file is gone while the event store still holds something of the case");
            }

            // The process is gone; the next one starts from what is on disk and in the two stores.
            Assert.Equal(0, await scene.Executor().RunPendingAsync(Ct));
            await scene.AssertArchivedAsync(await scene.TaskAsync(task.Id));
            Assert.Single(await h.AuditAsync(), a => a.Action == AuditActions.CaseArchive);
            Assert.Single(await h.AuditAsync(), a => a.Action == AuditActions.CaseArchiveComplete);
            Assert.True((await AuditVerifier.VerifyAsync(h.Store.Path, Ct)).Ok, when);
        }
    }

    [Fact]
    public async Task AByteChangedInTheWrittenTarIsCaughtBeforeAnythingIsDroppedAndThreeAttemptsEndTheArchive()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var scene = await ArchiveScene.BuildAsync(h);
        var task = await scene.RequestArchiveAsync();
        // Between the write and the read-back, every time: a disk that does not return what was written.
        void Corrupt(string step)
        {
            if (step == "verify")
            {
                var tar = Path.Combine(Directory.EnumerateDirectories(CaseFolders.CaseDirectory(scene.Archives, scene.CaseId), "*.partial").Single(), ArchiveManifest.TarName);
                var bytes = File.ReadAllBytes(tar);
                bytes[bytes.Length / 2] ^= 0x20;
                File.WriteAllBytes(tar, bytes);
            }
        }

        for (var attempt = 1; attempt <= LifecycleExecutor.MaxArchiveAttempts; attempt++)
        {
            Assert.Equal(0, await scene.Executor(onStep: Corrupt).RunPendingAsync(Ct));
            var row = await scene.TaskAsync(task.Id);
            Assert.Equal(attempt, row.Attempt);
            Assert.Equal(attempt < LifecycleExecutor.MaxArchiveAttempts ? CaseTaskState.Running : CaseTaskState.Failed, row.State);
            Assert.False(string.IsNullOrEmpty(row.Message));
            Assert.Equal(CaseState.Closed, await scene.StateAsync());
            Assert.True(scene.StoreWhole && scene.FilesWhole, $"attempt {attempt}: something of the case is gone and no archive was committed");
        }

        var failed = Assert.Single(await h.AuditAsync(), a => a.Action == AuditActions.CaseArchiveFailed);
        Assert.Contains("given up after 3 attempts", failed.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"case_state\":\"closed\"", failed.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain(await h.AuditAsync(), a => a.Action == AuditActions.CaseArchive);
        // Given up is final for that request: nothing is tried again, and the owner may ask anew or reopen the case.
        Assert.Equal(0, await scene.Executor(onStep: Corrupt).RunPendingAsync(Ct));
        Assert.Single(await h.AuditAsync(), a => a.Action == AuditActions.CaseArchiveFailed);
        await scene.AssertArchivedAsync(await scene.ArchiveAsync());
    }

    [Fact]
    public async Task AnArchiveThatChangedAfterItsCommitStopsBeforeAnythingIsDropped()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var scene = await ArchiveScene.BuildAsync(h);
        var task = await scene.RequestArchiveAsync();
        // Stopped right after the commit; while the process is down the archive is changed.
        await Assert.ThrowsAsync<IOException>(() => scene.Executor(onStep: step =>
        {
            if (step == "store")
            {
                throw new IOException("stopped by the test");
            }
        }).ArchiveAsync(task, Ct));
        var committed = await scene.TaskAsync(task.Id);
        Assert.Equal((CaseTaskState.Committed, CaseState.Archived), (committed.State, await scene.StateAsync()));
        var tar = Path.Combine(committed.ArchiveDirectory!, ArchiveManifest.TarName);
        ArchiveScene.Unlock(tar);
        var bytes = await File.ReadAllBytesAsync(tar, Ct);
        bytes[^10] ^= 0x01;
        await File.WriteAllBytesAsync(tar, bytes, Ct);

        Assert.Equal(0, await scene.Executor().RunPendingAsync(Ct));
        var row = await scene.TaskAsync(task.Id);
        Assert.Equal(CaseTaskState.Failed, row.State);
        Assert.Contains("changed after it was committed", row.Message, StringComparison.Ordinal);
        Assert.True(scene.StoreWhole && scene.FilesWhole, "the archive no longer matches its commit, and something of the case was removed all the same");
        var failed = Assert.Single(await h.AuditAsync(), a => a.Action == AuditActions.CaseArchiveFailed);
        Assert.Contains("\"case_state\":\"archived\"", failed.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain(await h.AuditAsync(), a => a.Action == AuditActions.CaseArchiveComplete);
        // And no re-import from an archive that is not the one committed.
        var refused = await Assert.ThrowsAsync<LifecycleRefusedException>(() => scene.RequestReimportAsync());
        Assert.Contains("its archive is failed", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingAndAChangedFileAreArchivedAsFoundAndListedWithBothHashes()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var scene = await ArchiveScene.BuildAsync(h);
        var changed = scene.Stored[0];
        var missing = scene.Stored[2];
        ArchiveScene.Unlock(scene.PathOf(changed));
        var tampered = await File.ReadAllBytesAsync(scene.PathOf(changed), Ct);
        tampered[5] ^= 0x01;
        await File.WriteAllBytesAsync(scene.PathOf(changed), tampered, Ct);
        ArchiveScene.Unlock(scene.PathOf(missing));
        File.Delete(scene.PathOf(missing));

        var task = await scene.RequestArchiveAsync();
        var flagged = await scene.Executor().ArchiveAsync(task, Ct);
        task = await scene.TaskAsync(task.Id);
        Assert.Equal((CaseTaskState.Done, CaseState.Archived), (task.State, await scene.StateAsync()));

        // What the command prints from.
        Assert.Equal([changed.Id, missing.Id], flagged.Select(f => f.FileId));
        Assert.Equal((changed.Sha256, changed.Size, ArchiveScene.Sha(tampered), (long?)tampered.Length), (flagged[0].ExpectedSha256, flagged[0].ExpectedSize, flagged[0].ActualSha256, flagged[0].ActualSize));
        Assert.Equal((missing.Sha256, missing.Size, "missing", (long?)null), (flagged[1].ExpectedSha256, flagged[1].ExpectedSize, flagged[1].ActualSha256, flagged[1].ActualSize));

        // The manifest.
        var manifest = ArchiveManifest.Parse(await File.ReadAllBytesAsync(Path.Combine(task.ArchiveDirectory!, ArchiveManifest.ManifestName), Ct));
        var changedEntry = manifest.Entries.Single(e => e.FileId == changed.Id);
        Assert.Equal((true, false, changed.Sha256, ArchiveScene.Sha(tampered)), (changedEntry.Changed, changedEntry.Missing, changedEntry.RecordSha256, changedEntry.TarSha256));
        var missingEntry = manifest.Entries.Single(e => e.FileId == missing.Id);
        Assert.Equal((false, true, missing.Sha256, (string?)null), (missingEntry.Changed, missingEntry.Missing, missingEntry.RecordSha256, missingEntry.TarSha256));
        Assert.DoesNotContain(TarForge.Read(Path.Combine(task.ArchiveDirectory!, ArchiveManifest.TarName)), e => e.Name == missingEntry.Name);

        // The commit row lists both, with what was expected and what was found.
        var audit = await h.AuditAsync();
        using var details = JsonDocument.Parse(audit.Single(a => a.Action == AuditActions.CaseArchive).DetailsJson);
        var listed = details.RootElement.GetProperty("flagged").EnumerateArray().ToList();
        Assert.Equal(2, listed.Count);
        Assert.Equal((changed.Id, changed.Sha256, changed.Size, ArchiveScene.Sha(tampered), (long)tampered.Length),
            (listed[0].GetProperty("file_id").GetInt64(), listed[0].GetProperty("expected_sha256").GetString(), listed[0].GetProperty("expected_size").GetInt64(), listed[0].GetProperty("actual_sha256").GetString(), listed[0].GetProperty("actual_size").GetInt64()));
        Assert.Equal((missing.Id, missing.Sha256, "missing", JsonValueKind.Null),
            (listed[1].GetProperty("file_id").GetInt64(), listed[1].GetProperty("expected_sha256").GetString(), listed[1].GetProperty("actual_sha256").GetString(), listed[1].GetProperty("actual_size").ValueKind));

        // Both records are quarantined, each with its own row from the read that found it.
        var mismatches = audit.Where(a => a.Action == AuditActions.IntegrityMismatch).ToList();
        Assert.Equal([changed.Id, missing.Id], mismatches.Select(m => m.TargetId!.Value));
        Assert.All(mismatches, m => Assert.Contains("\"check\":\"archive\"", m.DetailsJson, StringComparison.Ordinal));
        Assert.Equal(FileState.Quarantined, (await h.FileAsync(changed.Id)).State);
        Assert.Equal(FileState.Quarantined, (await h.FileAsync(missing.Id)).State);
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("late/dropped.log")]
    [InlineData("web/ffffffffffffffff")]
    public async Task AnEntryNobodyRecordedRefusesTheArchive(string stray)
    {
        await using var h = await RunnerHarness.CreateAsync();
        var scene = await ArchiveScene.BuildAsync(h);
        var path = Path.Combine(scene.CaseDirectory, stray);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "nobody recorded this", Ct);

        Assert.Equal([stray], await scene.Executor().StrayEntriesAsync(scene.CaseId, Ct));
        var task = await scene.RequestArchiveAsync();
        Assert.Equal(0, await scene.Executor().RunPendingAsync(Ct));
        var row = await scene.TaskAsync(task.Id);
        // Not a fault that another attempt would cure: given up at once.
        Assert.Equal((CaseTaskState.Failed, 1), (row.State, row.Attempt));
        Assert.Contains($"first: {stray}", row.Message, StringComparison.Ordinal);
        Assert.Equal(CaseState.Closed, await scene.StateAsync());
        Assert.True(scene.StoreWhole && scene.FilesWhole && File.Exists(path));
        Assert.Single(await h.AuditAsync(), a => a.Action == AuditActions.CaseArchiveFailed);
        Assert.False(Directory.Exists(CaseFolders.CaseDirectory(scene.Archives, scene.CaseId)) && Directory.EnumerateFileSystemEntries(CaseFolders.CaseDirectory(scene.Archives, scene.CaseId)).Any());
    }

    [Fact]
    public async Task AnArchiveIsNotStartedWithoutRoomForTheWholeOfTheEvidence()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var scene = await ArchiveScene.BuildAsync(h);
        var task = await scene.RequestArchiveAsync();
        // More headroom than any volume has.
        Assert.Equal(0, await scene.Executor(options: new LifecycleOptions { ArchiveHeadroomBytes = long.MaxValue / 4 }).RunPendingAsync(Ct));
        var row = await scene.TaskAsync(task.Id);
        Assert.Equal(CaseTaskState.Failed, row.State);
        Assert.Contains("bytes are kept free", row.Message, StringComparison.Ordinal);
        Assert.True(scene.StoreWhole && scene.FilesWhole);
    }

    [Fact]
    public async Task ADifferentStoreIdentityStopsACommittedArchiveBeforeAnyPartitionIsDropped()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var scene = await ArchiveScene.BuildAsync(h);
        var task = await scene.RequestArchiveAsync();
        var ours = h.Events.StoreId;
        var executor = scene.Executor(onStep: step =>
        {
            if (step == "store")
            {
                // The store at this address is replaced between the commit and the drop.
                h.Events.StoreId = StoreIdentity.NewId();
            }
        });
        await Assert.ThrowsAsync<StoreIdentityException>(() => executor.ArchiveAsync(task, Ct));
        Assert.True(scene.StoreWhole && scene.FilesWhole);
        Assert.Equal(CaseTaskState.Committed, (await scene.TaskAsync(task.Id)).State);
        h.Events.StoreId = ours;
        Assert.Equal(0, await scene.Executor().RunPendingAsync(Ct));
        await scene.AssertArchivedAsync(await scene.TaskAsync(task.Id));
    }

    [Fact]
    public async Task ThePurgeOfAnArchivedCaseTakesItsArchivesToo()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var scene = await ArchiveScene.BuildAsync(h);
        var archive = await scene.ArchiveAsync();
        await using (var db = h.Open())
        {
            var actor = new AuditActor("alice");
            await LifecycleOperations.SoftDeleteAsync(db, h.Store.Audit, actor, scene.CaseId, "to archive", TimeSpan.FromDays(7), h.Store.Clock, Ct);
            await LifecycleOperations.RequestPurgeAsync(db, h.Store.Audit, actor, scene.CaseId, twoPerson: false, h.Store.Clock, Ct);
        }

        Assert.Equal(1, await scene.Executor().RunPendingAsync(Ct));
        Assert.False(Directory.Exists(archive.ArchiveDirectory));
        Assert.False(Directory.Exists(CaseFolders.CaseDirectory(scene.Archives, scene.CaseId)));
        Assert.True(Directory.Exists(scene.Archives));
        Assert.False(Directory.Exists(scene.CaseDirectory));
        await using var check = h.Open();
        Assert.False(await check.Cases.AnyAsync(c => c.Id == scene.CaseId, Ct));
        Assert.All(scene.AllArrived, f => Assert.Equal(0u, h.Events.Current((ulong)f.Id)));
        await scene.AssertKeptAsync(check);
    }

    [Theory]
    [InlineData("evidence", "evidence", null)]
    [InlineData("evidence", "evidence/archives", null)]
    [InlineData("data/evidence", "data", null)]
    [InlineData("evidence", "cache", "cache")]
    [InlineData("evidence", "cache/archives", "cache")]
    public void TheArchiveRootMustBeApartFromTheEvidenceRootAndTheCacheRoot(string evidence, string archive, string? cache)
    {
        var root = Path.Combine(Path.GetTempPath(), "urlog-roots");
        var paths = new LifecyclePaths(Path.Combine(root, evidence), cache is null ? null : Path.Combine(root, cache), Path.Combine(root, "backups"), Path.Combine(root, archive));
        var refused = Assert.Throws<ArgumentException>(() => paths.CheckedArchiveRoot());
        Assert.Contains("must be apart", refused.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => (RunnerHarness.Options() with { EvidenceRoot = paths.EvidenceRoot, CacheRoot = paths.CacheRoot, ArchiveRoot = paths.ArchiveRoot }).Validate());
        // Beside each other is fine, and a name that only starts like the other root is not inside it.
        Assert.Equal(Path.Combine(root, "evidence-archives"), new LifecyclePaths(Path.Combine(root, "evidence"), Path.Combine(root, "evidence-cache"), "b", Path.Combine(root, "evidence-archives")).CheckedArchiveRoot());
        Assert.Null(new LifecyclePaths(Path.Combine(root, "evidence"), null, "b").CheckedArchiveRoot());
    }
}
