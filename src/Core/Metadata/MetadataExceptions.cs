namespace Urlog.Core.Metadata;

/// <summary>A domain rule of the metadata store refused the operation. The message names the rule in the lifecycle's words.</summary>
public class MetadataException : Exception
{
    public MetadataException()
    {
    }

    public MetadataException(string message)
        : base(message)
    {
    }

    public MetadataException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The lifecycle table has no cell for the pair (or restore was asked for a state other than the prior one).</summary>
public sealed class InvalidCaseTransitionException : MetadataException
{
    public InvalidCaseTransitionException()
    {
    }

    public InvalidCaseTransitionException(string message)
        : base(message)
    {
    }

    public InvalidCaseTransitionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public InvalidCaseTransitionException(CaseState from, CaseState to, CaseState? priorState)
        : base(priorState is { } prior && from == CaseState.SoftDeleted && CaseLifecycle.Operation(from, to) == CaseLifecycle.Restore
            ? $"a case in state {Names.Of(from)} restores to {Names.Of(prior)}, not to {Names.Of(to)}"
            : $"a case in state {Names.Of(from)} cannot move to {Names.Of(to)}")
    {
        From = from;
        To = to;
    }

    public CaseState From { get; }

    public CaseState To { get; }
}

public sealed class CaseNotFoundException : MetadataException
{
    public CaseNotFoundException()
    {
    }

    public CaseNotFoundException(string message)
        : base(message)
    {
    }

    public CaseNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public CaseNotFoundException(long caseId)
        : base($"no case with id {caseId}")
    {
        CaseId = caseId;
    }

    public long CaseId { get; }
}

public sealed class UserNotFoundException : MetadataException
{
    public UserNotFoundException()
    {
    }

    public UserNotFoundException(string message)
        : base(message)
    {
    }

    public UserNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public UserNotFoundException(long userId)
        : base($"no user with id {userId}")
    {
        UserId = userId;
    }

    public long UserId { get; }
}

/// <summary>The name is not lowercase <c>[a-z0-9][a-z0-9._-]{0,63}</c> once trimmed.</summary>
public sealed class InvalidUserNameException : MetadataException
{
    public InvalidUserNameException()
    {
    }

    public InvalidUserNameException(string name)
        : base($"'{name}' is not a valid user name: 1 to {UserOperations.NameMaxLength} characters from a-z, 0-9, '.', '_' and '-', starting with a letter or digit")
    {
    }

    public InvalidUserNameException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Another account already has the name (the unique index on <c>users.name</c>).</summary>
public sealed class UserNameTakenException : MetadataException
{
    public UserNameTakenException()
    {
    }

    public UserNameTakenException(string name)
        : base($"a user named '{name}' already exists")
    {
    }

    public UserNameTakenException(string name, Exception innerException)
        : base($"a user named '{name}' already exists", innerException)
    {
    }
}

/// <summary>The change would leave no admin account.</summary>
public sealed class LastAdminException : MetadataException
{
    public LastAdminException()
    {
    }

    public LastAdminException(string name)
        : base($"'{name}' is the only admin; make another user admin first")
    {
    }

    public LastAdminException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The user is already a member of the case: change the role instead of sharing again.</summary>
public sealed class AlreadyMemberException : MetadataException
{
    public AlreadyMemberException()
    {
    }

    public AlreadyMemberException(string message)
        : base(message)
    {
    }

    public AlreadyMemberException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public AlreadyMemberException(long caseId, string userName)
        : base($"'{userName}' is already a member of case {caseId}")
    {
    }
}

public sealed class NotAMemberException : MetadataException
{
    public NotAMemberException()
    {
    }

    public NotAMemberException(string message)
        : base(message)
    {
    }

    public NotAMemberException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public NotAMemberException(long caseId, long userId)
        : base($"user {userId} is not a member of case {caseId}")
    {
    }
}

/// <summary>The change would leave the case without an owner.</summary>
public sealed class LastOwnerException : MetadataException
{
    public LastOwnerException()
    {
    }

    public LastOwnerException(string caseName)
        : base($"case '{caseName}' needs at least one owner; make another member owner first")
    {
    }

    public LastOwnerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Ingest is refused unless the case is <c>active</c> (design record: closed freezes ingest and keeps query).</summary>
public sealed class CaseNotActiveException : MetadataException
{
    public CaseNotActiveException()
    {
    }

    public CaseNotActiveException(string message)
        : base(message)
    {
    }

    public CaseNotActiveException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public CaseNotActiveException(long caseId, CaseState state)
        : base(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"case {caseId} is {Names.Of(state)}; only an active case takes files"))
    {
        CaseId = caseId;
        State = state;
    }

    public long CaseId { get; }

    public CaseState State { get; }
}

/// <summary>A lifecycle operation whose transition the table allows is refused by what the case holds right now (a running job, unfinished work, a wrong typed name).</summary>
public sealed class LifecycleRefusedException : MetadataException
{
    public LifecycleRefusedException()
    {
    }

    public LifecycleRefusedException(string message)
        : base(message)
    {
    }

    public LifecycleRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The account may not carry out this lifecycle operation on this case.</summary>
public sealed class LifecycleRoleException : MetadataException
{
    public LifecycleRoleException()
    {
    }

    public LifecycleRoleException(string message)
        : base(message)
    {
    }

    public LifecycleRoleException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
