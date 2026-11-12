using Urlog.Core.Audit;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Core.Metadata;

/// <summary>What a worker holds after <see cref="JobOperations.ClaimAsync"/>: the job, its file, and what the pipeline needs to stamp rows.</summary>
/// <param name="CommittedGeneration">The generation <c>events_v</c> shows for the file today; 0 when nothing was ever committed.</param>
public sealed record ClaimedJob(Job Job, EvidenceFile File, string CaseName, Host? Host, int CommittedGeneration);

/// <summary>The final counts of a finished parse, written with the commit.</summary>
public sealed record JobCounts(long ChunksRead, long ChunksParsed, long Rows, long Rejects, long BytesRead);

/// <summary>What the runner found when it compared the bytes it read with the intake record (evidence rule 4).</summary>
public sealed record IntegrityMismatch(string ActualSha256, long ActualSize);

public enum RequeueOutcome
{
    /// <summary>Back in the queue for another attempt.</summary>
    Requeued,

    /// <summary>The attempt cap was reached: the job is <c>failed</c>.</summary>
    Failed,

    /// <summary>The job was not in <c>parsing</c>; nothing changed.</summary>
    NotParsing,
}

/// <summary>
/// The ingest queue in SQLite (design record, "Consistency"): <c>queued -> parsing -> done | failed</c>, with
/// <c>parsing -> queued</c> for a stop and <c>queued -> cancelled</c>. Every transition is one BEGIN IMMEDIATE
/// transaction, so two workers cannot act on the same row, and the ones that touch evidence state (a commit of a
/// re-parse, a quarantine, a queue pause) write their audit row inside it. A terminal job never changes again: a
/// retry or a re-parse is a new row.
/// </summary>
public static class JobOperations
{
    /// <summary>Unclean stops a job may see before it is failed, so a file that takes the process down cannot loop.</summary>
    public const int DefaultMaxAttempts = 3;

    /// <summary>The prefix of the message every queued job carries while the queue is paused by the disk watermark.</summary>
    public const string PausedPrefix = "paused: ";

    private const int MaxMessageLength = 4000;

    /// <summary>
    /// Queues a parse of <paramref name="fileId"/>: a <c>parse</c> when the file has no committed generation, a
    /// <c>reparse</c> otherwise. The case must be <c>active</c>, the file must be parseable, and a file has at most
    /// one job waiting or running.
    /// </summary>
    /// <exception cref="CaseNotActiveException">The case is not <c>active</c>.</exception>
    /// <exception cref="JobRefusedException">No such file, a quarantined or artifact file, or a job already waiting or running.</exception>
    public static async Task<Job> EnqueueAsync(MetadataDb db, long fileId, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            var file = await db.Files.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId, cancellationToken).ConfigureAwait(false)
                ?? throw new JobRefusedException($"file {fileId} does not exist");
            var @case = await db.Cases.AsNoTracking().FirstAsync(c => c.Id == file.CaseId, cancellationToken).ConfigureAwait(false);
            if (@case.State != CaseState.Active)
            {
                throw new CaseNotActiveException(@case.Id, @case.State);
            }

            if (file.State is FileState.Quarantined or FileState.Artifact or FileState.Archived)
            {
                throw new JobRefusedException($"file {fileId} is {Names.Of(file.State)} and is never parsed");
            }

            if (await db.Jobs.AnyAsync(j => j.FileId == fileId && (j.State == JobState.Queued || j.State == JobState.Parsing), cancellationToken).ConfigureAwait(false))
            {
                throw new JobRefusedException($"file {fileId} already has a job waiting or running");
            }

