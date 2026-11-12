namespace Urlog.Core.Sources;

public enum FramingKind : byte
{
    /// <summary>One record per line, terminated by <c>\n</c>; a preceding <c>\r</c> is not part of the record.</summary>
    Line,

    /// <summary>A record starts at a line that begins with the start pattern and runs until the next such line.</summary>
    MultiLine,

    /// <summary>Records separated by one or more blank lines (journald export, mail headers).</summary>
    BlankLineRecord,

    /// <summary>Binary records; an <see cref="IChunkSplitter"/> from the plugin finds the boundaries.</summary>
    Binary,
}

/// <summary>
/// Record boundaries for a binary format. Core calls <see cref="LastRecordEnd"/> to cut chunks and
/// <see cref="NextRecord"/> to walk the records of a framed chunk. Both must be pure functions of the bytes.
/// </summary>
public interface IChunkSplitter
{
    /// <summary>Index just past the last complete record in <paramref name="data"/>, or 0 if none is complete.</summary>
    int LastRecordEnd(ReadOnlySpan<byte> data);

    /// <summary>
    /// Length of the record payload at the start of <paramref name="data"/>; <paramref name="consumed"/> is how far to
    /// advance for the next record (payload plus any header or padding). Must consume at least one byte when
    /// <paramref name="data"/> is not empty.
    /// </summary>
    int NextRecord(ReadOnlySpan<byte> data, out int consumed);
}

/// <summary>
/// How core cuts a file into records: the 64 MB chunk boundary is always placed where <see cref="LastRecordEnd"/>
/// says a record ends, so a plugin never sees a split record (design record, "Parser plugin contract").
/// </summary>
public abstract class Framing
{
    private protected Framing(FramingKind kind) => Kind = kind;

    public FramingKind Kind { get; }

    public static Framing Line { get; } = new LineFraming();

    public static Framing BlankLineRecord { get; } = new BlankLineFraming();

    /// <summary>A record starts at every line that begins with <paramref name="startPattern"/> (for example a timestamp prefix).</summary>
    public static Framing MultiLine(ReadOnlySpan<byte> startPattern)
    {
        if (startPattern.IsEmpty)
        {
            throw new ArgumentException("start pattern must not be empty", nameof(startPattern));
        }

        return new MultiLineFraming(startPattern.ToArray());
    }

    public static Framing Binary(IChunkSplitter splitter)
    {
        ArgumentNullException.ThrowIfNull(splitter);
        return new BinaryFraming(splitter);
    }

    /// <summary>Index just past the last complete record in <paramref name="data"/>; 0 when no record is complete.</summary>
    public abstract int LastRecordEnd(ReadOnlySpan<byte> data);

    /// <summary>
    /// Payload length of the record at the start of <paramref name="data"/> and the bytes to advance by.
    /// Called by <see cref="RecordEnumerator"/>; <paramref name="data"/> is never empty.
    /// </summary>
    public abstract int NextRecord(ReadOnlySpan<byte> data, out int consumed);

    private static int TrimLineEnd(ReadOnlySpan<byte> data, int end)
    {
        if (end > 0 && data[end - 1] == (byte)'\n')
        {
            end--;
        }

        if (end > 0 && data[end - 1] == (byte)'\r')
        {
            end--;
        }

        return end;
    }

    private sealed class LineFraming() : Framing(FramingKind.Line)
    {
        public override int LastRecordEnd(ReadOnlySpan<byte> data) => data.LastIndexOf((byte)'\n') + 1;

        public override int NextRecord(ReadOnlySpan<byte> data, out int consumed)
        {
            var nl = data.IndexOf((byte)'\n');
            if (nl < 0)
            {
                consumed = data.Length;
                return TrimLineEnd(data, data.Length);
            }

            consumed = nl + 1;
            return nl > 0 && data[nl - 1] == (byte)'\r' ? nl - 1 : nl;
        }
    }

    private sealed class MultiLineFraming(byte[] startPattern) : Framing(FramingKind.MultiLine)
    {
        public ReadOnlySpan<byte> StartPattern => startPattern;

        public override int LastRecordEnd(ReadOnlySpan<byte> data)
        {
            // The boundary is the start of the last line that begins a record; position 0 never counts, because a
            // chunk must contain at least one record. Walk the newlines backwards.
            var searchEnd = data.Length;
            while (searchEnd > 0)
            {
                var nl = data[..searchEnd].LastIndexOf((byte)'\n');
                if (nl < 0)
                {
                    return 0;
                }

                var lineStart = nl + 1;
                if (lineStart < data.Length && lineStart > 0 && data[lineStart..].StartsWith(startPattern))
                {
                    return lineStart;
                }

                searchEnd = nl;
            }

            return 0;
        }

        public override int NextRecord(ReadOnlySpan<byte> data, out int consumed)
        {
            // Skip the first line (it starts this record, whether or not it matches), then stop at the next line that matches.
            var pos = data.IndexOf((byte)'\n') + 1;
            if (pos == 0)
            {
                consumed = data.Length;
                return TrimLineEnd(data, data.Length);
            }

            while (pos < data.Length)
            {
                if (data[pos..].StartsWith(startPattern))
                {
                    break;
                }

                var nl = data[pos..].IndexOf((byte)'\n');
                pos = nl < 0 ? data.Length : pos + nl + 1;
            }

            consumed = pos;
            return TrimLineEnd(data, pos);
        }
    }

    private sealed class BlankLineFraming() : Framing(FramingKind.BlankLineRecord)
    {
        public override int LastRecordEnd(ReadOnlySpan<byte> data)
        {
            var lf = data.LastIndexOf("\n\n"u8);
            var crlf = data.LastIndexOf("\n\r\n"u8);
            var end = Math.Max(lf < 0 ? 0 : lf + 2, crlf < 0 ? 0 : crlf + 3);
            return SkipBlankLines(data, end);
        }

        public override int NextRecord(ReadOnlySpan<byte> data, out int consumed)
        {
            var lf = data.IndexOf("\n\n"u8);
            var crlf = data.IndexOf("\n\r\n"u8);
            int sepAt, sepLen;
            if (lf < 0 && crlf < 0)
            {
                consumed = data.Length;
                return TrimLineEnd(data, data.Length);
            }

            if (crlf >= 0 && (lf < 0 || crlf < lf))
            {
                (sepAt, sepLen) = (crlf, 3);
            }
            else
            {
                (sepAt, sepLen) = (lf, 2);
            }

            consumed = SkipBlankLines(data, sepAt + sepLen);
            return TrimLineEnd(data, sepAt + 1);
        }

        private static int SkipBlankLines(ReadOnlySpan<byte> data, int pos)
        {
            while (pos < data.Length)
            {
                if (data[pos] == (byte)'\n')
                {
                    pos++;
                }
                else if (data[pos] == (byte)'\r' && pos + 1 < data.Length && data[pos + 1] == (byte)'\n')
                {
                    pos += 2;
                }
                else
                {
                    break;
                }
            }

            return pos;
        }
    }

    private sealed class BinaryFraming(IChunkSplitter splitter) : Framing(FramingKind.Binary)
    {
        public override int LastRecordEnd(ReadOnlySpan<byte> data) => splitter.LastRecordEnd(data);

        public override int NextRecord(ReadOnlySpan<byte> data, out int consumed)
        {
            var length = splitter.NextRecord(data, out consumed);
            if (consumed <= 0 || consumed > data.Length || length < 0 || length > consumed)
            {
                throw new InvalidOperationException($"{splitter.GetType().Name}.NextRecord returned payload {length}, consumed {consumed} for {data.Length} bytes");
            }

            return length;
        }
    }
}
