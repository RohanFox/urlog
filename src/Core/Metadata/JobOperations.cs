using Urlog.Core.Audit;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Core.Metadata;

/// <summary>What a worker holds after <see cref="JobOperations.ClaimAsync"/>: the job, its file, and what the pipeline needs to stamp rows.</summary>
/// <param name="CommittedGeneration">The generation <c>events_v</c> shows for the file today; 0 when nothing was ever committed.</param>
public sealed record ClaimedJob(Job Job, EvidenceFile File, string CaseName, Host? Host, int CommittedGeneration);

/// <summary>The final counts of a finished parse, written with the commit.</summary>
public sealed record JobCounts(long ChunksRead, long ChunksParsed, long Rows, long Rejects, long BytesRead);

/// <summary>
/// What a finished parse learned about the file besides its counts. <paramref name="End"/> has no usable default:
/// <see cref="Urlog.Core.Pipeline.InputEndKind.Unknown"/> is refused by the commit, so a path that never asked how
/// the input ended cannot mark a file parsed.
/// </summary>
/// <param name="TsMin">Least event time inside the bounds of <see cref="EventTime"/>, UTC; null when no row carried one.</param>
/// <param name="UndatedRows">Rows whose time is below <see cref="EventTime.Floor"/>.</param>
/// <param name="FutureRows">Rows dated later than the file's arrival plus <see cref="EventTime.FutureMargin"/>.</param>
/// <param name="SpansDst">The file was read with one zone offset and its events lie on both sides of a change of it.</param>
/// <param name="HistogramFilled">The stored histogram of this generation was written before the commit.</param>
public sealed record JobOutcome(Urlog.Core.Pipeline.InputEnd End, DateTime? TsMin = null, DateTime? TsMax = null, bool SpansDst = false, long UndatedRows = 0, long FutureRows = 0, bool HistogramFilled = false);

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
/// <para>
/// Every time written to a job row (<c>created_at</c>, <c>started_at</c>, <c>finished_at</c>) is read from the clock
/// after the operation holds the write lock. A time read before it would be the time of the call: an operation that
/// waited for the lock behind another would then carry the earlier time and be recorded as if it had happened first.
/// </para>
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
    public static Task<Job> EnqueueAsync(MetadataDb db, long fileId, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return EnqueueCoreAsync(db, fileId, () => clock.GetUtcNow().UtcDateTime, cancellationToken);
    }

    /// <summary>As the overload with a clock, at a time the caller fixes. For tests: a caller that reads a clock itself reads it before the write lock.</summary>
    internal static Task<Job> EnqueueAsync(MetadataDb db, long fileId, DateTime nowUtc, CancellationToken cancellationToken = default) =>
        EnqueueCoreAsync(db, fileId, () => nowUtc, cancellationToken);

    private static async Task<Job> EnqueueCoreAsync(MetadataDb db, long fileId, Func<DateTime> now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            // After the lock: the order of the times written is the order of the transactions.
            var nowUtc = now();
            var file = await db.Files.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId, cancellationToken).ConfigureAwait(false)
                ?? throw new JobRefusedException($"file {fileId} does not exist");
            var @case = await db.Cases.AsNoTracking().FirstAsync(c => c.Id == file.CaseId, cancellationToken).ConfigureAwait(false);
            if (@case.State != CaseState.Active)
            {
                throw new CaseNotActiveException(@case.Id, @case.State);
            }

            if (file.State is FileState.Quarantined or FileState.Artifact or FileState.Archived or FileState.Expanded or FileState.Unreadable)
            {
                throw new JobRefusedException($"file {fileId} is {Names.Of(file.State)} and is never parsed");
            }

            if (await db.Jobs.AnyAsync(j => j.FileId == fileId && (j.State == JobState.Queued || j.State == JobState.Parsing), cancellationToken).ConfigureAwait(false))
            {
                throw new JobRefusedException($"file {fileId} already has a job waiting or running");
            }

            var committed = await CommittedGenerationAsync(db, fileId, cancellationToken).ConfigureAwait(false);
            var kind = FileOperations.JobFor(file) == JobKind.Expand ? JobKind.Expand : committed == 0 ? JobKind.Parse : JobKind.Reparse;
            var job = new Job { CaseId = file.CaseId, FileId = fileId, Kind = kind, CreatedAt = nowUtc };
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
    /// Takes the oldest queued job whose case is <c>active</c> (or archived with a re-import under way, whose jobs
    /// these are) and has no job in <c>parsing</c> (one job per case at a
    /// time), marks it <c>parsing</c> for <paramref name="worker"/> and gives it a generation no earlier attempt on the
    /// file has used. Null when nothing can run. The write lock is taken at BEGIN, so two claimers never read the
    /// same row.
    /// </summary>
    public static Task<ClaimedJob?> ClaimAsync(MetadataDb db, string worker, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return ClaimCoreAsync(db, worker, () => clock.GetUtcNow().UtcDateTime, cancellationToken);
    }

    /// <summary>As the overload with a clock, at a time the caller fixes. For tests: a caller that reads a clock itself reads it before the write lock.</summary>
    internal static Task<ClaimedJob?> ClaimAsync(MetadataDb db, string worker, DateTime nowUtc, CancellationToken cancellationToken = default) =>
        ClaimCoreAsync(db, worker, () => nowUtc, cancellationToken);

    private static async Task<ClaimedJob?> ClaimCoreAsync(MetadataDb db, string worker, Func<DateTime> now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(worker);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            // After the lock: the order of the times written is the order of the transactions.
            var nowUtc = now();
            var job = await (
                from j in db.Jobs
                join c in db.Cases on j.CaseId equals c.Id
                where j.State == JobState.Queued
                    // An archived case takes no job, except the ones its re-import queued while that is under way.
                    && (c.State == CaseState.Active
                        || (c.State == CaseState.Archived && db.CaseTasks.Any(t => t.CaseId == c.Id && t.Kind == CaseTaskKind.Reimport && t.State == CaseTaskState.Committed)))
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
    /// <para>
    /// A truncated input is committed with its flag, its two offsets and an <c>input_truncated</c> audit row, all in
    /// this transaction: the rows become visible only together with the statement that the input was cut short.
    /// </para>
    /// <exception cref="JobStateException">The job is not in <c>parsing</c> for this worker.</exception>
    /// <exception cref="ArgumentException"><paramref name="outcome"/> does not say how the input ended.</exception>
    public static Task CompleteAsync(MetadataDb db, IAuditWriter audit, long jobId, string worker, JobCounts counts, JobOutcome outcome, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return CompleteCoreAsync(db, audit, jobId, worker, counts, outcome, () => clock.GetUtcNow().UtcDateTime, cancellationToken);
    }

    /// <summary>As the overload with a clock, at a time the caller fixes. For tests: a caller that reads a clock itself reads it before the write lock.</summary>
    internal static Task CompleteAsync(MetadataDb db, IAuditWriter audit, long jobId, string worker, JobCounts counts, JobOutcome outcome, DateTime nowUtc, CancellationToken cancellationToken = default) =>
        CompleteCoreAsync(db, audit, jobId, worker, counts, outcome, () => nowUtc, cancellationToken);

    private static async Task CompleteCoreAsync(MetadataDb db, IAuditWriter audit, long jobId, string worker, JobCounts counts, JobOutcome outcome, Func<DateTime> now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(counts);
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.End.Kind == Urlog.Core.Pipeline.InputEndKind.Unknown)
        {
            throw new ArgumentException($"job {jobId}: the end of the input is unknown, and a parse is committed only once it is known to be complete or truncated", nameof(outcome));
        }
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            // After the lock: the order of the times written is the order of the transactions.
            var nowUtc = now();
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
            // Every one of these is this parse's finding: a re-parse of a file that was cut short and has since
            // been replaced by nothing (the bytes never change) finds the same, and a new source may find new times.
            var end = outcome.End;
            if (end.IsTruncated || !file.ContainerCut)
            {
                file.InputTruncated = end.IsTruncated;
                file.TruncatedDecodedOffset = end.IsTruncated ? end.DecodedOffset : null;
                file.TruncatedStoredOffset = end.IsTruncated ? end.StoredOffset : null;
            }

            file.TrailingBytes = end.TrailingBytes;
            file.TrailingBytesInexact = end.TrailingBytes > 0 && !end.TrailingBytesExact;
            file.TsMin = outcome.TsMin;
            file.TsMax = outcome.TsMax;
            file.SpansDst = outcome.SpansDst;
            file.UndatedRows = outcome.UndatedRows;
            file.FutureRows = outcome.FutureRows;
            file.HistogramGeneration = outcome.HistogramFilled ? job.Generation : null;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            // The case's span and the mark that its field statistics are out of date, with the rows they describe.
            await CaseStatsOperations.OnCommitAsync(db, job.CaseId, nowUtc, cancellationToken).ConfigureAwait(false);

            var caseName = await db.Cases.Where(c => c.Id == job.CaseId).Select(c => c.Name).FirstAsync(cancellationToken).ConfigureAwait(false);
            if (end.IsTruncated)
            {
                var truncation = new Dictionary<string, object?>
                {
                    ["decoded_offset"] = end.DecodedOffset,
                    ["stored_offset"] = end.StoredOffset,
                    ["compression"] = file.Compression,
                    ["archive_path"] = file.ArchivePath,
                    ["job_id"] = job.Id,
                    ["generation"] = job.Generation,
                    ["rows"] = counts.Rows,
                };
                await audit.AppendAsync(db, AuditEntry.ForFile(AuditActions.SystemActor, null, AuditActions.InputTruncated, file, caseName, truncation), cancellationToken).ConfigureAwait(false);
            }

            if (committed > 0)
            {
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
    /// The end of an expansion: the job is <c>done</c> and the container is <c>expanded</c>. Its members were
    /// written before, batch by batch, each batch with its own audit rows; this adds only the summary.
    /// </summary>
    /// <param name="members">Entries of the container that were looked at, for the job row.</param>
    /// <param name="truncated">The container itself ends before its end structure; null when it is whole.</param>
    /// <exception cref="JobStateException">The job is not in <c>parsing</c> for this worker.</exception>
    public static Task CompleteExpandAsync(MetadataDb db, IAuditWriter audit, long jobId, string worker, long members, string summary, Urlog.Core.Pipeline.InputEnd? truncated, string? cachePath, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return CompleteExpandCoreAsync(db, audit, jobId, worker, members, summary, truncated, cachePath, () => clock.GetUtcNow().UtcDateTime, cancellationToken);
    }

    /// <summary>As the overload with a clock, at a time the caller fixes. For tests: a caller that reads a clock itself reads it before the write lock.</summary>
    internal static Task CompleteExpandAsync(MetadataDb db, IAuditWriter audit, long jobId, string worker, long members, string summary, Urlog.Core.Pipeline.InputEnd? truncated, string? cachePath, DateTime nowUtc, CancellationToken cancellationToken = default) =>
        CompleteExpandCoreAsync(db, audit, jobId, worker, members, summary, truncated, cachePath, () => nowUtc, cancellationToken);

    private static async Task CompleteExpandCoreAsync(MetadataDb db, IAuditWriter audit, long jobId, string worker, long members, string summary, Urlog.Core.Pipeline.InputEnd? truncated, string? cachePath, Func<DateTime> now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            // After the lock: the order of the times written is the order of the transactions.
            var nowUtc = now();
            var job = await RequireParsingAsync(db, jobId, worker, cancellationToken).ConfigureAwait(false);
            if (job.Kind != JobKind.Expand)
            {
                throw new JobStateException($"job {jobId} is a {Names.Of(job.Kind)}, not an expansion");
            }

            var file = await db.Files.FirstAsync(f => f.Id == job.FileId, cancellationToken).ConfigureAwait(false);
            await db.Entry(file).ReloadAsync(cancellationToken).ConfigureAwait(false);
            job.State = JobState.Done;
            job.FinishedAt = nowUtc;
            job.Rows = members;
            job.Message = Trim(summary);
            file.State = FileState.Expanded;
            file.CachePath = cachePath;
            if (truncated is { } end)
            {
                file.InputTruncated = true;
                file.TruncatedDecodedOffset = end.DecodedOffset;
                file.TruncatedStoredOffset = end.StoredOffset;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (truncated is { } cut)
            {
                var caseName = await db.Cases.Where(c => c.Id == job.CaseId).Select(c => c.Name).FirstAsync(cancellationToken).ConfigureAwait(false);
                var details = new Dictionary<string, object?>
                {
                    ["decoded_offset"] = cut.DecodedOffset,
                    ["stored_offset"] = cut.StoredOffset,
                    ["compression"] = file.Compression,
                    ["archive_path"] = file.ArchivePath,
                    ["job_id"] = job.Id,
                    ["members"] = members,
                };
                await audit.AppendAsync(db, AuditEntry.ForFile(AuditActions.SystemActor, null, AuditActions.InputTruncated, file, caseName, details), cancellationToken).ConfigureAwait(false);
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
    /// Queues the expansion of one archive again with limits of the caller's choosing, for an archive whose
    /// expansion failed at a cap. The limits ride on the job and apply to it alone; the request and the limits
    /// are in the chain as <c>file_expand_rerun</c>, in the same transaction as the job.
    /// </summary>
    /// <param name="limitsJson">The limits as <c>ArchiveLimits.ToJson</c> writes them.</param>
    /// <exception cref="JobRefusedException">No such file, not a container, not failed, or a job is already waiting or running.</exception>
    public static Task<Job> RequestExpandRerunAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, long fileId, string limitsJson, IReadOnlyDictionary<string, object?> limits, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return RequestExpandRerunCoreAsync(db, audit, actor, fileId, limitsJson, limits, () => clock.GetUtcNow().UtcDateTime, cancellationToken);
    }

    /// <summary>As the overload with a clock, at a time the caller fixes. For tests: a caller that reads a clock itself reads it before the write lock.</summary>
    internal static Task<Job> RequestExpandRerunAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, long fileId, string limitsJson, IReadOnlyDictionary<string, object?> limits, DateTime nowUtc, CancellationToken cancellationToken = default) =>
        RequestExpandRerunCoreAsync(db, audit, actor, fileId, limitsJson, limits, () => nowUtc, cancellationToken);

    private static async Task<Job> RequestExpandRerunCoreAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, long fileId, string limitsJson, IReadOnlyDictionary<string, object?> limits, Func<DateTime> now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(limits);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            // After the lock: the order of the times written is the order of the transactions.
            var nowUtc = now();
            var file = await db.Files.FirstOrDefaultAsync(f => f.Id == fileId, cancellationToken).ConfigureAwait(false)
                ?? throw new JobRefusedException($"file {fileId} does not exist");
            await db.Entry(file).ReloadAsync(cancellationToken).ConfigureAwait(false);
            var @case = await db.Cases.AsNoTracking().FirstAsync(c => c.Id == file.CaseId, cancellationToken).ConfigureAwait(false);
            if (@case.State != CaseState.Active)
            {
                throw new CaseNotActiveException(@case.Id, @case.State);
            }

            if (file.DetectedKind != Urlog.Core.Detection.FileKind.Container)
            {
                throw new JobRefusedException($"file {fileId} is not an archive");
            }

            if (file.State != FileState.Failed)
            {
                throw new JobRefusedException($"file {fileId} is {Names.Of(file.State)}; only an archive whose expansion failed is run again");
            }

            if (await db.Jobs.AnyAsync(j => j.FileId == fileId && (j.State == JobState.Queued || j.State == JobState.Parsing), cancellationToken).ConfigureAwait(false))
            {
                throw new JobRefusedException($"file {fileId} already has a job waiting or running");
            }

            var failed = await db.Jobs.AsNoTracking().Where(j => j.FileId == fileId && j.State == JobState.Failed).OrderByDescending(j => j.Id).Select(j => j.Message).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            file.State = FileState.Received;
            var job = new Job { CaseId = file.CaseId, FileId = fileId, Kind = JobKind.Expand, CreatedAt = nowUtc, Options = limitsJson };
            db.Jobs.Add(job);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            var details = new Dictionary<string, object?>(limits, StringComparer.Ordinal) { ["job_id"] = job.Id, ["after_failure"] = failed };
            await audit.AppendAsync(db, AuditEntry.ForFile(actor.Name, actor.UserId, AuditActions.FileExpandRerun, file, @case.Name, details), cancellationToken).ConfigureAwait(false);
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
    /// The job is <c>failed</c> with <paramref name="message"/> verbatim. A file with nothing committed becomes
    /// <c>failed</c>; a file whose earlier parse is still the visible one stays <c>parsed</c>.
    /// </summary>
    public static Task FailAsync(MetadataDb db, long jobId, string worker, string message, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return FailCoreAsync(db, jobId, worker, message, () => clock.GetUtcNow().UtcDateTime, cancellationToken);
    }

    /// <summary>As the overload with a clock, at a time the caller fixes. For tests: a caller that reads a clock itself reads it before the write lock.</summary>
    internal static Task FailAsync(MetadataDb db, long jobId, string worker, string message, DateTime nowUtc, CancellationToken cancellationToken = default) =>
        FailCoreAsync(db, jobId, worker, message, () => nowUtc, cancellationToken);

    private static async Task FailCoreAsync(MetadataDb db, long jobId, string worker, string message, Func<DateTime> now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            // After the lock: the order of the times written is the order of the transactions.
            var nowUtc = now();
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
    public static Task QuarantineAsync(MetadataDb db, IAuditWriter audit, long jobId, string worker, IntegrityMismatch mismatch, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return QuarantineCoreAsync(db, audit, jobId, worker, mismatch, () => clock.GetUtcNow().UtcDateTime, cancellationToken);
    }

    /// <summary>As the overload with a clock, at a time the caller fixes. For tests: a caller that reads a clock itself reads it before the write lock.</summary>
    internal static Task QuarantineAsync(MetadataDb db, IAuditWriter audit, long jobId, string worker, IntegrityMismatch mismatch, DateTime nowUtc, CancellationToken cancellationToken = default) =>
        QuarantineCoreAsync(db, audit, jobId, worker, mismatch, () => nowUtc, cancellationToken);

    private static async Task QuarantineCoreAsync(MetadataDb db, IAuditWriter audit, long jobId, string worker, IntegrityMismatch mismatch, Func<DateTime> now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(mismatch);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            // After the lock: the order of the times written is the order of the transactions.
            var nowUtc = now();
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
    public static Task<RequeueOutcome> RequeueAsync(MetadataDb db, long jobId, string reason, bool countAttempt, int maxAttempts, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return RequeueCoreAsync(db, jobId, reason, countAttempt, maxAttempts, () => clock.GetUtcNow().UtcDateTime, cancellationToken);
    }

    /// <summary>As the overload with a clock, at a time the caller fixes. For tests: a caller that reads a clock itself reads it before the write lock.</summary>
    internal static Task<RequeueOutcome> RequeueAsync(MetadataDb db, long jobId, string reason, bool countAttempt, int maxAttempts, DateTime nowUtc, CancellationToken cancellationToken = default) =>
        RequeueCoreAsync(db, jobId, reason, countAttempt, maxAttempts, () => nowUtc, cancellationToken);

    private static async Task<RequeueOutcome> RequeueCoreAsync(MetadataDb db, long jobId, string reason, bool countAttempt, int maxAttempts, Func<DateTime> now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            // After the lock: the order of the times written is the order of the transactions.
            var nowUtc = now();
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
    public static Task CancelAsync(MetadataDb db, long jobId, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return CancelCoreAsync(db, jobId, () => clock.GetUtcNow().UtcDateTime, cancellationToken);
    }

    /// <summary>As the overload with a clock, at a time the caller fixes. For tests: a caller that reads a clock itself reads it before the write lock.</summary>
    internal static Task CancelAsync(MetadataDb db, long jobId, DateTime nowUtc, CancellationToken cancellationToken = default) =>
        CancelCoreAsync(db, jobId, () => nowUtc, cancellationToken);

    private static async Task CancelCoreAsync(MetadataDb db, long jobId, Func<DateTime> now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            // After the lock: the order of the times written is the order of the transactions.
            var nowUtc = now();
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

    /// <summary>
    /// The committed generation of every file that has one: the highest generation among its <c>done</c> jobs. SQLite
    /// is the truth the event store is synced to. Files of a case in <c>purging</c> are left out: the purge removes
    /// their generation rows, and the sync must not write them back.
    /// </summary>
    public static async Task<Dictionary<long, int>> CommittedGenerationsAsync(MetadataDb db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rows = await db.Jobs.Where(j => j.State == JobState.Done && !db.Cases.Any(c => c.Id == j.CaseId && c.State == CaseState.Purging)).GroupBy(j => j.FileId).Select(g => new { FileId = g.Key, Generation = g.Max(j => j.Generation) }).ToListAsync(cancellationToken).ConfigureAwait(false);
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
