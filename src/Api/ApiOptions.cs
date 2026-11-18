using Urlog.Core.Metadata;

namespace Urlog.Api;

/// <summary>
/// The <c>Urlog</c> configuration section (appsettings.json, <c>Urlog__Auth__...</c> environment, and the CLI's
/// <c>URLOG_*</c> variables through <see cref="UrlogEnvironment"/>). Argon2 parameters are not here on purpose: they
/// are constants of <c>PasswordHasher</c>, and lowering them is a code change with a test to pass.
/// </summary>
public sealed class UrlogOptions
{
    public const string Section = "Urlog";

    public MetadataOptions Metadata { get; set; } = new();

    public AuthOptions Auth { get; set; } = new();

    public HttpOptions Http { get; set; } = new();

    public ClickHouseOptions ClickHouse { get; set; } = new();

    public IngestOptions Ingest { get; set; } = new();

    public LifecycleSettings Lifecycle { get; set; } = new();

    public UploadOptions Upload { get; set; } = new();
}

/// <summary>Web upload (<c>/api/v1/cases/{caseId}/uploads</c>). The files land under <see cref="IngestOptions.EvidenceRoot"/>.</summary>
public sealed class UploadOptions
{
    /// <summary>The largest file one upload may declare, in bytes. Default 20 GB.</summary>
    public long MaxFileBytes { get; set; } = 20L << 30;

