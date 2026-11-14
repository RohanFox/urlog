using System.Text.Json;
using Urlog.Core.Audit;
using Urlog.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Metadata.Tests;

/// <summary>
/// The metadata side of archive and re-import: who may ask and when, what the commit does to the jobs held
/// since the close, which jobs the runner may claim for an archived case, and how a re-import ends.
/// </summary>
public sealed class ArchiveOperationsTests
{
    private static readonly ArchiveCommit Commit = new(Path.Combine(Path.GetTempPath(), "archives", "1", "20261114T100000Z"), new string('a', 64), 1000, new string('b', 64), 200, 2, 12, []);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTime Now => DateTime.UtcNow;

    [Fact]
    public async Task AnArchiveIsAskedForByAnOwnerOfAClosedCaseWithNothingElseUnderWay()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var ownerId = await TempDb.AddUserAsync(db, "olga");
        var analystId = await TempDb.AddUserAsync(db, "ravi");
        var adminId = await AddAdminAsync(db, "ada");
        var @case = await TempDb.AddCaseAsync(db, ownerId);
        var owner = new AuditActor("olga", ownerId);
        var clock = temp.Store.Clock;

        var open = await Assert.ThrowsAsync<LifecycleRefusedException>(() => ArchiveOperations.RequestArchiveAsync(db, owner, @case.Id, clock, Ct));
        Assert.Contains("is active; only a closed case is archived", open.Message, StringComparison.Ordinal);
        await LifecycleOperations.CloseAsync(db, temp.Store.Audit, owner, @case.Id, clock, Ct);

        // An archive takes an owner of the case: a member who is not one and an administrator who is not one are refused.
        Assert.Equal(LifecycleRole.Owner, CaseLifecycle.RequiredRole(CaseLifecycle.Archive));
        Assert.Equal(LifecycleRole.Owner, CaseLifecycle.RequiredRole(CaseLifecycle.Reimport));
        await Assert.ThrowsAsync<LifecycleRoleException>(() => ArchiveOperations.RequestArchiveAsync(db, new AuditActor("ravi", analystId), @case.Id, clock, Ct));
        await Assert.ThrowsAsync<LifecycleRoleException>(() => ArchiveOperations.RequestArchiveAsync(db, new AuditActor("ada", adminId), @case.Id, clock, Ct));

        var before = await db.Audit.CountAsync(Ct);
        var task = await ArchiveOperations.RequestArchiveAsync(db, owner, @case.Id, clock, Ct);
        Assert.Equal((CaseTaskKind.Archive, CaseTaskState.Running, "olga", (long?)ownerId), (task.Kind, task.State, task.RequestedBy, task.RequestedById));
        // The request writes no row: the commit names who asked.
        Assert.Equal(before, await db.Audit.CountAsync(Ct));
        Assert.Equal(CaseState.Closed, await db.Cases.AsNoTracking().Select(c => c.State).SingleAsync(Ct));

        // While it is under way nothing else changes the case, and there is no second request.
        var second = await Assert.ThrowsAsync<LifecycleRefusedException>(() => ArchiveOperations.RequestArchiveAsync(db, owner, @case.Id, clock, Ct));
        Assert.Contains("unfinished work (archive, running, asked for by olga)", second.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<LifecycleRefusedException>(() => LifecycleOperations.ReopenAsync(db, temp.Store.Audit, owner, @case.Id, clock, Ct));
        await Assert.ThrowsAsync<LifecycleRefusedException>(() => LifecycleOperations.SoftDeleteAsync(db, temp.Store.Audit, owner, @case.Id, "incident 1", TimeSpan.FromDays(7), clock, Ct));
        // Nothing to re-import from: the case is not archived.
        await Assert.ThrowsAsync<LifecycleRefusedException>(() => ArchiveOperations.RequestReimportAsync(db, owner, @case.Id, clock, Ct));
    }

    [Fact]
    public async Task TheCommitCancelsTheHeldJobsWithARowEachAndAnArchiveThatIsGivenUpLeavesTheCaseClosed()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var owner = new AuditActor("olga", await TempDb.AddUserAsync(db, "olga"));
        var @case = await TempDb.AddCaseAsync(db, owner.UserId!.Value);
        var clock = temp.Store.Clock;
        var a = (await FileOperations.AddAsync(db, temp.Store.Audit, owner, TempDb.NewFile(@case.Id, "a\n", "a.log"), Ct)).File;
        var b = (await FileOperations.AddAsync(db, temp.Store.Audit, owner, TempDb.NewFile(@case.Id, "b\n", "b.log"), Ct)).File;
        var first = await JobOperations.EnqueueAsync(db, a.Id, Now, Ct);
        var second = await JobOperations.EnqueueAsync(db, b.Id, Now, Ct);
        await LifecycleOperations.CloseAsync(db, temp.Store.Audit, owner, @case.Id, clock, Ct);

