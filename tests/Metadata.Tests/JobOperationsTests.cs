using Urlog.Core.Audit;
using Urlog.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Metadata.Tests;

/// <summary>
/// The ingest queue's transitions: who may claim what, which generation an attempt gets, what each ending writes to
/// the file row and to the audit chain, and that an ending whose audit row fails leaves nothing behind.
/// </summary>
public sealed class JobOperationsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTime Now => DateTime.UtcNow;

    [Fact]
    public async Task EnqueueNeedsAnActiveCaseAParseableFileAndNoOtherOpenJob()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var owner = await TempDb.AddUserAsync(db);
        var @case = await TempDb.AddCaseAsync(db, owner);
        var file = (await FileOperations.AddAsync(db, temp.Store.Audit, TempDb.Alice, TempDb.NewFile(@case.Id, "a\n"), Ct)).File;

        var job = await JobOperations.EnqueueAsync(db, file.Id, Now, Ct);
        Assert.Equal((JobState.Queued, JobKind.Parse, 0, 1), (job.State, job.Kind, job.Generation, job.Attempt));
        var second = await Assert.ThrowsAsync<JobRefusedException>(() => JobOperations.EnqueueAsync(db, file.Id, Now, Ct));
        Assert.Contains("already has a job", second.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<JobRefusedException>(() => JobOperations.EnqueueAsync(db, 4242, Now, Ct));

        // A queued job can be cancelled; then the file takes a new one.
        await JobOperations.CancelAsync(db, job.Id, Now, Ct);
        await Assert.ThrowsAsync<JobStateException>(() => JobOperations.CancelAsync(db, job.Id, Now, Ct));
        var again = await JobOperations.EnqueueAsync(db, file.Id, Now, Ct);

        await JobOperations.CancelAsync(db, again.Id, Now, Ct);
        await CaseOperations.TransitionAsync(db, temp.Store.Audit, TempDb.Alice, @case.Id, CaseState.Closed, Now, Ct);
        await Assert.ThrowsAsync<CaseNotActiveException>(() => JobOperations.EnqueueAsync(db, file.Id, Now, Ct));
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Fact]
    public async Task ClaimTakesTheOldestJobOfACaseWithNothingRunningAndSkipsCasesThatAreNotActive()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var owner = await TempDb.AddUserAsync(db);
        var one = await TempDb.AddCaseAsync(db, owner, "one");
        var two = await TempDb.AddCaseAsync(db, owner, "two");
        var closed = await TempDb.AddCaseAsync(db, owner, "closed");
        var a = await QueueAsync(temp, db, one.Id, "a", Now.AddSeconds(-30));
        var b = await QueueAsync(temp, db, one.Id, "b", Now.AddSeconds(-20));
        var c = await QueueAsync(temp, db, two.Id, "c", Now.AddSeconds(-10));
        var d = await QueueAsync(temp, db, closed.Id, "d", Now.AddSeconds(-40));
        await CaseOperations.TransitionAsync(db, temp.Store.Audit, TempDb.Alice, closed.Id, CaseState.Closed, Now, Ct);

        var first = await JobOperations.ClaimAsync(db, "w:1", Now, Ct);
        Assert.Equal(a.Id, first!.Job.Id); // d is older, but its case is closed
        Assert.Equal((JobState.Parsing, "w:1", 1, 0), (first.Job.State, first.Job.Worker, first.Job.Generation, first.CommittedGeneration));
        Assert.Equal("one", first.CaseName);

        var next = await JobOperations.ClaimAsync(db, "w:2", Now, Ct);
        Assert.Equal(c.Id, next!.Job.Id); // b waits: its case has a job running
        Assert.Null(await JobOperations.ClaimAsync(db, "w:3", Now, Ct));

        await JobOperations.CompleteAsync(db, temp.Store.Audit, a.Id, "w:1", new JobCounts(1, 1, 10, 0, 100), Now, Ct);
        var third = await JobOperations.ClaimAsync(db, "w:1", Now, Ct);
        Assert.Equal(b.Id, third!.Job.Id);

        Assert.Equal(JobState.Queued, (await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == d.Id, Ct)).State);
    }

    [Fact]
    public async Task EightConcurrentClaimersGetOneJobEach()
    {
        await using var temp = await TempDb.CreateAsync();
        await using (var db = temp.Open())
        {
            var owner = await TempDb.AddUserAsync(db);
            for (var i = 0; i < 4; i++)
            {
                var @case = await TempDb.AddCaseAsync(db, owner, $"case {i}");
                await QueueAsync(temp, db, @case.Id, $"file {i}", Now);
            }
        }

        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            await using var db = temp.Open();
            return await JobOperations.ClaimAsync(db, $"w:{i}", Now, Ct);
        }, Ct)));

        var won = claims.Where(c => c is not null).Select(c => c!.Job.Id).ToList();
        Assert.Equal(4, won.Count);
        Assert.Equal(4, won.Distinct().Count());
    }

    [Fact]
    public async Task EveryAttemptGetsANewGenerationAndTheCommitWritesTheFileRow()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var @case = await TempDb.AddCaseAsync(db, await TempDb.AddUserAsync(db));
        var job = await QueueAsync(temp, db, @case.Id, "a", Now);

        var first = await JobOperations.ClaimAsync(db, "w:1", Now, Ct);
        Assert.Equal(1, first!.Job.Generation);
        await JobOperations.WriteProgressAsync(db, job.Id, 3, 2, 1, 500, 4, 1000, Ct);

        // An unclean stop: back to queued, attempt 2, counters zeroed; the retry writes generation 2.
        Assert.Equal(RequeueOutcome.Requeued, await JobOperations.RequeueAsync(db, job.Id, "unclean stop", countAttempt: true, 3, Now, Ct));
        var queued = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id, Ct);
        Assert.Equal((JobState.Queued, 2, 0L, 0L, (string?)null, "unclean stop"), (queued.State, queued.Attempt, queued.ChunksRead, queued.Rows, queued.Worker, queued.Message));
        var second = await JobOperations.ClaimAsync(db, "w:1", Now, Ct);
        Assert.Equal((2, 0), (second!.Job.Generation, second.CommittedGeneration));

        // A graceful stop does not count as an attempt, and still gets a new generation.
        Assert.Equal(RequeueOutcome.Requeued, await JobOperations.RequeueAsync(db, job.Id, "shutdown", countAttempt: false, 3, Now, Ct));
        var third = await JobOperations.ClaimAsync(db, "w:1", Now, Ct);
        Assert.Equal((3, 2), (third!.Job.Generation, third.Job.Attempt));

        await Assert.ThrowsAsync<JobStateException>(() => JobOperations.CompleteAsync(db, temp.Store.Audit, job.Id, "someone-else", new JobCounts(1, 1, 1, 0, 1), Now, Ct));
        await JobOperations.CompleteAsync(db, temp.Store.Audit, job.Id, "w:1", new JobCounts(5, 5, 900, 7, 4096), Now, Ct);
        var done = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id, Ct);
        var file = await db.Files.AsNoTracking().SingleAsync(f => f.Id == job.FileId, Ct);
        Assert.Equal((JobState.Done, 900L, 7L, 5L), (done.State, done.Rows, done.Rejects, done.ChunksAcked));
        Assert.Equal((FileState.Parsed, 3, 900L, 7L), (file.State, file.ParseGeneration, file.RowCount, file.RejectCount));
        Assert.Equal(3, await JobOperations.CommittedGenerationAsync(db, job.FileId, Ct));
        Assert.Equal(3, (await JobOperations.CommittedGenerationsAsync(db, Ct))[job.FileId]);

        // A first parse writes no audit row of its own; the re-parse does, with both generations.
        Assert.DoesNotContain(await Actions(db), a => a == AuditActions.FileReparse);
        var reparse = await JobOperations.EnqueueAsync(db, job.FileId, Now, Ct);
        Assert.Equal(JobKind.Reparse, reparse.Kind);
        var fourth = await JobOperations.ClaimAsync(db, "w:1", Now, Ct);
        Assert.Equal((4, 3), (fourth!.Job.Generation, fourth.CommittedGeneration));
        await JobOperations.CompleteAsync(db, temp.Store.Audit, reparse.Id, "w:1", new JobCounts(5, 5, 901, 7, 4096), Now, Ct);
        var row = await db.Audit.AsNoTracking().OrderBy(a => a.Seq).LastAsync(Ct);
        Assert.Equal((AuditActions.FileReparse, AuditActions.SystemActor, AuditTargets.File, (long?)job.FileId), (row.Action, row.Actor, row.TargetType, row.TargetId));
        Assert.Contains("\"from_generation\":3", row.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"to_generation\":4", row.DetailsJson, StringComparison.Ordinal);
        Assert.True((await AuditVerifier.VerifyAsync(temp.Path, Ct)).Ok);
    }

    [Fact]
    public async Task TheThirdUncleanStopFailsTheJob()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var @case = await TempDb.AddCaseAsync(db, await TempDb.AddUserAsync(db));
        var job = await QueueAsync(temp, db, @case.Id, "a", Now);
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await JobOperations.ClaimAsync(db, "w:1", Now, Ct);
            Assert.Equal(RequeueOutcome.Requeued, await JobOperations.RequeueAsync(db, job.Id, "unclean stop", countAttempt: true, 3, Now, Ct));
        }

        await JobOperations.ClaimAsync(db, "w:1", Now, Ct);
        Assert.Equal(RequeueOutcome.Failed, await JobOperations.RequeueAsync(db, job.Id, "unclean stop", countAttempt: true, 3, Now, Ct));
        var failed = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id, Ct);
        Assert.Equal((JobState.Failed, 3), (failed.State, failed.Attempt));
        Assert.Equal("not retried after 3 attempts: unclean stop", failed.Message);
        Assert.Equal(FileState.Failed, (await db.Files.AsNoTracking().SingleAsync(f => f.Id == job.FileId, Ct)).State);
        Assert.Equal(RequeueOutcome.NotParsing, await JobOperations.RequeueAsync(db, job.Id, "again", countAttempt: true, 3, Now, Ct));
    }

    [Fact]
    public async Task AFailureKeepsTheMessageVerbatimAndAFailedReparseLeavesTheFileParsed()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var @case = await TempDb.AddCaseAsync(db, await TempDb.AddUserAsync(db));
        var job = await QueueAsync(temp, db, @case.Id, "a", Now);
        await JobOperations.ClaimAsync(db, "w:1", Now, Ct);
        await JobOperations.CompleteAsync(db, temp.Store.Audit, job.Id, "w:1", new JobCounts(1, 1, 10, 0, 10), Now, Ct);

        var reparse = await JobOperations.EnqueueAsync(db, job.FileId, Now, Ct);
        await JobOperations.ClaimAsync(db, "w:1", Now, Ct);
        const string reason = "nginx: file 1, chunk 3 at offset 201326592: InvalidDataException: bad record";
        await JobOperations.FailAsync(db, reparse.Id, "w:1", reason, Now, Ct);
        var failed = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == reparse.Id, Ct);
        Assert.Equal((JobState.Failed, reason), (failed.State, failed.Message));
        var file = await db.Files.AsNoTracking().SingleAsync(f => f.Id == job.FileId, Ct);
        Assert.Equal((FileState.Parsed, 1), (file.State, file.ParseGeneration)); // generation 1 is still the visible parse
    }

    [Fact]
    public async Task QuarantineWritesTheMismatchInTheSameTransactionAndAFailedAuditWriteLeavesNothing()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var @case = await TempDb.AddCaseAsync(db, await TempDb.AddUserAsync(db));
        var job = await QueueAsync(temp, db, @case.Id, "a", Now);
        await JobOperations.ClaimAsync(db, "w:1", Now, Ct);
        var mismatch = new IntegrityMismatch(new string('e', 64), 7);

        // The audit append fails: no quarantine, no failed job, no row.
        await Assert.ThrowsAsync<InvalidOperationException>(() => JobOperations.QuarantineAsync(db, new ThrowingWriter(temp.Store.Audit), job.Id, "w:1", mismatch, Now, Ct));
        Assert.Equal(JobState.Parsing, (await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id, Ct)).State);
        Assert.Equal(FileState.Received, (await db.Files.AsNoTracking().SingleAsync(f => f.Id == job.FileId, Ct)).State);
        Assert.DoesNotContain(await Actions(db), a => a == AuditActions.IntegrityMismatch);

        await JobOperations.QuarantineAsync(db, temp.Store.Audit, job.Id, "w:1", mismatch, Now, Ct);
        var failed = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id, Ct);
        var file = await db.Files.AsNoTracking().SingleAsync(f => f.Id == job.FileId, Ct);
        Assert.Equal(JobState.Failed, failed.State);
        Assert.StartsWith("integrity mismatch", failed.Message, StringComparison.Ordinal);
        Assert.Equal(FileState.Quarantined, file.State);
        var row = await db.Audit.AsNoTracking().OrderBy(a => a.Seq).LastAsync(Ct);
        Assert.Equal((AuditActions.IntegrityMismatch, AuditTargets.File, (long?)file.Id), (row.Action, row.TargetType, row.TargetId));
        Assert.Contains($"\"sha256\":\"{file.Sha256}\"", row.DetailsJson, StringComparison.Ordinal);
        Assert.Contains($"\"actual_sha256\":\"{mismatch.ActualSha256}\"", row.DetailsJson, StringComparison.Ordinal);

        // A quarantined file is never queued again.
        await Assert.ThrowsAsync<JobRefusedException>(() => JobOperations.EnqueueAsync(db, file.Id, Now, Ct));
        Assert.True((await AuditVerifier.VerifyAsync(temp.Path, Ct)).Ok);
    }

    [Fact]
    public async Task PauseAndResumeMarkTheQueuedJobsAndWriteOneRowEach()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var owner = await TempDb.AddUserAsync(db);
        var one = await TempDb.AddCaseAsync(db, owner, "one");
        var two = await TempDb.AddCaseAsync(db, owner, "two");
        var running = await QueueAsync(temp, db, one.Id, "a", Now.AddSeconds(-5));
        var waiting = await QueueAsync(temp, db, two.Id, "b", Now);
        await JobOperations.ClaimAsync(db, "w:1", Now, Ct);

        var details = new Dictionary<string, object?> { ["volume"] = "clickhouse:default", ["free_bytes"] = 5L << 30, ["total_bytes"] = 100L << 30, ["mark_bytes"] = 20L << 30 };
        await JobOperations.PauseAsync(db, temp.Store.Audit, "5.0 GB free on clickhouse:default, below the mark of 20.0 GB", details, Ct);
        Assert.StartsWith(JobOperations.PausedPrefix, (await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == waiting.Id, Ct)).Message, StringComparison.Ordinal);
        Assert.Null((await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == running.Id, Ct)).Message);
        var paused = await db.Audit.AsNoTracking().OrderBy(a => a.Seq).LastAsync(Ct);
        Assert.Equal((AuditActions.IngestPaused, AuditActions.SystemActor, (string?)null), (paused.Action, paused.Actor, paused.TargetType));
        Assert.Equal("""{"free_bytes":5368709120,"mark_bytes":21474836480,"queued_jobs":1,"total_bytes":107374182400,"volume":"clickhouse:default"}""", paused.DetailsJson);

        await JobOperations.ResumeAsync(db, temp.Store.Audit, details, Ct);
        Assert.Null((await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == waiting.Id, Ct)).Message);
        Assert.Equal(AuditActions.IngestResumed, (await db.Audit.AsNoTracking().OrderBy(a => a.Seq).LastAsync(Ct)).Action);
        Assert.True((await AuditVerifier.VerifyAsync(temp.Path, Ct)).Ok);
    }

    private static async Task<Job> QueueAsync(TempDb temp, MetadataDb db, long caseId, string content, DateTime createdAt)
    {
        var file = (await FileOperations.AddAsync(db, temp.Store.Audit, TempDb.Alice, TempDb.NewFile(caseId, content, content + ".log"), Ct)).File;
        return await JobOperations.EnqueueAsync(db, file.Id, createdAt, Ct);
    }

    private static async Task<List<string>> Actions(MetadataDb db) => await db.Audit.AsNoTracking().OrderBy(a => a.Seq).Select(a => a.Action).ToListAsync(Ct);

    /// <summary>Writes the row, then fails: what a crash between the append and the commit looks like to the caller.</summary>
    private sealed class ThrowingWriter(IAuditWriter inner) : IAuditWriter
    {
        public async Task<AuditRow> AppendAsync(MetadataDb db, AuditEntry entry, CancellationToken cancellationToken = default)
        {
            await inner.AppendAsync(db, entry, cancellationToken);
            throw new InvalidOperationException("audit write failed");
        }
    }
}
