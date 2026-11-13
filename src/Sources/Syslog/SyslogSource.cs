using System.Buffers;
using System.Buffers.Text;
using Urlog.Core.Sources;
using Urlog.Sources.Common;

namespace Urlog.Sources.Syslog;

/// <summary>
/// syslog files (design record, "Parser plugin contract"): RFC 3164 (<c>Mmm dd HH:MM:SS host tag[pid]: msg</c>), the same
/// shape with an ISO 8601 timestamp (rsyslog <c>RSYSLOG_FileFormat</c>, syslog-ng ISO), and RFC 5424
/// (<c>1 ts host app procid msgid sd [msg]</c>), each with an optional <c>&lt;PRI&gt;</c>, plus a Cisco-style
/// <c>seq: </c> prefix. Per line: PRI, then the timestamp decides the shape, then the header fields are located by
/// vectorised scans and the row is written; nothing allocates per record. The 3164 timestamp is naive and takes
/// <see cref="ParseContext.YearHint"/> and <see cref="ParseContext.TimeZoneHint"/>; the year is tracked across the
/// file (Dec -> Jan adds one), which is why the source is <see cref="SourceInfo.Stateful"/> and parses on one thread
/// in file order. Columns and extras keys: design record, "Parser plugin contract" (the syslog source as built), and <c>src/Query/field-catalog.yaml</c>.
/// <c>message</c> is the text after the tag, <c>raw</c> the line.
/// </summary>
[LogSource("syslog", Aliases = ["rfc3164", "rfc5424", "postfix"])]
public sealed class SyslogSource : ILogSource
{
    private const int SniffHeadLines = 200;
    private const int SniffTailLines = 50;
    private const int KeyBufferBytes = 256;
    private const int MaxPri = 191;

    private static readonly SearchValues<byte> TagEnd = SearchValues.Create(" [:"u8);

    private static readonly byte[][] SeverityNames =
    [
        "emerg"u8.ToArray(), "alert"u8.ToArray(), "crit"u8.ToArray(), "err"u8.ToArray(),
        "warning"u8.ToArray(), "notice"u8.ToArray(), "info"u8.ToArray(), "debug"u8.ToArray(),
    ];

    private static readonly byte[][] FacilityNames =
    [
        "kern"u8.ToArray(), "user"u8.ToArray(), "mail"u8.ToArray(), "daemon"u8.ToArray(), "auth"u8.ToArray(),
        "syslog"u8.ToArray(), "lpr"u8.ToArray(), "news"u8.ToArray(), "uucp"u8.ToArray(), "cron"u8.ToArray(),
        "authpriv"u8.ToArray(), "ftp"u8.ToArray(), "ntp"u8.ToArray(), "security"u8.ToArray(), "console"u8.ToArray(),
        "solaris-cron"u8.ToArray(), "local0"u8.ToArray(), "local1"u8.ToArray(), "local2"u8.ToArray(), "local3"u8.ToArray(),
        "local4"u8.ToArray(), "local5"u8.ToArray(), "local6"u8.ToArray(), "local7"u8.ToArray(),
    ];

    private static readonly SearchValues<byte> HostNameBytes = SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.-_:"u8);

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    private static ReadOnlySpan<byte> PostfixPrefix => "postfix/"u8;

    public SourceInfo Info { get; } = new("syslog", ["rfc3164", "rfc5424", "postfix"], "syslog (RFC 3164, RFC 5424, Postfix hook)", 1, Stateful: true, NaiveTimestamps: true);

    public Framing Framing => Framing.Line;

    public FieldSchema Schema { get; } = new(1,
    [
        EventColumn.Ts, EventColumn.Host, EventColumn.Program, EventColumn.Severity, EventColumn.MsgOff, EventColumn.MsgLen, EventColumn.Raw, EventColumn.Extras,
        EventColumn.SyslogFacility, EventColumn.ProcessPid,
    ]);

