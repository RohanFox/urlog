namespace Urlog.Core.Metadata;

/// <summary>Stored as lowercase snake_case text (<see cref="Names.Snake"/>) with a CHECK constraint, on every provider.</summary>
public enum UserRole
{
    Admin,
    Analyst,
}

/// <summary>The case lifecycle of the design record; <see cref="CaseLifecycle"/> holds the allowed transitions.</summary>
public enum CaseState
{
    Active,
    Closed,
    Archived,
    SoftDeleted,
    Purging,
    Purged,
}

/// <summary>Membership of a user in a case (design record, "Security": <c>case_members</c>).</summary>
public enum MemberRole
{
    Owner,
    Rw,
    Ro,
}

/// <summary>How the bytes arrived (evidence rule 6; <c>cli</c> is <c>urlog ingest</c> and the job runner's worker).</summary>
public enum FileSource
{
    Web,
    Sftp,
    Cli,
}

/// <summary>
/// Where an evidence file stands. <c>Artifact</c> is a file that is recorded and never parsed; <c>Expanded</c> is a
/// container whose members all have their own records; <c>Unreadable</c> is an archive member whose content the
/// archive lists and cannot deliver (encrypted, an unsupported method, a wrong CRC), recorded and never parsed.
/// </summary>
public enum FileState
{
    Received,
    Parsed,
    Failed,
    Quarantined,
    Artifact,
    Archived,
    Expanded,
    Unreadable,
}

/// <summary><c>Expand</c> lists a container and records its members; it writes no events.</summary>
public enum JobKind
{
    Parse,
    Reparse,
    Expand,
}

/// <summary>Design record, "Consistency": <c>queued -> parsing -> done | failed</c>; <c>cancelled</c> is the cancel of a queued job.</summary>
public enum JobState
{
    Queued,
    Parsing,
    Done,
    Failed,
    Cancelled,
}

/// <summary>
/// How the last run of the field statistics of a case ended (<see cref="CaseStats"/>). <c>skipped</c>: the case holds
/// more rows than the task may read; <c>timed_out</c>: the run passed its time limit; in both, and in <c>failed</c>,
/// the message says why.
/// </summary>
public enum CaseStatsState
{
    None,
    Ok,
    Skipped,
    TimedOut,
    Failed,
}

/// <summary>Lifecycle work that outlasts a transaction (<see cref="CaseTask"/>).</summary>
public enum CaseTaskKind
{
    Purge,
    Archive,
    Reimport,
}

/// <summary>
/// <c>requested</c> waits for a second administrator; <c>running</c> is the executor's to carry out;
/// <c>committed</c> is past the point where it can only go forward; <c>done</c>, <c>failed</c> and
/// <c>withdrawn</c> are final.
/// </summary>
public enum CaseTaskState
{
    Requested,
    Running,
    Committed,
    Done,
    Failed,
    Withdrawn,
}
