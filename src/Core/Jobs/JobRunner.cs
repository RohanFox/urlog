using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Urlog.Core.Audit;
using Urlog.Core.Ingest;
using Urlog.Core.Intake;
using Urlog.Core.Intake.Archives;
using Urlog.Core.Lifecycle;
using Urlog.Core.Metadata;
using Urlog.Core.Pipeline;
using Urlog.Core.Detection;
using Urlog.Core.Sources;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Urlog.Core.Jobs;

/// <summary>
/// Runs the ingest queue (design record, "Consistency"). One runner per metadata file (<see cref="RunnerLock"/>);
/// <see cref="JobRunnerOptions.MaxConcurrentJobs"/> slots, each with its own pipeline buffers, each running one job
/// at a time; a case never has two jobs running.
/// <para>
/// Startup, in order: the lock, the event store answering the ingest account, the store identity of the metadata
/// file compared with the store's (<see cref="StoreIdentityGuard"/>; a mismatch ends the start with nothing
/// written), the metadata migrations, the reconcilers (<see cref="StartupReconciler"/>), the disk watermark, then
/// claiming. A job is one read of the file:
/// the bytes are hashed as they are read and the digest is compared with the intake record before anything is
/// committed, and a file shows no rows until its generation is committed.
/// </para>
/// <para>
/// Order within a job. Success: SQLite (job <c>done</c>, the file's generation) and then <c>file_generation</c> in
/// the event store. Failure: the attempt's rows are deleted in the event store first and SQLite is written last. A
/// stop anywhere leaves either a job in <c>parsing</c>, which the next start requeues, or SQLite ahead of the event
/// store, which the next start syncs.
/// </para>
/// </summary>
public sealed class JobRunner
{
    private readonly MetadataStore _store;
    private readonly IJobEventStore _events;
    private readonly SourceCatalog _sources;
    private readonly JobRunnerOptions _options;
    private readonly DiskWatermark _watermark;
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggers;
    private readonly MemberStore _members;
    private readonly ArchiveExpander _expander;
    private readonly LifecycleExecutor _lifecycle;
    private readonly IntegritySweep _sweep;
    private int _running;
    private int _paused;
    private int _syncNeeded;
    private long _claimNotBefore;
    private int _statsBusy;
    private string? _identity;
    private StoreIdentityException? _identityLost;

    public JobRunner(MetadataStore store, IJobEventStore events, SourceCatalog sources, JobRunnerOptions options, IDiskProbe? disk = null, ILoggerFactory? loggers = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _store = store;
        _events = events;
        _sources = sources;
        _options = options;
        _loggers = loggers ?? NullLoggerFactory.Instance;
        _logger = _loggers.CreateLogger<JobRunner>();
        var local = new List<string> { Path.GetDirectoryName(store.Path)! };
        if (options.EvidenceRoot is { Length: > 0 } root)
        {
            local.Add(root);
        }

        var probe = disk ?? new DriveDiskProbe();
        _members = new MemberStore(options.EvidenceRoot, options.CacheRoot, probe, options.Watermark);
        _expander = new ArchiveExpander(store, _members, FormatDetector.Instantiate(sources), options.HostPatterns is null ? null : new HostInference(options.HostPatterns));
        if (_members.CacheRoot is { } cache)
        {
            // The cache volume is watched like the others; the directory is made so the volume can be measured.
            Directory.CreateDirectory(cache);
            local.Add(cache);
        }

        _watermark = new DiskWatermark(options.Watermark, probe, local);
        var paths = new LifecyclePaths(options.EvidenceRoot, _members.CacheRoot, options.BackupDirectory ?? SqliteBackup.DefaultDirectory(store.Path), options.ArchiveRoot);
        _lifecycle = new LifecycleExecutor(store, events, options.Lifecycle, paths, _loggers.CreateLogger<LifecycleExecutor>());
        _sweep = new IntegritySweep(store, _members, options.EvidenceRoot, options.Lifecycle, _loggers.CreateLogger<IntegritySweep>());
    }

    /// <summary>True while the disk watermark holds the queue.</summary>
    public bool Paused => Volatile.Read(ref _paused) == 1;

    public int Running => Volatile.Read(ref _running);

    /// <summary>The expansion of archives this runner does; tests hook into its batches.</summary>
    public ArchiveExpander Expander => _expander;

    /// <summary>The purge executor of this runner.</summary>
    public LifecycleExecutor Lifecycle => _lifecycle;

    /// <summary>What the last integrity sweep of this runner did; null before the first has ended.</summary>
    public SweepReport? LastSweep { get; private set; }

    /// <summary>What the startup reconcilers did on the last <see cref="RunAsync"/>.</summary>
    public ReconcileReport? LastReconcile { get; private set; }