    /// <summary>An unfinished upload is removed this long after its last byte arrived.</summary>
    public TimeSpan Expiry { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How often expired uploads are removed and interrupted hand-overs to intake are finished.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long the record of a complete upload keeps trying while another writer holds the metadata file, before
    /// the request is answered 503. The job runner's commit waits as long by default (<see cref="BusyRetry"/>).
    /// </summary>
    public TimeSpan HandOverBusyTimeout { get; set; } = BusyRetry.DefaultTimeout;

    /// <summary>How often an upload whose record gave up on a locked metadata file is handed to intake again; also the 503's <c>Retry-After</c>.</summary>
    public TimeSpan HandOverRetry { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Where the event store is and the two accounts the backend holds: the ingest account of its write path
/// (<c>URLOG_CH_HOST</c>, <c>URLOG_CH_TCP_PORT</c>, <c>URLOG_CH_USER</c>, <c>URLOG_CH_PASSWORD</c>) and the
/// read-only query account its reads go through, over HTTP (<c>URLOG_CH_HTTP_PORT</c>, <c>URLOG_CH_QUERY_USER</c>,
/// <c>URLOG_CH_QUERY_PASSWORD</c>). The defaults are the development stack's stubs; a deployment takes the
/// passwords from secrets. Never the admin account.
/// </summary>
public sealed class ClickHouseOptions
{
    public string Host { get; set; } = "127.0.0.1";

    public int NativePort { get; set; } = 9000;

    public string User { get; set; } = "urlog_ingest";

    public string Password { get; set; } = "urlog_ingest";

    public int HttpPort { get; set; } = 8123;

    public string QueryUser { get; set; } = "urlog_query";

    public string QueryPassword { get; set; } = "urlog_query";

    /// <summary>How often the store identity comparison is tried again while it has not passed.</summary>
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>The job runner the backend hosts.</summary>
public sealed class IngestOptions
{
    /// <summary>False: this process ingests nothing (a <c>urlog worker</c> does, or nobody).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Jobs running at once, each in its own case (a case never has two). Every job has its own pooled buffers,
    /// about 1.25 GB at the default sizing.
    /// </summary>
    public int MaxConcurrentJobs { get; set; } = 1;

    /// <summary>Parser threads per job; 0 is min(cores, 8).</summary>
    public int Parallelism { get; set; }

    public int Writers { get; set; } = 4;

    /// <summary>The sizing for an 8 GB machine (about 180 MB of pooled buffers per job) instead of the one above.</summary>
    public bool SmallBox { get; set; }

    /// <summary>
    /// Where evidence files are kept: relative <c>stored_path</c> values resolve under <c>&lt;root&gt;/&lt;case id&gt;/</c>,
    /// uploads land there, and its volume is watched by the watermark. Never inside the web root.
    /// </summary>
    public string EvidenceRoot { get; set; } = "data/intake";

    public TimeSpan ProgressInterval { get; set; } = TimeSpan.FromSeconds(2);

    public IngestWatermarkOptions Watermark { get; set; } = new();

    /// <summary>UTC time of the nightly metadata backup; null turns it off.</summary>
    public TimeOnly? NightlyBackupAtUtc { get; set; } = new TimeOnly(2, 0);

    /// <summary>Where backups go; empty is <c>backups</c> next to the metadata file.</summary>
    public string BackupDirectory { get; set; } = "";

    public FieldStatsSettings Stats { get; set; } = new();
}

/// <summary>
/// <c>Urlog:Ingest:Stats</c>: the per-case field statistics the job runner computes after a case's parses
/// (design record, "Query language", Autocomplete). The defaults are the runner's.
/// </summary>
public sealed class FieldStatsSettings
{
    /// <summary>False: this deployment computes no field statistics; autocomplete offers the catalog alone.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long after a case's last commit a run may start.</summary>
    public TimeSpan QuietPeriod { get; set; } = new Urlog.Core.Jobs.FieldStatsOptions().QuietPeriod;

    /// <summary>For one run. A run that passes it writes nothing and the case says so.</summary>
    public TimeSpan TimeLimit { get; set; } = new Urlog.Core.Jobs.FieldStatsOptions().TimeLimit;

    public int MaxThreads { get; set; } = new Urlog.Core.Jobs.FieldStatsOptions().MaxThreads;

    public long MaxMemoryBytes { get; set; } = new Urlog.Core.Jobs.FieldStatsOptions().MaxMemoryBytes;

    /// <summary>A case with more events than this gets no statistics, and says why.</summary>
    public long MaxCaseRows { get; set; } = new Urlog.Core.Jobs.FieldStatsOptions().MaxCaseRows;
}

/// <summary><c>Urlog:Lifecycle</c>: the purge window and approval, and the integrity sweep (design record, "Case lifecycle").</summary>
public sealed class LifecycleSettings
{
    /// <summary>A purge needs two different administrators, and nothing is purged on schedule.</summary>
    public bool TwoPersonPurge { get; set; }

    /// <summary>How long a soft-deleted case can be restored.</summary>
    public TimeSpan PurgeAfter { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Purge soft-deleted cases whose window has passed, as <c>system</c>.</summary>
    public bool ScheduledPurge { get; set; } = true;

    /// <summary>Where archives are written, one directory per case; empty when this deployment archives nothing. Apart from the evidence root.</summary>
    public string ArchiveRoot { get; set; } = "";

    /// <summary>UTC time from which the nightly integrity sweep reads every stored file; null turns it off.</summary>
    public TimeOnly? SweepAtUtc { get; set; } = new TimeOnly(3, 0);
}

/// <summary>Ingest pauses while any watched volume has less free space than the larger of the two marks.</summary>
public sealed class IngestWatermarkOptions
{
    public long MinFreeBytes { get; set; } = 20L << 30;

    public int MinFreePercent { get; set; } = 10;

    /// <summary>Further local paths whose volumes are watched, beside the metadata directory, the evidence root and the event store's disks.</summary>
    public IList<string> Paths { get; } = [];
}

public sealed class MetadataOptions
{
    /// <summary>
    /// The SQLite metadata file, created by <c>urlog migrate --db</c> and migrated at startup once its store identity
    /// matches the event store's. <c>URLOG_DB</c> overrides it.
    /// </summary>
    public string Path { get; set; } = "data/metadata.db";
}

public sealed class AuthOptions
{
    /// <summary>A session ends when no request has touched it for this long.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromHours(8);

    /// <summary>A session ends this long after login whatever the activity.</summary>
    public TimeSpan AbsoluteTimeout { get; set; } = TimeSpan.FromHours(24);

    /// <summary><c>last_seen_at</c> is written at most this often.</summary>
    public TimeSpan SessionTouchInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Consecutive failed logins that lock the account.</summary>
    public int LockoutThreshold { get; set; } = 10;

    /// <summary>How long a lockout lasts unless an admin ends it earlier.</summary>
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Argon2 verifies and hashes in flight at once: bounds memory to this x 64 MiB whatever the number of clients.</summary>
    public int MaxConcurrentHashes { get; set; } = 4;

    public LoginThrottleOptions LoginThrottle { get; set; } = new();

    public LockoutPolicy Lockout => new(LockoutThreshold, LockoutDuration);

    public SessionPolicy Sessions => new(IdleTimeout, AbsoluteTimeout);
}

/// <summary>The per-address window on <c>POST /api/v1/auth/login</c>: more than <see cref="Attempts"/> in <see cref="Window"/> is 429.</summary>
public sealed class LoginThrottleOptions
{
    public int Attempts { get; set; } = 5;

    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
}

public sealed class HttpOptions
{
    /// <summary>
    /// Addresses (<c>10.0.0.1</c>) or networks (<c>10.0.0.0/8</c>) whose <c>X-Forwarded-For</c> is believed. Empty: the
    /// forwarded-headers middleware is not added and the socket address is the client address.
    /// </summary>
    public IList<string> TrustedProxies { get; } = [];
}
