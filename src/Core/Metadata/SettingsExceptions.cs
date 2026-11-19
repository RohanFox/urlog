namespace Urlog.Core.Metadata;

/// <summary>The write names a version of the record that is no longer the current one: someone else changed it in between.</summary>
public sealed class StaleWriteException : MetadataException
{
    public StaleWriteException()
    {
    }

    public StaleWriteException(string message)
        : base(message)
    {
    }

    public StaleWriteException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public StaleWriteException(string what, string given, string current)
        : base($"{what} has changed since it was read: the request names version {given}, the current one is {current}; read it again and repeat the change")
    {
        Current = current;
    }

    /// <summary>The version a retry has to name.</summary>
    public string? Current { get; }
}

/// <summary>The case is not in the state in which its hosts, files and jobs change.</summary>
public sealed class CaseFrozenException : MetadataException
{
    public CaseFrozenException()
    {
    }

    public CaseFrozenException(string message)
        : base(message)
    {
    }

    public CaseFrozenException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public CaseFrozenException(long caseId, CaseState state)
        : base(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"case {caseId} is {Names.Of(state)}; its hosts, files and jobs change only while it is active"))
    {
    }
}

/// <summary>No such host, file or job in the case.</summary>
public sealed class RecordNotFoundException : MetadataException
{
    public RecordNotFoundException()
    {
    }

    public RecordNotFoundException(string message)
        : base(message)
    {
    }

    public RecordNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public RecordNotFoundException(string what, long id, long caseId)
        : base(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"no {what} {id} in case {caseId}"))
    {
    }
}

/// <summary>What the record holds right now refuses the change (a merged host, a name that is taken, a file that is never parsed).</summary>
public sealed class SettingsRefusedException : MetadataException
{
    public SettingsRefusedException()
    {
    }

    public SettingsRefusedException(string message)
        : base(message)
    {
    }

    public SettingsRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A bulk change selects more files than one call may touch.</summary>
public sealed class SelectionTooLargeException : MetadataException
{
    public SelectionTooLargeException()
    {
    }

    public SelectionTooLargeException(string message)
        : base(message)
    {
    }

    public SelectionTooLargeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SelectionTooLargeException(int matched, int limit)
        : base(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"the selection matches {matched} files and one call changes at most {limit}; narrow it, or walk it with after_id and limit"))
    {
        Matched = matched;
    }

    public int Matched { get; }
}
