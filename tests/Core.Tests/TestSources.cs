using System.Buffers;
using System.Buffers.Text;
using System.Collections.Immutable;
using Urlog.Core.Sources;

namespace Urlog.Core.Tests;

/// <summary>
/// Line source for pipeline tests: records are <c>&lt;n&gt; &lt;text&gt;</c>; <c>n</c> becomes <c>ts</c>, the line is
/// <c>raw</c> and <c>message</c>. A record that does not start with a number is rejected, never thrown on.
/// </summary>
[LogSource("test-lines", Aliases = ["tl", "numbered"])]
public sealed class NumberedLineSource(bool stateful = false, string? throwOnText = null) : ILogSource
{
    public static readonly byte[] BadPrefix = "BAD"u8.ToArray();

    public SourceInfo Info { get; } = new("test-lines", ["tl", "numbered"], "numbered lines", 1, stateful);

    public Framing Framing => Framing.Line;

    public FieldSchema Schema { get; } = new(1, [EventColumn.Ts, EventColumn.MsgOff, EventColumn.MsgLen, EventColumn.Raw, EventColumn.Program]);

    public float Sniff(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, SniffContext ctx) => 0f;

    public void Parse(ReadOnlySequence<byte> framedChunk, ParseContext ctx, IBatchWriter batch)
    {
        foreach (var record in framedChunk.Records(Framing, ctx))
        {
            var line = record.Bytes;
            if (throwOnText is not null && line.IndexOf(System.Text.Encoding.UTF8.GetBytes(throwOnText)) >= 0)
            {
                throw new InvalidDataException("test source asked to throw on: " + throwOnText);
            }

            if (!Utf8Parser.TryParse(line, out long n, out var consumed) || consumed == line.Length || line[consumed] != (byte)' ')
            {
                ctx.Reject(line, record.FileOffset, "no leading number"u8);
                continue;
            }

            batch.AppendUInt64(EventColumn.FileOffset, (ulong)record.FileOffset);
            batch.AppendTimestamp(n);
            batch.AppendString(EventColumn.Program, "TEST"u8);
            batch.AppendMessage(consumed + 1, line.Length - consumed - 1);
            batch.AppendString(EventColumn.Raw, line);
            batch.CommitRow();
        }
    }
}

/// <summary>Fixed-size binary records of <see cref="Size"/> bytes.</summary>
public sealed class FixedSizeSplitter(int size) : IChunkSplitter
{
    public int Size => size;

    public int LastRecordEnd(ReadOnlySpan<byte> data) => data.Length / size * size;

    public int NextRecord(ReadOnlySpan<byte> data, out int consumed)
    {
        consumed = Math.Min(size, data.Length);
        return consumed;
    }
}

public static class TestData
{
    /// <summary>Deterministic text of numbered lines with varying lengths; returns the bytes and the line count.</summary>
    public static (byte[] Bytes, int Lines) NumberedLines(int approxBytes, int seed, int maxLineLength = 300, string newline = "\n", int badEvery = 0)
    {
        var rng = new Random(seed);
        var sb = new System.Text.StringBuilder(approxBytes + 1024);
        var lines = 0;
        while (sb.Length < approxBytes)
        {
            lines++;
            if (badEvery > 0 && lines % badEvery == 0)
            {
                sb.Append("BAD record without a number ");
            }
            else
            {
                sb.Append(lines).Append(' ');
            }

            var len = rng.Next(1, maxLineLength);
            for (var i = 0; i < len; i++)
            {
                sb.Append((char)('a' + rng.Next(26)));
            }

            sb.Append(newline);
        }

        return (System.Text.Encoding.UTF8.GetBytes(sb.ToString()), lines);
    }
}
