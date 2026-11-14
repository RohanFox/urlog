using System.Security.Cryptography;
using System.Text;
using Urlog.Core.Audit;
using Urlog.Core.Intake;
using Urlog.Core.Intake.Archives;
using Urlog.Core.Jobs;
using Urlog.Core.Lifecycle;
using Urlog.Core.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Urlog.Core.Tests.Lifecycle;

/// <summary>
/// The integrity sweep: what it reads and what it never opens, what a changed and a missing file become, and that
/// it holds while a job runs and goes on from the same byte.
/// </summary>
public sealed class IntegritySweepTests
{
    private static CancellationToken Ct => RunnerHarness.Ct;

    private static DateTime Later => DateTime.UtcNow.AddMinutes(1);

    [Fact]
    public async Task OfAHundredFilesTheOneThatChangedIsQuarantinedAndTheRestAreRecordedAsMatching()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var world = await World.BuildAsync(h, files: 100);
        var changed = world.Files[41];
        var queued = await world.EnqueueAsync(changed.Id);
        var tampered = world.Tamper(changed);

        var read = new HashSet<long>();
        var report = await world.Sweep(onRead: (id, _) => read.Add(id)).RunAsync(Later, () => SweepSignal.Go, Ct);
        Assert.Equal((99, 1, 0, 0, true), (report.Ok, report.Mismatched, report.Missing, report.Unreadable, report.Complete));
        Assert.Equal(world.Files.Select(f => f.Id).Order(), read.Order());

        var audit = await h.AuditAsync();
        var ok = audit.Where(a => a.Action == AuditActions.IntegrityOk).ToList();
        Assert.Equal(99, ok.Count);
        Assert.All(ok, row => Assert.Contains("\"check\":\"sweep\"", row.DetailsJson, StringComparison.Ordinal));
        Assert.DoesNotContain(ok, row => row.TargetId == changed.Id);
        var mismatch = Assert.Single(audit, a => a.Action == AuditActions.IntegrityMismatch);
        Assert.Equal((AuditTargets.File, (long?)changed.Id, AuditActions.SystemActor), (mismatch.TargetType, mismatch.TargetId, mismatch.Actor));
        Assert.Contains($"\"sha256\":\"{changed.Sha256}\"", mismatch.DetailsJson, StringComparison.Ordinal);
        Assert.Contains($"\"actual_sha256\":\"{Sha(tampered)}\"", mismatch.DetailsJson, StringComparison.Ordinal);
        Assert.Contains($"\"actual_size\":{tampered.Length}", mismatch.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"check\":\"sweep\"", mismatch.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"jobs_cancelled\":1", mismatch.DetailsJson, StringComparison.Ordinal);

