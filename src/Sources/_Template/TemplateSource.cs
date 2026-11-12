using System.Buffers;
using System.Buffers.Text;
using System.Collections.Immutable;
using Urlog.Core.Sources;

namespace Urlog.Sources.Template;

/// <summary>
/// Starting point for a new source (design record, "Parser plugin contract"): copy the folder, rename, add
/// <c>[LogSource("id", Aliases = [...])]</c> and the fixtures. It is not registered itself. The shape it shows:
/// <see cref="SearchValues{T}"/> for delimiters, <see cref="Utf8Parser"/> for numbers, <c>u8</c> literals for
/// constants, records walked with <see cref="FramedChunkExtensions.Records"/>, and <see cref="ParseContext.Reject"/>
/// for anything that does not parse. Nothing here allocates per record; keep it that way.
/// </summary>
public sealed class TemplateSource : ILogSource
{
    // Byte sets are built once; IndexOfAny over a SearchValues is vectorised.
    private static readonly SearchValues<byte> FieldDelimiters = SearchValues.Create(" \t"u8);

    public SourceInfo Info { get; } = new("template", [], "template (not registered)", 1);

    public Framing Framing => Framing.Line;

    public FieldSchema Schema { get; } = new(1, [EventColumn.Ts, EventColumn.Program, EventColumn.EventCode, EventColumn.MsgOff, EventColumn.MsgLen, EventColumn.Raw]);

    public float Sniff(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, SniffContext ctx)
    {
        // Cheap first: a fixed signature, then structural sampling of up to ~200 lines. Be honest: 190/200 is 0.95, not 1.0.
        var lines = 0;
        var matching = 0;
        foreach (var line in new RecordEnumerator(head, Framing.Line, 0))
        {
            if (++lines > 200)
            {
                break;
            }

            if (LooksLikeARecord(line.Bytes))
            {
                matching++;
            }
        }

        return lines == 0 ? 0f : (float)matching / lines;
    }

    public void Parse(ReadOnlySequence<byte> framedChunk, ParseContext ctx, IBatchWriter batch)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(batch);
        foreach (var record in framedChunk.Records(Framing, ctx))
        {
            var line = record.Bytes;

            // Format illustrated here: "<unix-ms> <program> <code> <message...>"
            if (!Utf8Parser.TryParse(line, out long unixMilliseconds, out var consumed) || consumed >= line.Length)
            {
                ctx.Reject(line, record.FileOffset, "no timestamp"u8);
                continue;
            }

            var rest = line[(consumed + 1)..];
            var end = rest.IndexOfAny(FieldDelimiters);
            if (end <= 0)
            {
                ctx.Reject(line, record.FileOffset, "no program"u8);
                continue;
            }

            var program = rest[..end];
            rest = rest[(end + 1)..];
            if (!Utf8Parser.TryParse(rest, out uint code, out consumed) || consumed >= rest.Length)
            {
                ctx.Reject(line, record.FileOffset, "no event code"u8);
                continue;
            }

            batch.AppendUInt64(EventColumn.FileOffset, (ulong)record.FileOffset);
            batch.AppendTimestamp(unixMilliseconds);                    // UTC; a naive format would apply ctx.TimeZoneHint / ctx.YearHint here
            batch.AppendString(EventColumn.Program, program);           // LowCardinality columns are lowercased by the writer
            batch.AppendUInt32(EventColumn.EventCode, code);
            var messageStart = line.Length - rest.Length + consumed + 1;
            batch.AppendMessage(messageStart, line.Length - messageStart); // the message is a slice of raw; AppendString(MessageText, ...) only when it is not
            batch.AppendString(EventColumn.Raw, line);                  // byte for byte, always
            batch.CommitRow();                                          // host defaults to ctx.DefaultHost: this format carries none
        }
    }

    private static bool LooksLikeARecord(ReadOnlySpan<byte> line) =>
        Utf8Parser.TryParse(line, out long _, out var consumed) && consumed < line.Length && line[consumed] == (byte)' ';
}
