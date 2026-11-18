using System.Security.Cryptography;
using System.Text;
using Urlog.Core.Audit;
using Urlog.Core.Jobs;
using Urlog.Core.Lifecycle;
using Urlog.Core.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Urlog.Core.Tests.Lifecycle;

/// <summary>
/// The purge as the executor carries it out against a fake event store and real directories: the end state, a
/// stop at every call it makes with a second executor finishing each one, and what it refuses to go past.
/// </summary>
public sealed class PurgeTests
{
    private static CancellationToken Ct => RunnerHarness.Ct;

    [Fact]
    public async Task APurgeLeavesNothingOfTheCaseAndEverythingOfTheOther()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var scene = await Scene.BuildAsync(h);
        var steps = new List<string>();
        var executor = scene.Executor(onStep: steps.Add);

        Assert.Equal(1, await executor.RunPendingAsync(Ct));
        Assert.Equal(["backup", "mark", "store", "files", "metadata"], steps);
        await scene.AssertPurgedAsync();

        // The order the store saw: drops, the stored histogram with them, then the statistics, then the generation rows; the identity read before each group.
        var calls = h.Events.Calls;
        Assert.Equal(
            [$"drop {scene.Purged} 202601", $"drop {scene.Purged} 202602", $"drop {scene.Purged} 202603", $"drop-histogram {scene.Purged}", $"field-stats {scene.Purged}", $"clear-generations {string.Join(',', scene.PurgedFiles)}"],
            calls.Where(c => c.StartsWith("drop", StringComparison.Ordinal) || c.StartsWith("field-stats", StringComparison.Ordinal) || c.StartsWith("clear", StringComparison.Ordinal)));
        Assert.True(h.Events.IdentityReads >= 2);

        // The chain: the backup's hash is in the backup row, in case_purge and in case_purge_complete, in that order.
        var audit = await h.AuditAsync();
        var backup = audit.Single(a => a.Action == AuditActions.Backup);
        var mark = audit.Single(a => a.Action == AuditActions.CasePurge);
        var complete = audit.Single(a => a.Action == AuditActions.CasePurgeComplete);
        Assert.True(backup.Seq < mark.Seq && mark.Seq < complete.Seq);
        var sha = System.Text.Json.JsonDocument.Parse(backup.DetailsJson).RootElement.GetProperty("sha256").GetString()!;
        Assert.Contains("\"reason\":\"pre_purge\"", backup.DetailsJson, StringComparison.Ordinal);
        Assert.Contains($"\"backup_sha256\":\"{sha}\"", mark.DetailsJson, StringComparison.Ordinal);
        Assert.Contains($"\"backup_sha256\":\"{sha}\"", complete.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"case_name\":\"to purge\"", complete.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"file_count\":3", complete.DetailsJson, StringComparison.Ordinal);
        Assert.Contains($"\"evidence_sha256\":\"{LifecycleOperations.EvidenceDigest(scene.PurgedHashes)}\"", complete.DetailsJson, StringComparison.Ordinal);