            var committed = await CommittedGenerationAsync(db, fileId, cancellationToken).ConfigureAwait(false);
            var job = new Job { CaseId = file.CaseId, FileId = fileId, Kind = committed == 0 ? JobKind.Parse : JobKind.Reparse, CreatedAt = nowUtc };
            db.Jobs.Add(job);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return job;
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// Takes the oldest queued job whose case is <c>active</c> and has no job in <c>parsing</c> (one job per case at a
    /// time), marks it <c>parsing</c> for <paramref name="worker"/> and gives it a generation no earlier attempt on the
    /// file has used. Null when nothing can run. The write lock is taken at BEGIN, so two claimers never read the
    /// same row.
    /// </summary>
    public static async Task<ClaimedJob?> ClaimAsync(MetadataDb db, string worker, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(worker);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            var job = await (
                from j in db.Jobs
                join c in db.Cases on j.CaseId equals c.Id
                where j.State == JobState.Queued && c.State == CaseState.Active
                    && !db.Jobs.Any(o => o.CaseId == j.CaseId && o.State == JobState.Parsing)
                orderby j.CreatedAt, j.Id
                select j).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (job is null)
            {
                return null;
            }

            // Read again under the write lock: a context that tracked these rows earlier must not act on stale values.
            await db.Entry(job).ReloadAsync(cancellationToken).ConfigureAwait(false);
            var file = await db.Files.FirstAsync(f => f.Id == job.FileId, cancellationToken).ConfigureAwait(false);
            await db.Entry(file).ReloadAsync(cancellationToken).ConfigureAwait(false);
            var caseName = await db.Cases.Where(c => c.Id == job.CaseId).Select(c => c.Name).FirstAsync(cancellationToken).ConfigureAwait(false);
            var host = file.HostId is { } hostId ? await db.Hosts.AsNoTracking().FirstOrDefaultAsync(h => h.Id == hostId, cancellationToken).ConfigureAwait(false) : null;
            var committed = await CommittedGenerationAsync(db, job.FileId, cancellationToken).ConfigureAwait(false);
            var used = await db.Jobs.Where(j => j.FileId == job.FileId).MaxAsync(j => (int?)j.Generation, cancellationToken).ConfigureAwait(false) ?? 0;

            job.State = JobState.Parsing;
            job.Generation = Math.Max(used, committed) + 1;
            job.Worker = worker;
            job.StartedAt = nowUtc;
            job.FinishedAt = null;
            job.Message = null;
            ResetCounters(job);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ClaimedJob(job, file, caseName, host, committed);
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// The commit in SQLite: the job is <c>done</c> and the file's <c>parse_generation</c> is the job's, in one
    /// transaction. A commit over an earlier generation is audited as <c>file_reparse</c>. The caller writes
    /// <c>file_generation</c> in ClickHouse next; a stop between the two is what the startup sync repairs.
    /// </summary>
    /// <exception cref="JobStateException">The job is not in <c>parsing</c> for this worker.</exception>
    public static async Task CompleteAsync(MetadataDb db, IAuditWriter audit, long jobId, string worker, JobCounts counts, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(counts);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            var job = await RequireParsingAsync(db, jobId, worker, cancellationToken).ConfigureAwait(false);
            var file = await db.Files.FirstAsync(f => f.Id == job.FileId, cancellationToken).ConfigureAwait(false);
            await db.Entry(file).ReloadAsync(cancellationToken).ConfigureAwait(false);
            var committed = await CommittedGenerationAsync(db, job.FileId, cancellationToken).ConfigureAwait(false);

            job.State = JobState.Done;
            job.FinishedAt = nowUtc;
            job.Message = null;
            job.ChunksRead = counts.ChunksRead;
            job.ChunksParsed = counts.ChunksParsed;
            job.ChunksAcked = counts.ChunksParsed;
            job.Rows = counts.Rows;
            job.Rejects = counts.Rejects;
            job.BytesRead = counts.BytesRead;
            file.ParseGeneration = job.Generation;
            file.State = FileState.Parsed;
            file.RowCount = counts.Rows;
            file.RejectCount = counts.Rejects;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            if (committed > 0)
            {
                var caseName = await db.Cases.Where(c => c.Id == job.CaseId).Select(c => c.Name).FirstAsync(cancellationToken).ConfigureAwait(false);
                var details = new Dictionary<string, object?> { ["from_generation"] = committed, ["to_generation"] = job.Generation, ["job_id"] = job.Id, ["rows"] = counts.Rows, ["rejects"] = counts.Rejects };
                await audit.AppendAsync(db, AuditEntry.ForFile(AuditActions.SystemActor, null, AuditActions.FileReparse, file, caseName, details), cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// The job is <c>failed</c> with <paramref name="message"/> verbatim. A file with nothing committed becomes
    /// <c>failed</c>; a file whose earlier parse is still the visible one stays <c>parsed</c>.
    /// </summary>
    public static async Task FailAsync(MetadataDb db, long jobId, string worker, string message, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            var job = await RequireParsingAsync(db, jobId, worker, cancellationToken).ConfigureAwait(false);
            await MarkFailedAsync(db, job, message, nowUtc, cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// The bytes read do not match the intake record: the job is <c>failed</c>, the file <c>quarantined</c> (never
    /// queued again) and <c>integrity_mismatch</c> is in the chain, in one transaction.
    /// </summary>
    public static async Task QuarantineAsync(MetadataDb db, IAuditWriter audit, long jobId, string worker, IntegrityMismatch mismatch, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(mismatch);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            var job = await RequireParsingAsync(db, jobId, worker, cancellationToken).ConfigureAwait(false);
            var file = await db.Files.FirstAsync(f => f.Id == job.FileId, cancellationToken).ConfigureAwait(false);
            await db.Entry(file).ReloadAsync(cancellationToken).ConfigureAwait(false);
            job.State = JobState.Failed;
            job.FinishedAt = nowUtc;
            job.Message = Trim($"integrity mismatch: the file on disk has sha256 {mismatch.ActualSha256} and {mismatch.ActualSize} bytes, the intake record says {file.Sha256} and {file.Size} bytes; the file is quarantined and none of its rows were committed");
            file.State = FileState.Quarantined;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            var caseName = await db.Cases.Where(c => c.Id == job.CaseId).Select(c => c.Name).FirstAsync(cancellationToken).ConfigureAwait(false);
            var details = new Dictionary<string, object?> { ["actual_sha256"] = mismatch.ActualSha256, ["actual_size"] = mismatch.ActualSize, ["job_id"] = job.Id, ["check"] = "pre_parse" };
            await audit.AppendAsync(db, AuditEntry.ForFile(AuditActions.SystemActor, null, AuditActions.IntegrityMismatch, file, caseName, details), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// A job in <c>parsing</c> goes back to <c>queued</c>. An unclean stop or a refusal of the event store that may
    /// pass (<paramref name="countAttempt"/>) raises the attempt and fails the job at <paramref name="maxAttempts"/>;
    /// a graceful stop leaves the attempt as it is. Any worker may requeue: the caller holds the runner lock.
    /// </summary>
    public static async Task<RequeueOutcome> RequeueAsync(MetadataDb db, long jobId, string reason, bool countAttempt, int maxAttempts, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken).ConfigureAwait(false);
            if (job is not null)
            {
                await db.Entry(job).ReloadAsync(cancellationToken).ConfigureAwait(false);
            }

            if (job is null || job.State != JobState.Parsing)
            {
                return RequeueOutcome.NotParsing;
            }

            var outcome = RequeueOutcome.Requeued;
            if (countAttempt && job.Attempt >= maxAttempts)
            {
                await MarkFailedAsync(db, job, $"not retried after {job.Attempt} attempts: {reason}", nowUtc, cancellationToken).ConfigureAwait(false);
                outcome = RequeueOutcome.Failed;
            }
            else
            {
                job.State = JobState.Queued;
                job.Attempt += countAttempt ? 1 : 0;
                job.Worker = null;
                job.StartedAt = null;
                job.Message = Trim(reason);
                ResetCounters(job);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return outcome;
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// Moves a running job to a higher generation before it writes anything: the event store already holds a visible
    /// generation at or above the one SQLite allocated (a reused file id, or a metadata file older than the store).
    /// </summary>
    /// <exception cref="JobStateException">The job is not in <c>parsing</c> for this worker, or the generation is not higher.</exception>
    public static async Task RaiseGenerationAsync(MetadataDb db, long jobId, string worker, int generation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            var job = await RequireParsingAsync(db, jobId, worker, cancellationToken).ConfigureAwait(false);
            if (generation <= job.Generation)
            {
                throw new JobStateException($"job {jobId} is at generation {job.Generation}; it cannot move to {generation}");
            }

            job.Generation = generation;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>Cancels a queued job. A job that is running or finished is refused.</summary>
    /// <exception cref="JobStateException">The job is not <c>queued</c>.</exception>
    public static async Task CancelAsync(MetadataDb db, long jobId, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken).ConfigureAwait(false) ?? throw new JobStateException($"job {jobId} does not exist");
            await db.Entry(job).ReloadAsync(cancellationToken).ConfigureAwait(false);
            if (job.State != JobState.Queued)
            {
                throw new JobStateException($"job {jobId} is {Names.Of(job.State)}; only a queued job can be cancelled");
            }

            job.State = JobState.Cancelled;
            job.FinishedAt = nowUtc;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>The advisory counters of a running job, in one statement. Returns false when the job is no longer <c>parsing</c>.</summary>
    public static async Task<bool> WriteProgressAsync(MetadataDb db, long jobId, long chunksRead, long chunksParsed, long chunksAcked, long rows, long rejects, long bytesRead, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var updated = await db.Jobs.Where(j => j.Id == jobId && j.State == JobState.Parsing).ExecuteUpdateAsync(
            s => s.SetProperty(j => j.ChunksRead, chunksRead)
                .SetProperty(j => j.ChunksParsed, chunksParsed)
                .SetProperty(j => j.ChunksAcked, chunksAcked)
                .SetProperty(j => j.Rows, rows)
                .SetProperty(j => j.Rejects, rejects)
                .SetProperty(j => j.BytesRead, bytesRead),
            cancellationToken).ConfigureAwait(false);
        return updated == 1;
    }

    /// <summary>Jobs still in <c>parsing</c>. With the runner lock held and no slot running, every one is an orphan.</summary>
    public static Task<List<Job>> ParsingAsync(MetadataDb db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Jobs.AsNoTracking().Where(j => j.State == JobState.Parsing).OrderBy(j => j.Id).ToListAsync(cancellationToken);
    }

    /// <summary>True when a job could be claimed now.</summary>
    public static Task<bool> AnyQueuedAsync(MetadataDb db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Jobs.AnyAsync(j => j.State == JobState.Queued, cancellationToken);
    }

    /// <summary>The committed generation of every file that has one: the highest generation among its <c>done</c> jobs. SQLite is the truth the event store is synced to.</summary>
    public static async Task<Dictionary<long, int>> CommittedGenerationsAsync(MetadataDb db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rows = await db.Jobs.Where(j => j.State == JobState.Done).GroupBy(j => j.FileId).Select(g => new { FileId = g.Key, Generation = g.Max(j => j.Generation) }).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.ToDictionary(r => r.FileId, r => r.Generation);
    }

    /// <summary>The highest generation among the file's <c>done</c> jobs, 0 when it has none.</summary>
    public static async Task<int> CommittedGenerationAsync(MetadataDb db, long fileId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.Jobs.Where(j => j.FileId == fileId && j.State == JobState.Done).MaxAsync(j => (int?)j.Generation, cancellationToken).ConfigureAwait(false) ?? 0;
    }

    /// <summary>
    /// The disk watermark stopped the queue: every queued job says why and <c>ingest_paused</c> is in the chain, in
    /// one transaction. <paramref name="details"/> carries the volume and the byte counts.
    /// </summary>
    public static Task PauseAsync(MetadataDb db, IAuditWriter audit, string reason, IReadOnlyDictionary<string, object?> details, CancellationToken cancellationToken = default) =>
        SetPausedAsync(db, audit, AuditActions.IngestPaused, PausedPrefix + reason, details, cancellationToken);

    /// <summary>Free space is back above the mark: the pause message is cleared and <c>ingest_resumed</c> is in the chain.</summary>
    public static Task ResumeAsync(MetadataDb db, IAuditWriter audit, IReadOnlyDictionary<string, object?> details, CancellationToken cancellationToken = default) =>
        SetPausedAsync(db, audit, AuditActions.IngestResumed, null, details, cancellationToken);

    private static async Task SetPausedAsync(MetadataDb db, IAuditWriter audit, string action, string? message, IReadOnlyDictionary<string, object?> details, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(details);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            var trimmed = message is null ? null : Trim(message);
            var queued = await db.Jobs.Where(j => j.State == JobState.Queued && (trimmed != null || (j.Message != null && j.Message.StartsWith(PausedPrefix))))
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.Message, trimmed), cancellationToken).ConfigureAwait(false);
            var row = new Dictionary<string, object?>(details, StringComparer.Ordinal) { ["queued_jobs"] = queued };
            await audit.AppendAsync(db, new AuditEntry(AuditActions.SystemActor, null, action, null, null, row), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private static async Task<Job> RequireParsingAsync(MetadataDb db, long jobId, string worker, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken).ConfigureAwait(false) ?? throw new JobStateException($"job {jobId} does not exist");
        await db.Entry(job).ReloadAsync(cancellationToken).ConfigureAwait(false);
        if (job.State != JobState.Parsing || !string.Equals(job.Worker, worker, StringComparison.Ordinal))
        {
            throw new JobStateException($"job {jobId} is {Names.Of(job.State)} for worker '{job.Worker}', not parsing for '{worker}'");
        }

        return job;
    }

    private static async Task MarkFailedAsync(MetadataDb db, Job job, string message, DateTime nowUtc, CancellationToken cancellationToken)
    {
        job.State = JobState.Failed;
        job.FinishedAt = nowUtc;
        job.Message = Trim(message);
        if (await CommittedGenerationAsync(db, job.FileId, cancellationToken).ConfigureAwait(false) == 0)
        {
            var file = await db.Files.FirstAsync(f => f.Id == job.FileId, cancellationToken).ConfigureAwait(false);
            await db.Entry(file).ReloadAsync(cancellationToken).ConfigureAwait(false);
            if (file.State == FileState.Received)
            {
                file.State = FileState.Failed;
            }
        }
    }

    private static void ResetCounters(Job job)
    {
        job.ChunksRead = 0;
        job.ChunksParsed = 0;
        job.ChunksAcked = 0;
        job.Rows = 0;
        job.Rejects = 0;
        job.BytesRead = 0;
    }

    private static string Trim(string message) => message.Length <= MaxMessageLength ? message : message[..MaxMessageLength];
}

/// <summary>A job cannot be queued for this file.</summary>
public sealed class JobRefusedException : MetadataException
{
    public JobRefusedException()
    {
    }

    public JobRefusedException(string message)
        : base(message)
    {
    }

    public JobRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The job is not in the state the transition needs (another worker owns it, or it already finished).</summary>
public sealed class JobStateException : MetadataException
{
    public JobStateException()
    {
    }

    public JobStateException(string message)
        : base(message)
    {
    }

    public JobStateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
