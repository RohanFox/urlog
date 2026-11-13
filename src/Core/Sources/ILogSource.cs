using System.Buffers;
using System.Collections.Immutable;

namespace Urlog.Core.Sources;

/// <summary>
/// A log format. One class per source under <c>src/Sources/&lt;Name&gt;/</c>, marked <see cref="LogSourceAttribute"/>;
/// the registry is source-generated. Contract: design record, "Parser plugin contract". A plugin never opens a
/// file, touches a socket, or allocates per event; core guarantees a framed chunk never splits a record.
/// </summary>
public interface ILogSource
{
    SourceInfo Info { get; }

    Framing Framing { get; }

    FieldSchema Schema { get; }

    /// <summary>Confidence 0..1 that <paramref name="head"/> (first 64 KB) and <paramref name="tail"/> (last 4 KB) are this format.</summary>
    float Sniff(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, SniffContext ctx);

    /// <summary>
    /// Tokenise every record of <paramref name="framedChunk"/> into <paramref name="batch"/>. Walk the records with
    /// <see cref="FramedChunkExtensions.Records"/>. Malformed records go to <see cref="ParseContext.Reject"/>; never
    /// throw for bad input, an exception fails the whole chunk.
    /// </summary>
    void Parse(ReadOnlySequence<byte> framedChunk, ParseContext ctx, IBatchWriter batch);
}

/// <summary>
/// Identity of a source. <paramref name="Stateful"/> sources get their chunks in file order on one thread.
/// <paramref name="NaiveTimestamps"/> is true when the format can carry times without a zone, which the source
/// reads with <see cref="ParseContext.TimeZoneHint"/>: one offset per file, so such a file is flagged when its
/// events lie on both sides of a change of the zone's offset.
/// </summary>
public sealed record SourceInfo(string Id, ImmutableArray<string> Aliases, string DisplayName, int Version, bool Stateful = false, bool NaiveTimestamps = false);

/// <summary>Columns a source writes, besides the ones core fills. <paramref name="Version"/> bumps when the set changes.</summary>
public sealed record FieldSchema(int Version, ImmutableArray<EventColumn> Columns);

/// <summary>
/// Marks an <see cref="ILogSource"/> for the generated registry (<c>Urlog.Core.SourceGen</c>). The class must be
/// public, non-abstract, and have a parameterless constructor.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class LogSourceAttribute(string id) : Attribute
{
    public string Id { get; } = id;

    public string[] Aliases { get; init; } = [];
}

/// <summary>What the generated registry records per source.</summary>
public sealed record SourceRegistration(string Id, ImmutableArray<string> Aliases, Func<ILogSource> Factory);
