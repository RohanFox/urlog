using System.Buffers;
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
/// the rest as one record.
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
        foreach (var record in framedChunk.Records(Framing, ctx))
        {
            var line = record.Bytes;
            if (line.IsEmpty)
            {
                continue; // a blank line is neither an event nor an error
            }

            var position = 0;
            while (line.Length - position > MaxLineBytes)
            {
                Write(line.Slice(position, PieceBytes), record.FileOffset + position, state, batch);
                position += PieceBytes;
            }

            Write(line[position..], record.FileOffset + position, state, batch);
        }
    }

    private static void Write(ReadOnlySpan<byte> record, long fileOffset, FileState state, IBatchWriter batch)
    {
        batch.AppendUInt64(EventColumn.FileOffset, (ulong)fileOffset);
        if (GenericTimestamps.TryFind(record, state.ZoneOffsetMilliseconds, state.Years, out var unixMilliseconds))
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
    }
}
