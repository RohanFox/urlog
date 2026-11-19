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

    /// <summary>The nearest container this file is a member of; null for a file that arrived on its own.</summary>
    public long? ParentFileId { get; init; }

    /// <summary>The file that arrived, for a member at any depth: the caps of an archive are counted over its whole tree.</summary>
    public long? RootFileId { get; init; }

    /// <summary>Containers above this file: 0 for a file that arrived on its own.</summary>
    public int ArchiveDepth { get; init; }

    /// <summary>
    /// The whole chain from the file that arrived, <c>!</c> after each container:
    /// <c>bundle.zip!logs/inner.tar.gz!web-01/nginx/access.log.3</c>. Host inference reads it.
    /// </summary>
    public string? ArchivePath { get; init; }

    /// <summary>Entry number in the parent container, in the order the container lists them, from 0.</summary>
    public int? MemberOrdinal { get; init; }

    /// <summary>For a tar member, where its data starts in the tar, so a parse seeks instead of walking the headers.</summary>
    public long? MemberOffset { get; init; }

    /// <summary>
    /// For a container that cannot be seeked to a member as stored (a compressed tar, a container inside another):
    /// its seekable copy, relative to the cache root. Derived, never evidence: rebuilt when missing.
    /// </summary>
    public string? CachePath { get; set; }

    /// <summary>Members of this container whose records are written: where an interrupted expansion goes on.</summary>
    public int ExpandedMembers { get; set; }

    /// <summary>64 lowercase hex characters, taken over the raw bytes before anything else (rule 1).</summary>
    public required string Sha256 { get; init; }

    public long Size { get; init; }

    /// <summary>
    /// True when <see cref="Sha256"/> and <see cref="Size"/> are over the entry's bytes as the archive stores them
    /// (compressed or encrypted), because its content could not be read. Only for <c>unreadable</c> members.
    /// </summary>
    public bool HashOfStoredBytes { get; init; }

    /// <summary>For an unreadable member: the content size its archive states.</summary>
    public long? StatedSize { get; init; }

    /// <summary>For an unreadable member: why its content could not be read.</summary>
    public string? UnreadableReason { get; init; }

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

    /// <summary>
    /// True when a person gave <see cref="YearHint"/>; false when it was derived from the file's own time or the
    /// arrival date. A given year holds for every member of an archive, like the zone; a derived one does not.
    /// </summary>
    public bool YearGiven { get; set; }

    /// <summary>True when nothing but the arrival date supports <see cref="YearHint"/>.</summary>
    public bool YearAssumed { get; set; }

    /// <summary>True when <see cref="HostId"/> came from a path pattern and no person has confirmed it.</summary>
    public bool HostInferred { get; set; }

    /// <summary>The pattern that proposed the host, kept so a wrong rule can be found.</summary>
    public string? HostRule { get; set; }

    /// <summary>
    /// Every other place the same bytes arrived at in this case: a JSON array of <c>{path, host, at}</c>, one entry
    /// per <c>file_duplicate</c> row. Null when the bytes were seen once.
    /// </summary>
    public string? SeenPaths { get; set; }

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

    /// <summary>
    /// Raised by every change that alters what a parse of this file writes: its host, zone, year or override, and
    /// the zone, the name or the default options of its host. The claim of a job stamps the job with it, and a job
    /// that ends under an older value is followed by another parse. A write that names an older value is refused.
    /// </summary>
    public int SettingsVersion { get; set; } = 1;

    /// <summary>The <see cref="SettingsVersion"/> the committed parse was made under; null while nothing is committed or for a parse from before the column.</summary>
    public int? ParsedSettingsVersion { get; set; }

    /// <summary>
    /// The detector version and the source ids the detection record was made with, as <c>DetectionResult.RegistryKey</c>
    /// writes them. A value other than the running build's makes the record stale.
    /// </summary>
    public string? DetectionRegistry { get; set; }

    public int ParseGeneration { get; set; } = 1;

    /// <summary>
    /// The generation whose stored histogram was written, set by the commit of that parse; null for a file whose
    /// committed parse has none (it was parsed before the histogram was kept). A case answers its unfiltered
    /// timeline from the stored histogram only when this equals the committed generation for every parsed file.
    /// </summary>
    public int? HistogramGeneration { get; set; }

    public FileState State { get; set; } = FileState.Received;

    public DateTime? TsMin { get; set; }

    public DateTime? TsMax { get; set; }

    /// <summary>Rows of the committed parse whose time is below <see cref="EventTime.Floor"/>; outside <see cref="TsMin"/> and <see cref="TsMax"/>.</summary>
    public long UndatedRows { get; set; }

    /// <summary>Rows of the committed parse dated later than <see cref="ReceivedAt"/> plus <see cref="EventTime.FutureMargin"/>; outside the range too.</summary>
    public long FutureRows { get; set; }

    public long RowCount { get; set; }

    public long RejectCount { get; set; }

    /// <summary>
    /// The compressed input ended before its format says it should: the rows are what could be read. Set by the
    /// commit that made them visible, with the two offsets below.
    /// </summary>
    public bool InputTruncated { get; set; }

    /// <summary>
    /// For an archive member: its container ends inside this member's data, so the member's bytes are the part
    /// that is there. Set when the member is recorded and never cleared: no parse can make the missing bytes appear.
    /// </summary>
    public bool ContainerCut { get; init; }

    /// <summary>Bytes of decoded content that were read before the input ended.</summary>
    public long? TruncatedDecodedOffset { get; set; }

    /// <summary>Bytes of the stored (compressed) input the decoder had when it ended.</summary>
    public long? TruncatedStoredOffset { get; set; }

    /// <summary>
    /// Stored bytes behind the end of the compressed data that are no further member, frame or stream (padding,
    /// anything else). The content is whole; the count is kept so the record says the file holds more than its logs.
    /// </summary>
    public long TrailingBytes { get; set; }

    /// <summary>True when <see cref="TrailingBytes"/> is a lower bound and not a proved count.</summary>
    public bool TrailingBytesInexact { get; set; }

    /// <summary>
    /// The file has naive timestamps, was read with one offset of a zone, and its events lie on both sides of a
    /// change of that zone's offset: times may be one hour off on one side.
    /// </summary>
    public bool SpansDst { get; set; }

    /// <summary>When the integrity sweep last read the stored bytes and compared them with <see cref="Sha256"/>; null for never.</summary>
    public DateTime? VerifiedAt { get; set; }
}
