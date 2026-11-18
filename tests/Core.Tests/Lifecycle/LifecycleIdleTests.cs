using System.Diagnostics;
using Urlog.Core.Audit;
using Urlog.Core.Intake.Archives;
using Urlog.Core.Lifecycle;
using Urlog.Core.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Urlog.Core.Tests.Lifecycle;

/// <summary>
/// The lifecycle loop runs every few seconds for the life of the runner. With nothing to do it only reads: it
/// asks for no write lock, so it never stands in the way of a job's claim or commit, and it changes nothing.
/// </summary>
public sealed class LifecycleIdleTests
{
    private static CancellationToken Ct => RunnerHarness.Ct;

    [Fact]
    public Task ATickWithNothingToDoTakesNoWriteLockAndChangesNothing() => TwentyIdleTicksAsync(timed: false);

    /// <summary>
    /// The same ticks against the clock: none of them may wait for the lock either. A wall-clock bound fails on a
    /// loaded box, so this one runs from <c>tools/timing-tests.ps1</c> alone; the test above still fails when a
    /// tick tries to write, since the write is refused after the store's one-second busy timeout.
    /// </summary>
    [Fact(Explicit = true)]
    [Trait("Category", "Timing")]
    public Task TwentyIdleTicksBehindAHeldWriteLockTakeUnderASecond() => TwentyIdleTicksAsync(timed: true);

    private static async Task TwentyIdleTicksAsync(bool timed)
    {
        await using var h = await RunnerHarness.CreateAsync();
        // Cases in every state the loop looks at, none with work that is due: an open case with a file that
        // was verified, a case soft-deleted a moment ago, and a purge that is asked for and not confirmed.
        var open = await h.AddCaseAsync("open");
        var deleted = await h.AddCaseAsync("deleted");
        var asked = await h.AddCaseAsync("asked");
        var (file, job, _) = await h.AddFileAsync(open.Id, "line\n"u8.ToArray(), "a.log");
        await using (var db = h.Open())
        {
            var actor = new AuditActor("alice");
            await JobOperations.CancelAsync(db, job.Id, DateTime.UtcNow, Ct);
            await IntegrityOperations.RecordVerifiedAsync(db, h.Store.Audit, [file.Id], DateTime.UtcNow, Ct);
            await LifecycleOperations.SoftDeleteAsync(db, h.Store.Audit, actor, deleted.Id, "deleted", TimeSpan.FromDays(7), DateTime.UtcNow, Ct);
            await LifecycleOperations.SoftDeleteAsync(db, h.Store.Audit, actor, asked.Id, "asked", TimeSpan.FromDays(7), DateTime.UtcNow, Ct);
            db.CaseTasks.Add(new CaseTask { CaseId = asked.Id, Kind = CaseTaskKind.Purge, State = CaseTaskState.Requested, RequestedBy = "ada", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync(Ct);
        }

        // A store of its own with a one-second busy timeout: an attempt to write fails fast instead of waiting.
        var store = new MetadataStore(h.Store.Path, busyTimeoutSeconds: 1);
        var options = new LifecycleOptions();
        var executor = new LifecycleExecutor(store, h.Events, options, new LifecyclePaths(h.Root, h.CacheRoot, Path.Combine(h.Root, "backups")), NullLogger.Instance);
        var sweep = new IntegritySweep(store, new MemberStore(h.Root, h.CacheRoot), h.Root, options, NullLogger.Instance);

        await using var holder = new SqliteConnection(MetadataStore.ConnectionString(h.Store.Path));
        await holder.OpenAsync(Ct);
        var before = await ScalarAsync(holder, "PRAGMA data_version");
        var rows = await ScalarAsync(holder, "SELECT (SELECT count(*) FROM audit) + (SELECT count(*) FROM case_tasks)");
        await ExecuteAsync(holder, "BEGIN IMMEDIATE");

        // Twenty ticks of what the runner's loop does, while another connection holds the write lock throughout.
        var watch = Stopwatch.StartNew();
        for (var tick = 0; tick < 20; tick++)
        {
            Assert.Equal(0, await executor.RunPendingAsync(Ct));
            Assert.False(await executor.HasPendingAsync(Ct));
            var report = await sweep.RunAsync(DateTime.UtcNow.AddDays(-1), () => SweepSignal.Go, Ct);
            Assert.Equal((0, 0, 0, 0L, true), (report.Ok, report.Mismatched, report.Missing, report.Bytes, report.Complete));
        }

        Assert.True(!timed || watch.Elapsed < TimeSpan.FromSeconds(1), $"twenty idle ticks took {watch.Elapsed.TotalMilliseconds:F0} ms behind a held write lock: something waited for it");
        await ExecuteAsync(holder, "ROLLBACK");
        Assert.Equal(before, await ScalarAsync(holder, "PRAGMA data_version"));
        Assert.Equal(rows, await ScalarAsync(holder, "SELECT (SELECT count(*) FROM audit) + (SELECT count(*) FROM case_tasks)"));
        Assert.Equal(0, sweep.BytesRead);
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }
}
