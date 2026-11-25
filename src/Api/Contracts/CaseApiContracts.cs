using System.Text.Json;
using Urlog.Core.Detection;
using Urlog.Core.Metadata;

namespace Urlog.Api.Contracts;

// Responses of the cases, hosts, files and jobs routes. A host and a file carry `version`: a change sends it back in
// If-Match, and a change that names an older one is refused.

/// <summary>Lifecycle work on a case that has not ended: a purge that waits for its confirmation, an archive or a re-import under way.</summary>
public sealed record CaseTaskResponse(long Id, CaseTaskKind Kind, CaseTaskState State, string? Step, string RequestedBy, string? ConfirmedBy, string? Message, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Where the field statistics of a case stand, from the metadata file: the fields route has the statistics themselves.</summary>
public sealed record StatisticsStateResponse(CaseStatsState State, bool Pending, DateTimeOffset? ComputedAt, string? Reason);

/// <summary>One case with what the UI shows beside its name: its span, the state of its statistics, the lifecycle work under way.</summary>
public sealed record CaseDetailResponse(long Id, string Name, CaseState State, string Owner, DateTimeOffset CreatedAt, DateTimeOffset? ClosedAt, DateTimeOffset? ArchivedAt, int Members, MemberRole MyRole, SpanResponse Span, StatisticsStateResponse Statistics, CaseTaskResponse? Task);

/// <summary>A soft-deleted case as its owner still sees it: until <paramref name="PurgeDue"/> it can be restored to <paramref name="PriorState"/>.</summary>
public sealed record DeletedCaseResponse(long Id, string Name, CaseState? PriorState, DateTimeOffset? DeletedAt, DateTimeOffset? PurgeDue, CaseTaskResponse? Task);

/// <summary>The typed confirmation of a soft-delete: the case's name, exactly.</summary>
public sealed record DeleteCaseRequest
{
    public required string Name { get; init; }
}

/// <summary>A host of a case with what its files add up to. <c>merged_into</c> is set on a host that was merged away: its name leads to that host.</summary>
public sealed record HostResponse(long Id, string Name, string Timezone, bool TimezoneAssumed, IReadOnlyDictionary<string, string>? SourceOptions, long? MergedInto, int Files, long Rows, long Rejects, DateTimeOffset? From, DateTimeOffset? To, int Version);

/// <summary>What a change would have parsed again, or did.</summary>
public sealed record ReparseResponse(int Files, long Bytes);

/// <summary>
/// The answer to a change of a host. <c>applied</c> false: nothing changed, because files would be parsed again
/// and the request did not confirm it; <c>reparse</c> is what the confirmation would queue.
/// </summary>
public sealed record HostChangeResponse(bool Applied, bool Changed, HostResponse Host, ReparseResponse Reparse, int JobsQueued);

public sealed record HostMergeResponse(HostResponse Into, int FilesMoved);

public sealed record FileHostResponse(long Id, string Name, bool Inferred, string? Rule);

public sealed record FileYearResponse(int? Value, bool Given, bool Assumed);

/// <summary>What the detector took the file for. Never changed by an override; <c>stale</c> when a newer detector could send the file to a parser.</summary>
public sealed record FileDetectionResponse(FileKind? Kind, string? Source, float? Confidence, string? Layout, string? Format, bool Stale);

public sealed record FileOverrideResponse(string? Source, IReadOnlyDictionary<string, string>? Options);

/// <summary>What the next parse uses, and for each option whether it is the file's own (<c>file</c>) or its host's default (<c>host</c>).</summary>
public sealed record FileEffectiveResponse(string Source, IReadOnlyDictionary<string, string> Options, IReadOnlyDictionary<string, string> OptionsFrom);

public sealed record FileSpanResponse(DateTimeOffset? From, DateTimeOffset? To, long UndatedEvents, long FutureEvents);

public sealed record JobRefResponse(long JobId, JobState State, JobKind Kind);

/// <summary>
/// A file record. <c>rows</c>, <c>rejects</c>, <c>span</c> and <c>parse_generation</c> describe the committed
/// parse, which is what a search reads; <c>reparse</c> is the job that will replace it, and
/// <c>settings_applied</c> is false while the committed parse was made under settings that have since changed.
/// </summary>
public sealed record FileResponse(
    long Id, long? ParentFileId, long? RootFileId, string? ArchivePath, string OriginalName, string Sha256, long Size, bool HashOfStoredBytes,
    FileSource Source, string Uploader, DateTimeOffset ReceivedAt, FileState State, bool Quarantined,
    FileHostResponse? Host, string? Timezone, FileYearResponse Year, string Encoding, string? Compression,
    FileDetectionResponse Detection, FileOverrideResponse? Override, FileEffectiveResponse Effective,
    int ParseGeneration, long Rows, long Rejects, FileSpanResponse Span, bool StoredHistogram,
    IReadOnlyList<string> Flags, JobRefResponse? Reparse, bool SettingsApplied, int Version);

/// <summary>One file with everything its record holds: the whole detection record, every other place its bytes were seen, its jobs.</summary>
public sealed record FileDetailResponse(
    FileResponse File, JsonElement? DetectionRecord, JsonElement? Seen, string? UnreadableReason, long? StatedSize,
    long? TruncatedDecodedOffset, long? TruncatedStoredOffset, long TrailingBytes, bool TrailingBytesInexact, DateTimeOffset? VerifiedAt,
    IReadOnlyList<JobResponse> Jobs);

/// <summary>A page of files in id order. <c>next_after</c> is the <c>after</c> of the next page; null on the last.</summary>
public sealed record FilePageResponse(IReadOnlyList<FileResponse> Files, long? NextAfter);

/// <summary>The answer to a change of one file: the record after it and the parse it queued.</summary>
public sealed record FileChangeResponse(bool Changed, FileResponse File, JobRefResponse? Job, ReparseResponse Reparse);

/// <summary>
/// The answer of a bulk change. <c>applied</c> false: nothing changed; this is what the call would do, and
/// <c>digest</c> is what the confirmation sends in If-Match. <c>skipped</c> counts the selected files the change
/// leaves as they are, by reason.
/// </summary>
public sealed record BulkResponse(bool Applied, int Matched, string Digest, ReparseResponse Reparse, int Changed, int JobsQueued, IReadOnlyDictionary<string, int> Skipped, long? NextAfterId);

/// <summary>A job. <c>message</c> is the failure reason as the pipeline reported it, or why the job waits.</summary>
public sealed record JobResponse(
    long Id, long FileId, JobKind Kind, JobState State, int Generation, int Attempt, bool Redetect,
    DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    long ChunksRead, long ChunksParsed, long ChunksAcked, long Rows, long Rejects, long BytesRead, long? BytesTotal, string? Message);

public sealed record JobPageResponse(IReadOnlyList<JobResponse> Jobs, long? NextAfter);

public sealed record DashboardCaseResponse(long Id, string Name, CaseState State, MemberRole MyRole, string Owner, CaseTaskResponse? Task);

public sealed record DashboardFilesResponse(int Total, int Arrived, long BytesArrived, IReadOnlyDictionary<string, int> ByState, IReadOnlyDictionary<string, int> ByKind, long Rows, long Rejects, IReadOnlyDictionary<string, int> Flags);

public sealed record DashboardJobResponse(long JobId, long FileId, JobKind Kind, long BytesRead, long BytesTotal, long Rows, long Rejects, DateTimeOffset? StartedAt);

public sealed record DashboardIngestResponse(int Queued, int Parsing, int Failed, int Cancelled, int Done, string? Paused, IReadOnlyList<DashboardJobResponse> Current, long BytesDone, long BytesTotal);

/// <summary>
/// Disk use of the case. <c>store_rows</c> is what the event store holds in the case's partitions: every
/// generation, and rows a delete has masked until a merge drops them, so it can be above the files' row total.
/// </summary>
public sealed record DashboardUsageResponse(long RecordedBytes, long IntakeBytes, long UploadBytes, long CacheBytes, long ArchiveBytes, long StoreRows, long StoreBytes, DateTimeOffset MeasuredAt);

/// <summary>
/// The case dashboard: counts and hosts, no file list (the files route pages it). <c>files.rows</c> and
/// <c>files.rejects</c> are the sums of the file records. <c>usage</c> is null when it could not be measured,
/// and <c>usage_unavailable</c> says why.
/// </summary>
public sealed record DashboardResponse(
    DashboardCaseResponse Case, SpanResponse Span, StatisticsStateResponse Statistics, DashboardFilesResponse Files,
    DashboardIngestResponse Ingest, IReadOnlyList<HostResponse> Hosts, HostResponse? Unassigned, DashboardUsageResponse? Usage, string? UsageUnavailable);

/// <summary>A user's own change of password.</summary>
public sealed record ChangePasswordRequest
{
    [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.MaxLength(Urlog.Core.Auth.PasswordPolicy.MaxLength)]
    public required string CurrentPassword { get; init; }

    [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.MaxLength(Urlog.Core.Auth.PasswordPolicy.MaxLength)]
    public required string NewPassword { get; init; }
}
