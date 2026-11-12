using Urlog.Core.Audit;
using Urlog.Core.Jobs;
using Urlog.Core.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Core.Tests;

/// <summary>
/// The job runner against a real metadata file and an in-memory event store: what a job writes on each ending, who
/// runs at the same time, what the reconcilers do at startup, and what the event store would show at every point.
/// </summary>
public sealed class JobRunnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AQueuedFileIsParsedHashedAndVisibleOnlyAfterItsCommit()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var (bytes, lines) = TestData.NumberedLines(20_000, seed: 1);
        var (file, job, _) = await h.AddFileAsync(@case.Id, bytes, "a.log", HookedSource.Id);
        var fileId = (ulong)file.Id;
        long visibleDuringTheParse = -1;
        h.BeforeChunk = ctx =>
        {
            if (ctx.ChunkIndex == 2)
            {
                visibleDuringTheParse = h.Events.Visible(fileId);
            }
        };

        await h.Runner().RunAsync(Ct);

        var done = await h.JobAsync(job.Id);
        Assert.Equal((JobState.Done, 1, 1, (long)lines, 0L), (done.State, done.Generation, done.Attempt, done.Rows, done.Rejects));
        Assert.Equal(done.ChunksRead, done.ChunksParsed);
        Assert.True(done.ChunksRead >= 4, $"chunks: {done.ChunksRead}");
        Assert.Equal(bytes.Length, done.BytesRead);
        Assert.Null(done.Message);
        var parsed = await h.FileAsync(file.Id);
        Assert.Equal((FileState.Parsed, 1, (long)lines), (parsed.State, parsed.ParseGeneration, parsed.RowCount));

        Assert.Equal(0, visibleDuringTheParse); // rows were in the store, none of them visible
        Assert.Equal(lines, h.Events.Visible(fileId));
        Assert.Equal([1u], h.Events.Generations(fileId));
        Assert.Equal(["commit " + fileId + " 1"], h.Events.Calls); // a first attempt has nothing to delete
        Assert.Equal(["genesis", "case_create", "file_intake"], (await h.AuditAsync()).Select(a => a.Action).ToArray());
    }

    [Fact]
    public async Task AFileChangedAfterIntakeIsQuarantinedAndShowsNoRows()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var (bytes, _) = TestData.NumberedLines(20_000, seed: 2);
        var (file, job, path) = await h.AddFileAsync(@case.Id, bytes, "a.log");
        var good = await h.AddFileAsync(@case.Id, TestData.NumberedLines(5_000, seed: 3).Bytes, "b.log");

        // One byte of a letter becomes another letter: same size, same line count, another hash.
        var tampered = (byte[])bytes.Clone();
        var at = Array.FindLastIndex(tampered, b => b is >= (byte)'a' and <= (byte)'y');
        tampered[at]++;
        await File.WriteAllBytesAsync(path, tampered, Ct);

        await h.Runner().RunAsync(Ct);

        var failed = await h.JobAsync(job.Id);
        Assert.Equal(JobState.Failed, failed.State);
        Assert.StartsWith("integrity mismatch", failed.Message, StringComparison.Ordinal);
        Assert.Contains(file.Sha256, failed.Message, StringComparison.Ordinal);
        Assert.Equal(FileState.Quarantined, (await h.FileAsync(file.Id)).State);
        Assert.Equal(0, h.Events.Visible((ulong)file.Id));
        Assert.Empty(h.Events.Generations((ulong)file.Id)); // the attempt's rows were removed, not only hidden
        Assert.Equal(0u, h.Events.Current((ulong)file.Id));

        var row = Assert.Single(await h.AuditAsync(), a => a.Action == AuditActions.IntegrityMismatch);
        Assert.Equal((AuditTargets.File, (long?)file.Id, AuditActions.SystemActor), (row.TargetType, row.TargetId, row.Actor));
        Assert.Contains("\"check\":\"pre_parse\"", row.DetailsJson, StringComparison.Ordinal);
        Assert.True((await AuditVerifier.VerifyAsync(h.Store.Path, Ct)).Ok);

        // The queue went on: the next file of the case is parsed.
        Assert.Equal(JobState.Done, (await h.JobAsync(good.Job.Id)).State);
        await using var db = h.Open();
        await Assert.ThrowsAsync<JobRefusedException>(() => JobOperations.EnqueueAsync(db, file.Id, DateTime.UtcNow, Ct));
    }

    [Fact]
    public async Task ATruncatedFileIsAMismatchToo()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var (bytes, _) = TestData.NumberedLines(20_000, seed: 4);
        var (file, job, path) = await h.AddFileAsync(@case.Id, bytes, "a.log");
        await File.WriteAllBytesAsync(path, bytes.AsMemory(0, bytes.Length / 2), Ct);

        await h.Runner().RunAsync(Ct);

        Assert.Equal(JobState.Failed, (await h.JobAsync(job.Id)).State);
        Assert.Equal(FileState.Quarantined, (await h.FileAsync(file.Id)).State);
        Assert.Equal(0, h.Events.Visible((ulong)file.Id));
    }

    [Fact]
    public async Task TwoFilesOfOneCaseRunOneAtATimeAndTwoCasesRunTogether()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var one = await h.AddCaseAsync("one");
        var two = await h.AddCaseAsync("two");
        var a = await h.AddFileAsync(one.Id, TestData.NumberedLines(9_000, seed: 5).Bytes, "a.log", HookedSource.Id);
        var b = await h.AddFileAsync(one.Id, TestData.NumberedLines(9_000, seed: 6).Bytes, "b.log", HookedSource.Id);
        var c = await h.AddFileAsync(two.Id, TestData.NumberedLines(9_000, seed: 7).Bytes, "c.log", HookedSource.Id);

        var gate = new Lock();
        var parsing = new Dictionary<ulong, HashSet<ulong>>(); // case -> files being parsed now
        var maxPerCase = 0;
        var bothCases = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim();
        h.BeforeChunk = ctx =>
        {
            lock (gate)
            {
                if (!parsing.TryGetValue(ctx.CaseId, out var files))
                {
                    parsing[ctx.CaseId] = files = [];
                }

                files.Add(ctx.FileId);
                maxPerCase = Math.Max(maxPerCase, files.Count);
                if (parsing.Count(p => p.Value.Count > 0) == 2)
                {
                    bothCases.TrySetResult();
                }
            }

            // The first chunk of the first two files waits here until both cases are in a parse at once.
            if (ctx.ChunkIndex == 0 && !release.IsSet)
            {
                release.Wait(TimeSpan.FromSeconds(20), Ct);
            }

            if (ctx.ChunkIndex >= 2)
            {
                lock (gate)
                {
                    parsing[ctx.CaseId].Remove(ctx.FileId);
                }
            }
        };

        var run = h.Runner(RunnerHarness.Options(jobs: 3)).RunAsync(Ct);
        await bothCases.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        // Three slots, three queued files, two cases: exactly two run, the second file of case one waits.
        await using (var db = h.Open())
        {
            Assert.Equal(2, await db.Jobs.CountAsync(j => j.State == JobState.Parsing, Ct));
            Assert.Equal(JobState.Queued, (await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == b.Job.Id, Ct)).State);
        }

        release.Set();
        await run;

        Assert.Equal(1, maxPerCase);
        foreach (var job in new[] { a.Job, b.Job, c.Job })
        {
            Assert.Equal(JobState.Done, (await h.JobAsync(job.Id)).State);
        }

        var first = await h.JobAsync(a.Job.Id);
        var second = await h.JobAsync(b.Job.Id);
        Assert.True(second.StartedAt >= first.FinishedAt, "the second file of a case starts after the first has finished");
    }

    [Fact]
    public async Task AParserFailureFailsTheJobWithItsChunkContextAndTheQueueGoesOn()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var (bad, badJob, _) = await h.AddFileAsync(@case.Id, TestData.NumberedLines(20_000, seed: 8).Bytes, "bad.log", HookedSource.Id);
        var good = await h.AddFileAsync(@case.Id, TestData.NumberedLines(5_000, seed: 9).Bytes, "good.log");
        h.BeforeChunk = ctx =>
        {
            if (ctx.FileId == (ulong)bad.Id && ctx.ChunkIndex == 2)
            {
                throw new InvalidDataException("record 42 is not a record");
            }
        };

        await h.Runner().RunAsync(Ct);

        var failed = await h.JobAsync(badJob.Id);
        Assert.Equal(JobState.Failed, failed.State);
        Assert.Contains($"file {bad.Id}, chunk 2 at offset ", failed.Message, StringComparison.Ordinal);
        Assert.Contains("InvalidDataException: record 42 is not a record", failed.Message, StringComparison.Ordinal);
        Assert.Equal(FileState.Failed, (await h.FileAsync(bad.Id)).State);
        Assert.Empty(h.Events.Generations((ulong)bad.Id));
        Assert.Equal(JobState.Done, (await h.JobAsync(good.Job.Id)).State);
        Assert.DoesNotContain(await h.AuditAsync(), a => a.Action == AuditActions.IntegrityMismatch);
    }

    [Fact]
    public async Task AJobLeftInParsingIsCleanedRequeuedAndRunAgainWithANewGeneration()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var (bytes, lines) = TestData.NumberedLines(20_000, seed: 10);
        var (file, job, _) = await h.AddFileAsync(@case.Id, bytes, "a.log");
        var fileId = (ulong)file.Id;

        // What a killed worker leaves: the job in parsing, half the rows of generation 1 in the store.
        await using (var db = h.Open())
        {
            var claimed = await JobOperations.ClaimAsync(db, "dead:1", DateTime.UtcNow, Ct);
            Assert.Equal(1, claimed!.Job.Generation);
        }

        h.Events.Seed(fileId, 1, lines / 2);

        var runner = h.Runner();
        await runner.RunAsync(Ct);

        Assert.Equal((1, 0, 0, 0), (runner.LastReconcile!.Requeued, runner.LastReconcile.FailedAtCap, runner.LastReconcile.GenerationsWritten, runner.LastReconcile.PurgingCases.Count));
        var done = await h.JobAsync(job.Id);
        Assert.Equal((JobState.Done, 2, 2, (long)lines), (done.State, done.Attempt, done.Generation, done.Rows));
        Assert.Equal([2u], h.Events.Generations(fileId));
        Assert.Equal(lines, h.Events.Visible(fileId));
        Assert.Equal(2, (await h.FileAsync(file.Id)).ParseGeneration);
        Assert.Equal($"delete-except {fileId} keep 0", h.Events.Calls[0]); // the reconciler, before anything is claimed
    }

    [Fact]
    public async Task AFileWithNoDetectedSourceAndNoOverrideIsParsedByTheGenericSource()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var (bytes, lines) = TestData.NumberedLines(3_000, seed: 41);
        var (file, job, _) = await h.AddFileAsync(@case.Id, bytes, "a.log", source: null);

        await h.Runner().RunAsync(Ct);

        var done = await h.JobAsync(job.Id);
        Assert.True(done.State == JobState.Done, done.Message);
        Assert.Equal(lines, h.Events.Visible((ulong)file.Id));
        Assert.Null((await h.FileAsync(file.Id)).DetectedSource);
    }

    [Fact]
    public async Task AnArtifactIsRecordedAndNeverQueued()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        await using var db = h.Open();
        var host = await HostOperations.GetOrCreateAsync(db, @case.Id, "web-01", DateTime.UtcNow, Ct);
        var artifact = new EvidenceFile
        {
            CaseId = @case.Id,
            HostId = host.Id,
            Sha256 = new string('a', 64),
            Size = 0,
            OriginalName = "empty.log",
            StoredPath = "empty.log",
            Source = FileSource.Cli,
            Uploader = "alice",
            ReceivedAt = DateTime.UtcNow,
            Encoding = "utf8",
            DetectedKind = Urlog.Core.Detection.FileKind.Artifact,
            Detection = "{\"kind\":\"artifact\",\"format\":\"empty\"}",
            State = FileState.Artifact,
        };
        var added = (await FileOperations.AddAsync(db, h.Store.Audit, new AuditActor("alice"), artifact, Ct)).File;

        await Assert.ThrowsAsync<JobRefusedException>(() => JobOperations.EnqueueAsync(db, added.Id, DateTime.UtcNow, Ct));
        await h.Runner().RunAsync(Ct);

        var stored = await h.FileAsync(added.Id);
        Assert.Equal(FileState.Artifact, stored.State);
        Assert.Equal(Urlog.Core.Detection.FileKind.Artifact, stored.DetectedKind);
        Assert.Contains(await h.AuditAsync(), row => row.Action == AuditActions.FileIntake && row.TargetId == added.Id);
        Assert.DoesNotContain(h.Events.Calls, c => c.StartsWith("commit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AJobLeftInParsingForTheThirdTimeIsFailed()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var (file, job, _) = await h.AddFileAsync(@case.Id, TestData.NumberedLines(5_000, seed: 11).Bytes, "a.log");
        await using (var db = h.Open())
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                await JobOperations.ClaimAsync(db, "dead:1", DateTime.UtcNow, Ct);
                if (attempt < 3)
                {
                    await JobOperations.RequeueAsync(db, job.Id, "unclean stop", countAttempt: true, 3, DateTime.UtcNow, Ct);
                }
            }
        }

        var runner = h.Runner();
        await runner.RunAsync(Ct);

        Assert.Equal((0, 1, 0, 0), (runner.LastReconcile!.Requeued, runner.LastReconcile.FailedAtCap, runner.LastReconcile.GenerationsWritten, runner.LastReconcile.PurgingCases.Count));
        Assert.Equal(JobState.Failed, (await h.JobAsync(job.Id)).State);
        Assert.Equal(0, h.Events.Visible((ulong)file.Id));
    }

    [Fact]
    public async Task TheGenerationSyncWritesTheEventStoreFromSqlite()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var (bytes, lines) = TestData.NumberedLines(5_000, seed: 12);
        var (file, _, _) = await h.AddFileAsync(@case.Id, bytes, "a.log");
        var fileId = (ulong)file.Id;

        // The event store refuses the commit once: SQLite says done, the store has no row, the file shows nothing.
        h.Events.FailNextCommit = new IOException("connection reset");
        await h.Runner().RunAsync(Ct);
        Assert.Equal(1, (await h.FileAsync(file.Id)).ParseGeneration);
        Assert.Equal(0u, h.Events.Current(fileId));
        Assert.Equal(0, h.Events.Visible(fileId));

        // The next start reads SQLite and writes the row.
        var runner = h.Runner();
        await runner.RunAsync(Ct);
        Assert.Equal((0, 0, 1, 0), (runner.LastReconcile!.Requeued, runner.LastReconcile.FailedAtCap, runner.LastReconcile.GenerationsWritten, runner.LastReconcile.PurgingCases.Count));
        Assert.Equal(1u, h.Events.Current(fileId));
        Assert.Equal(lines, h.Events.Visible(fileId));

        // And a row the store has that disagrees with SQLite is overwritten.
        h.Events.Seed(fileId, 1, lines, current: 7);
        runner = h.Runner();
        await runner.RunAsync(Ct);
        Assert.Equal(1, runner.LastReconcile!.GenerationsWritten);
        Assert.Equal(1u, h.Events.Current(fileId));
    }

    [Fact]
    public async Task AReparseKeepsTheOldGenerationVisibleUntilTheNewOneIsCommitted()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var (bytes, lines) = TestData.NumberedLines(20_000, seed: 13);
        var (file, _, _) = await h.AddFileAsync(@case.Id, bytes, "a.log", HookedSource.Id);
        var fileId = (ulong)file.Id;
        await h.Runner().RunAsync(Ct);
        Assert.Equal(lines, h.Events.Visible(fileId));

        Job reparse;
        await using (var db = h.Open())
        {
            reparse = await JobOperations.EnqueueAsync(db, file.Id, DateTime.UtcNow, Ct);
        }

        (long Visible, uint Current)? during = null;
        h.BeforeChunk = ctx =>
        {
            if (ctx.ChunkIndex == 3)
            {
                during = (h.Events.Visible(fileId), h.Events.Current(fileId));
            }
        };
        await h.Runner().RunAsync(Ct);

        Assert.Equal((lines, 1u), during); // the first parse, whole, while the second runs
        var done = await h.JobAsync(reparse.Id);
        Assert.Equal((JobState.Done, JobKind.Reparse, 2), (done.State, done.Kind, done.Generation));
        Assert.Equal([2u], h.Events.Generations(fileId));
        Assert.Equal(lines, h.Events.Visible(fileId));
        Assert.Equal([$"commit {fileId} 1", $"delete-except {fileId} keep 1", $"commit {fileId} 2", $"delete-below {fileId} 2"], h.Events.Calls);
        var row = Assert.Single(await h.AuditAsync(), a => a.Action == AuditActions.FileReparse);
        Assert.Contains("\"from_generation\":1", row.DetailsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BelowTheWatermarkTheQueueWaitsSaysWhyAndResumesWhenSpaceIsBack()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var (bytes, lines) = TestData.NumberedLines(5_000, seed: 14);
        var (file, job, _) = await h.AddFileAsync(@case.Id, bytes, "a.log");
        h.Events.FreeBytes = 5L << 30; // the event store's disk: 5 GB free of 1,000, the mark is 100 GB (10 %)

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var runner = h.Runner(RunnerHarness.Options(once: false));
        var run = runner.RunAsync(stop.Token);
        await RunnerHarness.EventuallyAsync(async () => (await h.JobAsync(job.Id)).Message is not null, "the pause message on the queued job");

        var waiting = await h.JobAsync(job.Id);
        Assert.Equal(JobState.Queued, waiting.State);
        Assert.Equal("paused: 5.0 GB free on clickhouse:default, below the mark of 100.0 GB; ingest resumes when space is freed", waiting.Message);
        Assert.True(runner.Paused);
        await Task.Delay(300, Ct); // several watermark ticks: still one row, still queued
        var paused = Assert.Single(await h.AuditAsync(), a => a.Action == AuditActions.IngestPaused);
        Assert.Equal("""{"free_bytes":5368709120,"mark_bytes":107374182400,"queued_jobs":1,"total_bytes":1073741824000,"volume":"clickhouse:default"}""", paused.DetailsJson);
        Assert.Equal(JobState.Queued, (await h.JobAsync(job.Id)).State);

        h.Events.FreeBytes = 500L << 30;
        await RunnerHarness.EventuallyAsync(async () => (await h.JobAsync(job.Id)).State == JobState.Done, "the job after space came back");
        await stop.CancelAsync();
        await run;

        Assert.False(runner.Paused);
        Assert.Null((await h.JobAsync(job.Id)).Message);
        Assert.Equal(lines, h.Events.Visible((ulong)file.Id));
        Assert.Single(await h.AuditAsync(), a => a.Action == AuditActions.IngestResumed);
        Assert.True((await AuditVerifier.VerifyAsync(h.Store.Path, Ct)).Ok);
    }

    [Fact]
    public async Task ALocalVolumeBelowTheMarkPausesToo()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var (_, job, _) = await h.AddFileAsync(@case.Id, TestData.NumberedLines(2_000, seed: 15).Bytes, "a.log");
        h.Disk.FreeBytes = 19L << 30; // under the 20 GB floor on a 100 GB volume
        h.Disk.TotalBytes = 100L << 30;

        await h.Runner().RunAsync(Ct); // --once with a paused queue: leaves, the job stays

        var waiting = await h.JobAsync(job.Id);
        Assert.Equal(JobState.Queued, waiting.State);
        Assert.StartsWith("paused: 19.0 GB free on ", waiting.Message, StringComparison.Ordinal);
        Assert.Contains("below the mark of 20.0 GB", waiting.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStopPutsTheRunningJobBackWithoutCountingAnAttempt()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var (file, job, _) = await h.AddFileAsync(@case.Id, TestData.NumberedLines(20_000, seed: 16).Bytes, "a.log", HookedSource.Id);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var inChunkTwo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.BeforeChunk = ctx =>
        {
            if (ctx.ChunkIndex == 2)
            {
                inChunkTwo.TrySetResult();
                stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(20));
            }
        };

        var run = h.Runner(RunnerHarness.Options(once: false)).RunAsync(stop.Token);
        await inChunkTwo.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        await stop.CancelAsync();
        await run;

        var queued = await h.JobAsync(job.Id);
        Assert.Equal((JobState.Queued, 1, (string?)null), (queued.State, queued.Attempt, queued.Worker));
        Assert.Contains("shutdown", queued.Message, StringComparison.Ordinal);
        Assert.Empty(h.Events.Generations((ulong)file.Id));

        // The next start runs it as attempt 1 again, with a generation the stopped attempt never used.
        h.BeforeChunk = _ => { };
        await h.Runner().RunAsync(Ct);
        var done = await h.JobAsync(job.Id);
        Assert.Equal((JobState.Done, 1, 2), (done.State, done.Attempt, done.Generation));
    }

    [Fact]
    public async Task ProgressIsInSqliteWhileTheJobRuns()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var (_, job, _) = await h.AddFileAsync(@case.Id, TestData.NumberedLines(40_000, seed: 17).Bytes, "a.log", HookedSource.Id);
        var hold = new ManualResetEventSlim();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.BeforeChunk = ctx =>
        {
            if (ctx.ChunkIndex == 6)
            {
                held.TrySetResult();
                hold.Wait(TimeSpan.FromSeconds(20), Ct);
            }
        };

        var run = h.Runner().RunAsync(Ct);
        await held.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        var since = DateTime.UtcNow;
        await RunnerHarness.EventuallyAsync(async () => (await h.JobAsync(job.Id)) is { ChunksParsed: >= 6, Rows: > 0 }, "six parsed chunks and acknowledged rows in the job row", seconds: 5);
        var seen = await h.JobAsync(job.Id);
        Assert.True(DateTime.UtcNow - since < TimeSpan.FromSeconds(5));
        Assert.Equal(JobState.Parsing, seen.State);
        Assert.True(seen.ChunksRead >= seen.ChunksParsed && seen.ChunksAcked <= seen.ChunksParsed && seen.BytesRead > 0, $"read {seen.ChunksRead}, parsed {seen.ChunksParsed}, acked {seen.ChunksAcked}");
        hold.Set();
        await run;
        Assert.Equal(JobState.Done, (await h.JobAsync(job.Id)).State);
    }

    [Fact]
    public async Task TheCommitWaitsOutAWriterThatHoldsTheMetadataLock()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var (bytes, lines) = TestData.NumberedLines(5_000, seed: 18);
        var (file, job, _) = await h.AddFileAsync(@case.Id, bytes, "a.log", HookedSource.Id);

        // Another writer takes the SQLite write lock while the file is being parsed and keeps it for seven seconds,
        // past the store's own 5 s busy timeout.
        await using var blocker = new SqliteConnection(MetadataStore.ConnectionString(h.Store.Path));
        await blocker.OpenAsync(Ct);
        var locked = false;
        h.BeforeChunk = _ =>
        {
            if (!locked)
            {
                locked = true;
                using var begin = blocker.CreateCommand();
                begin.CommandText = "BEGIN IMMEDIATE";
                begin.ExecuteNonQuery();
            }
        };

        var run = h.Runner().RunAsync(Ct);
        await Task.Delay(TimeSpan.FromSeconds(7), Ct);
        Assert.Equal(JobState.Parsing, (await h.JobAsync(job.Id)).State);
        Assert.Equal(0, h.Events.Visible((ulong)file.Id));
        await using (var commit = blocker.CreateCommand())
        {
            commit.CommandText = "COMMIT";
            await commit.ExecuteNonQueryAsync(Ct);
        }

        await run;
        Assert.Equal(JobState.Done, (await h.JobAsync(job.Id)).State);
        Assert.Equal(lines, h.Events.Visible((ulong)file.Id));
    }

    [Fact]
    public async Task ASecondRunnerOnTheSameMetadataFileIsRefused()
    {
        await using var h = await RunnerHarness.CreateAsync();
        using (var held = RunnerLock.TryAcquire(h.Store.Path))
        {
            Assert.NotNull(held);
            Assert.Null(RunnerLock.TryAcquire(h.Store.Path));
            var refused = await Assert.ThrowsAsync<RunnerLockedException>(() => h.Runner().RunAsync(Ct));
            Assert.Contains(".runner.lock", refused.Message, StringComparison.Ordinal);
        }

        await h.Runner().RunAsync(Ct); // free again once the holder is gone
    }

    [Fact]
    public async Task AReusedFileIdStartsAboveTheStoresGenerationAndShowsNothingUntilItsCommit()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var (bytes, lines) = TestData.NumberedLines(20_000, seed: 30);
        var (file, job, _) = await h.AddFileAsync(@case.Id, bytes, "a.log", HookedSource.Id);
        var fileId = (ulong)file.Id;

        // The id belonged to a purged file: its partitions are gone, its file_generation row (current 3) is not.
        h.Events.Seed(fileId, 3, rows: 0, current: 3);
        long visibleDuringTheParse = -1;
        h.BeforeChunk = ctx =>
        {
            if (ctx.ChunkIndex == 2)
            {
                Assert.Equal(4u, ctx.ParseGeneration);
                visibleDuringTheParse = h.Events.Visible(fileId);
            }
        };

        await h.Runner().RunAsync(Ct);

        var done = await h.JobAsync(job.Id);
        Assert.Equal((JobState.Done, 4, 1), (done.State, done.Generation, done.Attempt));
        Assert.Equal(0, visibleDuringTheParse);
        Assert.Equal((4u, (long)lines), (h.Events.Current(fileId), h.Events.Visible(fileId)));
        Assert.Equal([4u], h.Events.Generations(fileId));
        Assert.Equal(4, (await h.FileAsync(file.Id)).ParseGeneration);
    }

    [Fact]
    public async Task AMetadataFileOlderThanTheEventStoreNeverReusesAGenerationTheStoreHolds()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var (bytes, lines) = TestData.NumberedLines(20_000, seed: 31);
        var (file, job, _) = await h.AddFileAsync(@case.Id, bytes, "a.log", HookedSource.Id);
        var fileId = (ulong)file.Id;

        // The metadata file was restored from a backup taken before the parse: SQLite says queued, the store holds
        // the whole first parse as generation 1 and shows it.
        h.Events.Seed(fileId, 1, rows: lines + 5, current: 1);
        var seen = new List<(uint Generation, long Visible)>();
        h.BeforeChunk = ctx =>
        {
            if (ctx.ChunkIndex == 2)
            {
                seen.Add((ctx.ParseGeneration, h.Events.Visible(fileId)));
            }
        };

        await h.Runner().RunAsync(Ct);

        // The attempt wrote generation 2, never 1; the rows SQLite does not know went first; the end is one generation.
        Assert.Equal([(2u, 0L)], seen);
        var done = await h.JobAsync(job.Id);
        Assert.Equal((JobState.Done, 2), (done.State, done.Generation));
        Assert.Equal([2u], h.Events.Generations(fileId));
        Assert.Equal((2u, (long)lines), (h.Events.Current(fileId), h.Events.Visible(fileId)));

        // The other way round: SQLite knows generation 2 is committed, and a later re-parse the backup never saw left
        // generation 3 in the store. The sync puts SQLite's 2 back; the next re-parse may not write into the stale 3.
        h.Events.Seed(fileId, 3, rows: 77, current: 3);
        await using (var db = h.Open())
        {
            await JobOperations.EnqueueAsync(db, file.Id, DateTime.UtcNow, Ct);
        }

        seen.Clear();
        var runner = h.Runner();
        await runner.RunAsync(Ct);
        Assert.Equal(1, runner.LastReconcile!.GenerationsWritten);
        Assert.Equal([(3u, (long)lines)], seen); // generation 2 visible and whole while 3 is rewritten from scratch
        Assert.Contains($"delete-except {fileId} keep 2", h.Events.Calls);
        Assert.Equal([3u], h.Events.Generations(fileId));
        Assert.Equal((3u, (long)lines), (h.Events.Current(fileId), h.Events.Visible(fileId)));
    }

    [Fact]
    public async Task AMemoryLimitRefusalPutsTheJobBackAndTheNextAttemptRunsAfterTheBackoff()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var (bytes, lines) = TestData.NumberedLines(20_000, seed: 32);
        var (file, job, _) = await h.AddFileAsync(@case.Id, bytes, "a.log", HookedSource.Id);
        var fileId = (ulong)file.Id;
        h.Events.RefuseSubmits = 1;
        h.Events.Refusal = MemoryLimit;
        var starts = new List<(uint Generation, DateTime At)>();
        h.BeforeChunk = ctx =>
        {
            if (ctx.ChunkIndex == 0)
            {
                starts.Add((ctx.ParseGeneration, DateTime.UtcNow));
            }
        };

        await h.Runner().RunAsync(Ct);

        var done = await h.JobAsync(job.Id);
        Assert.Equal((JobState.Done, 2, 2, (long)lines), (done.State, done.Attempt, done.Generation, done.Rows));
        Assert.Null(done.Message);
        Assert.Equal([1u, 2u], starts.Select(s => s.Generation).ToArray());
        Assert.True(starts[1].At - starts[0].At >= TimeSpan.FromMilliseconds(250), $"the second attempt started {(starts[1].At - starts[0].At).TotalMilliseconds:F0} ms after the first; the backoff is 300 ms");
        Assert.Equal([2u], h.Events.Generations(fileId)); // the refused attempt's rows were removed
        Assert.Equal(lines, h.Events.Visible(fileId));
        Assert.Equal(FileState.Parsed, (await h.FileAsync(file.Id)).State);
    }

    [Fact]
    public async Task AMemoryLimitRefusalOnEveryAttemptFailsTheJobAtTheCap()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var (file, job, _) = await h.AddFileAsync(@case.Id, TestData.NumberedLines(20_000, seed: 33).Bytes, "a.log", HookedSource.Id); // one parser thread: one refused batch per attempt
        var good = await h.AddFileAsync(@case.Id, TestData.NumberedLines(5_000, seed: 34).Bytes, "b.log");
        h.Events.RefuseSubmits = 3;
        h.Events.Refusal = MemoryLimit;

        await h.Runner().RunAsync(Ct);

        var failed = await h.JobAsync(job.Id);
        Assert.Equal((JobState.Failed, 3), (failed.State, failed.Attempt));
        Assert.StartsWith("not retried after 3 attempts: the event store refused the insert for memory", failed.Message, StringComparison.Ordinal);
        Assert.Contains("ClickHouse error 241", failed.Message, StringComparison.Ordinal);
        Assert.Equal(FileState.Failed, (await h.FileAsync(file.Id)).State);
        Assert.Empty(h.Events.Generations((ulong)file.Id));
        Assert.Equal(JobState.Done, (await h.JobAsync(good.Job.Id)).State);
    }

    [Fact]
    public async Task AnyOtherInsertFailureFailsTheJobAtOnce()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var (_, job, _) = await h.AddFileAsync(@case.Id, TestData.NumberedLines(20_000, seed: 35).Bytes, "a.log");
        h.Events.RefuseSubmits = 1;
        h.Events.Refusal = () => new Urlog.Core.Ingest.Native.ClickHouseInsertException(60, "DB::Exception", "Table default.events does not exist", null);

        await h.Runner().RunAsync(Ct);

        var failed = await h.JobAsync(job.Id);
        Assert.Equal((JobState.Failed, 1), (failed.State, failed.Attempt));
        Assert.Contains("ClickHouse error 60", failed.Message, StringComparison.Ordinal);
    }

    private static Urlog.Core.Ingest.Native.ClickHouseInsertException MemoryLimit() => new(
        241, "DB::Exception", "(total) memory limit exceeded: would use 6.58 GiB, current RSS: 5.74 GiB, maximum: 6.54 GiB", null);
}