    public float Sniff(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, SniffContext ctx)
    {
        var headLength = head.Length;
        if (head.StartsWith(Utf8Bom))
        {
            head = head[3..]; // core strips it before Parse; the sniffer sees the file as it is
        }

        var lines = 0;
        var matching = 0;
        foreach (var record in new RecordEnumerator(head, Framing.Line, 0))
        {
            if (lines >= SniffHeadLines)
            {
                break;
            }

            if (record.Bytes.IsEmpty)
            {
                continue; // a blank line is not evidence either way
            }

            lines++;
            if (CountsAsSyslog(record.Bytes))
            {
                matching++;
            }
        }

        // The tail is a different part of the file only when the head did not cover it; its first line is partial.
        if (ctx.FileLength > headLength && !tail.IsEmpty)
        {
            var firstNewline = tail.IndexOf((byte)'\n');
            var complete = firstNewline < 0 ? default : tail[(firstNewline + 1)..];
            var tailLines = 0;
            foreach (var record in new RecordEnumerator(complete, Framing.Line, 0))
            {
                if (tailLines >= SniffTailLines)
                {
                    break;
                }

                if (record.Bytes.IsEmpty)
                {
                    continue;
                }

                tailLines++;
                lines++;
                if (CountsAsSyslog(record.Bytes))
                {
                    matching++;
                }
            }
        }

        return lines == 0 ? 0f : (float)matching / lines;
    }

    /// <summary>
    /// What <see cref="Sniff"/> counts, which is narrower than what <see cref="Parse"/> accepts. A line that starts
    /// with an ISO timestamp and has no PRI is also how most application logs begin, and the tokeniser takes any
    /// token after the timestamp as the host. As evidence of syslog such a line counts only in rsyslog's file
    /// shape: a host made of host-name characters (or none) and then a tag.
    /// </summary>
    private static bool CountsAsSyslog(ReadOnlySpan<byte> line)
    {
        if (Tokenise(line, out var header) != Status.Ok)
        {
            return false;
        }

        if (header.Pri >= 0 || header.Time != TimeKind.Unix || line.StartsWith("1 "u8))
        {
            return true;
        }

        return header.ProgramLength > 0 && (header.HostLength == 0 || !line.Slice(header.HostStart, header.HostLength).ContainsAnyExcept(HostNameBytes));
    }

    public void Parse(ReadOnlySequence<byte> framedChunk, ParseContext ctx, IBatchWriter batch)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(batch);

        var state = ctx.Scratch as FileState ?? NewState(ctx);
        var offsetMilliseconds = (long)ctx.TimeZoneHint.TotalMilliseconds;
        Span<byte> key = stackalloc byte[KeyBufferBytes];