        // The backup still holds the purged case's records, and its bytes hash to what the chain says.
        var path = Directory.EnumerateFiles(scene.Backups, "*.sqlite").Single();
        Assert.Equal(sha, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path, Ct))));
        var old = new MetadataStore(path);
        await using (var db = old.CreateDbContext())
        {
            Assert.Equal(CaseState.SoftDeleted, await db.Cases.AsNoTracking().Where(c => c.Id == scene.Purged).Select(c => c.State).SingleAsync(Ct));
            Assert.Equal(scene.PurgedHashes.Order(), (await db.Files.AsNoTracking().Where(f => f.CaseId == scene.Purged).Select(f => f.Sha256).ToListAsync(Ct)).Order());
        }

        Assert.True((await AuditVerifier.VerifyAsync(h.Store.Path, Ct)).Ok);
        Assert.Equal(0, await executor.RunPendingAsync(Ct));
    }

    [Fact]
    public async Task AStopAtAnyCallIsFinishedByTheNextExecutor()
    {
        // One clean run counts every step, every call to the event store and every path removed.
        int total;
        await using (var clean = await RunnerHarness.CreateAsync())
        {
            var scene = await Scene.BuildAsync(clean);
            var ticks = 0;
            Assert.Equal(1, await scene.Executor(_ => ticks++, _ => ticks++, _ => ticks++).RunPendingAsync(Ct));
            total = ticks;
        }

        Assert.InRange(total, 18, 80);
        for (var stopAt = 0; stopAt < total; stopAt++)
        {
            await using var h = await RunnerHarness.CreateAsync();
            var scene = await Scene.BuildAsync(h);
            var seen = 0;
            string? stoppedOn = null;
            void Tick(string what)
            {
                if (seen++ == stopAt)
                {
                    stoppedOn = what;
                    throw new IOException($"stopped by the test at call {stopAt} ({what})");
                }
            }

            var first = scene.Executor(Tick, Tick, Tick);
            var task = await scene.TaskAsync();
            var stop = await Assert.ThrowsAsync<IOException>(() => first.PurgeAsync(scene.Purged, task, Ct));
            Assert.Contains("stopped by the test", stop.Message, StringComparison.Ordinal);
            await scene.AssertOrderHeldAsync($"after a stop at call {stopAt} ({stoppedOn})");

            // The process is gone; the next one starts from what is on disk and in the two stores.
            h.Events.BeforePurgeCall = null;
            Assert.Equal(1, await scene.Executor().RunPendingAsync(Ct));
            await scene.AssertPurgedAsync();
            Assert.True((await AuditVerifier.VerifyAsync(h.Store.Path, Ct)).Ok, $"the chain after a stop at call {stopAt}");
        }
    }

    [Fact]
    public async Task ADifferentStoreIdentityStopsThePurgeBeforeAnythingIsDropped()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var scene = await Scene.BuildAsync(h);
        var ours = h.Events.StoreId;
        // The store at this address was replaced after the purge was released.
        h.Events.StoreId = StoreIdentity.NewId();

        var refused = await Assert.ThrowsAsync<StoreIdentityException>(() => scene.Executor().RunPendingAsync(Ct));
        Assert.Contains("do not belong together", refused.Message, StringComparison.Ordinal);
        Assert.Equal([202601, 202602, 202603], h.Events.Partitions((ulong)scene.Purged));
        Assert.True(h.Events.HasFieldStats((ulong)scene.Purged));
        Assert.All(scene.PurgedFiles, id => Assert.NotEqual(0u, h.Events.Current((ulong)id)));
        Assert.True(File.Exists(scene.StoredFile));
        await using (var db = h.Open())
        {
            // Marked, with the reason on the task; nothing else happened.
            Assert.Equal(CaseState.Purging, await db.Cases.AsNoTracking().Where(c => c.Id == scene.Purged).Select(c => c.State).SingleAsync(Ct));
            Assert.Contains("do not belong together", (await db.CaseTasks.AsNoTracking().SingleAsync(Ct)).Message, StringComparison.Ordinal);
        }

        h.Events.StoreId = ours;
        Assert.Equal(1, await scene.Executor().RunPendingAsync(Ct));
        await scene.AssertPurgedAsync();
    }

    [Fact]
    public async Task GenerationRowsThatAreStillThereStopThePurgeBeforeAnyFileIsRemoved()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var scene = await Scene.BuildAsync(h);
        h.Events.GenerationDeleteStuck = true;
        var executor = scene.Executor(wait: TimeSpan.FromMilliseconds(300));

        Assert.Equal(0, await executor.RunPendingAsync(Ct));
        Assert.True(File.Exists(scene.StoredFile));
        await using (var db = h.Open())
        {
            var task = await db.CaseTasks.AsNoTracking().SingleAsync(Ct);
            Assert.Equal((CaseTaskState.Committed, LifecycleOperations.StepStore), (task.State, task.Step));
            Assert.Contains("still holds 3 generation row(s)", task.Message, StringComparison.Ordinal);
            Assert.Equal(3, await db.Files.CountAsync(f => f.CaseId == scene.Purged, Ct));
        }

        // Not retried at once: the executor waits before it takes the same purge up again.
        h.Events.GenerationDeleteStuck = false;
        Assert.Equal(0, await executor.RunPendingAsync(Ct));
        Assert.Equal(1, await scene.Executor().RunPendingAsync(Ct));
        await scene.AssertPurgedAsync();
    }

    [Fact]
    public async Task APartitionThatKeepsComingBackStopsThePurge()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var scene = await Scene.BuildAsync(h);
        h.Events.PartitionThatReturns = ((ulong)scene.Purged, 202602);
        Assert.Equal(0, await scene.Executor().RunPendingAsync(Ct));
        Assert.True(File.Exists(scene.StoredFile));
        await using var db = h.Open();
        Assert.Contains("still has 1 partition(s) with active parts", (await db.CaseTasks.AsNoTracking().SingleAsync(Ct)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutAnEvidenceRootTheRecordsOfStoredFilesAreNotDeleted()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var scene = await Scene.BuildAsync(h);
        var blind = new LifecycleExecutor(h.Store, h.Events, new LifecycleOptions(), new LifecyclePaths(null, scene.Cache, scene.Backups), NullLogger.Instance);
        Assert.Equal(0, await blind.RunPendingAsync(Ct));
        Assert.True(File.Exists(scene.StoredFile));
        await using (var db = h.Open())
        {
            Assert.Contains("was given none", (await db.CaseTasks.AsNoTracking().SingleAsync(Ct)).Message, StringComparison.Ordinal);
            Assert.Equal(3, await db.Files.CountAsync(f => f.CaseId == scene.Purged, Ct));
        }

        Assert.Equal(1, await scene.Executor().RunPendingAsync(Ct));
        await scene.AssertPurgedAsync();
    }

    [Fact]
    public async Task ACaseRestoredBeforeTheMarkIsNotPurged()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var scene = await Scene.BuildAsync(h);
        // The restore lands between the backup and the mark.
        var executor = scene.Executor(onStep: step =>
        {
            if (step == "mark")
            {
                using var db = h.Open();
                LifecycleOperations.RestoreAsync(db, h.Store.Audit, new AuditActor("alice"), scene.Purged, DateTime.UtcNow, Ct).GetAwaiter().GetResult();
            }
        });
        Assert.Equal(0, await executor.RunPendingAsync(Ct));
        Assert.Equal([202601, 202602, 202603], h.Events.Partitions((ulong)scene.Purged));
        Assert.True(File.Exists(scene.StoredFile));
        await using var check = h.Open();
        Assert.Equal(CaseState.Closed, await check.Cases.AsNoTracking().Where(c => c.Id == scene.Purged).Select(c => c.State).SingleAsync(Ct));
        Assert.Equal(CaseTaskState.Withdrawn, (await check.CaseTasks.AsNoTracking().SingleAsync(Ct)).State);
        Assert.DoesNotContain(await h.AuditAsync(), a => a.Action == AuditActions.CasePurge);
    }

    [Fact]
    public async Task TheScheduledPurgeTakesACaseAfterSevenDaysAndNotWithTwoPersonApproval()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var scene = await Scene.BuildAsync(h, release: false);
        DateTime deletedAt;
        await using (var db = h.Open())
        {
            deletedAt = (await db.Cases.AsNoTracking().Where(c => c.Id == scene.Purged).Select(c => c.DeletedAt).SingleAsync(Ct))!.Value;
        }

        var clock = new MovableClock(deletedAt.AddDays(7).AddSeconds(-1));
        var later = new MetadataStore(h.Store.Path, clock);
        LifecycleExecutor At(LifecycleOptions options) => new(later, h.Events, options, new LifecyclePaths(scene.Evidence, scene.Cache, scene.Backups), NullLogger.Instance);

        Assert.Equal(0, await At(new LifecycleOptions()).RunPendingAsync(Ct));
        clock.Now = deletedAt.AddDays(7);
        // Due, and held: with two-person approval nothing is purged without two administrators.
        Assert.Equal(0, await At(new LifecycleOptions { TwoPersonPurge = true }).RunPendingAsync(Ct));
        Assert.Equal(0, await At(new LifecycleOptions { ScheduledPurge = false }).RunPendingAsync(Ct));
        await using (var db = h.Open())
        {
            Assert.Equal(0, await db.CaseTasks.CountAsync(Ct));
        }

        Assert.Equal(1, await At(new LifecycleOptions()).RunPendingAsync(Ct));
        await scene.AssertPurgedAsync();
        var audit = await h.AuditAsync();
        var request = audit.Single(a => a.Action == AuditActions.CasePurgeRequest);
        Assert.Equal((AuditActions.SystemActor, (long?)null), (request.Actor, request.ActorId));
        Assert.Equal(AuditActions.SystemActor, audit.Single(a => a.Action == AuditActions.CasePurge).Actor);
    }

    [Fact]
    public async Task ARunnerFinishesAtStartWhatAStopLeftInPurging()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var scene = await Scene.BuildAsync(h);
        var seen = 0;
        var task = await scene.TaskAsync();
        // Stopped in the middle of the file removal: the case is purging, the store is empty, some files are left.
        var first = scene.Executor(beforeRemoval: _ =>
        {
            if (++seen == 3)
            {
                throw new IOException("stopped by the test");
            }
        });
        await Assert.ThrowsAsync<IOException>(() => first.PurgeAsync(scene.Purged, task, Ct));

        var runner = h.Runner(RunnerHarness.Options() with { EvidenceRoot = scene.Evidence, CacheRoot = scene.Cache, BackupDirectory = scene.Backups });
        await runner.RunAsync(Ct);
        Assert.Equal([scene.Purged], runner.LastReconcile!.PurgingCases);
        Assert.Equal(1, runner.LastReconcile.Purged);
        await scene.AssertPurgedAsync();
        // The sync that follows a start did not write the purged files' generations back.
        Assert.All(scene.PurgedFiles, id => Assert.Equal(0u, h.Events.Current((ulong)id)));
    }

    [Fact]
    public async Task ACaseMarkedPurgingWithoutATaskIsFinishedAllTheSame()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var scene = await Scene.BuildAsync(h, release: false);
        await using (var db = h.Open())
        {
            await CaseOperations.TransitionAsync(db, h.Store.Audit, new AuditActor("alice"), scene.Purged, CaseState.Purging, DateTime.UtcNow, Ct);
        }

        Assert.Equal(1, await scene.Executor().RunPendingAsync(Ct));
        await scene.AssertPurgedAsync();
    }

    /// <summary>A clock a test moves.</summary>
    private sealed class MovableClock(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }

    /// <summary>
    /// Two cases. One is closed, soft-deleted and released for purge: three months of rows, field statistics, three
    /// file records (two stored files under the evidence root, one of them an upload that arrived, and a member),
    /// an unfinished upload with its sidecar, a cache copy and a queued job. The other is active and has one of each.
    /// </summary>
    private sealed class Scene
    {
        private readonly RunnerHarness _h;

        private Scene(RunnerHarness h)
        {
            _h = h;
            Evidence = Path.Combine(h.Root, "evidence");
            Cache = Path.Combine(h.Root, "evidence-cache");
            Backups = Path.Combine(h.Root, "backups");
        }

        public string Evidence { get; }

        public string Cache { get; }

        public string Backups { get; }

        public long Purged { get; private set; }

        public long Kept { get; private set; }

        public List<long> PurgedFiles { get; } = [];

        public List<string> PurgedHashes { get; } = [];

        public long KeptFile { get; private set; }

        public string StoredFile => Path.Combine(Evidence, Purged.ToString(System.Globalization.CultureInfo.InvariantCulture), "access.log");

        private string KeptStoredFile => Path.Combine(Evidence, Kept.ToString(System.Globalization.CultureInfo.InvariantCulture), "kept.log");

        private string OutsideFile => Path.Combine(_h.Root, "outside", "elsewhere.log");

        public static async Task<Scene> BuildAsync(RunnerHarness h, bool release = true)
        {
            var scene = new Scene(h);
            var actor = new AuditActor("alice");
            scene.Kept = (await h.AddCaseAsync("kept")).Id;
            scene.Purged = (await h.AddCaseAsync("to purge")).Id;
            await using var db = h.Open();

            scene.KeptFile = (await scene.StoreAsync(db, scene.Kept, "kept.log", "kept\n")).Id;
            var plain = await scene.StoreAsync(db, scene.Purged, "access.log", "one\ntwo\n");
            var upload = await scene.StoreAsync(db, scene.Purged, Path.Combine(CaseFolders.Web, "0123456789abcdef"), "container bytes");
            var member = new EvidenceFile
            {
                CaseId = scene.Purged, ParentFileId = upload.Id, RootFileId = upload.Id, ArchiveDepth = 1, ArchivePath = "bundle.zip!a.log", MemberOrdinal = 0,
                Sha256 = Sha("member"), Size = 6, OriginalName = "a.log", StoredPath = upload.StoredPath, Source = FileSource.Web, Uploader = "alice", ReceivedAt = DateTime.UtcNow, Encoding = "utf8",
            };
            await FileOperations.AddMembersAsync(db, h.Store.Audit, upload.Id, [new MemberDraft(member, null)], 1, DateTime.UtcNow, Ct);
            // A file recorded where it was found, outside the evidence root: the purge never removes it.
            Directory.CreateDirectory(Path.GetDirectoryName(scene.OutsideFile)!);
            await File.WriteAllTextAsync(scene.OutsideFile, "elsewhere\n", Ct);
            var files = await db.Files.AsNoTracking().Where(f => f.CaseId == scene.Purged).OrderBy(f => f.Id).ToListAsync(Ct);
            scene.PurgedFiles.AddRange(files.Select(f => f.Id));
            scene.PurgedHashes.AddRange(files.Select(f => f.Sha256));
            await JobOperations.EnqueueAsync(db, plain.Id, DateTime.UtcNow, Ct);

            // What intake leaves beside the stored files.
            var caseDirectory = CaseFolders.CaseDirectory(scene.Evidence, scene.Purged);
            Directory.CreateDirectory(Path.Combine(caseDirectory, CaseFolders.Uploads));
            await File.WriteAllTextAsync(Path.Combine(caseDirectory, CaseFolders.Uploads, "fedcba9876543210"), "half an upl", Ct);
            await File.WriteAllTextAsync(Path.Combine(caseDirectory, CaseFolders.Uploads, "fedcba9876543210.json"), "{}", Ct);
            foreach (var id in new[] { scene.Purged, scene.Kept })
            {
                var cache = CaseFolders.CaseDirectory(scene.Cache, id);
                Directory.CreateDirectory(cache);
                await File.WriteAllTextAsync(Path.Combine(cache, "1.body"), "seekable copy", Ct);
            }

            // The event store's side.
            foreach (var month in new[] { 202601, 202602, 202603 })
            {
                h.Events.SeedPartition((ulong)scene.Purged, month, 1000);
            }

            h.Events.SeedPartition((ulong)scene.Kept, 202601, 500);
            h.Events.SeedFieldStats((ulong)scene.Purged);
            h.Events.SeedFieldStats((ulong)scene.Kept);
            foreach (var id in scene.PurgedFiles)
            {
                h.Events.Seed((ulong)id, 1, 10, current: 1);
            }

            h.Events.Seed((ulong)scene.KeptFile, 2, 10, current: 2);

            await LifecycleOperations.CloseAsync(db, h.Store.Audit, actor, scene.Purged, DateTime.UtcNow, Ct);
            await LifecycleOperations.SoftDeleteAsync(db, h.Store.Audit, actor, scene.Purged, "to purge", TimeSpan.FromDays(7), DateTime.UtcNow, Ct);
            if (release)
            {
                await LifecycleOperations.RequestPurgeAsync(db, h.Store.Audit, actor, scene.Purged, twoPerson: false, DateTime.UtcNow, Ct);
            }

            return scene;
        }

        public LifecycleExecutor Executor(Action<string>? onStep = null, Action<string>? beforeRemoval = null, Action<string>? beforeStoreCall = null, TimeSpan? wait = null)
        {
            _h.Events.BeforePurgeCall = beforeStoreCall;
            return new LifecycleExecutor(_h.Store, _h.Events, new LifecycleOptions(), new LifecyclePaths(Evidence, Cache, Backups), NullLogger.Instance)
            {
                OnStep = onStep,
                BeforeRemoval = beforeRemoval,
                DeleteWait = wait ?? TimeSpan.FromSeconds(5),
            };
        }

        public async Task<CaseTask> TaskAsync()
        {
            await using var db = _h.Open();
            return await db.CaseTasks.AsNoTracking().SingleAsync(t => t.CaseId == Purged, Ct);
        }

        /// <summary>Whatever a stop left, the order held: files go only after the store is empty, records only after the files.</summary>
        public async Task AssertOrderHeldAsync(string when)
        {
            await using var db = _h.Open();
            var state = await db.Cases.AsNoTracking().Where(c => c.Id == Purged).Select(c => (CaseState?)c.State).SingleOrDefaultAsync(Ct);
            var caseDirectory = CaseFolders.CaseDirectory(Evidence, Purged);
            var filesWhole = File.Exists(StoredFile) && Directory.EnumerateFiles(caseDirectory, "*", SearchOption.AllDirectories).Count() == 4
                && File.Exists(Path.Combine(CaseFolders.CaseDirectory(Cache, Purged), "1.body"));
            var storeEmpty = _h.Events.Partitions((ulong)Purged).Length == 0 && !_h.Events.HasFieldStats((ulong)Purged) && PurgedFiles.All(id => _h.Events.Current((ulong)id) == 0);
            var storeWhole = _h.Events.Partitions((ulong)Purged).Length == 3 && _h.Events.HasFieldStats((ulong)Purged) && PurgedFiles.All(id => _h.Events.Current((ulong)id) == 1);
            if (state == CaseState.SoftDeleted)
            {
                Assert.True(storeWhole && filesWhole, $"{when}: the case is not marked, so nothing of it may be gone");
            }
            else if (state == CaseState.Purging)
            {
                Assert.True(filesWhole || storeEmpty, $"{when}: a file is gone while the event store still holds something of the case");
                Assert.Equal(3, await db.Files.CountAsync(f => f.CaseId == Purged, Ct));
            }
            else
            {
                Assert.Null(state);
                Assert.True(storeEmpty && !Directory.Exists(caseDirectory), $"{when}: the records are gone before the rest");
            }

            await AssertKeptAsync(db);
        }

        public async Task AssertPurgedAsync()
        {
            await using var db = _h.Open();
            Assert.False(await db.Cases.AnyAsync(c => c.Id == Purged, Ct));
            Assert.Equal(0, await db.Files.CountAsync(f => f.CaseId == Purged, Ct));
            Assert.Equal(0, await db.Jobs.CountAsync(j => j.CaseId == Purged, Ct));
            Assert.Equal(0, await db.Hosts.CountAsync(x => x.CaseId == Purged, Ct));
            Assert.Equal(0, await db.CaseMembers.CountAsync(m => m.CaseId == Purged, Ct));
            Assert.Equal(0, await db.CaseTasks.CountAsync(t => t.CaseId == Purged, Ct));
            Assert.Empty(_h.Events.Partitions((ulong)Purged));
            Assert.False(_h.Events.HasFieldStats((ulong)Purged));
            Assert.All(PurgedFiles, id => Assert.Equal(0u, _h.Events.Current((ulong)id)));
            Assert.False(Directory.Exists(CaseFolders.CaseDirectory(Evidence, Purged)));
            Assert.False(Directory.Exists(CaseFolders.CaseDirectory(Cache, Purged)));
            Assert.True(Directory.Exists(Evidence));
            Assert.True(File.Exists(OutsideFile));
            Assert.Single(await _h.AuditAsync(), a => a.Action == AuditActions.CasePurgeComplete);
            await AssertKeptAsync(db);
        }

        private async Task AssertKeptAsync(MetadataDb db)
        {
            Assert.Equal(CaseState.Active, await db.Cases.AsNoTracking().Where(c => c.Id == Kept).Select(c => c.State).SingleAsync(Ct));
            Assert.Equal(1, await db.Files.CountAsync(f => f.CaseId == Kept, Ct));
            Assert.Equal([202601], _h.Events.Partitions((ulong)Kept));
            Assert.True(_h.Events.HasFieldStats((ulong)Kept));
            Assert.Equal(2u, _h.Events.Current((ulong)KeptFile));
            Assert.True(File.Exists(KeptStoredFile));
            Assert.True(File.Exists(Path.Combine(CaseFolders.CaseDirectory(Cache, Kept), "1.body")));
        }

        private async Task<EvidenceFile> StoreAsync(MetadataDb db, long caseId, string relative, string content)
        {
            var path = Path.Combine(CaseFolders.CaseDirectory(Evidence, caseId), relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content, Ct);
            Urlog.Core.Intake.EvidencePermissions.MakeReadOnly(path);
            var file = new EvidenceFile
            {
                CaseId = caseId, Sha256 = Sha(content), Size = Encoding.UTF8.GetByteCount(content), OriginalName = Path.GetFileName(relative), StoredPath = relative.Replace('\\', '/'),
                Source = FileSource.Web, Uploader = "alice", ReceivedAt = DateTime.UtcNow, Encoding = "utf8", DetectedSource = "test-lines",
            };
            return (await FileOperations.AddAsync(db, _h.Store.Audit, new AuditActor("alice"), file, Ct)).File;
        }

        private static string Sha(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }
}
