using Urlog.Core.Audit;
using Urlog.Core.Metadata;
using Urlog.Core.Pipeline;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Metadata.Tests;

/// <summary>
/// The case's span and the mark on its field statistics, both written by the commit of a parse: the span is
/// recomputed from the file records, the mark says a run is due, and a run is due only when the case has nothing
/// left to parse and its last commit is a quiet period old.
/// </summary>
public sealed class CaseStatsOperationsTests
{
    private static readonly DateTime T0 = new(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc);

    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(15);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Job> QueueAsync(TempDb temp, MetadataDb db, long caseId, string content, DateTime at)
    {
        var added = (await FileOperations.AddAsync(db, temp.Store.Audit, new AuditActor("alice"), TempDb.NewFile(caseId, content, content + ".log"), Ct)).File;
        return await JobOperations.EnqueueAsync(db, added.Id, at, Ct);
    }

    private static async Task CommitAsync(TempDb temp, MetadataDb db, DateTime at, JobOutcome outcome, long rows = 10)
    {
        var claimed = await JobOperations.ClaimAsync(db, "w:1", at, Ct);
        Assert.NotNull(claimed);
        await JobOperations.CompleteAsync(db, temp.Store.Audit, claimed.Job.Id, "w:1", new JobCounts(1, 1, rows, 0, 100), outcome, at, Ct);
    }

    private static JobOutcome Outcome(DateTime? min, DateTime? max, long undated = 0, long future = 0) =>
        new(InputEnd.Complete(100, 100), min, max, false, undated, future);

    [Fact]
    public async Task TheSpanIsTheLeastAndGreatestTimeOfTheFilesAndFollowsAReparse()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var @case = await TempDb.AddCaseAsync(db, await TempDb.AddUserAsync(db));
        Assert.Equal(new CaseSpan(null, null, 0, 0), await CaseStatsOperations.SpanAsync(db, @case.Id, Ct));

        var first = await QueueAsync(temp, db, @case.Id, "a", T0);
        await CommitAsync(temp, db, T0, Outcome(T0.AddDays(-9), T0.AddDays(-8), undated: 3));
        await QueueAsync(temp, db, @case.Id, "b", T0);
        await CommitAsync(temp, db, T0, Outcome(T0.AddDays(-5), T0.AddDays(-1), undated: 1, future: 4));
        // A file without one usable time: it adds to the counts and leaves the span alone.
        await QueueAsync(temp, db, @case.Id, "c", T0);
        await CommitAsync(temp, db, T0, Outcome(null, null, undated: 7));

        Assert.Equal(new CaseSpan(T0.AddDays(-9), T0.AddDays(-1), 11, 4), await CaseStatsOperations.SpanAsync(db, @case.Id, Ct));

        // The first file parsed again with a better year: its times move, and the span narrows with them.
        await JobOperations.EnqueueAsync(db, first.FileId, T0, Ct);
        await CommitAsync(temp, db, T0, Outcome(T0.AddDays(-6), T0.AddDays(-6)));
        Assert.Equal(new CaseSpan(T0.AddDays(-6), T0.AddDays(-1), 8, 4), await CaseStatsOperations.SpanAsync(db, @case.Id, Ct));

