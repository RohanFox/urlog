using Urlog.Core.Metadata;

namespace Urlog.Core.Audit;

/// <summary><see cref="IAuditWriter.AppendAsync"/> was called outside a transaction that serialises writers (<see cref="AuditChain.BeginTransactionAsync"/>).</summary>
public sealed class AuditTransactionException : MetadataException
{
    public AuditTransactionException()
    {
    }

    public AuditTransactionException(string message)
        : base(message)
    {
    }

    public AuditTransactionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The chain cannot be extended: no genesis row (the store was not migrated) or a tail that is not well formed.</summary>
public sealed class AuditChainException : MetadataException
{
    public AuditChainException()
    {
    }

    public AuditChainException(string message)
        : base(message)
    {
    }

    public AuditChainException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
