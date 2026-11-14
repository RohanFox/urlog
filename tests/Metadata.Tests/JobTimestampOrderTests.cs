using System.Globalization;
using Urlog.Core.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Metadata.Tests;

/// <summary>
/// The times on a job row are taken after the operation holds the write lock, so their order is the order of the
/// transactions. Each test holds the lock from a connection of its own, starts the operation behind it, moves the
/// clock, and lets go: the operation must stamp the time at which it got the lock, never the time at which it was called.
/// </summary>
public sealed class JobTimestampOrderTests
{
    private static readonly DateTime Called = new(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Released = Called.AddMinutes(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AClaimThatWaitsBehindACommitDoesNotCarryATimeEarlierThanThatCommit()
    {
        var clock = new FakeClock(Called.AddHours(-1));
        await using var temp = await TempDb.CreateAsync(clock);
        long caseId, first, second;
        await using (var db = temp.Open())
        {
            caseId = (await TempDb.AddCaseAsync(db, await TempDb.AddUserAsync(db))).Id;
            var a = (await FileOperations.AddAsync(db, temp.Store.Audit, TempDb.Alice, TempDb.NewFile(caseId, "a\n", "a.log"), Ct)).File;
            var b = (await FileOperations.AddAsync(db, temp.Store.Audit, TempDb.Alice, TempDb.NewFile(caseId, "b\n", "b.log"), Ct)).File;
            first = (await JobOperations.EnqueueAsync(db, a.Id, clock, Ct)).Id;
            second = (await JobOperations.EnqueueAsync(db, b.Id, clock, Ct)).Id;
            // The first file of the case is being parsed: the second waits for it.
            Assert.Equal(first, (await JobOperations.ClaimAsync(db, "w:1", clock, Ct))!.Job.Id);
            Assert.Null(await JobOperations.ClaimAsync(db, "w:2", clock, Ct));
        }

        // The commit of the first job takes the write lock ...
        await using var commit = new SqliteConnection(MetadataStore.ConnectionString(temp.Path));
        await commit.OpenAsync(Ct);
        await ExecuteAsync(commit, "BEGIN IMMEDIATE");

        // ... and an idle slot asks for work while it is held.
        clock.UtcNow = Called;
        await using var claimer = temp.Open();
        var claim = Task.Run(() => JobOperations.ClaimAsync(claimer, "w:2", clock, Ct), Ct);
        await Task.Delay(400, Ct);
        Assert.False(claim.IsCompleted, "the claim got past a held write lock");

        // The commit is written ten minutes later and the lock is released.
        clock.UtcNow = Released;
        var finished = Released.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        await ExecuteAsync(commit, $"UPDATE jobs SET state = 'done', finished_at = '{finished}' WHERE id = {first}");
        await ExecuteAsync(commit, "COMMIT");

        var claimed = await claim;
        Assert.NotNull(claimed);
        Assert.Equal(second, claimed.Job.Id);
        await using var check = temp.Open();
        var done = await check.Jobs.AsNoTracking().SingleAsync(j => j.Id == first, Ct);
        var started = await check.Jobs.AsNoTracking().SingleAsync(j => j.Id == second, Ct);
        Assert.Equal(Released, done.FinishedAt);
        Assert.True(started.StartedAt >= done.FinishedAt, $"the second job of the case started at {started.StartedAt:O}, before the first finished at {done.FinishedAt:O}");
    }

    [Theory]
    [InlineData("enqueue")]
    [InlineData("complete")]
    [InlineData("complete-expand")]
    [InlineData("fail")]
    [InlineData("quarantine")]
    [InlineData("requeue-at-cap")]
    [InlineData("cancel")]
    [InlineData("expand-rerun")]
    public async Task EveryTimeOnAJobRowIsTakenUnderTheWriteLock(string operation)
    {
        var clock = new FakeClock(Called.AddHours(-1));
        await using var temp = await TempDb.CreateAsync(clock);
        long fileId, jobId = 0;
        await using (var db = temp.Open())
        {
            var caseId = (await TempDb.AddCaseAsync(db, await TempDb.AddUserAsync(db))).Id;
            var file = TempDb.NewFile(caseId, "a\n", "a.log");
            if (operation is "complete-expand" or "expand-rerun")
            {
                // An archive: its job is an expansion.
                file.DetectedKind = Urlog.Core.Detection.FileKind.Container;
                // Only an archive whose expansion failed is run again.
                file.State = operation == "expand-rerun" ? FileState.Failed : file.State;
            }

            fileId = (await FileOperations.AddAsync(db, temp.Store.Audit, TempDb.Alice, file, Ct)).File.Id;
            if (operation is not ("enqueue" or "expand-rerun"))
            {
                jobId = (await JobOperations.EnqueueAsync(db, fileId, clock, Ct)).Id;
                if (operation != "cancel")
                {
                    await JobOperations.ClaimAsync(db, "w:1", clock, Ct);
                }
            }
        }

        await using var holder = new SqliteConnection(MetadataStore.ConnectionString(temp.Path));
        await holder.OpenAsync(Ct);
        await ExecuteAsync(holder, "BEGIN IMMEDIATE");
        clock.UtcNow = Called;
        await using var writer = temp.Open();
        var audit = temp.Store.Audit;
        var whole = new JobOutcome(Urlog.Core.Pipeline.InputEnd.Complete(2, 2));
        var write = Task.Run<long>(
            async () =>
            {
                switch (operation)
                {
                    case "enqueue":
                        return (await JobOperations.EnqueueAsync(writer, fileId, clock, Ct)).Id;
                    case "expand-rerun":
                        return (await JobOperations.RequestExpandRerunAsync(writer, audit, TempDb.Alice, fileId, "{}", new Dictionary<string, object?>(), clock, Ct)).Id;
                    case "complete":
                        await JobOperations.CompleteAsync(writer, audit, jobId, "w:1", new JobCounts(1, 1, 1, 0, 2), whole, clock, Ct);
                        break;
                    case "complete-expand":
                        await JobOperations.CompleteExpandAsync(writer, audit, jobId, "w:1", 0, "nothing in it", null, null, clock, Ct);
                        break;
                    case "fail":
                        await JobOperations.FailAsync(writer, jobId, "w:1", "failed for the test", clock, Ct);
                        break;
                    case "quarantine":
                        await JobOperations.QuarantineAsync(writer, audit, jobId, "w:1", new IntegrityMismatch(new string('0', 64), 1), clock, Ct);
                        break;
                    case "requeue-at-cap":
                        await JobOperations.RequeueAsync(writer, jobId, "stopped for the test", countAttempt: true, maxAttempts: 1, clock, Ct);
                        break;
                    default:
                        await JobOperations.CancelAsync(writer, jobId, clock, Ct);
                        break;
                }

                return jobId;
            },
            Ct);
        await Task.Delay(400, Ct);
        Assert.False(write.IsCompleted, "the write got past a held write lock");
        clock.UtcNow = Released;
        await ExecuteAsync(holder, "COMMIT");

        var id = await write;
        await using var check = temp.Open();
        var job = await check.Jobs.AsNoTracking().SingleAsync(j => j.Id == id, Ct);
        var stamped = operation is "enqueue" or "expand-rerun" ? job.CreatedAt : job.FinishedAt;
        Assert.Equal(Released, stamped);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }
}
