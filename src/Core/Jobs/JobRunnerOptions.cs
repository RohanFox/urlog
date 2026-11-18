using Urlog.Core.Intake.Archives;
using Urlog.Core.Lifecycle;
using Urlog.Core.Metadata;
using Urlog.Core.Pipeline;

namespace Urlog.Core.Jobs;

/// <summary>How the job runner runs. The defaults are the backend's; <c>urlog worker</c> and the tests override a few.</summary>
public sealed record JobRunnerOptions
{
    /// <summary>
    /// Jobs running at once, each in its own case (a case never has two). Every slot owns a full set of pipeline
    /// buffers for the life of the process, so pooled memory is this many times
    /// <see cref="PipelineOptions.EstimatePoolBytes"/>.
    /// </summary>
    public int MaxConcurrentJobs { get; init; } = 1;

    /// <summary>The pipeline sizing of every job (<see cref="PipelineOptions.SmallBox"/> for an 8 GB machine).</summary>
    public PipelineOptions Pipeline { get; init; } = PipelineOptions.Default;

    /// <summary>How often a running job's counters are written to SQLite. Advisory; nothing resumes from them.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long an idle slot waits before it looks at the queue again.</summary>
    public TimeSpan IdlePoll { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How often the disk watermark is read (and before the first claim).</summary>
    public TimeSpan WatermarkInterval { get; init; } = TimeSpan.FromSeconds(30);

    public WatermarkOptions Watermark { get; init; } = new();

    /// <summary>Unclean stops a job may see before it is failed.</summary>
    public int MaxAttempts { get; init; } = JobOperations.DefaultMaxAttempts;

    /// <summary>
    /// How long a terminal write (commit, failure, quarantine) keeps retrying while another writer holds the SQLite
    /// lock. The store's own busy timeout is 5 s; a parse that took twenty minutes must not fail on that.
    /// The policy is <see cref="BusyRetry"/>, shared with the intake record of a web upload.
    /// </summary>
    public TimeSpan CommitBusyTimeout { get; init; } = BusyRetry.DefaultTimeout;

    /// <summary>
    /// After the event store refuses an insert for memory (code 241) the job goes back to the queue and no slot
    /// claims anything for this long, so the server's merges and other inserts can release what they hold.
    /// </summary>
    public TimeSpan MemoryLimitBackoff { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a starting runner keeps trying for the runner lock before it reports another runner. Covers the
    /// moment between a killed runner's exit and the release of its handle.
    /// </summary>
    public TimeSpan LockWait { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long the runner waits between attempts to reach the event store at startup.</summary>
    public TimeSpan StoreRetryInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Return once the queue is empty and nothing runs (<c>urlog worker --once</c>, tests). An unreachable event store is then an error, not a wait.</summary>
    public bool ExitWhenIdle { get; init; }

    /// <summary>Evidence paths in <c>files.stored_path</c> that are relative resolve against <c>&lt;root&gt;/&lt;case id&gt;/</c>.</summary>
    public string? EvidenceRoot { get; init; }

    /// <summary>
    /// Where the seekable copies of containers go (a compressed tar, an archive inside an archive). Derived data,
    /// outside the evidence root; null puts it beside the root as <c>&lt;root&gt;-cache</c>.
    /// </summary>
    public string? CacheRoot { get; init; }

    /// <summary>Where the archives of archived cases are written, one directory per case; null when this runner archives nothing. Apart from the evidence root and the cache root.</summary>
    public string? ArchiveRoot { get; init; }

    /// <summary>What an archive may expand to; also the ratio and size a single compressed log is held to.</summary>
    public ArchiveLimits Archive { get; init; } = ArchiveLimits.Default;

    /// <summary>Host patterns for archive members, in order of precedence; null uses the built-in list.</summary>
    public IReadOnlyList<string>? HostPatterns { get; init; }

    /// <summary>Where the nightly <c>VACUUM INTO</c> backup goes; null uses the default next to the database.</summary>
    public string? BackupDirectory { get; init; }

    /// <summary>The UTC time of day of the nightly backup; null turns the schedule off (<c>urlog worker --once</c>).</summary>
    public TimeOnly? NightlyBackupAtUtc { get; init; }

    /// <summary>The purge window and approval, and the integrity sweep's schedule.</summary>
    public LifecycleOptions Lifecycle { get; init; } = new();

    /// <summary>The field statistics the runner computes after a case's parses; without fields it computes none.</summary>
    public FieldStatsOptions Stats { get; init; } = new();

    /// <summary>The name written to <c>jobs.worker</c>, without the slot number.</summary>
    public string WorkerName { get; init; } = $"{Environment.MachineName}:{Environment.ProcessId}";

    public void Validate()
    {
        Pipeline.Validate();
        Archive.Validate();
        Lifecycle.Validate();
        // Throws when the archive root is, holds or lies inside the evidence root or the cache root.
        new Urlog.Core.Lifecycle.LifecyclePaths(EvidenceRoot, CacheRoot, BackupDirectory ?? string.Empty, ArchiveRoot).CheckedArchiveRoot();
        Stats.Validate();
        if (MaxConcurrentJobs < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrentJobs), MaxConcurrentJobs, "must be positive");
        }

        if (MaxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts), MaxAttempts, "must be positive");
        }

