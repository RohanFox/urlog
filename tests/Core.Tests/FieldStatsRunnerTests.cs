using System.Text;
using Urlog.Core.Ingest;
using Urlog.Core.Jobs;
using Urlog.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Core.Tests;

/// <summary>
/// When the runner computes a case's field statistics and what it records: once behind the case's parses however
/// many files were committed, not at all for a case over the row limit, and with the reason when a run does not
/// write a set. The event store is the fake; what the statements return is tested against a real one.
/// </summary>
public sealed class FieldStatsRunnerTests
{
    private static readonly StatsField[] Fields = [new("program", StatsFieldKind.Column, "program", "program", "notEmpty(program)", true)];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JobRunnerOptions WithStats(long maxCaseRows = 1_000_000) =>
        RunnerHarness.Options() with { Stats = new FieldStatsOptions { Fields = Fields, MaxCaseRows = maxCaseRows } };

    [Fact]
    public void TheDefaultsCoverABillionRowCaseWithTwiceItsRunInTheTimeLimit()
    {
        var defaults = new FieldStatsOptions();

        // A 120 GB syslog case is about a billion rows; the default must not skip it.
        Assert.True(defaults.MaxCaseRows >= 2_000_000_000);

        // The run of the largest case the defaults read, at the measured rate, fits twice into the time limit.
        var largestRun = TimeSpan.FromSeconds(defaults.MaxCaseRows / 1e6 * FieldStatsOptions.MeasuredSecondsPerMillionRows);
        Assert.True(largestRun * 2 <= defaults.TimeLimit, $"{largestRun.TotalSeconds:0} s twice is over {defaults.TimeLimit.TotalSeconds:0} s");
    }

    private static byte[] Lines(int count, int seed)
    {
        var at = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds();
        var text = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            text.Append(at + i).Append(" line ").Append(seed).Append('-').Append(i).Append('\n');
        }

