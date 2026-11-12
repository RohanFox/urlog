namespace Urlog.Core.Metadata;

/// <summary>
/// What analysts open, share and delete. <see cref="State"/> moves only through <see cref="CaseLifecycle.Apply"/>;
/// each <c>*At</c> column records the latest entry into that state, except <see cref="DeletedAt"/>, which restore clears
/// because it drives the 7-day purge window.
/// </summary>
public sealed class CaseRecord
{
    public long Id { get; init; }

    public required string Name { get; set; }

    public CaseState State { get; set; } = CaseState.Active;

    public long OwnerId { get; init; }

    /// <summary>The state the case was in when it was soft-deleted; restore returns to it and clears this.</summary>
    public CaseState? PriorState { get; set; }

    public DateTime CreatedAt { get; init; }

    public DateTime? ClosedAt { get; set; }

    public DateTime? ArchivedAt { get; set; }

    public DateTime? DeletedAt { get; set; }

    public DateTime? PurgingAt { get; set; }

    public DateTime? PurgedAt { get; set; }
}