        if (MemoryLimitBackoff < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MemoryLimitBackoff), MemoryLimitBackoff, "cannot be negative");
        }

        if (LockWait < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(LockWait), LockWait, "cannot be negative");
        }

        if (ProgressInterval <= TimeSpan.Zero || IdlePoll <= TimeSpan.Zero || WatermarkInterval <= TimeSpan.Zero || CommitBusyTimeout <= TimeSpan.Zero || StoreRetryInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ProgressInterval), "every interval must be positive");
        }

        if (Watermark.MinFreeBytes < 0 || Watermark.MinFreePercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(Watermark), "the watermark is a byte count and a percentage");
        }
    }
}

/// <summary>
/// When and at what cost the runner computes a case's field statistics (design record, "Query language",
/// Autocomplete). A run waits behind the queue: a slot takes it only when it finds no job to claim, the case has no
/// job left to run, and <see cref="QuietPeriod"/> has passed since the case's last commit, so files that arrive one
/// after the other end in one run.
/// </summary>
public sealed record FieldStatsOptions
{
    /// <summary>The fields covered, from the field catalog. Empty: this runner computes no statistics.</summary>
    public IReadOnlyList<Urlog.Core.Ingest.StatsField> Fields { get; init; } = [];

    /// <summary>How long after a case's last commit a run may start. A one-shot runner does not wait.</summary>
    public TimeSpan QuietPeriod { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// For one run, all statements together. A run that passes it writes nothing. Also the longest a file queued
    /// behind a run can wait for its slot. Measured: 10.7 s for 100.8 M rows, linear from 25 M (design record,
    /// Field statistics); the default is 2.25 times the run of a case at <see cref="MaxCaseRows"/>.
    /// </summary>
    public TimeSpan TimeLimit { get; init; } = TimeSpan.FromSeconds(600);

    /// <summary>
    /// Server threads one statement of a run may use: half of the reference box's cores, so a search beside a
    /// run keeps the other half. Measured: 1.28 times the run at 8 threads.
    /// </summary>
    public int MaxThreads { get; init; } = 4;

    /// <summary>
    /// Server memory one statement of a run may use, inside the ingest profile's own bound. Measured peak: 67 MiB;
    /// the bound on distinct values is what keeps the hash tables small.
    /// </summary>
    public long MaxMemoryBytes { get; init; } = 1_000_000_000;

    /// <summary>
    /// A case with more rows than this (the sum of its files' row counts) is not read at all: its statistics are
    /// removed and the case says why. The default covers a syslog case of 300 GB. At the measured rate
    /// (<see cref="MeasuredSecondsPerMillionRows"/>) a case of this size is a run of about 266 s; that is an
    /// extrapolation from 100.8 M rows, 25 times the measured size, not a measurement.
    /// </summary>
    public long MaxCaseRows { get; init; } = 2_500_000_000;

    /// <summary>Seconds of a run per million rows at 4 threads on the development box: 10,726 ms for 100,835,963 rows.</summary>
    public const double MeasuredSecondsPerMillionRows = 0.1064;

    public Urlog.Core.Ingest.FieldStatsLimits Limits => new(TimeLimit, MaxThreads, MaxMemoryBytes);

    public void Validate()
    {
        if (QuietPeriod < TimeSpan.Zero || TimeLimit < TimeSpan.FromSeconds(1) || MaxThreads < 1 || MaxMemoryBytes < 1 || MaxCaseRows < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(TimeLimit), "the statistics limits are positive, and the quiet period is not negative");
        }
    }
}