        foreach (var record in framedChunk.Records(Framing, ctx))
        {
            var line = record.Bytes;
            var status = Tokenise(line, out var header);
            if (status != Status.Ok)
            {
                ctx.Reject(line, record.FileOffset, Reason(status));
                continue;
            }

            batch.AppendUInt64(EventColumn.FileOffset, (ulong)record.FileOffset);
            switch (header.Time)
            {
                case TimeKind.Bsd:
                    var year = header.Bsd.Year != 0 ? state.Set(header.Bsd.Year, header.Bsd.Month) : state.YearFor(header.Bsd.Month);
                    batch.AppendTimestamp(Rfc3164Timestamp.ToUnixMilliseconds(in header.Bsd, year, offsetMilliseconds));
                    break;
                case TimeKind.Unix:
                    batch.AppendTimestamp(header.UnixMilliseconds);
                    break;
                default:
                    break; // RFC 5424 "-": no timestamp on the line, the column stays at its default
            }

            if (header.Pri >= 0)
            {
                batch.AppendString(EventColumn.Severity, SeverityNames[header.Pri & 7]);
                batch.AppendString(EventColumn.SyslogFacility, FacilityNames[header.Pri >> 3]); // a LowCardinality column
            }

            if (header.HostLength > 0)
            {
                batch.AppendString(EventColumn.Host, line.Slice(header.HostStart, header.HostLength));
            }

            if (header.ProgramLength > 0)
            {
                var program = line.Slice(header.ProgramStart, header.ProgramLength);
                if (program.Length > PostfixPrefix.Length && program.StartsWith(PostfixPrefix))
                {
                    // The Postfix hook: program = postfix, the daemon aside; no Postfix field parsing here.
                    batch.AppendString(EventColumn.Program, "postfix"u8);
                    batch.AppendExtra("postfix.daemon"u8, program[PostfixPrefix.Length..]);
                }
                else
                {
                    batch.AppendString(EventColumn.Program, program);
                }
            }

            if (header.PidLength > 0)
            {
                var pid = line.Slice(header.PidStart, header.PidLength);
                if (IsDigits(pid) && Utf8Parser.TryParse(pid, out uint pidValue, out var pidDigits) && pidDigits == pid.Length)
                {
                    batch.AppendUInt32(EventColumn.ProcessPid, pidValue); // a UInt32 column
                }
                else
                {
                    batch.AppendExtra("log.syslog.procid"u8, pid); // not a number (or wider than UInt32): as logged
                }
            }

            if (header.MsgIdLength > 0)
            {
                batch.AppendExtra("log.syslog.msgid"u8, line.Slice(header.MsgIdStart, header.MsgIdLength));
            }

            if (header.SeqLength > 0)
            {
                batch.AppendExtra("event.sequence"u8, line.Slice(header.SeqStart, header.SeqLength));
            }

            if (header.SdLength > 0)
            {
                SyslogStructuredData.Write(line.Slice(header.SdStart, header.SdLength), batch, key);
            }

            batch.AppendMessage(header.MessageStart, header.MessageLength); // message = this slice of raw
            batch.AppendString(EventColumn.Raw, line); // byte for byte, always
            batch.CommitRow();
        }
    }

    /// <summary>
    /// Locates the header fields of one line without writing anything: PRI, the shape by its timestamp, host, tag,
    /// pid, the RFC 5424 fields and structured data, and where the message starts. Used by <see cref="Parse"/> and
    /// <see cref="Sniff"/> alike.
    /// </summary>
    internal static Status Tokenise(ReadOnlySpan<byte> line, out Header header)
    {
        header = default;
        header.Pri = -1;
        if (line.IsEmpty)
        {
            return Status.EmptyLine;
        }

        var pos = 0;
        if (line[0] == (byte)'<')
        {
            var pri = 0;
            var i = 1;
            while (i < line.Length && i <= 3 && (uint)(line[i] - '0') <= 9)
            {
                pri = (pri * 10) + (line[i] - '0');
                i++;
            }

            if (i > 1 && i < line.Length && line[i] == (byte)'>' && pri <= MaxPri)
            {
                header.Pri = pri;
                pos = i + 1;
            }
        }

        // RFC 5424: VERSION "1" then a space, then the timestamp.
        if (pos + 2 <= line.Length && line[pos] == (byte)'1' && line[pos + 1] == (byte)' ')
        {
            return Tokenise5424(line, pos + 2, ref header);
        }

        // Cisco-style "seq: " before the timestamp.
        if (pos < line.Length && (uint)(line[pos] - '0') <= 9)
        {
            var i = pos;
            while (i < line.Length && (uint)(line[i] - '0') <= 9)
            {
                i++;
            }

            if (i + 1 < line.Length && line[i] == (byte)':' && line[i + 1] == (byte)' ')
            {
                header.SeqStart = pos;
                header.SeqLength = i - pos;
                pos = i + 2;
            }
        }

        var rest = line[pos..];
        if (Rfc3164Timestamp.TryParse(rest, out header.Bsd, out var consumed))
        {
            header.Time = TimeKind.Bsd;
            pos += consumed;
        }
        else if (!rest.IsEmpty && (uint)(rest[0] - '0') <= 9)
        {
            // The ISO variant: the timestamp runs to the next space and carries its own offset.
            var end = rest.IndexOf((byte)' ');
            var token = end < 0 ? rest : rest[..end];
            if (!Iso8601.TryParse(token, out header.UnixMilliseconds))
            {
                return Status.BadTimestamp;
            }

            header.Time = TimeKind.Unix;
            pos += token.Length;
        }
        else
        {
            return Status.BadTimestamp;
        }

        if (pos == line.Length)
        {
            header.MessageStart = pos; // a bare timestamp: a row with nothing else
            return Status.Ok;
        }

        if (line[pos] != (byte)' ')
        {
            return Status.BadTimestamp;
        }

        pos++;

        // The token after the timestamp is the host unless it ends with a colon: then it is the tag and the line has no host.
        rest = line[pos..];
        var space = rest.IndexOf((byte)' ');
        var first = space < 0 ? rest : rest[..space];
        if (first.Length > 0 && first[^1] != (byte)':')
        {
            header.HostStart = pos;
            header.HostLength = first.Length;
            pos += first.Length;
            if (space < 0)
            {
                header.MessageStart = pos;
                return Status.Ok;
            }

            pos++;
        }

        // The tag: "program[pid]: " or "program: ". Anything else is message text from here.
        rest = line[pos..];
        var tagEnd = rest.IndexOfAny(TagEnd);
        if (tagEnd >= 0 && rest[tagEnd] != (byte)' ')
        {
            var after = tagEnd;
            if (rest[tagEnd] == (byte)'[')
            {
                var close = rest[(tagEnd + 1)..].IndexOf((byte)']');
                if (close < 0)
                {
                    tagEnd = -1; // "[" with no "]": not a tag
                }
                else
                {
                    header.PidStart = pos + tagEnd + 1;
                    header.PidLength = close;
                    after = tagEnd + 1 + close + 1;
                    if (after < rest.Length && rest[after] == (byte)':')
                    {
                        after++;
                    }
                }
            }
            else
            {
                after = tagEnd + 1; // the colon
            }

            if (tagEnd >= 0)
            {
                header.ProgramStart = pos;
                header.ProgramLength = tagEnd;
                if (after < rest.Length && rest[after] == (byte)' ')
                {
                    after++; // the single space syslog puts after the tag
                }

                pos += after;
            }
        }

        header.MessageStart = pos;
        header.MessageLength = line.Length - pos;
        return Status.Ok;
    }

    private static Status Tokenise5424(ReadOnlySpan<byte> line, int pos, ref Header header)
    {
        header.Time = TimeKind.None;

        // TIMESTAMP HOSTNAME APP-NAME PROCID MSGID, each followed by a space (the structured data must follow).
        for (var field = 0; field < 5; field++)
        {
            var rest = line[pos..];
            var space = rest.IndexOf((byte)' ');
            if (space <= 0)
            {
                return Status.TruncatedHeader;
            }

            var token = rest[..space];
            var nil = token.Length == 1 && token[0] == (byte)'-';
            switch (field)
            {
                case 0:
                    if (!nil)
                    {
                        if (!Iso8601.TryParse(token, out header.UnixMilliseconds))
                        {
                            return Status.BadTimestamp;
                        }

                        header.Time = TimeKind.Unix;
                    }

                    break;
                case 1:
                    if (!nil)
                    {
                        header.HostStart = pos;
                        header.HostLength = space;
                    }

                    break;
                case 2:
                    if (!nil)
                    {
                        header.ProgramStart = pos;
                        header.ProgramLength = space;
                    }

                    break;
                case 3:
                    if (!nil)
                    {
                        header.PidStart = pos;
                        header.PidLength = space;
                    }

                    break;
                default:
                    if (!nil)
                    {
                        header.MsgIdStart = pos;
                        header.MsgIdLength = space;
                    }

                    break;
            }

            pos += space + 1;
        }

        // STRUCTURED-DATA: "-" or one or more elements.
        if (pos >= line.Length)
        {
            return Status.TruncatedHeader;
        }

        if (line[pos] == (byte)'-')
        {
            pos++;
        }
        else if (line[pos] == (byte)'[')
        {
            var length = SyslogStructuredData.Scan(line[pos..]);
            if (length < 0)
            {
                return Status.BadStructuredData;
            }

            header.SdStart = pos;
            header.SdLength = length;
            pos += length;
        }
        else
        {
            return Status.BadStructuredData;
        }

        if (pos == line.Length)
        {
            header.MessageStart = pos; // no MSG
            return Status.Ok;
        }

        if (line[pos] != (byte)' ')
        {
            return Status.BadStructuredData;
        }

        pos++;
        if (line[pos..].StartsWith(Utf8Bom))
        {
            pos += 3; // RFC 5424: a BOM announces UTF-8 and is not part of the message
        }

        header.MessageStart = pos;
        header.MessageLength = line.Length - pos;
        return Status.Ok;
    }

    private static FileState NewState(ParseContext ctx)
    {
        var state = new FileState(ctx.YearHint);
        ctx.Scratch = state;
        return state;
    }

    private static bool IsDigits(ReadOnlySpan<byte> text)
    {
        foreach (var b in text)
        {
            if ((uint)(b - '0') > 9)
            {
                return false;
            }
        }

        return true;
    }

    private static ReadOnlySpan<byte> Reason(Status status) => status switch
    {
        Status.EmptyLine => "empty line"u8,
        Status.BadTimestamp => "bad timestamp"u8,
        Status.TruncatedHeader => "truncated header"u8,
        Status.BadStructuredData => "bad structured data"u8,
        _ => "unparseable"u8,
    };

    internal enum Status : byte
    {
        Ok,
        EmptyLine,
        BadTimestamp,
        TruncatedHeader,
        BadStructuredData,
    }

    internal enum TimeKind : byte
    {
        /// <summary>RFC 5424 <c>-</c>: the line carries no timestamp.</summary>
        None,

        /// <summary>A BSD timestamp in <see cref="Header.Bsd"/>; the caller supplies year and offset.</summary>
        Bsd,

        /// <summary>Resolved to UTC in <see cref="Header.UnixMilliseconds"/> (ISO 8601 with an offset).</summary>
        Unix,
    }

    /// <summary>Field positions of one line, filled by <see cref="Tokenise"/>; a length of 0 means the field is absent.</summary>
    internal struct Header
    {
        public int Pri;
        public TimeKind Time;
        public Rfc3164Timestamp.Parts Bsd;
        public long UnixMilliseconds;
        public int HostStart;
        public int HostLength;
        public int ProgramStart;
        public int ProgramLength;
        public int PidStart;
        public int PidLength;
        public int MsgIdStart;
        public int MsgIdLength;
        public int SeqStart;
        public int SeqLength;
        public int SdStart;
        public int SdLength;
        public int MessageStart;
        public int MessageLength;
    }

    /// <summary>
    /// Per-file state (the source is stateful, so this really is per file): the year of the naive timestamps,
    /// tracked from <see cref="ParseContext.YearHint"/> through month changes. A month six or more below the
    /// previous one is the next year (Dec -> Jan, Jul -> Jan: a file runs forward); more than six above is the
    /// previous year (a late Dec line after Jan). Smaller moves are the same year, so out-of-order lines near a
    /// month boundary do not flip it.
    /// </summary>
    private sealed class FileState(int yearHint)
    {
        private int _year = yearHint;
        private int _lastMonth;

        public int YearFor(int month)
        {
            if (_lastMonth != 0)
            {
                if (month <= _lastMonth - 6)
                {
                    _year++;
                }
                else if (month > _lastMonth + 6)
                {
                    _year--;
                }
            }

            _lastMonth = month;
            return _year;
        }

        /// <summary>A year on the line itself is better evidence than the hint: it resets the tracking.</summary>
        public int Set(int year, int month)
        {
            _year = year;
            _lastMonth = month;
            return year;
        }
    }
}