        await using (var db = h.Open())
        {
            var file = await db.Files.AsNoTracking().SingleAsync(f => f.Id == changed.Id, Ct);
            Assert.Equal(FileState.Quarantined, file.State);
            Assert.NotNull(file.VerifiedAt);
            Assert.Equal(JobState.Cancelled, (await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == queued, Ct)).State);
            var refused = await Assert.ThrowsAsync<JobRefusedException>(() => JobOperations.EnqueueAsync(db, changed.Id, DateTime.UtcNow, Ct));
            Assert.Contains("quarantined", refused.Message, StringComparison.Ordinal);
            Assert.Equal(99, await db.Files.CountAsync(f => f.VerifiedAt != null && f.State != FileState.Quarantined && f.ParentFileId == null, Ct));
        }

        // The bytes on disk are as the sweep found them.
        Assert.Equal(tampered, await File.ReadAllBytesAsync(world.PathOf(changed), Ct));
        Assert.True((await AuditVerifier.VerifyAsync(h.Store.Path, Ct)).Ok);

        // The next sweep reads the 99 again and leaves the quarantined file alone: its mismatch is on record.
        read.Clear();
        var second = await world.Sweep(onRead: (id, _) => read.Add(id)).RunAsync(DateTime.UtcNow.AddMinutes(2), () => SweepSignal.Go, Ct);
        Assert.Equal((99, 0), (second.Ok, second.Mismatched));
        Assert.DoesNotContain(changed.Id, read);
        // And one that is asked for results older than the ones it already has reads nothing.
        var none = await world.Sweep().RunAsync(DateTime.UtcNow.AddDays(-1), () => SweepSignal.Go, Ct);
        Assert.Equal((0, 0L, true), (none.Ok, none.Bytes, none.Complete));
    }

    [Fact]
    public async Task AFileThatIsGoneIsAMismatchAndAMissingRootIsNot()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var world = await World.BuildAsync(h, files: 4);
        var gone = world.Files[1];
        File.SetAttributes(world.PathOf(gone), FileAttributes.Normal);
        File.Delete(world.PathOf(gone));

        var report = await world.Sweep().RunAsync(Later, () => SweepSignal.Go, Ct);
        Assert.Equal((3, 0, 1, 0), (report.Ok, report.Mismatched, report.Missing, report.Unreadable));
        var mismatch = Assert.Single(await h.AuditAsync(), a => a.Action == AuditActions.IntegrityMismatch);
        Assert.Equal(gone.Id, mismatch.TargetId);
        Assert.Contains("\"actual_sha256\":\"missing\"", mismatch.DetailsJson, StringComparison.Ordinal);
        Assert.Equal(FileState.Quarantined, (await h.FileAsync(gone.Id)).State);

        // The whole evidence root away (a volume that is not mounted): no result for any file, nothing quarantined.
        Directory.Move(world.Evidence, world.Evidence + "-unmounted");
        var before = (await h.AuditAsync()).Count;
        var blind = await world.Sweep().RunAsync(DateTime.UtcNow.AddMinutes(5), () => SweepSignal.Go, Ct);
        Assert.Equal((0, 0, 0, 3, true), (blind.Ok, blind.Mismatched, blind.Missing, blind.Unreadable, blind.Complete));
        Assert.Equal(before, (await h.AuditAsync()).Count);
        Directory.Move(world.Evidence + "-unmounted", world.Evidence);
        Assert.Equal(3, (await world.Sweep().RunAsync(DateTime.UtcNow.AddMinutes(5), () => SweepSignal.Go, Ct)).Ok);
    }

    [Fact]
    public async Task OnlyStoredFilesOfCasesWhoseFilesAreInPlaceAreRead()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var world = await World.BuildAsync(h, files: 2);
        // A member of a container, an unfinished upload and a cache copy: none is a stored file of its own.
        await using (var db = h.Open())
        {
            var member = new EvidenceFile
            {
                CaseId = world.CaseId, ParentFileId = world.Files[0].Id, RootFileId = world.Files[0].Id, ArchiveDepth = 1, ArchivePath = "x!y", MemberOrdinal = 0,
                Sha256 = Sha(Encoding.UTF8.GetBytes("member")), Size = 6, OriginalName = "y", StoredPath = world.Files[0].StoredPath, Source = FileSource.Web, Uploader = "alice", ReceivedAt = DateTime.UtcNow, Encoding = "utf8",
            };
            await FileOperations.AddMembersAsync(db, h.Store.Audit, world.Files[0].Id, [new MemberDraft(member, null)], 1, DateTime.UtcNow, Ct);
        }

        var uploads = Directory.CreateDirectory(Path.Combine(CaseFolders.CaseDirectory(world.Evidence, world.CaseId), CaseFolders.Uploads)).FullName;
        await File.WriteAllTextAsync(Path.Combine(uploads, "0123456789abcdef"), "half", Ct);
        var archived = await World.BuildAsync(h, files: 1, name: "archived", evidence: world.Evidence);
        var purging = await World.BuildAsync(h, files: 1, name: "purging", evidence: world.Evidence);
        var deleted = await World.BuildAsync(h, files: 1, name: "deleted", evidence: world.Evidence);
        var busy = await World.BuildAsync(h, files: 1, name: "busy", evidence: world.Evidence);
        await using (var db = h.Open())
        {
            await db.Cases.Where(c => c.Id == archived.CaseId).ExecuteUpdateAsync(s => s.SetProperty(c => c.State, CaseState.Archived), Ct);
            await db.Cases.Where(c => c.Id == purging.CaseId).ExecuteUpdateAsync(s => s.SetProperty(c => c.State, CaseState.Purging), Ct);
            await LifecycleOperations.SoftDeleteAsync(db, h.Store.Audit, new AuditActor("alice"), deleted.CaseId, "deleted", TimeSpan.FromDays(7), DateTime.UtcNow, Ct);
            db.CaseTasks.Add(new CaseTask { CaseId = busy.CaseId, Kind = CaseTaskKind.Archive, State = CaseTaskState.Running, RequestedBy = "alice", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync(Ct);
        }

        var read = new HashSet<long>();
        var report = await world.Sweep(onRead: (id, _) => read.Add(id)).RunAsync(Later, () => SweepSignal.Go, Ct);
        // The two files of the active case and the one of the soft-deleted case, which can still be restored.
        Assert.Equal(world.Files.Select(f => f.Id).Append(deleted.Files[0].Id).Order(), read.Order());
        Assert.Equal(3, report.Ok);
    }

    [Fact]
    public async Task ItReadsNothingWhileAJobRunsAndGoesOnFromTheSameByte()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var big = new byte[(3 << 20) + 12345];
        Random.Shared.NextBytes(big);
        var world = await World.BuildAsync(h, files: 1, content: _ => big);
        var jobRunning = false;
        var readsWhileRunning = 0;
        var asked = 0;
        var held = 0;
        long bytes = 0;
        SweepSignal Signal()
        {
            asked++;
            // A job starts after the first megabyte and runs for five questions.
            if (asked is >= 2 and <= 6)
            {
                jobRunning = true;
                held++;
                return SweepSignal.Wait;
            }

            jobRunning = false;
            return SweepSignal.Go;
        }

        var sweep = world.Sweep(yieldBytes: 1 << 20, onRead: (_, count) =>
        {
            bytes += count;
            readsWhileRunning += jobRunning ? 1 : 0;
        });
        var report = await sweep.RunAsync(Later, Signal, Ct);
        Assert.Equal(5, held);
        Assert.Equal(0, readsWhileRunning);
        // Read once, start to end: the hash state survived the hold, and the file matches.
        Assert.Equal(big.Length, bytes);
        Assert.Equal((1, 0, (long)big.Length, true), (report.Ok, report.Mismatched, report.Bytes, report.Complete));
        Assert.True(asked >= 9, $"asked {asked} times: before the file and after every megabyte");
    }

    [Fact]
    public async Task AStopKeepsWhatWasReadAndTheNextRunTakesTheRestFirst()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var world = await World.BuildAsync(h, files: 10);
        var asked = 0;
        var first = await world.Sweep(batch: 3).RunAsync(Later, () => ++asked > 4 ? SweepSignal.Stop : SweepSignal.Go, Ct);
        Assert.Equal((4, false), (first.Ok, first.Complete));
        await using (var db = h.Open())
        {
            Assert.Equal(4, await db.Files.CountAsync(f => f.VerifiedAt != null, Ct));
            Assert.Equal(4, await db.Audit.CountAsync(a => a.Action == AuditActions.IntegrityOk, Ct));
        }

        var order = new List<long>();
        var second = await world.Sweep(onRead: (id, _) => order.Add(id)).RunAsync(DateTime.UtcNow.AddMinutes(2), () => SweepSignal.Go, Ct);
        Assert.Equal((10, true), (second.Ok, second.Complete));
        // Least recently verified first: the six that have no result yet, then the four that have one.
        Assert.Equal(world.Files.Skip(4).Select(f => f.Id).Concat(world.Files.Take(4).Select(f => f.Id)), order.Distinct());
    }

    [Fact]
    public async Task WhatWasReadIsRecordedBeforeTheSweepHoldsSoAKillInTheHoldLosesNothing()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var world = await World.BuildAsync(h, files: 6);
        world.Tamper(world.Files[1]);
        var asked = 0;
        var holding = false;
        var readsInTheHold = 0;
        // One cut-off for both runs: what the first has recorded is not due for the second.
        var cutoff = DateTime.UtcNow;
        using var kill = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        // A job starts when the sweep has read four files, and outlives the process.
        var sweep = world.Sweep(onRead: (_, _) => readsInTheHold += holding ? 1 : 0);
        var running = sweep.RunAsync(cutoff, () =>
        {
            if (++asked <= 4)
            {
                return SweepSignal.Go;
            }

            holding = true;
            return SweepSignal.Wait;
        }, kill.Token);

        // While it holds, with no end of the run in sight, the three matches and the mismatch are in the chain already.
        await RunnerHarness.EventuallyAsync(async () =>
        {
            await using var db = h.Open();
            return holding && await db.Audit.CountAsync(a => a.Action == AuditActions.IntegrityOk, Ct) == 3;
        }, "the results written at the start of the hold");
        Assert.False(running.IsCompleted);
        var bytesAtTheHold = sweep.BytesRead;
        await using (var db = h.Open())
        {
            Assert.Equal(4, await db.Files.CountAsync(f => f.VerifiedAt != null, Ct));
            Assert.Equal(1, await db.Audit.CountAsync(a => a.Action == AuditActions.IntegrityMismatch, Ct));
        }

        // The kill: nothing after this line runs in the sweep but its unwinding.
        await Task.Delay(500, Ct);
        await kill.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(0, readsInTheHold);
        Assert.Equal(bytesAtTheHold, sweep.BytesRead);
        await using (var db = h.Open())
        {
            Assert.Equal(3, await db.Audit.CountAsync(a => a.Action == AuditActions.IntegrityOk, Ct));
            Assert.Equal(4, await db.Files.CountAsync(f => f.VerifiedAt != null, Ct));
        }

        // The next run reads the two that were left, and only those.
        var read = new HashSet<long>();
        var rest = await world.Sweep(onRead: (id, _) => read.Add(id)).RunAsync(cutoff, () => SweepSignal.Go, Ct);
        Assert.Equal((2, 0, true), (rest.Ok, rest.Mismatched, rest.Complete));
        Assert.Equal(world.Files.Skip(4).Select(f => f.Id), read.Order());
    }

    [Fact]
    public void TheSweepIsDueFromTheLatestScheduledInstant()
    {
        var at = new TimeOnly(3, 0);
        Assert.Null(IntegritySweep.DueFrom(null, new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 11, 12, 3, 0, 0, DateTimeKind.Utc), IntegritySweep.DueFrom(at, new DateTime(2026, 11, 13, 2, 59, 59, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 11, 13, 3, 0, 0, DateTimeKind.Utc), IntegritySweep.DueFrom(at, new DateTime(2026, 11, 13, 3, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 11, 13, 3, 0, 0, DateTimeKind.Utc), IntegritySweep.DueFrom(at, new DateTime(2026, 11, 13, 23, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public async Task ARunnerSweepsOnItsScheduleOnceAndAfterTheQueueIsEmpty()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var world = await World.BuildAsync(h, files: 5);
        world.Tamper(world.Files[2]);
        // A job of another case, parsed by this runner before the sweep gets the disk.
        var other = await h.AddCaseAsync("ingest");
        var lines = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 2000).Select(i => $"line {i}\n")));
        var (parsed, job, _) = await h.AddFileAsync(other.Id, lines, "ingest.log");

        var options = RunnerHarness.Options(once: false) with
        {
            EvidenceRoot = world.Evidence,
            Lifecycle = new LifecycleOptions { SweepAtUtc = new TimeOnly(0, 0), Interval = TimeSpan.FromMilliseconds(50) },
        };
        var runner = h.Runner(options);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var running = runner.RunAsync(stop.Token);
        await RunnerHarness.EventuallyAsync(() => Task.FromResult(runner.LastSweep is { Complete: true }), "the sweep ended");
        var report = runner.LastSweep!;
        // The five stored files and the one that was parsed in place.
        Assert.Equal((5, 1, 0), (report.Ok, report.Mismatched, report.Missing));
        Assert.Equal(JobState.Done, (await h.JobAsync(job.Id)).State);
        Assert.Equal(FileState.Parsed, (await h.FileAsync(parsed.Id)).State);
        Assert.Equal(FileState.Quarantined, (await h.FileAsync(world.Files[2].Id)).State);

        // The same day's sweep is not taken twice.
        await Task.Delay(300, Ct);
        await stop.CancelAsync();
        await running;
        Assert.Equal(5, (await h.AuditAsync()).Count(a => a.Action == AuditActions.IntegrityOk));
    }

    private static string Sha(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    /// <summary>One active case with files stored under an evidence root, as intake leaves them: read-only, recorded relative to the case's directory.</summary>
    private sealed class World
    {
        private readonly RunnerHarness _h;

        private World(RunnerHarness h, string evidence)
        {
            _h = h;
            Evidence = evidence;
        }

        public string Evidence { get; }

        public long CaseId { get; private set; }

        public List<EvidenceFile> Files { get; } = [];

        public static async Task<World> BuildAsync(RunnerHarness h, int files, string name = "sweep", string? evidence = null, Func<int, byte[]>? content = null)
        {
            var world = new World(h, evidence ?? Path.Combine(h.Root, "evidence"));
            world.CaseId = (await h.AddCaseAsync(name)).Id;
            await using var db = h.Open();
            for (var i = 0; i < files; i++)
            {
                var bytes = content?.Invoke(i) ?? Encoding.UTF8.GetBytes($"{name} file {i}\n" + new string('x', 200 + i));
                var relative = $"file-{i:D3}.log";
                var path = Path.Combine(CaseFolders.CaseDirectory(world.Evidence, world.CaseId), relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, bytes, Ct);
                EvidencePermissions.MakeReadOnly(path);
                var file = new EvidenceFile
                {
                    CaseId = world.CaseId, Sha256 = Sha(bytes), Size = bytes.Length, OriginalName = relative, StoredPath = relative,
                    Source = FileSource.Web, Uploader = "alice", ReceivedAt = DateTime.UtcNow, Encoding = "utf8", DetectedSource = "test-lines",
                };
                world.Files.Add((await FileOperations.AddAsync(db, h.Store.Audit, new AuditActor("alice"), file, Ct)).File);
            }

            return world;
        }

        public string PathOf(EvidenceFile file) => Path.Combine(CaseFolders.CaseDirectory(Evidence, file.CaseId), file.StoredPath);

        public IntegritySweep Sweep(Action<long, int>? onRead = null, int batch = 200, int yieldBytes = 16 << 20) =>
            new(_h.Store, new MemberStore(Evidence, _h.CacheRoot), Evidence, new LifecycleOptions { SweepBatch = batch, SweepYieldBytes = yieldBytes }, NullLogger.Instance) { OnRead = onRead };

        public async Task<long> EnqueueAsync(long fileId)
        {
            await using var db = _h.Open();
            return (await JobOperations.EnqueueAsync(db, fileId, DateTime.UtcNow, Ct)).Id;
        }

        /// <summary>Changes one byte of the stored file, size unchanged, as someone with access to the disk could. Returns the new content.</summary>
        public byte[] Tamper(EvidenceFile file)
        {
            var path = PathOf(file);
            var bytes = File.ReadAllBytes(path);
            bytes[^3] ^= 0x01;
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
            else
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.WriteAllBytes(path, bytes);
            EvidencePermissions.MakeReadOnly(path);
            return bytes;
        }
    }
}
