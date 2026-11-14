using Urlog.Core.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Urlog.Core.Jobs;

/// <summary>What <see cref="StartupReconciler.RunAsync"/> did.</summary>
/// <param name="Requeued">Jobs found in <c>parsing</c> and queued again.</param>
/// <param name="FailedAtCap">Jobs found in <c>parsing</c> for the last allowed time and failed.</param>
/// <param name="GenerationsWritten">Files whose <c>file_generation</c> row was written from SQLite.</param>
/// <param name="PurgingCases">Cases still in <c>purging</c>.</param>
public sealed record ReconcileReport(int Requeued, int FailedAtCap, int GenerationsWritten, IReadOnlyList<long> PurgingCases);

/// <summary>
/// The cross-store reconcilers (design record, "Consistency"), run by the runner after it holds the lock and before
/// it claims anything, in this order:
/// <list type="number">
/// <item>Jobs in <c>parsing</c>. Nothing else can be running them, so each was cut off: the file's uncommitted rows
/// are deleted in the event store, then the job goes back to <c>queued</c> (or to <c>failed</c> at the attempt cap).
/// The event store first, SQLite last: a stop in between repeats the step.</item>
/// <item><c>file_generation</c>. SQLite is the truth: every file whose committed generation differs from the event
/// store's row gets the row written. This repairs a stop between the SQLite commit and the event store's.</item>
/// <item>Cases in <c>purging</c>, where the purge resumes.</item>
/// </list>
/// Jobs before generations, so the sync reads settled job states; both before any claim, so no new insert races a
/// recovery delete.
/// </summary>
public sealed class StartupReconciler(MetadataStore store, IJobEventStore events, JobRunnerOptions options, ILogger logger)
{
    public async Task<ReconcileReport> RunAsync(CancellationToken cancellationToken)
    {
        var (requeued, failed) = await RecoverParsingJobsAsync(cancellationToken).ConfigureAwait(false);
        var written = await SyncGenerationsAsync(cancellationToken).ConfigureAwait(false);
        var purging = await PurgingCasesAsync(cancellationToken).ConfigureAwait(false);
        return new ReconcileReport(requeued, failed, written, purging);
    }

    /// <summary>Step 1. Returns the jobs requeued and the jobs failed at the cap.</summary>
    public async Task<(int Requeued, int Failed)> RecoverParsingJobsAsync(CancellationToken cancellationToken)
    {
        await using var db = store.CreateDbContext();
        var orphans = await JobOperations.ParsingAsync(db, cancellationToken).ConfigureAwait(false);
        int requeued = 0, failed = 0;
        foreach (var job in orphans)
        {
            var committed = await JobOperations.CommittedGenerationAsync(db, job.FileId, cancellationToken).ConfigureAwait(false);
            await events.DeleteFileGenerationsExceptAsync((ulong)job.CaseId, (ulong)job.FileId, (uint)committed, cancellationToken).ConfigureAwait(false);
            var reason = $"queued again after an unclean stop of worker '{job.Worker}' during attempt {job.Attempt} (generation {job.Generation}); the attempt's rows were removed";
            var outcome = await JobOperations.RequeueAsync(db, job.Id, reason, countAttempt: true, options.MaxAttempts, store.Clock, cancellationToken).ConfigureAwait(false);
            if (outcome == RequeueOutcome.Failed)
            {
                failed++;
            }
            else if (outcome == RequeueOutcome.Requeued)
            {
                requeued++;
            }

            JobLog.Recovered(logger, job.Id, job.FileId, job.Worker ?? string.Empty, job.Attempt, outcome.ToString());
        }

        return (requeued, failed);
    }

    /// <summary>Step 2. Returns the number of rows written.</summary>
    public async Task<int> SyncGenerationsAsync(CancellationToken cancellationToken)
    {
        Dictionary<long, int> truth;
        await using (var db = store.CreateDbContext())
        {
            truth = await JobOperations.CommittedGenerationsAsync(db, cancellationToken).ConfigureAwait(false);
        }

        if (truth.Count == 0)
        {
            return 0;
        }

        var mirrored = await events.ReadFileGenerationsAsync(cancellationToken).ConfigureAwait(false);
        var differ = new List<KeyValuePair<ulong, uint>>();
        foreach (var (fileId, generation) in truth)
        {
            if (!mirrored.TryGetValue((ulong)fileId, out var current) || current != (uint)generation)
            {
                differ.Add(new((ulong)fileId, (uint)generation));
            }
        }

        if (differ.Count > 0)
        {
            await events.CommitFileGenerationsAsync(differ, cancellationToken).ConfigureAwait(false);
            JobLog.GenerationsSynced(logger, differ.Count, truth.Count);
        }

        return differ.Count;
    }

    /// <summary>Step 3. The purge's own steps arrive with the case lifecycle; until then a case left in <c>purging</c> is reported.</summary>
    public async Task<IReadOnlyList<long>> PurgingCasesAsync(CancellationToken cancellationToken)
    {
        await using var db = store.CreateDbContext();
        var purging = await db.Cases.AsNoTracking().Where(c => c.State == CaseState.Purging).OrderBy(c => c.Id).Select(c => c.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var caseId in purging)
        {
            JobLog.PurgeWaiting(logger, caseId);
        }

        return purging;
    }
}
