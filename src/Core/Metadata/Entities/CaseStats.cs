namespace Urlog.Core.Metadata;

/// <summary>
/// Where the field statistics of a case stand (design record, "Query language", Autocomplete). The statistics
/// themselves are rows of <c>field_stats</c> in the event store, derived from the events and rebuilt at will; this
/// row says whether they are out of date and how the last run ended. No audit row is written for a run.
/// </summary>
public sealed class CaseStats
{
    public long CaseId { get; init; }

    /// <summary>
    /// The time of the latest commit of a parse in the case since the statistics were last computed; null when
    /// they are up to date. A run clears it only if it is still the value the run started from.
    /// </summary>
    public DateTime? DirtySince { get; set; }

    public CaseStatsState State { get; set; } = CaseStatsState.None;

    /// <summary>Why the last run was skipped, timed out or failed; null after a run that wrote a set.</summary>
    public string? Message { get; set; }

    /// <summary>The <c>computed_at</c> of the set the event store holds for the case; null when it holds none.</summary>
    public DateTime? ComputedAt { get; set; }

    /// <summary>Events the last written set counted.</summary>
    public long Events { get; set; }

    /// <summary>How long the last run took, in milliseconds.</summary>
    public long DurationMs { get; set; }

    public DateTime? FinishedAt { get; set; }
}
