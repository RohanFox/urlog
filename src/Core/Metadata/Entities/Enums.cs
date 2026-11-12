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

/// <summary>Where an evidence file stands. <c>Artifact</c> is a non-log member recorded and never parsed.</summary>
public enum FileState
{
    Received,
    Parsed,
    Failed,
    Quarantined,
    Artifact,
    Archived,
}

public enum JobKind
{
    Parse,
    Reparse,
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