    /// <summary>
    /// Runs until cancelled, or until the queue is empty with <see cref="JobRunnerOptions.ExitWhenIdle"/>. A
    /// cancellation stops the running jobs, removes their rows and puts them back in the queue.
    /// </summary>
    /// <exception cref="RunnerLockedException">Another runner holds the metadata file.</exception>
    /// <exception cref="StoreIdentityException">The metadata file and the event store do not carry the same identity.</exception>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var runnerLock = await RunnerLock.AcquireAsync(_store.Path, _options.LockWait, cancellationToken).ConfigureAwait(false)
            ?? throw new RunnerLockedException($"another job runner holds {RunnerLock.PathFor(_store.Path)}; one runner per metadata file");
        // Before the metadata migrations, the reconcilers and any claim: a file that does not belong to this store is
        // left as it was found, and so is the store.
        var metadataId = await StoreIdentity.ReadAsync(_store.Path, cancellationToken).ConfigureAwait(false);
        var (answered, storeId) = await WaitForStoreAsync(cancellationToken).ConfigureAwait(false);
        if (!answered)
        {
            return;
        }

        _identity = StoreIdentityGuard.Compare(_store.Path, metadataId, _events.Endpoint, storeId);
        await _store.MigrateAsync(cancellationToken).ConfigureAwait(false);
        LastReconcile = await new StartupReconciler(_store, _events, _options, _logger, _lifecycle).RunAsync(cancellationToken).ConfigureAwait(false);
        await CheckWatermarkAsync(cancellationToken).ConfigureAwait(false);

        // A slot that cannot go on (the metadata store refuses a commit) stops the others; cancellation from outside is a normal stop.
        using var fatal = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var maintenanceStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var maintenance = Task.Run(() => MaintenanceLoopAsync(fatal, maintenanceStop.Token), CancellationToken.None);
        var lifecycle = Task.Run(() => LifecycleLoopAsync(maintenanceStop.Token), CancellationToken.None);
        var slots = new Task[_options.MaxConcurrentJobs];
        for (var i = 0; i < slots.Length; i++)
        {
            var slot = i + 1;
            slots[i] = Task.Run(() => SlotLoopAsync(slot, fatal, cancellationToken), CancellationToken.None);
        }

        try
        {
            await Task.WhenAll(slots).ConfigureAwait(false);
        }
        finally
        {
            await maintenanceStop.CancelAsync().ConfigureAwait(false);
            await maintenance.ConfigureAwait(false);
            await lifecycle.ConfigureAwait(false);
        }

        if (_options.ExitWhenIdle && !cancellationToken.IsCancellationRequested && Volatile.Read(ref _identityLost) is null)
        {
            // A one-shot run does not wait for the next tick: what its last job made possible (a re-import whose
            // parses have ended) is finished before it leaves.
            await _lifecycle.RunPendingAsync(cancellationToken).ConfigureAwait(false);
            // A re-import the line above has just closed: its case can have its statistics only now.
            while (!cancellationToken.IsCancellationRequested && await TryRunStatsAsync(cancellationToken).ConfigureAwait(false))
            {
            }
        }

