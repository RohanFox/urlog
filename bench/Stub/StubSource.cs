using System.Buffers;
using System.Collections.Immutable;
using Urlog.Core.Sources;

namespace Urlog.Bench.Stub;

/// <summary>
/// Stand-in used before the real tokenisers existed: walks every field delimiter of a line with
/// <see cref="SearchValues{T}"/> (the memory work of a parser, none of the parsing) and writes the line as
/// <c>raw</c> and <c>message</c> with constant typed columns. Allocation-free, which is why the <c>Tokenise*</c>
/// numbers it produces are <c>provisional</c> in <c>bench/baseline.json</c> while the allocation rule still applies.
/// </summary>
public sealed class StubSource : ILogSource
{
    private const long EpochMs = 1_772_323_200_000; // 2026-03-01T00:00:00Z

    private static readonly SearchValues<byte> Delimiters = SearchValues.Create(" \"[]"u8);

    public StubSource(string program = "nginx")
    {
        Program = System.Text.Encoding.UTF8.GetBytes(program);
        Info = new SourceInfo("stub-" + program, [], "stub " + program, 1);
    }

    public SourceInfo Info { get; }

    public Framing Framing => Framing.Line;

    public FieldSchema Schema { get; } = new(1, [EventColumn.Ts, EventColumn.Program, EventColumn.Severity, EventColumn.EventAction, EventColumn.EventOutcome, EventColumn.HttpStatus, EventColumn.MsgOff, EventColumn.MsgLen, EventColumn.Raw]);

    /// <summary>Delimiters seen since construction (the stub's "work", kept so the JIT cannot drop the scan).</summary>
    public long Fields { get; private set; }

    private byte[] Program { get; }

    public float Sniff(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, SniffContext ctx) => 0f;

    public void Parse(ReadOnlySequence<byte> framedChunk, ParseContext ctx, IBatchWriter batch)
    {
        long fields = 0;
        foreach (var record in framedChunk.Records(Framing, ctx))
        {
            var line = record.Bytes;
            var rest = line;
            while (true)
            {
                var i = rest.IndexOfAny(Delimiters);
                if (i < 0)
                {
                    break;
                }

                fields++;
                rest = rest[(i + 1)..];
            }

            batch.AppendUInt64(EventColumn.FileOffset, (ulong)record.FileOffset);
            batch.AppendTimestamp(EpochMs + record.FileOffset); // monotonic, deterministic, one day per ~86 MB
            batch.AppendString(EventColumn.Program, Program);
            batch.AppendString(EventColumn.Severity, "info"u8);
            batch.AppendString(EventColumn.EventAction, "access"u8);
            batch.AppendString(EventColumn.EventOutcome, "success"u8);
            batch.AppendUInt16(EventColumn.HttpStatus, 200);
            batch.AppendMessage(0, line.Length);
            batch.AppendString(EventColumn.Raw, line);
            batch.CommitRow();
        }

        Fields += fields;
    }
}

/// <summary>In-memory chunking for the single-threaded <c>Tokenise*</c> benches: 64 MB pieces cut on a record boundary.</summary>
public static class InMemoryChunks
{
    public const int DefaultChunk = 64 << 20;

    /// <summary>Length of the next chunk at the start of <paramref name="data"/> for <paramref name="framing"/>; never splits a record unless one exceeds <paramref name="maxChunk"/>.</summary>
    public static int NextChunkLength(ReadOnlySpan<byte> data, Framing framing, int maxChunk = DefaultChunk)
    {
        if (data.Length <= maxChunk)
        {
            return data.Length;
        }

        var end = framing.LastRecordEnd(data[..maxChunk]);
        return end > 0 ? end : maxChunk;
    }

    /// <summary>A context for <see cref="Parse"/>; build it once, outside the measured operation.</summary>
    public static ParseContext Context(string defaultHost, IBatchWriter batch)
    {
        var ctx = new ParseContext { CaseId = 1, HostId = 1, FileId = 1, DefaultHost = System.Text.Encoding.UTF8.GetBytes(defaultHost) };
        ctx.Bind(batch);
        return ctx;
    }

    /// <summary>Runs <paramref name="source"/> over <paramref name="data"/> chunk by chunk into <paramref name="batch"/>, single-threaded. Allocation-free.</summary>
    public static void Parse(ILogSource source, byte[] data, ParseContext ctx, IBatchWriter batch, int maxChunk = DefaultChunk)
    {
        var offset = 0;
        var index = 0;
        while (offset < data.Length)
        {
            var len = NextChunkLength(data.AsSpan(offset), source.Framing, maxChunk);
            ctx.ChunkFileOffset = offset;
            ctx.ChunkIndex = index++;
            source.Parse(new ReadOnlySequence<byte>(data, offset, len), ctx, batch);
            offset += len;
        }
    }
}
