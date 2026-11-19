namespace Urlog.Core.Metadata;

/// <summary>The ingest queue (design record, "Consistency"); the job runner runs it. The counters are advisory, never used for resume.</summary>
public sealed class Job
{
    public long Id { get; init; }

    public long CaseId { get; init; }

    public long FileId { get; init; }

    public JobKind Kind { get; init; }

    /// <summary>
    /// The <c>parse_generation</c> this job writes: 0 until the first claim, then one above every generation the
    /// file's jobs have used, and a new one for every attempt, so a crashed attempt's rows never share a number with
    /// the retry's.
    /// </summary>
    public int Generation { get; set; }

    public JobState State { get; set; } = JobState.Queued;

    /// <summary>The file's <c>settings_version</c> at the claim; 0 before the first claim and for an expansion.</summary>
    public int SettingsVersion { get; set; }

    public int Attempt { get; set; } = 1;

    public DateTime CreatedAt { get; init; }

    public DateTime? StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    public string? Worker { get; set; }

    public long ChunksRead { get; set; }

    public long ChunksParsed { get; set; }

    public long ChunksAcked { get; set; }

    public long Rows { get; set; }

    public long Rejects { get; set; }

    public long BytesRead { get; set; }

    /// <summary>
    /// JSON settings of this one job, null for none: the archive limits an administrator set for a rerun of an
    /// expansion. They apply to this job only and are in the audit row of the rerun.
    /// </summary>
    public string? Options { get; init; }

    /// <summary>The failure reason verbatim (pipeline exception with chunk context), or the watermark pause message.</summary>
    public string? Message { get; set; }
}
