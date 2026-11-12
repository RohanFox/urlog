namespace Urlog.Core.Metadata;

/// <summary>
/// One link of the hash chain (design record, "Audit log"): <c>hash = SHA-256(prev_hash || canonical_json(row without hash))</c>.
/// No setters after construction; <c>src/Core/Audit</c> has the writer, the insert-only triggers and the genesis row (the only row with
/// <see cref="SchemaVersion"/>). <see cref="Seq"/> is assigned by the writer, never by the database, so it has no gaps.
/// </summary>
public sealed class AuditRow
{
    public long Seq { get; init; }

    public DateTime TsUtc { get; init; }

    /// <summary>The user name or the OS user.</summary>
    public required string Actor { get; init; }

    public long? ActorId { get; init; }

    public required string Action { get; init; }

    public string? TargetType { get; init; }

    public long? TargetId { get; init; }

    public required string DetailsJson { get; init; }

    public required string PrevHash { get; init; }

    public required string Hash { get; init; }

    public int? SchemaVersion { get; init; }
}
