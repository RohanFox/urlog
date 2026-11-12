namespace Urlog.Core.Metadata;

/// <summary>
/// The intake record of evidence rule 6 plus the parse bookkeeping. One row per distinct SHA-256 within a case
/// (rule 3); an archive member has its own row with <see cref="ArchivePath"/> and <see cref="ParentFileId"/> (rule 7).
/// <see cref="ParseGeneration"/> is the source of truth that ClickHouse's <c>file_generation</c> mirrors.
/// </summary>
public sealed class EvidenceFile
{
    public long Id { get; init; }

    public long CaseId { get; init; }

    /// <summary>Null = unassigned (flagged in the UI, never blocked).</summary>
    public long? HostId { get; set; }

    public long? ParentFileId { get; init; }

    /// <summary>Path inside the parent archive, e.g. <c>web-01/nginx/access.log.3</c>.</summary>
    public string? ArchivePath { get; init; }

    /// <summary>64 lowercase hex characters, taken over the raw bytes before anything else (rule 1).</summary>
    public required string Sha256 { get; init; }

    public long Size { get; init; }

    public required string OriginalName { get; init; }

    /// <summary>Where the bytes are, relative to the case's intake directory.</summary>
    public required string StoredPath { get; init; }

    public FileSource Source { get; init; }

    /// <summary>The account name (web) or the OS user (SFTP drop, CLI).</summary>
    public required string Uploader { get; init; }

    public long? UploaderUserId { get; init; }

    public DateTime ReceivedAt { get; init; }

    /// <summary>Per-file override of the host's zone; null = the host's.</summary>
    public string? Timezone { get; set; }

    public int? YearHint { get; set; }

    public required string Encoding { get; init; }

    public string? Compression { get; init; }

    /// <summary>What the detector took the file for; null on a row written before detection records existed.</summary>
    public Urlog.Core.Detection.FileKind? DetectedKind { get; set; }

    public string? DetectedSource { get; set; }

    public float? DetectedConfidence { get; set; }

    public string? DetectedLayout { get; set; }

    /// <summary>
    /// The whole detection record as JSON (<c>DetectionResult.ToJson</c>): every source's score, the container chain,
    /// flags, the message, the detector version and the sources registered at the time. An override never changes it.
    /// </summary>
    public string? Detection { get; set; }

    /// <summary>The file's own modification time where intake knows it (a local path, an archive entry); UTC.</summary>
    public DateTime? FileTime { get; init; }

    public string? OverrideSource { get; set; }

    /// <summary>JSON object of per-file options (<c>nginx.log_format</c>, <c>csv.delimiter</c>, ...); null when none.</summary>
    public string? OverrideOptions { get; set; }

    public int ParseGeneration { get; set; } = 1;

    public FileState State { get; set; } = FileState.Received;

    public DateTime? TsMin { get; set; }

    public DateTime? TsMax { get; set; }

    public long RowCount { get; set; }

    public long RejectCount { get; set; }
}