        // Given up before any commit: the case is as it was, the jobs still held, and the row says why.
        var abandoned = await ArchiveOperations.RequestArchiveAsync(db, owner, @case.Id, clock, Ct);
        Assert.True(await ArchiveOperations.FailArchiveAsync(db, temp.Store.Audit, abandoned.Id, "no room on the archive volume", clock, Ct));
        Assert.False(await ArchiveOperations.FailArchiveAsync(db, temp.Store.Audit, abandoned.Id, "again", clock, Ct));
        var failed = await db.Audit.AsNoTracking().OrderByDescending(x => x.Seq).FirstAsync(Ct);
        Assert.Equal((AuditActions.CaseArchiveFailed, AuditActions.SystemActor), (failed.Action, failed.Actor));
        Assert.Contains("\"reason\":\"no room on the archive volume\"", failed.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"case_state\":\"closed\"", failed.DetailsJson, StringComparison.Ordinal);
        Assert.Equal(2, await db.Jobs.CountAsync(j => j.State == JobState.Queued, Ct));
        await Assert.ThrowsAsync<LifecycleRefusedException>(() => ArchiveOperations.CommitAsync(db, temp.Store.Audit, abandoned.Id, Commit, clock, Ct));

        // A new request, committed.
        var task = await ArchiveOperations.RequestArchiveAsync(db, owner, @case.Id, clock, Ct);
        await ArchiveOperations.CommitAsync(db, temp.Store.Audit, task.Id, Commit, clock, Ct);
        var archived = await db.Cases.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(CaseState.Archived, archived.State);
        Assert.NotNull(archived.ArchivedAt);
        Assert.All(await db.Jobs.AsNoTracking().ToListAsync(Ct), job => Assert.Equal((JobState.Cancelled, "cancelled: the case was archived; a re-import parses every file again"), (job.State, job.Message)));
        var rows = await db.Audit.AsNoTracking().OrderByDescending(x => x.Seq).Take(3).ToListAsync(Ct);
        Assert.Equal([AuditActions.CaseArchive, AuditActions.JobCancelled, AuditActions.JobCancelled], rows.Select(r => r.Action));
        Assert.Equal([b.Id, a.Id], rows.Skip(1).Select(r => r.TargetId!.Value));
        Assert.Contains($"\"job_id\":{second.Id}", rows[1].DetailsJson, StringComparison.Ordinal);
        Assert.Contains($"\"job_id\":{first.Id}", rows[2].DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"jobs_cancelled\":2", rows[0].DetailsJson, StringComparison.Ordinal);
        var row = await db.CaseTasks.AsNoTracking().SingleAsync(t => t.Id == task.Id, Ct);
        Assert.Equal((CaseTaskState.Committed, ArchiveOperations.StepStore, Commit.Directory, Commit.TarSha256, Commit.ManifestSha256), (row.State, row.Step, row.ArchiveDirectory, row.TarSha256, row.ManifestSha256));