        Assert.Equal(new CaseSpan(T0.AddDays(-6), T0.AddDays(-1), 8, 4), await CaseStatsOperations.ReadSpanAsync(temp.Store.Path, @case.Id, Ct));
        Assert.Null(await CaseStatsOperations.ReadSpanAsync(temp.Store.Path, @case.Id + 100, Ct));
        Assert.Null(await CaseStatsOperations.SpanAsync(db, @case.Id + 100, Ct));
    }

    [Fact]
    public async Task ACaseIsDueOnceItsQueueIsEmptyAndItsLastCommitIsAQuietPeriodOld()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var @case = await TempDb.AddCaseAsync(db, await TempDb.AddUserAsync(db));
        Assert.Null(await CaseStatsOperations.NextDueAsync(db, T0, Quiet, Ct));

        await QueueAsync(temp, db, @case.Id, "a", T0);
        await QueueAsync(temp, db, @case.Id, "b", T0);
        await CommitAsync(temp, db, T0, Outcome(T0, T0), rows: 10);
        // Marked, and long enough ago, but the second file is still queued: its commit would start another run.
        Assert.NotNull((await CaseStatsOperations.GetAsync(db, @case.Id, Ct))!.DirtySince);
        Assert.Null(await CaseStatsOperations.NextDueAsync(db, T0.AddHours(1), Quiet, Ct));

        var claimed = await JobOperations.ClaimAsync(db, "w:1", T0.AddSeconds(1), Ct);
        // While it is parsed, too.
        Assert.Null(await CaseStatsOperations.NextDueAsync(db, T0.AddHours(1), Quiet, Ct));
        await JobOperations.CompleteAsync(db, temp.Store.Audit, claimed!.Job.Id, "w:1", new JobCounts(1, 1, 32, 0, 100), Outcome(T0, T0), T0.AddSeconds(2), Ct);

        // The quiet period counts from the last commit.
        Assert.Null(await CaseStatsOperations.NextDueAsync(db, T0.AddSeconds(16), Quiet, Ct));
        var due = await CaseStatsOperations.NextDueAsync(db, T0.AddSeconds(17), Quiet, Ct);
        Assert.Equal(new StatsClaim(@case.Id, T0.AddSeconds(2), null, 42), due);
        // A one-shot runner does not wait.
        Assert.NotNull(await CaseStatsOperations.NextDueAsync(db, T0.AddSeconds(2), TimeSpan.Zero, Ct));
    }

    [Fact]
    public async Task AClosedCaseIsDueWithJobsHeldAndAHiddenOrBusyCaseIsNot()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var owner = await TempDb.AddUserAsync(db);
        var closed = await TempDb.AddCaseAsync(db, owner, "closed with a held job");
        await QueueAsync(temp, db, closed.Id, "a", T0);
        await CommitAsync(temp, db, T0, Outcome(T0, T0));
        await QueueAsync(temp, db, closed.Id, "b", T0);
        await LifecycleOperations.CloseAsync(db, temp.Store.Audit, TempDb.Alice, closed.Id, temp.Store.Clock, Ct);
        // The held job may wait for months; the rows that are visible get their statistics now.
        Assert.Equal(closed.Id, (await CaseStatsOperations.NextDueAsync(db, T0.AddHours(1), Quiet, Ct))!.CaseId);

        await db.CaseStats.Where(s => s.CaseId == closed.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.DirtySince, (DateTime?)null), Ct);
        var deleted = await TempDb.AddCaseAsync(db, owner, "soft-deleted");
        await QueueAsync(temp, db, deleted.Id, "c", T0);
        await CommitAsync(temp, db, T0, Outcome(T0, T0));
        Assert.Equal(deleted.Id, (await CaseStatsOperations.NextDueAsync(db, T0.AddHours(1), Quiet, Ct))!.CaseId);
        await LifecycleOperations.SoftDeleteAsync(db, temp.Store.Audit, TempDb.Alice, deleted.Id, "soft-deleted", TimeSpan.FromDays(7), temp.Store.Clock, Ct);
        Assert.Null(await CaseStatsOperations.NextDueAsync(db, T0.AddHours(1), Quiet, Ct));
    }

    [Fact]
    public async Task ARunClearsTheMarkOnlyIfNothingWasCommittedSinceItBegan()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var @case = await TempDb.AddCaseAsync(db, await TempDb.AddUserAsync(db));
        await QueueAsync(temp, db, @case.Id, "a", T0);
        await CommitAsync(temp, db, T0, Outcome(T0, T0));
        var claim = (await CaseStatsOperations.NextDueAsync(db, T0, TimeSpan.Zero, Ct))!;

        // A second file is committed while the run reads the case.
        await QueueAsync(temp, db, @case.Id, "b", T0.AddSeconds(5));
        await CommitAsync(temp, db, T0.AddSeconds(5), Outcome(T0, T0));
        var computed = T0.AddSeconds(6);
        await CaseStatsOperations.RecordAsync(db, claim, CaseStatsState.Ok, null, computed, 10, TimeSpan.FromSeconds(1), temp.Store.Clock, Ct);

        var state = (await CaseStatsOperations.GetAsync(db, @case.Id, Ct))!;
        Assert.Equal((CaseStatsState.Ok, computed, 10L, 1000L), (state.State, state.ComputedAt, state.Events, state.DurationMs));
        Assert.Equal(T0.AddSeconds(5), state.DirtySince);

        // The next run starts from the new mark and the set on record, and clears it.
        var next = (await CaseStatsOperations.NextDueAsync(db, T0.AddSeconds(5), TimeSpan.Zero, Ct))!;
        Assert.Equal((T0.AddSeconds(5), computed, 20L), (next.DirtySince, next.LastComputedAt, next.Rows));
        await CaseStatsOperations.RecordAsync(db, next, CaseStatsState.TimedOut, "too slow", next.LastComputedAt, 0, TimeSpan.FromSeconds(120), temp.Store.Clock, Ct);
        state = (await CaseStatsOperations.GetAsync(db, @case.Id, Ct))!;
        Assert.Equal((CaseStatsState.TimedOut, "too slow", computed, 10L), (state.State, state.Message, state.ComputedAt, state.Events));
        Assert.Null(state.DirtySince);
        Assert.Null(await CaseStatsOperations.NextDueAsync(db, T0.AddDays(1), TimeSpan.Zero, Ct));
    }
}
