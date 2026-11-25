using System.Collections.Immutable;

namespace Urlog.Query;

/// <summary>
/// The place of a row in a listing's whole order: its value of each sort key of the query, as a result printed
/// it, then the key of the event.
/// </summary>
public sealed record RowKey(ImmutableArray<string> SortValues, string Ts, ulong FileId, ulong FileOffset);

/// <summary>A file at one parse generation.</summary>
public readonly record struct FileGeneration(ulong FileId, uint Generation);
