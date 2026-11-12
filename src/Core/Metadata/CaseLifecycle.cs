namespace Urlog.Core.Metadata;

/// <summary>
/// The case lifecycle of the design record as a table. Rows are the current state, cells the operation, blank is refused
/// (self-transitions included): 11 of the 36 pairs pass.
/// <code>
/// from \ to     active   closed   archived  soft_deleted  purging  purged
/// active        -        close    -         soft_delete   -        -
/// closed        reopen   -        archive   soft_delete   -        -
/// archived      -        reimport -         soft_delete   -        -
/// soft_deleted  restore* restore* restore*  -             purge    -
/// purging       -        -        -         -             -        purge_complete
/// purged        -        -        -         -             -        -
/// </code>
/// <c>*</c> only to the state in <see cref="CaseRecord.PriorState"/>. Archive is reachable from <c>closed</c> only, so re-import
/// returns to <c>closed</c> and never to <c>active</c>: evidence that was frozen stays frozen. Purge is reachable only
/// through soft-delete, so the reconciler only ever resumes <c>purging</c>.
/// </summary>
public static class CaseLifecycle
{
    public const string Close = "close";
    public const string Reopen = "reopen";
    public const string Archive = "archive";
    public const string SoftDelete = "soft_delete";
    public const string Reimport = "reimport";
    public const string Restore = "restore";
    public const string Purge = "purge";
    public const string PurgeComplete = "purge_complete";

    /// <summary>The operation that moves <paramref name="from"/> to <paramref name="to"/>, or null when the table has no cell.</summary>
    public static string? Operation(CaseState from, CaseState to) => (from, to) switch
    {
        (CaseState.Active, CaseState.Closed) => Close,
        (CaseState.Closed, CaseState.Active) => Reopen,
        (CaseState.Closed, CaseState.Archived) => Archive,
        (CaseState.Active or CaseState.Closed or CaseState.Archived, CaseState.SoftDeleted) => SoftDelete,
        (CaseState.Archived, CaseState.Closed) => Reimport,
        (CaseState.SoftDeleted, CaseState.Active or CaseState.Closed or CaseState.Archived) => Restore,
        (CaseState.SoftDeleted, CaseState.Purging) => Purge,
        (CaseState.Purging, CaseState.Purged) => PurgeComplete,
        _ => null,
    };

    /// <summary>True when <paramref name="case"/> may move to <paramref name="to"/>; restore only to the prior state.</summary>
    public static bool CanTransition(CaseRecord @case, CaseState to, out string? operation)
    {
        ArgumentNullException.ThrowIfNull(@case);
        operation = Operation(@case.State, to);
        if (operation == Restore && @case.PriorState != to)
        {
            operation = null;
        }

        return operation is not null;
    }

    /// <summary>
    /// Moves the case and stamps the timestamp of the state entered. Forward operations stamp; restore undoes the
    /// soft-delete (clears <see cref="CaseRecord.DeletedAt"/> and <see cref="CaseRecord.PriorState"/>) and touches nothing else, so the
    /// original close or archive time survives. Returns the operation name for the audit row.
    /// </summary>
    /// <exception cref="InvalidCaseTransitionException">The table has no cell for the pair.</exception>
    public static string Apply(CaseRecord @case, CaseState to, DateTime nowUtc)
    {
        if (!CanTransition(@case, to, out var operation))
        {
            throw new InvalidCaseTransitionException(@case.State, to, @case.PriorState);
        }

        var from = @case.State;
        if (operation == Restore)
        {
            @case.PriorState = null;
            @case.DeletedAt = null;
        }
        else
        {
            switch (to)
            {
                case CaseState.Closed:
                    @case.ClosedAt = nowUtc;
                    break;
                case CaseState.Archived:
                    @case.ArchivedAt = nowUtc;
                    break;
                case CaseState.SoftDeleted:
                    @case.PriorState = from;
                    @case.DeletedAt = nowUtc;
                    break;
                case CaseState.Purging:
                    @case.PurgingAt = nowUtc;
                    break;
                case CaseState.Purged:
                    @case.PurgedAt = nowUtc;
                    break;
                case CaseState.Active:
                    break;
            }
        }

        @case.State = to;
        return operation!;
    }
}