        // Committed twice is refused; completed, it is done and verified as of now.
        await Assert.ThrowsAsync<LifecycleRefusedException>(() => ArchiveOperations.CommitAsync(db, temp.Store.Audit, task.Id, Commit, clock, Ct));
        await ArchiveOperations.CompleteArchiveAsync(db, temp.Store.Audit, task.Id, 3, 2, clock, Ct);
        var done = await db.CaseTasks.AsNoTracking().SingleAsync(t => t.Id == task.Id, Ct);
        Assert.Equal(CaseTaskState.Done, done.State);
        Assert.NotNull(done.VerifiedAt);
        Assert.Equal(task.Id, (await ArchiveOperations.LatestArchiveAsync(db, @case.Id, Ct))!.Id);
        Assert.True((await AuditVerifier.VerifyAsync(temp.Path, Ct)).Ok);
    }

    [Fact]
    public async Task AnArchivedCaseTakesNoJobExceptTheOnesOfItsReimportAndClosesWhenTheLastHasEnded()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var owner = new AuditActor("olga", await TempDb.AddUserAsync(db, "olga"));
        var @case = await TempDb.AddCaseAsync(db, owner.UserId!.Value);
        var clock = temp.Store.Clock;
        var whole = new JobOutcome(Urlog.Core.Pipeline.InputEnd.Complete(2, 2));
        var files = new List<EvidenceFile>();
        foreach (var name in new[] { "a.log", "b.log", "c.log" })
        {
            var file = (await FileOperations.AddAsync(db, temp.Store.Audit, owner, TempDb.NewFile(@case.Id, name + "\n", name), Ct)).File;
            files.Add(file);
            if (name != "c.log")
            {
                // Two files were parsed; the third never was.
                var job = await JobOperations.EnqueueAsync(db, file.Id, Now, Ct);
                await JobOperations.ClaimAsync(db, "w:1", Now, Ct);
                await JobOperations.CompleteAsync(db, temp.Store.Audit, job.Id, "w:1", new JobCounts(1, 1, 10, 0, 6), whole, Now, Ct);
            }
        }

        await LifecycleOperations.CloseAsync(db, temp.Store.Audit, owner, @case.Id, clock, Ct);
        var archive = await ArchiveOperations.RequestArchiveAsync(db, owner, @case.Id, clock, Ct);
        await ArchiveOperations.CommitAsync(db, temp.Store.Audit, archive.Id, Commit, clock, Ct);
        // Not complete yet: there is nothing to re-import from, and the task in the way says so.
        await Assert.ThrowsAsync<LifecycleRefusedException>(() => ArchiveOperations.RequestReimportAsync(db, owner, @case.Id, clock, Ct));
        await ArchiveOperations.CompleteArchiveAsync(db, temp.Store.Audit, archive.Id, 1, 3, clock, Ct);

        var task = await ArchiveOperations.RequestReimportAsync(db, owner, @case.Id, clock, Ct);
        Assert.Equal((Commit.Directory, Commit.TarSha256, Commit.ManifestSha256), (task.ArchiveDirectory, task.TarSha256, task.ManifestSha256));
        // A job that someone put in by hand while the files are only being verified is not claimed.
        db.Jobs.Add(new Job { CaseId = @case.Id, FileId = files[2].Id, Kind = JobKind.Parse, CreatedAt = Now });
        await db.SaveChangesAsync(Ct);
        Assert.Null(await JobOperations.ClaimAsync(db, "w:1", Now, Ct));
        await db.Jobs.Where(j => j.State == JobState.Queued).ExecuteDeleteAsync(Ct);
        db.ChangeTracker.Clear();

        // Queued: one re-parse for each file that had a parse, none for the one that had not.
        Assert.Equal(2, await ArchiveOperations.QueueReimportAsync(db, task.Id, new Dictionary<long, string> { [files[1].Id] = $"restored/{files[1].Id}" }, clock, Ct));
        Assert.Equal($"restored/{files[1].Id}", (await db.Files.AsNoTracking().SingleAsync(f => f.Id == files[1].Id, Ct)).StoredPath);
        Assert.Equal(CaseState.Archived, await db.Cases.AsNoTracking().Select(c => c.State).SingleAsync(Ct));
        Assert.Null(await ArchiveOperations.CompleteReimportAsync(db, temp.Store.Audit, task.Id, null, clock, Ct));

        // Now the runner may claim for the archived case, one job at a time as for any case.
        var first = await JobOperations.ClaimAsync(db, "w:1", Now, Ct);
        Assert.Equal((files[0].Id, JobKind.Reparse, 2), (first!.File.Id, first.Job.Kind, first.Job.Generation));
        Assert.Null(await JobOperations.ClaimAsync(db, "w:2", Now, Ct));
        await JobOperations.CompleteAsync(db, temp.Store.Audit, first.Job.Id, "w:1", new JobCounts(1, 1, 12, 0, 6), whole, Now, Ct);
        Assert.Null(await ArchiveOperations.CompleteReimportAsync(db, temp.Store.Audit, task.Id, null, clock, Ct));
        var second = await JobOperations.ClaimAsync(db, "w:1", Now, Ct);
        await JobOperations.FailAsync(db, second!.Job.Id, "w:1", "the parser gave up", Now, Ct);

        // The last job has ended. A failed parse does not hold the case: it is closed, and the row lists what failed
        // and where the row count is another than the manifest's.
        var result = await ArchiveOperations.CompleteReimportAsync(db, temp.Store.Audit, task.Id, new Dictionary<long, long> { [files[0].Id] = 10, [files[1].Id] = 10 }, clock, Ct);
        Assert.NotNull(result);
        Assert.Equal((3, 1), (result.Files, result.Parsed));
        Assert.Equal([files[1].Id], result.FailedFiles);
        var closed = await db.Cases.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(CaseState.Closed, closed.State);
        var row = await db.Audit.AsNoTracking().OrderByDescending(x => x.Seq).FirstAsync(Ct);
        Assert.Equal((AuditActions.CaseReimport, "olga"), (row.Action, row.Actor));
        using var details = JsonDocument.Parse(row.DetailsJson);
        Assert.Equal(files[1].Id, details.RootElement.GetProperty("failed_files")[0].GetInt64());
        var difference = details.RootElement.GetProperty("row_differences")[0];
        Assert.Equal((files[0].Id, 10L, 12L), (difference.GetProperty("file_id").GetInt64(), difference.GetProperty("rows_before").GetInt64(), difference.GetProperty("rows_after").GetInt64()));
        Assert.Equal(1, details.RootElement.GetProperty("row_differences").GetArrayLength());

        // Closed again: nothing is claimed for it, and it is never active by this road.
        db.Jobs.Add(new Job { CaseId = @case.Id, FileId = files[2].Id, Kind = JobKind.Parse, CreatedAt = Now });
        await db.SaveChangesAsync(Ct);
        Assert.Null(await JobOperations.ClaimAsync(db, "w:1", Now, Ct));
        Assert.True((await AuditVerifier.VerifyAsync(temp.Path, Ct)).Ok);
    }

    [Fact]
    public async Task ARefusedReimportLeavesTheCaseArchivedAndNamesTheFile()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var owner = new AuditActor("olga", await TempDb.AddUserAsync(db, "olga"));
        var @case = await TempDb.AddCaseAsync(db, owner.UserId!.Value);
        var clock = temp.Store.Clock;
        var file = (await FileOperations.AddAsync(db, temp.Store.Audit, owner, TempDb.NewFile(@case.Id, "a\n", "a.log"), Ct)).File;
        await LifecycleOperations.CloseAsync(db, temp.Store.Audit, owner, @case.Id, clock, Ct);
        var archive = await ArchiveOperations.RequestArchiveAsync(db, owner, @case.Id, clock, Ct);
        await ArchiveOperations.CommitAsync(db, temp.Store.Audit, archive.Id, Commit, clock, Ct);
        await ArchiveOperations.CompleteArchiveAsync(db, temp.Store.Audit, archive.Id, 0, 1, clock, Ct);
        var task = await ArchiveOperations.RequestReimportAsync(db, owner, @case.Id, clock, Ct);

        await ArchiveOperations.RefuseReimportAsync(db, temp.Store.Audit, task.Id, new ReimportRefusal("'files/1' in the tar is not what the manifest says", file.Id, "expected-hash", "actual-hash"), clock, Ct);
        Assert.Equal(CaseState.Archived, await db.Cases.AsNoTracking().Select(c => c.State).SingleAsync(Ct));
        Assert.Equal(CaseTaskState.Failed, (await db.CaseTasks.AsNoTracking().SingleAsync(t => t.Id == task.Id, Ct)).State);
        var rows = await db.Audit.AsNoTracking().OrderByDescending(x => x.Seq).Take(2).ToListAsync(Ct);
        Assert.Equal([AuditActions.CaseReimportRefused, AuditActions.IntegrityMismatch], rows.Select(r => r.Action));
        Assert.Equal((AuditTargets.File, (long?)file.Id), (rows[1].TargetType, rows[1].TargetId));
        Assert.Contains("\"check\":\"reimport\"", rows[1].DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"expected\":\"expected-hash\"", rows[1].DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"actual\":\"actual-hash\"", rows[0].DetailsJson, StringComparison.Ordinal);
        Assert.Contains($"\"file_id\":{file.Id}", rows[0].DetailsJson, StringComparison.Ordinal);
        // The record itself is as it was: what is wrong is the archive.
        Assert.Equal(FileState.Received, (await db.Files.AsNoTracking().SingleAsync(Ct)).State);
        // The archive stands; another re-import may be asked for.
        Assert.Equal(CaseTaskState.Running, (await ArchiveOperations.RequestReimportAsync(db, owner, @case.Id, clock, Ct)).State);
    }

    private static async Task<long> AddAdminAsync(MetadataDb db, string name)
    {
        var user = new User { Name = name, PasswordHash = "argon2id$placeholder", CreatedAt = DateTime.UtcNow, Role = UserRole.Admin };
        db.Users.Add(user);
        await db.SaveChangesAsync(Ct);
        return user.Id;
    }
}
