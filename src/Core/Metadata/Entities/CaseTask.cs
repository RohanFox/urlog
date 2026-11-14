namespace Urlog.Core.Metadata;

/// <summary>
/// One piece of lifecycle work on a case that takes more than a transaction: a purge, an archive, a re-import. The
/// row is the request, the approval and the resume point. A case with an unfinished task takes no transition but
/// a restore, which withdraws a purge that has not marked the case yet.
/// </summary>
public sealed class CaseTask
{
    public long Id { get; init; }

    public long CaseId { get; init; }

    public CaseTaskKind Kind { get; init; }

    public CaseTaskState State { get; set; } = CaseTaskState.Running;

    /// <summary>The step the executor is in or starts next; null before the first. Written before the step starts.</summary>
    public string? Step { get; set; }

    /// <summary>Times the executor has taken the task up.</summary>
    public int Attempt { get; set; }

    public required string RequestedBy { get; init; }

    /// <summary>A plain number, like the audit trail's target: the task may outlive the account.</summary>
    public long? RequestedById { get; init; }

    /// <summary>The second administrator of a two-person purge.</summary>
    public string? ConfirmedBy { get; set; }

    public long? ConfirmedById { get; set; }

    /// <summary>The SHA-256 of the metadata backup taken before a purge marked the case.</summary>
    public string? BackupSha256 { get; set; }

    /// <summary>The directory of the written archive, for an archive or a re-import.</summary>
    public string? ArchiveDirectory { get; set; }

    public string? TarSha256 { get; set; }

    public string? ManifestSha256 { get; set; }

    /// <summary>Why the task failed or was withdrawn, or what the last attempt stopped on.</summary>
    public string? Message { get; set; }

    public DateTime CreatedAt { get; init; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    /// <summary>For an archive: when its files were last read and compared with the hashes above.</summary>
    public DateTime? VerifiedAt { get; set; }
}