        if (Volatile.Read(ref _identityLost) is { } lost)
        {
            throw lost;
        }
    }

    /// <summary>True when the failure is SQLite reporting the database busy or locked, at any depth.</summary>
    internal static bool IsBusy(Exception? e) => BusyRetry.IsBusy(e);

    /// <summary>True when the event store refused for memory (ClickHouse 241, MEMORY_LIMIT_EXCEEDED), at any depth.</summary>
    internal static bool IsMemoryLimit(Exception? e)
    {
        for (; e is not null; e = e.InnerException)
        {
            if (e is Urlog.Core.Ingest.Native.ClickHouseInsertException { Code: 241 })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The message stored on a failed job: the reported exception's own message, then what caused it.</summary>
    internal static string Describe(Exception e)
    {
        var text = new StringBuilder(e.Message);
        var depth = 0;
        for (var inner = e.InnerException; inner is not null && depth < 4; inner = inner.InnerException, depth++)
        {
            if (!text.ToString().Contains(inner.Message, StringComparison.Ordinal))
            {
                text.Append("; caused by ").Append(inner.GetType().Name).Append(": ").Append(inner.Message);
            }
        }

        return text.ToString();
    }

    /// <summary>Waits until the event store answers the ingest account, then reads its identity (null for none).</summary>
    private async Task<(bool Answered, string? StoreId)> WaitForStoreAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await _events.RequireSchemaAsync(cancellationToken).ConfigureAwait(false);
                return (true, await _events.ReadStoreIdentityAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return (false, null);
            }
            catch (Exception e) when (!_options.ExitWhenIdle && e is not StoreIdentityException)
            {
                // The backend keeps serving while the event store is away or not migrated yet; nothing is claimed until it answers.
                JobLog.StoreNotReady(_logger, e.Message, _options.StoreRetryInterval.TotalSeconds);
            }

            try
            {
                await Task.Delay(_options.StoreRetryInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return (false, null);
            }
        }
    }

    /// <summary>
    /// The store at this address may be replaced while the runner lives (a container recreated on another volume).
    /// Compared again on every maintenance tick: a different identity stops the claims, and a job that is running
    /// stops before its commit, in <c>parsing</c>, with no further write to the store.
    /// </summary>
    private async Task RecheckIdentityAsync(CancellationTokenSource fatal, CancellationToken cancellationToken)
    {
        var storeId = await _events.ReadStoreIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (string.Equals(storeId, _identity, StringComparison.Ordinal))
        {
            return;
        }

        var lost = new StoreIdentityException(
            $"the event store {_events.Endpoint} now has store identity {StoreIdentity.Show(storeId)}, and this runner started against {_identity} for metadata file {_store.Path}: the store was replaced while the runner was up. The runner stops; nothing more is written.",
            _identity,
            storeId);
        if (Interlocked.CompareExchange(ref _identityLost, lost, null) is null)
        {
            JobLog.IdentityLost(_logger, lost.Message);
            await fatal.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task SlotLoopAsync(int slot, CancellationTokenSource fatal, CancellationToken stopping)
    {
        var worker = string.Create(CultureInfo.InvariantCulture, $"{_options.WorkerName}:{slot}");
        var buffers = new PipelineBuffers(_options.Pipeline);
        var token = fatal.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                ClaimedJob? claimed = null;
                var backoff = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), Interlocked.Read(ref _claimNotBefore));
                if (backoff > TimeSpan.Zero)
                {
                    // The event store refused an insert for memory a moment ago: nobody claims until it has had time.
                    try
                    {
                        await Task.Delay(backoff, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    continue;
                }

                if (!Paused)
                {
                    try
                    {
                        await using var db = _store.CreateDbContext();
                        claimed = await JobOperations.ClaimAsync(db, worker, _store.Clock, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception e) when (IsBusy(e))
                    {
                        // Another writer held the lock past the busy timeout; the queue is still there next time round.
                    }
                }

                if (claimed is null && !Paused && await TryRunStatsAsync(token).ConfigureAwait(false))
                {
                    continue;
                }

                if (claimed is null)
                {
                    if (_options.ExitWhenIdle && Running == 0 && !Paused)
                    {
                        return;
                    }

                    if (_options.ExitWhenIdle && Paused && Running == 0)
                    {
                        JobLog.PausedExit(_logger);
                        return;
                    }

                    try
                    {
                        await Task.Delay(_options.IdlePoll, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    continue;
                }

                Interlocked.Increment(ref _running);
                try
                {
                    await ExecuteAsync(claimed, worker, buffers, stopping).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref _running);
                    if (buffers.Chunks.Outstanding != 0 || buffers.Batches.Outstanding != 0)
                    {
                        // A job left buffers out: never hand a short pool to the next file.
                        JobLog.PoolsUnbalanced(_logger, claimed.Job.Id, buffers.Chunks.Outstanding, buffers.Batches.Outstanding);
                        buffers.Batches.Dispose();
                        buffers = new PipelineBuffers(_options.Pipeline);
                    }
                }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            JobLog.SlotStopped(_logger, slot, e);
            await fatal.CancelAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            buffers.Batches.Dispose();
        }
    }

    /// <summary>
    /// The field statistics of one case that is due, when the queue has nothing for this slot. One run at a time
    /// in the process, counted as running, so the integrity sweep waits for it as it does for a job. True when a
    /// run took place, whatever its result.
    /// </summary>
    private async Task<bool> TryRunStatsAsync(CancellationToken token)
    {
        var stats = _options.Stats;
        if (stats.Fields.Count == 0 || Interlocked.CompareExchange(ref _statsBusy, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            StatsClaim? claim;
            try
            {
                await using var db = _store.CreateDbContext();
                claim = await CaseStatsOperations.NextDueAsync(db, Now(), _options.ExitWhenIdle ? TimeSpan.Zero : stats.QuietPeriod, token).ConfigureAwait(false);
            }
            catch (Exception e) when (IsBusy(e) || (e is OperationCanceledException && token.IsCancellationRequested))
            {
                return false;
            }

            if (claim is null)
            {
                return false;
            }

            Interlocked.Increment(ref _running);
            try
            {
                return await RunStatsAsync(claim, stats, token).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
        finally
        {
            Volatile.Write(ref _statsBusy, 0);
        }
    }

    private async Task<bool> RunStatsAsync(StatsClaim claim, FieldStatsOptions stats, CancellationToken token)
    {
        var caseId = (ulong)claim.CaseId;
        var clock = Stopwatch.StartNew();
        var state = CaseStatsState.Ok;
        string? message = null;
        var computedAt = claim.LastComputedAt;
        long events = 0;
        try
        {
            if (claim.Rows > stats.MaxCaseRows)
            {
                // Not read at all. An older set would describe a smaller case, so it goes.
                await _events.DeleteCaseFieldStatsAsync(caseId, token).ConfigureAwait(false);
                state = CaseStatsState.Skipped;
                message = string.Create(CultureInfo.InvariantCulture, $"the case holds {claim.Rows:N0} events, more than the {stats.MaxCaseRows:N0} the field statistics are computed for");
                computedAt = null;
            }
            else
            {
                // Later than the set it replaces whatever the clock says: the view shows the newest set.
                var at = Now();
                if (claim.LastComputedAt is { } last && at <= last)
                {
                    at = last.AddMilliseconds(1);
                }

                var set = await _events.RefreshFieldStatsAsync(caseId, stats.Fields, stats.Limits, at, token).ConfigureAwait(false);
                computedAt = new DateTime(at.Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
                events = (long)set.Events;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A stop: nothing is recorded, the mark stays, the next start runs it again.
            return false;
        }
        catch (FieldStatsTimeoutException e)
        {
            state = CaseStatsState.TimedOut;
            message = e.Message;
        }
        catch (Exception e)
        {
            state = CaseStatsState.Failed;
            message = Describe(e);
        }

        try
        {
            await new BusyRetry { Timeout = _options.CommitBusyTimeout }.RunAsync(
                async () =>
                {
                    await using var db = _store.CreateDbContext();
                    await CaseStatsOperations.RecordAsync(db, claim, state, message, computedAt, events, clock.Elapsed, _store.Clock, CancellationToken.None).ConfigureAwait(false);
                    return true;
                },
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        catch (MetadataBusyException)
        {
            // The mark stays, so the run is repeated; this slot goes back to the queue first.
            return false;
        }

        if (state == CaseStatsState.Ok)
        {
            JobLog.StatsDone(_logger, claim.CaseId, events, clock.Elapsed.TotalSeconds);
        }
        else
        {
            JobLog.StatsNotDone(_logger, claim.CaseId, Names.Of(state), message ?? string.Empty);
        }

        return true;
    }

    private async Task ExecuteAsync(ClaimedJob claimed, string worker, PipelineBuffers buffers, CancellationToken stopping)
    {
        var job = claimed.Job;
        var file = claimed.File;
        var caseId = (ulong)job.CaseId;
        var fileId = (ulong)file.Id;
        var generation = (uint)job.Generation;
        var committed = (uint)claimed.CommittedGeneration;
        var progress = new PipelineProgress();
        var total = Stopwatch.StartNew();
        try
        {
            if (job.Kind == JobKind.Expand)
            {
                await ExpandAsync(claimed, worker, stopping).ConfigureAwait(false);
                return;
            }

            // The event store may already show a generation at or above the one SQLite allocated: the id belonged to
            // a purged file, or this metadata file is older than the store. The attempt goes above it, so its rows
            // are never the visible ones before its own commit.
            var mirrored = await _events.ReadFileGenerationAsync(fileId, stopping).ConfigureAwait(false);
            if (mirrored >= generation)
            {
                generation = mirrored + 1;
                var raised = (int)generation;
                await TerminalAsync(job.Id, db => JobOperations.RaiseGenerationAsync(db, job.Id, worker, raised, CancellationToken.None)).ConfigureAwait(false);
                job.Generation = raised;
            }

            JobLog.Started(_logger, job.Id, file.Id, job.CaseId, job.Generation, job.Attempt, worker);
            var source = ResolveSource(file);

            if (generation > 1)
            {
                // Rows of earlier attempts and of generations that were never committed. The committed parse stays.
                await _events.DeleteFileGenerationsExceptAsync(caseId, fileId, committed, stopping).ConfigureAwait(false);
            }

            // One offset for the file, taken at the file's own time and never from the clock: the same bytes give
            // the same rows whenever they are parsed.
            var zone = file.Timezone ?? claimed.Host?.Timezone;
            var identity = new FileIdentity(caseId, (ulong)(file.HostId ?? 0), fileId, generation, claimed.Host?.Name ?? string.Empty,
                ZoneOffset(zone, file.FileTime ?? file.ReceivedAt), file.YearHint ?? 1970, ParseOptions(file.OverrideOptions), file.FileTime);
            var compression = file.Compression is { Length: > 0 } c ? Names.Parse<CompressionKind>(c) : CompressionKind.None;
            var encoding = Names.Parse<InputEncoding>(file.Encoding);

            PipelineResult result;
            InputEnd inputEnd;
            DateTime? tsMin;
            DateTime? tsMax;
            long undated;
            long future;
            long acked;
            string sha256;
            long hashedBytes;
            TimeSpan hashTime;
            TimeSpan readTime;
            var sink = _events.OpenSink(_options.Pipeline);
            await using (sink.ConfigureAwait(false))
            {
                // The file's own bytes, read-only: its stored file, or its place inside the container it came in.
                using var raw = OpenRaw(file);
                using var hashing = new HashingReadStream(raw);
                using var decompressed = InputDecoding.OpenChecked(hashing, compression);
                // A compressed log is held to the same expansion ratio and size as an archive.
                using var limited = compression == CompressionKind.None ? (Stream)decompressed : new LimitedDecodeStream(decompressed, _options.Archive, file.Size);
                using var decoded = InputDecoding.Transcode(limited, encoding);
                // Times later than the file can have been written do not count toward its span.
                var ranged = new TimeRangeSink(sink, EventTime.CeilingMilliseconds(file.ReceivedAt));
                using var progressStop = new CancellationTokenSource();
                var progressWriter = Task.Run(() => ProgressLoopAsync(job.Id, progress, sink, progressStop.Token), CancellationToken.None);
                try
                {
                    result = await new ParsePipeline(buffers, _loggers.CreateLogger<ParsePipeline>()).RunAsync(decoded, source, identity, ranged, progress, stopping).ConfigureAwait(false);
                }
                finally
                {
                    await progressStop.CancelAsync().ConfigureAwait(false);
                    await progressWriter.ConfigureAwait(false);
                }

                acked = sink.AckedRows;
                // The pipeline read to the end of the decoded stream, so the decoder has said how the stored bytes ended.
                inputEnd = decompressed.End;
                readTime = decompressed.ReadTime;
                tsMin = ranged.Min;
                tsMax = ranged.Max;
                undated = ranged.Undated;
                future = ranged.Future;
                // A decoder may stop short of the end of the file; the digest is over every byte of it.
                var scratch = new byte[1 << 20];
                sha256 = hashing.DrainAndFinish(scratch);
                hashedBytes = hashing.BytesHashed;
                hashTime = hashing.HashTime;
            }

            if (!string.Equals(sha256, file.Sha256, StringComparison.Ordinal) || hashedBytes != file.Size)
            {
                await _events.DeleteFileGenerationsExceptAsync(caseId, fileId, committed, CancellationToken.None).ConfigureAwait(false);
                var mismatch = new IntegrityMismatch(sha256, hashedBytes);
                await TerminalAsync(job.Id, db => JobOperations.QuarantineAsync(db, _store.Audit, job.Id, worker, mismatch, _store.Clock, CancellationToken.None)).ConfigureAwait(false);
                JobLog.Quarantined(_logger, job.Id, file.Id, file.Sha256, sha256);
                return;
            }

            if (acked != result.Rows)
            {
                throw new JobFailedException($"the event store acknowledged {acked} of the {result.Rows} rows the parsers produced");
            }

            if (Volatile.Read(ref _identityLost) is { } lost)
            {
                // Not committed and not cleaned: the store these rows went to is not this file's.
                throw lost;
            }

            var counts = new JobCounts(progress.ChunksRead, progress.ChunksParsed, result.Rows, result.Rejects, result.Bytes);
            var spansDst = source.Info.NaiveTimestamps && tsMin is { } first && tsMax is { } last && IntakeHints.SpansTransition(zone, first, last);
            var outcome = new JobOutcome(inputEnd, tsMin, tsMax, spansDst, undated, future);
            await TerminalAsync(job.Id, db => JobOperations.CompleteAsync(db, _store.Audit, job.Id, worker, counts, outcome, _store.Clock, CancellationToken.None)).ConfigureAwait(false);
            try
            {
                await _events.CommitFileGenerationsAsync([new(fileId, generation)], CancellationToken.None).ConfigureAwait(false);
                if (generation > 1)
                {
                    await _events.DeleteFileGenerationsBelowAsync(caseId, fileId, generation, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                // SQLite is the truth and already says done; the sync writes the event store's row as soon as it answers.
                Interlocked.Exchange(ref _syncNeeded, 1);
                JobLog.CommitNotMirrored(_logger, job.Id, file.Id, e.Message);
            }

            // The decoder's reads include the stored-bytes read and the hash under it; what is left is the decoding.
            var compressionName = Names.Of(compression);
            var decodeSeconds = Math.Max(0, (readTime - hashTime).TotalSeconds);
            JobLog.Input(_logger, job.Id, hashedBytes, compressionName, readTime.TotalSeconds, hashTime.TotalSeconds, decodeSeconds);
            var seconds = result.Elapsed.TotalSeconds;
            JobLog.Done(_logger, job.Id, file.Id, job.Generation, result.Rows, result.Rejects, result.Bytes, seconds,
                seconds > 0 ? result.Bytes / 1e6 / seconds : 0, hashTime.TotalSeconds, total.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // A graceful stop: the attempt's rows go, the job waits for the next start. If either step fails the job
            // stays in parsing and the next start's reconciler does the same thing.
            try
            {
                await _events.DeleteFileGenerationsExceptAsync(caseId, fileId, committed, CancellationToken.None).ConfigureAwait(false);
                await using var db = _store.CreateDbContext();
                await JobOperations.RequeueAsync(db, job.Id, "interrupted by a shutdown of the worker; queued again", countAttempt: false, _options.MaxAttempts, _store.Clock, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                JobLog.StopNotClean(_logger, job.Id, e.Message);
            }
        }
        catch (JobCommitException)
        {
            throw;
        }
        catch (StoreIdentityException)
        {
            // The job stays in parsing; the next start is refused until the pair is right, and recovers it then.
            throw;
        }
        catch (Exception e) when (IsMemoryLimit(e))
        {
            // The server ran out of memory for this insert (241). That passes: the attempt's rows go, the job waits
            // in the queue, and nothing is claimed for a while. It counts as an attempt, so it cannot repeat for ever.
            var message = Describe(e);
            Interlocked.Exchange(ref _claimNotBefore, Stopwatch.GetTimestamp() + (long)(_options.MemoryLimitBackoff.TotalSeconds * Stopwatch.Frequency));
            try
            {
                await _events.DeleteFileGenerationsExceptAsync(caseId, fileId, committed, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception deleteFailure)
            {
                JobLog.DeleteAfterFailure(_logger, job.Id, deleteFailure.Message);
            }

            var outcome = RequeueOutcome.NotParsing;
            var reason = $"the event store refused the insert for memory and the job was queued again: {message}";
            await TerminalAsync(job.Id, async db => outcome = await JobOperations.RequeueAsync(db, job.Id, reason, countAttempt: true, _options.MaxAttempts, _store.Clock, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            JobLog.MemoryLimit(_logger, job.Id, file.Id, job.Attempt, outcome.ToString(), _options.MemoryLimitBackoff.TotalSeconds);
        }
        catch (Exception e)
        {
            var message = Describe(e);
            JobLog.Failed(_logger, job.Id, file.Id, message);
            try
            {
                await _events.DeleteFileGenerationsExceptAsync(caseId, fileId, committed, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception deleteFailure)
            {
                // The rows carry a generation that is never committed, so they stay invisible; the next attempt on the file removes them.
                JobLog.DeleteAfterFailure(_logger, job.Id, deleteFailure.Message);
            }

            await TerminalAsync(job.Id, db => JobOperations.FailAsync(db, job.Id, worker, message, _store.Clock, CancellationToken.None)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A write that ends a job. While another writer holds the SQLite lock it is tried again until
    /// <see cref="JobRunnerOptions.CommitBusyTimeout"/>; past that the runner stops with the job still in
    /// <c>parsing</c>, and the next start recovers it.
    /// </summary>
    private async Task TerminalAsync(long jobId, Func<MetadataDb, Task> write)
    {
        try
        {
            await new BusyRetry { Timeout = _options.CommitBusyTimeout }.RunAsync(
                async () =>
                {
                    await using var db = _store.CreateDbContext();
                    await write(db).ConfigureAwait(false);
                    return true;
                },
                () => JobLog.CommitWaiting(_logger, jobId)).ConfigureAwait(false);
        }
        catch (MetadataBusyException e)
        {
            throw new JobCommitException($"job {jobId}: the metadata store stayed locked for {e.Waited.TotalSeconds:F0} s; the job is left in parsing for the next start", e.InnerException ?? e);
        }
    }

    private async Task ProgressLoopAsync(long jobId, PipelineProgress progress, IJobSink sink, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(_options.ProgressInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                var acked = sink.AckedRows;
                var parsed = progress.ChunksParsed;
                var rows = progress.Rows;
                // The store acknowledges INSERTs, not chunks: the chunk figure is the acknowledged share of the parsed chunks.
                var chunksAcked = rows == 0 ? 0 : (long)(parsed * (double)Math.Min(acked, rows) / rows);
                try
                {
                    await using var db = _store.CreateDbContext();
                    await JobOperations.WriteProgressAsync(db, jobId, progress.ChunksRead, parsed, chunksAcked, acked, progress.Rejects, progress.BytesRead, stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    // Advisory: a busy store costs one tick of the progress bar, never the job.
                    JobLog.ProgressSkipped(_logger, jobId, e.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task MaintenanceLoopAsync(CancellationTokenSource fatal, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(_options.WatermarkInterval);
        DateOnly? lastBackup = null;
        try
        {
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                try
                {
                    await RecheckIdentityAsync(fatal, stop).ConfigureAwait(false);
                    if (Volatile.Read(ref _identityLost) is not null)
                    {
                        return;
                    }

                    await CheckWatermarkAsync(stop).ConfigureAwait(false);
                    if (Interlocked.Exchange(ref _syncNeeded, 0) == 1)
                    {
                        try
                        {
                            await new StartupReconciler(_store, _events, _options, _logger).SyncGenerationsAsync(stop).ConfigureAwait(false);
                        }
                        catch
                        {
                            Interlocked.Exchange(ref _syncNeeded, 1);
                            throw;
                        }
                    }

                    lastBackup = await NightlyBackupAsync(lastBackup, stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    JobLog.MaintenanceFailed(_logger, e.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// The lifecycle's own work beside the queue: purges that are due, released or interrupted, and the
    /// integrity sweep. The sweep reads only while no slot runs a job, and gives way to a purge that waits.
    /// </summary>
    private async Task LifecycleLoopAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(_options.Lifecycle.Interval);
        DateTime? sweptFrom = null;
        try
        {
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                if (Volatile.Read(ref _identityLost) is not null)
                {
                    return;
                }

                try
                {
                    await _lifecycle.RunPendingAsync(stop).ConfigureAwait(false);
                    // One sweep per scheduled instant; one that was stopped is taken up again on the next tick.
                    if (IntegritySweep.DueFrom(_options.Lifecycle.SweepAtUtc, Now()) is { } dueFrom && dueFrom != sweptFrom && Running == 0)
                    {
                        var gate = new SweepGate(this, stop);
                        var report = await _sweep.RunAsync(dueFrom, gate.Ask, stop).ConfigureAwait(false);
                        LastSweep = report;
                        sweptFrom = report.Complete ? dueFrom : sweptFrom;
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    LifecycleLog.Failed(_logger, e.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>What the sweep is told each time it asks whether it may read.</summary>
    private sealed class SweepGate(JobRunner runner, CancellationToken stop)
    {
        private bool _purgeWaits;
        private int _asked;

        public SweepSignal Ask()
        {
            if (stop.IsCancellationRequested || Volatile.Read(ref runner._identityLost) is not null)
            {
                return SweepSignal.Stop;
            }

            // The queue first: while a slot has a job the disk is its.
            if (runner.Running > 0)
            {
                return SweepSignal.Wait;
            }

            // A purge released while the sweep runs is looked for on every 8th question.
            if (!_purgeWaits && _asked++ % 8 == 7)
            {
                _purgeWaits = runner._lifecycle.HasPendingAsync(stop).GetAwaiter().GetResult();
            }

            return _purgeWaits ? SweepSignal.Stop : SweepSignal.Go;
        }
    }

    private async Task CheckWatermarkAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<DiskSpace> storeDisks = [];
        try
        {
            storeDisks = await _events.DiskSpaceAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            JobLog.StoreDisksUnread(_logger, e.Message);
        }

        var reading = _watermark.Read(storeDisks);
        await using var db = _store.CreateDbContext();
        if (reading.Paused)
        {
            var reason = reading.Describe();
            if (!Paused)
            {
                await JobOperations.PauseAsync(db, _store.Audit, reason, reading.Details(), cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _paused, 1);
                JobLog.Paused(_logger, reason);
            }
            else
            {
                // Jobs queued since the pause began say why they wait; no second audit row.
                await db.Jobs.Where(j => j.State == JobState.Queued && j.Message == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.Message, JobOperations.PausedPrefix + reason), cancellationToken).ConfigureAwait(false);
            }
        }
        else if (Paused)
        {
            var lowest = reading.Volumes.OrderBy(v => v.FreeBytes - _watermark.MarkFor(v)).First();
            var details = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["volume"] = lowest.Name,
                ["free_bytes"] = lowest.FreeBytes,
                ["total_bytes"] = lowest.TotalBytes,
                ["mark_bytes"] = _watermark.MarkFor(lowest),
            };
            await JobOperations.ResumeAsync(db, _store.Audit, details, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _paused, 0);
            JobLog.Resumed(_logger, lowest.Name, lowest.FreeBytes);
        }
    }

    private async Task<DateOnly?> NightlyBackupAsync(DateOnly? lastBackup, CancellationToken cancellationToken)
    {
        if (_options.NightlyBackupAtUtc is not { } at)
        {
            return lastBackup;
        }

        var now = _store.Clock.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);
        if (lastBackup is null)
        {
            // The newest nightly row in the chain says whether today's was taken before a restart.
            await using var db = _store.CreateDbContext();
            var newest = await db.Audit.AsNoTracking().Where(a => a.Action == AuditActions.Backup && a.DetailsJson.Contains("\"reason\":\"" + SqliteBackup.ReasonNightly + "\""))
                .OrderByDescending(a => a.Seq).Select(a => (DateTime?)a.TsUtc).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            lastBackup = newest is { } ts ? DateOnly.FromDateTime(ts) : DateOnly.MinValue;
        }

        if (lastBackup >= today || TimeOnly.FromDateTime(now) < at)
        {
            return lastBackup;
        }

        var directory = _options.BackupDirectory ?? SqliteBackup.DefaultDirectory(_store.Path);
        var backup = await SqliteBackup.TakeAsync(_store, directory, SqliteBackup.ReasonNightly, AuditActions.SystemActor, null, cancellationToken).ConfigureAwait(false);
        JobLog.BackupTaken(_logger, backup.Path, backup.Sha256);
        return today;
    }

    private ILogSource ResolveSource(EvidenceFile file)
    {
        // A file always resolves: the override, what the detector chose, or the generic source for a row that
        // carries neither (written before detection recorded a fallback).
        var id = file.OverrideSource ?? file.DetectedSource;
        if (string.IsNullOrWhiteSpace(id))
        {
            id = FormatDetector.GenericSourceId;
        }

        if (!_sources.TryGet(id, out _))
        {
            throw new JobFailedException($"no log source called '{id}' is registered in this build");
        }

        return _sources.Create(id);
    }

    private Stream OpenRaw(EvidenceFile file)
    {
        try
        {
            return _members.OpenRaw(file, LoadFile);
        }
        catch (Exception e) when (e is FileNotFoundException or InvalidOperationException or InvalidDataException)
        {
            throw new JobFailedException(e.Message, e);
        }
    }

    private EvidenceFile LoadFile(long id)
    {
        using var db = _store.CreateDbContext();
        return db.Files.AsNoTracking().FirstOrDefault(f => f.Id == id) ?? throw new JobFailedException($"file {id}, the container of this member, has no record");
    }

    /// <summary>
    /// One <c>expand</c> job: the container is verified against its record, its members are recorded with their
    /// own jobs, and the container becomes <c>expanded</c>. It writes no events, so there is no generation to
    /// commit and nothing in the event store to remove when it fails.
    /// </summary>
    private async Task ExpandAsync(ClaimedJob claimed, string worker, CancellationToken stopping)
    {
        var job = claimed.Job;
        var file = claimed.File;
        JobLog.Started(_logger, job.Id, file.Id, job.CaseId, job.Generation, job.Attempt, worker);
        ExpandOutcome outcome;
        try
        {
            // Limits an administrator set for this one run take the place of the configured ones.
            var limits = ArchiveLimits.FromJson(job.Options, _options.Archive);
            outcome = await _expander.RunAsync(file, limits, stopping).ConfigureAwait(false);
        }
        catch (CacheSpaceException e)
        {
            // Not the archive's fault: the job waits, the watermark holds the queue until there is room.
            await TerminalAsync(job.Id, db => JobOperations.RequeueAsync(db, job.Id, e.Message, countAttempt: false, _options.MaxAttempts, _store.Clock, CancellationToken.None)).ConfigureAwait(false);
            await CheckWatermarkAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (outcome.Mismatch is { } mismatch)
        {
            await TerminalAsync(job.Id, db => JobOperations.QuarantineAsync(db, _store.Audit, job.Id, worker, mismatch, _store.Clock, CancellationToken.None)).ConfigureAwait(false);
            JobLog.Quarantined(_logger, job.Id, file.Id, file.Sha256, mismatch.ActualSha256);
            return;
        }

        await TerminalAsync(job.Id, db => JobOperations.CompleteExpandAsync(db, _store.Audit, job.Id, worker, outcome.Entries, outcome.Summary, outcome.Truncated, outcome.CachePath, _store.Clock, CancellationToken.None)).ConfigureAwait(false);
    }

    private static TimeSpan ZoneOffset(string? zone, DateTime atUtc)
    {
        try
        {
            return IntakeHints.OffsetAt(zone, atUtc);
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new JobFailedException($"unknown timezone '{zone}' on the file or its host; use an IANA id or an offset like +02:00", e);
        }
    }

    private static Dictionary<string, string>? ParseOptions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                options[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : property.Value.GetRawText();
            }

            return options.Count == 0 ? null : options;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            throw new JobFailedException("override_options is not a JSON object of source options", e);
        }
    }

    private DateTime Now() => _store.Clock.GetUtcNow().UtcDateTime;
}

/// <summary>The job cannot succeed as it stands; the message is what the job row keeps.</summary>
public sealed class JobFailedException : Exception
{
    public JobFailedException()
    {
    }

    public JobFailedException(string message)
        : base(message)
    {
    }

    public JobFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The metadata store would not take a job's final write; the runner stops and the next start recovers the job.</summary>
public sealed class JobCommitException : Exception
{
    public JobCommitException()
    {
    }

    public JobCommitException(string message)
        : base(message)
    {
    }

    public JobCommitException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
