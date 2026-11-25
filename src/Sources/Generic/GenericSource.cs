using System.Buffers;
using System.Buffers.Text;
using Urlog.Core.Sources;

namespace Urlog.Sources.Generic;

/// <summary>
/// The fallback for text no other source claims (design record, "Format detection"): one event per non-blank line,
/// <c>message</c> = <c>raw</c> = the line, a best-effort timestamp (<see cref="GenericTimestamps"/>). Nothing is
/// rejected, so a file is never lost for want of a parser, and it can be parsed again when one exists.
/// <para>
/// A line with no timestamp takes the last one found before it in the file, so a stack trace sorts with the event
/// that produced it; before the first one it takes <see cref="ParseContext.FileTime"/>, and with neither the column
/// keeps its default. Those rows say so in <c>extras['timestamp.source']</c> (<c>previous</c>, <c>file</c>,
/// <c>none</c>). Carrying a timestamp forward needs the file in order: the source is stateful.
/// </para>
/// <para>
/// A line longer than 1 MiB is not a line. It is written as 64 KiB records while more than 1 MiB of it remains and
/// the rest as one record. Every such piece says that it is one: <c>extras['line.first_offset']</c> is the file
/// offset of the line's first byte, the same on every piece, and <c>extras['line.part']</c> is the piece's place,
/// <c>2/3</c>. A piece after the first takes the time of the first and is not searched for one of its own. The
/// same marks are written when the chunk reader had to cut a record that outgrew its buffer: the line then
/// continues in the next chunk, its total is not known while its first pieces are written, and those say
/// <c>2</c> instead of <c>2/3</c>.
/// </para>
/// Outside the throughput SLO; benchmarked and reported. It never scores a sample: the detector picks it when
/// nothing else fits.
/// </summary>
[LogSource("generic", Aliases = ["raw"])]
public sealed class GenericSource : ILogSource
{
    internal const int MaxLineBytes = 1 << 20;

    internal const int PieceBytes = 64 << 10;

    public SourceInfo Info { get; } = new("generic", ["raw"], "generic text (one event per line, best-effort timestamp)", 1, Stateful: true, NaiveTimestamps: true);

    public Framing Framing => Framing.Line;

    public FieldSchema Schema { get; } = new(1, [EventColumn.Ts, EventColumn.MsgOff, EventColumn.MsgLen, EventColumn.Raw, EventColumn.Extras]);

    public float Sniff(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, SniffContext ctx) => 0f;

    public void Parse(ReadOnlySequence<byte> framedChunk, ParseContext ctx, IBatchWriter batch)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(batch);

        var state = ctx.Scratch as FileState ?? NewState(ctx);
        var first = true;
        foreach (var record in framedChunk.Records(Framing, ctx))
        {
            var line = record.Bytes;
            // The first record of a chunk that continues a cut one is the rest of the line the chunk before began.
            var continues = first && ctx.ChunkContinuesSplit && state.Open;
            first = false;
            if (line.IsEmpty)
            {
                continue; // a blank line is neither an event nor an error
            }

            // A chunk the reader had to cut holds one record and nothing else: the line goes on in the next chunk.
            var cut = ctx.ChunkForcedSplit;
            var pieces = line.Length > MaxLineBytes ? ((line.Length - MaxLineBytes + PieceBytes - 1) / PieceBytes) + 1 : 1;
            if (pieces == 1 && !continues && !cut)
            {
                state.Open = false;
                Write(line, record.FileOffset, state, batch, default);
                continue;
            }

            var firstOffset = continues ? state.OpenFirstOffset : record.FileOffset;
            var before = continues ? state.OpenParts : 0;
            var total = cut ? 0 : before + pieces;
            var position = 0;
            for (var piece = 1; piece <= pieces; piece++)
            {
                var length = piece == pieces ? line.Length - position : PieceBytes;
                Write(line.Slice(position, length), record.FileOffset + position, state, batch, new LinePart(firstOffset, before + piece, total));
                position += length;
            }

            state.Open = cut;
            state.OpenFirstOffset = firstOffset;
            state.OpenParts = before + pieces;
        }
    }

    /// <summary>The place of a piece in a line that was written as several records. <see cref="Part"/> 0: a whole line.</summary>
    /// <param name="Total">The number of pieces, or 0 while the line is still being read.</param>
    private readonly record struct LinePart(long FirstOffset, int Part, int Total);

    private static void Write(ReadOnlySpan<byte> record, long fileOffset, FileState state, IBatchWriter batch, LinePart part)
    {
        batch.AppendUInt64(EventColumn.FileOffset, (ulong)fileOffset);
        if (part.Part > 0)
        {
            Span<byte> number = stackalloc byte[44];
            Utf8Formatter.TryFormat(part.FirstOffset, number, out var written);
            batch.AppendExtra("line.first_offset"u8, number[..written]);
            Utf8Formatter.TryFormat(part.Part, number, out written);
            if (part.Total > 0)
            {
                number[written++] = (byte)'/';
                Utf8Formatter.TryFormat(part.Total, number[written..], out var more);
                written += more;
            }

            batch.AppendExtra("line.part"u8, number[..written]);
        }

        // A piece after the first is the middle of a line: what looks like a time inside it is not the line's.
        if (part.Part <= 1 && GenericTimestamps.TryFind(record, state.ZoneOffsetMilliseconds, state.Years, out var unixMilliseconds))
        {
            state.Last = unixMilliseconds;
            state.HasLast = true;
            batch.AppendTimestamp(unixMilliseconds);
        }
        else if (state.HasLast)
        {
            batch.AppendTimestamp(state.Last);
            batch.AppendExtra("timestamp.source"u8, "previous"u8);
        }
        else if (state.FileTime is { } fileTime)
        {
            batch.AppendTimestamp(fileTime);
            batch.AppendExtra("timestamp.source"u8, "file"u8);
        }
        else
        {
            batch.AppendExtra("timestamp.source"u8, "none"u8);
        }

        batch.AppendMessage(0, record.Length);
        batch.AppendString(EventColumn.Raw, record);
        batch.CommitRow();
    }

    private static FileState NewState(ParseContext ctx)
    {
        var state = new FileState(ctx.YearHint)
        {
            ZoneOffsetMilliseconds = (long)ctx.TimeZoneHint.TotalMilliseconds,
            FileTime = ctx.FileTime is { } time ? new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)).ToUnixTimeMilliseconds() : null,
        };
        ctx.Scratch = state;
        return state;
    }

    private sealed class FileState(int yearHint)
    {
        public YearTracker Years { get; } = new(yearHint);

        public long ZoneOffsetMilliseconds { get; init; }

        public long? FileTime { get; init; }

        public long Last { get; set; }

        public bool HasLast { get; set; }

        /// <summary>True while the last line written was cut by the chunk reader and goes on in the next chunk.</summary>
        public bool Open { get; set; }

        /// <summary>The file offset of the first byte of that line.</summary>
        public long OpenFirstOffset { get; set; }

        /// <summary>How many pieces of it are written.</summary>
        public int OpenParts { get; set; }
    }
}