        return Encoding.UTF8.GetBytes(text.ToString());
    }

    private static async Task<CaseStats?> StateAsync(RunnerHarness h, long caseId)
    {
        await using var db = h.Open();
        return await CaseStatsOperations.GetAsync(db, caseId, Ct);
    }

    [Fact]
    public async Task TenFilesOfOneCaseEndInOneRunAfterTheLastCommit()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var a = await h.AddCaseAsync("ten files");
        var b = await h.AddCaseAsync("one file");
        for (var i = 0; i < 10; i++)
        {
            await h.AddFileAsync(a.Id, Lines(40, i), $"f{i}.log");
        }

        await h.AddFileAsync(b.Id, Lines(40, 99), "g.log");
        h.Events.OnFieldStats = id => id == (ulong)a.Id ? 400UL : 40UL;

        await h.Runner(WithStats()).RunAsync(Ct);

        Assert.Equal([(ulong)a.Id, (ulong)b.Id], h.Events.StatsRuns.Select(r => r.Case).Order());
        var calls = h.Events.Calls.ToList();
        Assert.Equal(11, calls.Count(c => c.StartsWith("commit ", StringComparison.Ordinal)));
        // Every parse is committed before the first statistics run: the queue goes first.
        Assert.True(calls.FindLastIndex(c => c.StartsWith("commit ", StringComparison.Ordinal)) < calls.FindIndex(c => c.StartsWith("refresh-field-stats", StringComparison.Ordinal)), string.Join(" | ", calls));

        var state = await StateAsync(h, a.Id);
        Assert.NotNull(state);
        Assert.Equal((CaseStatsState.Ok, 400L, null), (state.State, state.Events, state.Message));
        Assert.Null(state.DirtySince);
        var run = h.Events.StatsRuns.Single(r => r.Case == (ulong)a.Id);
        Assert.Equal(run.ComputedAt.Ticks / TimeSpan.TicksPerMillisecond, state.ComputedAt!.Value.Ticks / TimeSpan.TicksPerMillisecond);
    }

    [Fact]
    public async Task ARunnerWithoutFieldsMarksTheCaseAndComputesNothing()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var a = await h.AddCaseAsync("no statistics here");
        await h.AddFileAsync(a.Id, Lines(20, 1), "a.log");

        await h.Runner().RunAsync(Ct);

        Assert.Empty(h.Events.StatsRuns);
        var state = await StateAsync(h, a.Id);
        Assert.NotNull(state?.DirtySince);
        Assert.Equal(CaseStatsState.None, state.State);
    }

    [Fact]
    public async Task ACaseOverTheRowLimitIsNotReadItsOldSetGoesAndTheCaseSaysWhy()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var a = await h.AddCaseAsync("too large");
        await h.AddFileAsync(a.Id, Lines(60, 1), "a.log");
        h.Events.SeedFieldStats((ulong)a.Id);

        await h.Runner(WithStats(maxCaseRows: 59)).RunAsync(Ct);

        Assert.Empty(h.Events.StatsRuns);
        Assert.False(h.Events.HasFieldStats((ulong)a.Id));
        var state = await StateAsync(h, a.Id);
        Assert.Equal(CaseStatsState.Skipped, state!.State);
        Assert.Contains("60", state.Message, StringComparison.Ordinal);
        Assert.Contains("59", state.Message, StringComparison.Ordinal);
        Assert.Null(state.ComputedAt);
        Assert.Null(state.DirtySince);
    }

    [Fact]
    public async Task ARunPastItsTimeLimitWritesNothingAndIsRecordedAsTimedOut()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var a = await h.AddCaseAsync("slow");
        await h.AddFileAsync(a.Id, Lines(20, 1), "a.log");
        h.Events.OnFieldStats = _ => throw new FieldStatsTimeoutException("the statistics of case 1 passed their time limit of 120 s in 'presence'");

        await h.Runner(WithStats()).RunAsync(Ct);

        var state = await StateAsync(h, a.Id);
        Assert.Equal(CaseStatsState.TimedOut, state!.State);
        Assert.Contains("time limit of 120 s", state.Message, StringComparison.Ordinal);
        Assert.Null(state.ComputedAt);
        // Not tried again until a parse is committed: the same run would end the same way.
        Assert.Null(state.DirtySince);
        Assert.False(h.Events.HasFieldStats((ulong)a.Id));
    }

    [Fact]
    public async Task AFailedRunIsRecordedWithItsReasonAndTheRunnerGoesOn()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var a = await h.AddCaseAsync("fails");
        var b = await h.AddCaseAsync("works");
        await h.AddFileAsync(a.Id, Lines(20, 1), "a.log");
        await h.AddFileAsync(b.Id, Lines(20, 2), "b.log");
        h.Events.OnFieldStats = id => id == (ulong)a.Id ? throw new InvalidOperationException("the store said no") : 20UL;

        await h.Runner(WithStats()).RunAsync(Ct);

        var failed = await StateAsync(h, a.Id);
        Assert.Equal((CaseStatsState.Failed, "the store said no"), (failed!.State, failed.Message));
        Assert.Equal(CaseStatsState.Ok, (await StateAsync(h, b.Id))!.State);
    }

    [Fact]
    public async Task ANewSetIsStampedLaterThanTheOneItReplacesWhateverTheClockSays()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var a = await h.AddCaseAsync("twice");
        await h.AddFileAsync(a.Id, Lines(20, 1), "a.log");
        await h.Runner(WithStats()).RunAsync(Ct);

        // The set on record is from a clock that ran ahead.
        var ahead = DateTime.UtcNow.AddHours(3);
        ahead = new DateTime(ahead.Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        await using (var db = h.Open())
        {
            await db.CaseStats.Where(s => s.CaseId == a.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ComputedAt, ahead), Ct);
        }

        await h.AddFileAsync(a.Id, Lines(20, 2), "b.log");
        await h.Runner(WithStats()).RunAsync(Ct);

        Assert.Equal(2, h.Events.StatsRuns.Count);
        Assert.Equal(ahead.AddMilliseconds(1), h.Events.StatsRuns[1].ComputedAt);
        Assert.Equal(ahead.AddMilliseconds(1), (await StateAsync(h, a.Id))!.ComputedAt);
    }

    [Fact]
    public async Task TimesBelowTheFloorAndAfterTheArrivalStayOutOfTheSpanAndAreCounted()
    {
        var now = DateTimeOffset.UtcNow;
        var first = now.AddDays(-20).ToUnixTimeMilliseconds();
        var last = now.AddDays(-19).ToUnixTimeMilliseconds();
        var future = now.AddDays(3).ToUnixTimeMilliseconds();
        // An epoch-sized time, a rejected line (the column default), two real times, one after the file arrived.
        var content = Encoding.UTF8.GetBytes($"5 at the epoch\nBAD no number\n{first} first\n{last} last\n{future} from a clock that ran ahead\n");

        await using var h = await RunnerHarness.CreateAsync();
        var a = await h.AddCaseAsync("span");
        var (file, job, _) = await h.AddFileAsync(a.Id, content, "a.log");
        await h.Runner().RunAsync(Ct);

        Assert.Equal(JobState.Done, (await h.JobAsync(job.Id)).State);
        var record = await h.FileAsync(file.Id);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(first).UtcDateTime, record.TsMin);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(last).UtcDateTime, record.TsMax);
        Assert.Equal((2L, 1L), (record.UndatedRows, record.FutureRows));

        await using var db = h.Open();
        var span = await CaseStatsOperations.SpanAsync(db, a.Id, Ct);
        Assert.Equal(new CaseSpan(record.TsMin, record.TsMax, 2, 1), span);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(last + 1), span!.DefaultRange!.Value.To);
    }

    [Fact]
    public async Task ACaseWhoseOnlyFileHasNoUsableTimeHasNoSpan()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var a = await h.AddCaseAsync("epoch only");
        var (file, _, _) = await h.AddFileAsync(a.Id, "BAD first line\n7 seven\n9 nine\n"u8.ToArray(), "a.log");
        await h.Runner().RunAsync(Ct);

        var record = await h.FileAsync(file.Id);
        Assert.Null(record.TsMin);
        Assert.Equal(3, record.UndatedRows);
        await using var db = h.Open();
        var span = await CaseStatsOperations.SpanAsync(db, a.Id, Ct);
        Assert.Equal(new CaseSpan(null, null, 3, 0), span);
        Assert.Null(span!.DefaultRange);
    }
}
