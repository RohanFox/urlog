namespace Urlog.Core.Metadata;

/// <summary>
/// What analysts open, share and delete. <see cref="State"/> moves only through <see cref="CaseLifecycle.Apply"/>,
/// and its setter is internal with the fields a transition stamps, so code outside this assembly cannot move a case;
/// each <c>*At</c> column records the latest entry into that state, except <see cref="DeletedAt"/>, which restore clears
/// because it drives the 7-day purge window.
/// </summary>
public sealed class CaseRecord
{
    public long Id { get; init; }

    public required string Name { get; set; }

    public CaseState State { get; internal set; } = CaseState.Active;

    public long OwnerId { get; init; }

    /// <summary>The state the case was in when it was soft-deleted; restore returns to it and clears this.</summary>
    public CaseState? PriorState { get; internal set; }

    public DateTime CreatedAt { get; init; }

    public DateTime? ClosedAt { get; set; }

    public DateTime? ArchivedAt { get; set; }

    public DateTime? DeletedAt { get; internal set; }

    public DateTime? PurgingAt { get; internal set; }

    public DateTime? PurgedAt { get; set; }

    /// <summary>
    /// The least <c>ts_min</c> of the case's files with a committed parse: with <see cref="TsMax"/> the span of the
    /// case and the default range of a search. Written by the commit of a parse; null while no file has a time.
    /// </summary>
    public DateTime? TsMin { get; set; }

    public DateTime? TsMax { get; set; }

    /// <summary>The sum of the files' <c>undated_rows</c>: events outside the span because their time is below <see cref="EventTime.Floor"/>.</summary>
    public long UndatedRows { get; set; }

    /// <summary>The sum of the files' <c>future_rows</c>: events outside the span because they are dated after their file arrived.</summary>
    public long FutureRows { get; set; }
}
