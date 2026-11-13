namespace Urlog.Core.Metadata;

/// <summary>
/// The identity of the event store this metadata file belongs to (design record, "Consistency", Store identity).
/// One row, <see cref="Id"/> always 1. File ids, case ids and generations in this file mean something only in that
/// store, so every writer compares this value with the store's own before it writes to either.
/// Written by <see cref="StoreIdentityOperations"/> only, each time with its audit row.
/// </summary>
public sealed class StoreIdentityRow
{
    public const long SingleId = 1;

    public long Id { get; init; } = SingleId;

    /// <summary>32 lowercase hex characters: 128 random bits, generated once for the pair.</summary>
    public required string StoreId { get; set; }

    /// <summary>When this file got the value it holds now.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Whether the value was generated for a new pair or taken over by a pairing.</summary>
    public StoreIdentityOrigin Origin { get; set; }
}

public enum StoreIdentityOrigin
{
    Created,
    Paired,
}
