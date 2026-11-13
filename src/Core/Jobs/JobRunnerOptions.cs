using Urlog.Core.Intake.Archives;
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
    /// </summary>
    public TimeSpan CommitBusyTimeout { get; init; } = TimeSpan.FromSeconds(60);

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

    /// <summary>What an archive may expand to; also the ratio and size a single compressed log is held to.</summary>
    public ArchiveLimits Archive { get; init; } = ArchiveLimits.Default;

    /// <summary>Host patterns for archive members, in order of precedence; null uses the built-in list.</summary>
    public IReadOnlyList<string>? HostPatterns { get; init; }

    /// <summary>Where the nightly <c>VACUUM INTO</c> backup goes; null uses the default next to the database.</summary>
    public string? BackupDirectory { get; init; }

    /// <summary>The UTC time of day of the nightly backup; null turns the schedule off (<c>urlog worker --once</c>).</summary>
    public TimeOnly? NightlyBackupAtUtc { get; init; }

    /// <summary>The name written to <c>jobs.worker</c>, without the slot number.</summary>
    public string WorkerName { get; init; } = $"{Environment.MachineName}:{Environment.ProcessId}";

    public void Validate()
    {
        Pipeline.Validate();
        Archive.Validate();
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
