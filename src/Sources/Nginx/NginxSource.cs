using System.Buffers;
using System.Buffers.Text;
using System.Collections.Immutable;
using Urlog.Core.Sources;
using Urlog.Sources.Common;

namespace Urlog.Sources.Nginx;

/// <summary>
/// nginx and Apache access logs (design record, "Parser plugin contract"), driven by a layout compiled from an nginx <c>log_format</c>
/// string (<see cref="NginxLayout"/>). Built-ins <c>combined</c>, <c>common</c> and <c>ingress-nginx</c> are detected
/// per chunk; a custom format comes in through <see cref="ParseContext.Options"/> as <c>nginx.log_format</c> (a format
/// string or a built-in name) and is then the only layout used.
/// <para>
/// Per line: the layout's literals are matched left to right with vectorised scans (unquoted values end at the next
/// space, quoted values at the closing quote with a backslash escaping the next byte, timestamps at their fixed width),
/// typed fields are validated, then the row is written. Escapes are stored as logged. Nothing allocates per record; the
/// per-field positions live on the stack. Columns and extras keys: <see cref="NginxLayout"/> and the design record, "Parser plugin contract".
/// <c>message</c> is the <c>"request" status bytes</c> slice of the line, <c>raw</c> the line.
/// </para>
/// </summary>
[LogSource("nginx", Aliases = ["nginx-access", "apache-combined", "apache-common"])]
public sealed class NginxSource : ILogSource, ISniffDetail
{
    /// <summary>The <see cref="ParseContext.Options"/> key: a <c>log_format</c> string or a built-in name.</summary>
    public const string LogFormatOption = "nginx.log_format";

    private const int SniffHeadLines = 200;
    private const int SniffTailLines = 50;
    private const int DetectLines = 32;
    private const double CommonLogWeight = 0.8; // the design record calls the 7-field common log the weaker match
    private const double TrailingWeight = 0.8;  // a line that fits a layout with bytes left over
    private const double UnknownLayoutWeight = 0.6; // nginx-like shape, no built-in fits

    private static readonly SearchValues<byte> QuoteOrBackslash = SearchValues.Create("\"\\"u8);

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    public SourceInfo Info { get; } = new("nginx", ["nginx-access", "apache-combined", "apache-common"], "nginx / Apache access log (combined, common, ingress-nginx, custom log_format)", 2);

    public Framing Framing => Framing.Line;

    public FieldSchema Schema { get; } = new(2,
    [
        EventColumn.Ts, EventColumn.Program, EventColumn.EventCode, EventColumn.EventOutcome, EventColumn.UserName,
        EventColumn.SourceIp, EventColumn.UrlPath, EventColumn.HttpStatus, EventColumn.MsgOff, EventColumn.MsgLen, EventColumn.Raw, EventColumn.Extras,
        EventColumn.HttpMethod, EventColumn.HttpVersion, EventColumn.HttpResponseBodyBytes, EventColumn.HttpReferrer, EventColumn.UserAgent, EventColumn.UrlQuery,
    ]);

    public float Sniff(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, SniffContext ctx) => Detect(head, tail, ctx, null).Confidence;

    /// <summary>The detector's entry: <see cref="Detect"/> with the file's <c>nginx.log_format</c> option, if it has one.</summary>
    public SniffDetail SniffDetailed(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, SniffContext ctx, IReadOnlyDictionary<string, string>? options)
    {
        string? logFormat = null;
        options?.TryGetValue(LogFormatOption, out logFormat);
        var detection = Detect(head, tail, ctx, logFormat);
        return new SniffDetail(detection.Confidence, detection.Layout, detection.LayoutKnown);
    }

    /// <summary>
    /// What <see cref="Sniff"/> knows and cannot return: the layout that fits best, or null with the "nginx-like, layout
    /// unknown" confidence when the lines have the shape but no built-in fits. With <paramref name="logFormat"/> set
    /// (the file's <c>nginx.log_format</c> option) only that layout is scored.
    /// </summary>
    public static NginxDetection Detect(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, SniffContext ctx, string? logFormat)
    {
        var headLength = head.Length;
        if (head.StartsWith(Utf8Bom))
        {
            head = head[3..]; // core strips it before Parse; the sniffer sees the file as it is
        }

        NginxLayout? fixedLayout = null;
        if (!string.IsNullOrWhiteSpace(logFormat))
        {
            fixedLayout = NginxLayout.BuiltIn(logFormat.Trim()) ?? (NginxLayout.TryCompile(logFormat, out var compiled, out _) ? compiled : null);
        }

        var scorer = new Scorer(fixedLayout);
        var lines = 0;
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
            scorer.Add(record.Bytes);
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
                scorer.Add(record.Bytes);
            }
        }

        return scorer.Result(lines);
    }

    public void Parse(ReadOnlySequence<byte> framedChunk, ParseContext ctx, IBatchWriter batch)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(batch);

        var state = ctx.Scratch as FileState ?? NewState(ctx);
        var chunk = framedChunk.IsSingleSegment ? framedChunk.FirstSpan : throw new ArgumentException("a framed chunk is always a single segment", nameof(framedChunk));
        var layout = state.Fixed ?? DetectInChunk(chunk);

        Span<int> starts = stackalloc int[NginxLayout.MaxFields];
        Span<int> lengths = stackalloc int[NginxLayout.MaxFields];
        Span<byte> address = stackalloc byte[16];
        Span<byte> number = stackalloc byte[24];

        foreach (var record in framedChunk.Records(Framing, ctx))
        {
            var line = record.Bytes;
            var used = layout;
            TypedValues typed = default;
            var status = used is null ? Status.LayoutMismatch : Tokenise(line, used, starts, lengths, out typed);
            if (status != Status.Ok && state.Fixed is null)
            {
                // Auto mode: a line the chunk's layout does not fit may be another built-in (a common line in a combined
                // file). Another layout counts only when it explains the whole line; with no layout detected at all,
                // the first one that fits is taken, and the first failure's reason is reported.
                foreach (var other in NginxLayout.BuiltIns)
                {
                    if (ReferenceEquals(other, layout))
                    {
                        continue;
                    }

                    var s = Tokenise(line, other, starts, lengths, out var t);
                    if (s == Status.Ok && (layout is null || t.End == line.Length))
                    {
                        used = other;
                        typed = t;
                        status = Status.Ok;
                        break;
                    }

                    if (used is null)
                    {
                        used = other;
                        status = s;
                    }
                }
            }

            if (status != Status.Ok)
            {
                ctx.Reject(line, record.FileOffset, Reason(status));
                continue;
            }

            Write(batch, line, used!, starts, lengths, in typed, record.FileOffset, address, number);
        }
    }

    /// <summary>
    /// Splits one line by <paramref name="layout"/> without writing anything: field positions into
    /// <paramref name="starts"/>/<paramref name="lengths"/>, the typed values into <paramref name="typed"/>.
    /// </summary>
    internal static Status Tokenise(ReadOnlySpan<byte> line, NginxLayout layout, Span<int> starts, Span<int> lengths, out TypedValues typed)
    {
        if (ReferenceEquals(layout, NginxLayout.Combined) && TryScanCombined(line, starts, lengths, out typed))
        {
            return Status.Ok;
        }

        return TokeniseGeneric(line, layout, starts, lengths, out typed);
    }

    /// <summary>
    /// The <see cref="NginxLayout.Combined"/> field table unrolled: the same scans and the same positions as
    /// <see cref="TokeniseGeneric"/> would produce, with the terminators as constants and no per-field loop, which
    /// is what keeps <c>TokeniseNginx</c> within 5 % of the first hand-written parser. Success only: on any mismatch it
    /// returns false and the generic walk decides the reject reason, so no failure semantics live here.
    /// <c>tests/Sources.Tests</c> asserts the two agree on every fixture line and on the generated sample.
    /// </summary>
    internal static bool TryScanCombined(ReadOnlySpan<byte> line, Span<int> starts, Span<int> lengths, out TypedValues typed)
    {
        typed = default;
        starts = starts[..9];
        lengths = lengths[..9];

        // $remote_addr $ident $remote_user [
        var sp = line.IndexOf((byte)' ');
        if (sp <= 0)
        {
            return false;
        }

        starts[0] = 0;
        lengths[0] = sp;
        var pos = sp + 1;
        var rest = line[pos..];
        sp = rest.IndexOf((byte)' ');
        if (sp <= 0)
        {
            return false;
        }

        starts[1] = pos;
        lengths[1] = sp;
        pos += sp + 1;
        rest = line[pos..];
        sp = rest.IndexOf((byte)' ');
        if (sp <= 0 || sp + 1 >= rest.Length || rest[sp + 1] != (byte)'[')
        {
            return false;
        }

        starts[2] = pos;
        lengths[2] = sp;
        pos += sp + 2;
        rest = line[pos..];

        // $time_local] "
        const int T = ClfTimestamp.Length;
        if (rest.Length < T + 3 || rest[T] != (byte)']' || rest[T + 1] != (byte)' ' || rest[T + 2] != (byte)'"')
        {
            return false;
        }

        starts[3] = pos;
        lengths[3] = T;
        pos += T + 3;
        rest = line[pos..];

        // $request"
        var close = ClosingQuote(rest);
        if (close < 0 || close + 1 >= rest.Length || rest[close + 1] != (byte)' ')
        {
            return false;
        }

        starts[4] = pos;
        lengths[4] = close;
        pos += close + 2;
        rest = line[pos..];

        // $status
        sp = rest.IndexOf((byte)' ');
        if (sp <= 0)
        {
            return false;
        }

        starts[5] = pos;
        lengths[5] = sp;
        pos += sp + 1;
        rest = line[pos..];

        // $body_bytes_sent "
        sp = rest.IndexOf((byte)' ');
        if (sp <= 0 || sp + 1 >= rest.Length || rest[sp + 1] != (byte)'"')
        {
            return false;
        }

        starts[6] = pos;
        lengths[6] = sp;
        pos += sp + 2;
        rest = line[pos..];

        // $http_referer" "
        close = ClosingQuote(rest);
        if (close < 0 || close + 2 >= rest.Length || rest[close + 1] != (byte)' ' || rest[close + 2] != (byte)'"')
        {
            return false;
        }

        starts[7] = pos;
        lengths[7] = close;
        pos += close + 3;
        rest = line[pos..];

        // $http_user_agent"
        close = ClosingQuote(rest);
        if (close < 0)
        {
            return false;
        }

        starts[8] = pos;
        lengths[8] = close;
        typed.End = pos + close + 1;

        if (!ClfTimestamp.TryParse(line.Slice(starts[3], T), out typed.UnixMilliseconds))
        {
            return false;
        }

        var status = line.Slice(starts[5], lengths[5]);
        return Utf8Parser.TryParse(status, out typed.HttpStatus, out var consumed) && consumed == status.Length;
    }

    /// <summary>The table-driven walk over any layout (see <see cref="NginxLayout"/>).</summary>
    internal static Status TokeniseGeneric(ReadOnlySpan<byte> line, NginxLayout layout, Span<int> starts, Span<int> lengths, out TypedValues typed)
    {
        typed = default;
        if (line.IsEmpty)
        {
            return Status.EmptyLine;
        }

        var fields = layout.Fields;
        var count = fields.Length;
        starts = starts[..count];   // same length as the loop bound: no bounds check per store
        lengths = lengths[..count];
        var pos = 0;
        if (layout.Prefix.Length > 0)
        {
            if (!line.StartsWith(layout.Prefix))
            {
                return Status.LayoutMismatch;
            }

            pos = layout.Prefix.Length;
        }

        // Pass 1: structure only. Values are located by the layout's literals; nothing is interpreted here, so the
        // loop body has one scan and one small branch per field and no dispatch on the variable kind.
        for (var i = 0; i < count; i++)
        {
            ref readonly var field = ref fields[i];
            var rest = line[pos..];
            var termLength = field.TermLength;
            int length;

            var scan = field.Scan;
            if (scan >= NginxScan.ToEnd)
            {
                // Unquoted: up to the terminator's first byte (a space for the last field, which may also end the line).
                // A space-delimited token is never empty; a value between explicit literals may be (ingress-nginx logs "[]").
                length = rest.IndexOf(field.TermFirst);
                if (length < 0)
                {
                    if (scan == NginxScan.ToByte)
                    {
                        return FailureOf(field.Kind, Status.LayoutMismatch);
                    }

                    length = rest.Length;
                }
                else if (length == 0 && field.TermFirst == (byte)' ')
                {
                    return FailureOf(field.Kind, Status.LayoutMismatch);
                }
            }
            else if (scan == NginxScan.Quoted)
            {
                length = ClosingQuote(rest);
                if (length < 0)
                {
                    return Status.UnterminatedQuote;
                }
            }
            else
            {
                length = field.FixedWidth;
                if (rest.Length < length || (termLength > 0 && (rest.Length == length || rest[length] != field.TermFirst)))
                {
                    return FailureOf(field.Kind, Status.LayoutMismatch);
                }
            }

            // The terminator's first byte is where the scan stopped; the rest of the literal must follow it.
            if (termLength > 1)
            {
                var at = length + 1;
                if (at >= rest.Length || rest[at] != field.TermSecond
                    || (termLength > 2 && (at + 1 >= rest.Length || rest[at + 1] != field.TermThird))
                    || (termLength > 3 && !rest[(at + 2)..].StartsWith(field.Terminator.AsSpan(3))))
                {
                    // The literal after this value is wrong: blame the field that should have started there.
                    return i + 1 < count ? FailureOf(fields[i + 1].Kind, Status.LayoutMismatch) : Status.LayoutMismatch;
                }
            }

            starts[i] = pos;
            lengths[i] = length;
            pos += length + termLength;
        }

        typed.End = pos;

        // Pass 1b: the two typed fields, by their compiled index.
        var time = layout.TimeIndex;
        if (time >= 0)
        {
            var value = line.Slice(starts[time], lengths[time]);
            var ok = fields[time].Kind switch
            {
                NginxVariableKind.TimeLocal => ClfTimestamp.TryParse(value, out typed.UnixMilliseconds),
                NginxVariableKind.TimeIso8601 => Iso8601.TryParse(value, out typed.UnixMilliseconds),
                _ => TryParseMsec(value, out typed.UnixMilliseconds),
            };
            if (!ok)
            {
                return Status.BadTimestamp;
            }
        }

        var status = layout.StatusIndex;
        if (status >= 0)
        {
            var value = line.Slice(starts[status], lengths[status]);
            if (!Utf8Parser.TryParse(value, out typed.HttpStatus, out var consumed) || consumed != value.Length)
            {
                return Status.BadStatus;
            }
        }

        return Status.Ok;
    }

    private static void Write(IBatchWriter batch, ReadOnlySpan<byte> line, NginxLayout layout, ReadOnlySpan<int> starts, ReadOnlySpan<int> lengths, in TypedValues typed, long fileOffset, Span<byte> address, Span<byte> number)
    {
        batch.AppendUInt64(EventColumn.FileOffset, (ulong)fileOffset);
        batch.AppendString(EventColumn.Program, "nginx"u8);

        var fields = layout.Fields;
        if (layout.TimeIndex >= 0)
        {
            batch.AppendTimestamp(typed.UnixMilliseconds);
        }

        var at = layout.SourceIpIndex;
        if (at >= 0)
        {
            var value = line.Slice(starts[at], lengths[at]);
            var addressLength = IpText.TryParse(value, address);
            if (addressLength > 0)
            {
                batch.AppendIp(EventColumn.SourceIp, address[..addressLength]);
            }
            else if (IsSet(value))
            {
                batch.AppendExtra("source.address"u8, value); // HostnameLookups, "unknown", a proxy's placeholder
            }
        }

        at = layout.UserIndex;
        if (at >= 0)
        {
            var value = line.Slice(starts[at], lengths[at]);
            if (IsSet(value))
            {
                batch.AppendString(EventColumn.UserName, value);
            }
        }

        at = layout.RequestIndex;
        if (at >= 0)
        {
            WriteRequest(batch, line.Slice(starts[at], lengths[at]));
        }

        if (layout.StatusIndex >= 0)
        {
            batch.AppendUInt16(EventColumn.HttpStatus, typed.HttpStatus);
            batch.AppendUInt32(EventColumn.EventCode, typed.HttpStatus);
            batch.AppendString(EventColumn.EventOutcome, typed.HttpStatus < 400 ? "success"u8 : "failure"u8);
        }

        foreach (var d in layout.DurationIndexes)
        {
            var value = line.Slice(starts[d], lengths[d]);
            if (IsSet(value))
            {
                if (TryFormatNanoseconds(value, number, out var written))
                {
                    batch.AppendExtra(fields[d].KeyBytes, number[..written]);
                }
                else
                {
                    batch.AppendExtra(fields[d].KeyBytes, value); // not a decimal number of seconds: as logged
                }
            }
        }

        foreach (var c in layout.ColumnIndexes)
        {
            var value = line.Slice(starts[c], lengths[c]);
            if (IsSet(value))
            {
                batch.AppendString(fields[c].Column, value); // referrer, user agent, method, version, query: promoted columns
            }
        }

        foreach (var b in layout.BodyBytesIndexes)
        {
            var value = line.Slice(starts[b], lengths[b]);
            if (IsSet(value))
            {
                if (Utf8Parser.TryParse(value, out ulong bodyBytes, out var digits) && digits == value.Length)
                {
                    batch.AppendUInt64(EventColumn.HttpResponseBodyBytes, bodyBytes);
                }
                else
                {
                    batch.AppendExtra(fields[b].KeyBytes, value); // not a number: as logged, under the ECS key
                }
            }
        }

        foreach (var e in layout.ExtraIndexes)
        {
            var value = line.Slice(starts[e], lengths[e]);
            if (IsSet(value))
            {
                batch.AppendExtra(fields[e].KeyBytes, value);
            }
        }

        if (typed.End < line.Length)
        {
            var trailing = line[typed.End..].TrimStart((byte)' ');
            if (!trailing.IsEmpty)
            {
                batch.AppendExtra("trailing"u8, trailing); // custom fields the layout does not name
            }
        }

        if (layout.MessageFirst >= 0)
        {
            var first = layout.MessageFirst;
            var last = layout.MessageLast;
            var start = starts[first] - (fields[first].Quoted ? 1 : 0);
            var end = starts[last] + lengths[last] + (fields[last].Quoted ? 1 : 0);
            batch.AppendMessage(start, end - start); // message = this slice of raw (an alias, not a copy)
        }
        else
        {
            batch.AppendMessage(0, line.Length);
        }

        batch.AppendString(EventColumn.Raw, line); // byte for byte, always
        batch.CommitRow();
    }

    private static FileState NewState(ParseContext ctx)
    {
        NginxLayout? fixedLayout = null;
        var option = ctx.Option(LogFormatOption);
        if (!string.IsNullOrWhiteSpace(option))
        {
            fixedLayout = NginxLayout.Resolve(option); // a bad format is a configuration error, not bad input: let it throw
        }

        var state = new FileState(fixedLayout);
        ctx.Scratch = state;
        return state;
    }

    /// <summary>The built-in that fits the first lines of a chunk best; null when none fits any. Deterministic per chunk.</summary>
    private static NginxLayout? DetectInChunk(ReadOnlySpan<byte> chunk)
    {
        Span<int> starts = stackalloc int[NginxLayout.MaxFields];
        Span<int> lengths = stackalloc int[NginxLayout.MaxFields];
        Span<double> scores = stackalloc double[NginxLayout.BuiltIns.Length];
        var lines = 0;
        foreach (var record in new RecordEnumerator(chunk, Framing.Line, 0))
        {
            if (record.Bytes.IsEmpty)
            {
                continue;
            }

            if (++lines > DetectLines)
            {
                break;
            }

            for (var l = 0; l < NginxLayout.BuiltIns.Length; l++)
            {
                var candidate = NginxLayout.BuiltIns[l];
                if (Tokenise(record.Bytes, candidate, starts, lengths, out var typed) == Status.Ok)
                {
                    var score = typed.End == record.Bytes.Length ? 1.0 : TrailingWeight;
                    scores[l] += ReferenceEquals(candidate, NginxLayout.Common) ? score * CommonLogWeight : score;
                }
            }
        }

        var best = -1;
        var bestScore = 0.0;
        for (var l = 0; l < scores.Length; l++)
        {
            if (scores[l] > bestScore)
            {
                best = l;
                bestScore = scores[l];
            }
        }

        return best < 0 ? null : NginxLayout.BuiltIns[best];
    }

    private static void WriteRequest(IBatchWriter batch, ReadOnlySpan<byte> request)
    {
        if (!IsSet(request))
        {
            return; // nginx logs "-" when it read no request line
        }

        var first = request.IndexOf((byte)' ');
        if (first < 0)
        {
            batch.AppendExtra("http.request.line"u8, request); // a TLS hello on port 80, a scanner's garbage: keep it whole
            return;
        }

        var last = request.LastIndexOf((byte)' ');
        var method = request[..first];
        var target = last == first ? request[(first + 1)..] : request[(first + 1)..last];
        var protocol = last == first ? default : request[(last + 1)..];

        batch.AppendString(EventColumn.HttpMethod, method); // LowCardinality: lowercased by the writer

        var query = target.IndexOf((byte)'?');
        batch.AppendString(EventColumn.UrlPath, query < 0 ? target : target[..query]);
        if (query >= 0)
        {
            batch.AppendString(EventColumn.UrlQuery, target[(query + 1)..]);
        }

        if (!protocol.IsEmpty)
        {
            batch.AppendString(EventColumn.HttpVersion, protocol.StartsWith("HTTP/"u8) ? protocol[5..] : protocol);
        }
    }

    /// <summary><c>$msec</c>: <c>1772323200.123</c> (seconds, optional fraction) to milliseconds.</summary>
    private static bool TryParseMsec(ReadOnlySpan<byte> value, out long unixMilliseconds)
    {
        unixMilliseconds = 0;
        if (!Utf8Parser.TryParse(value, out long seconds, out var consumed) || consumed == 0 || seconds < 0)
        {
            return false;
        }

        var millis = 0;
        if (consumed < value.Length)
        {
            if (value[consumed] != (byte)'.')
            {
                return false;
            }

            var digits = 0;
            for (var i = consumed + 1; i < value.Length; i++)
            {
                var d = value[i] - '0';
                if ((uint)d > 9)
                {
                    return false;
                }

                if (digits < 3)
                {
                    millis = (millis * 10) + d;
                }

                digits++;
            }

            while (digits < 3)
            {
                millis *= 10;
                digits++;
            }
        }

        unixMilliseconds = (seconds * 1000) + millis;
        return true;
    }

    /// <summary>Seconds with up to 9 fraction digits (<c>0.123</c>) to integer nanoseconds as ASCII (ECS <c>event.duration</c>).</summary>
    private static bool TryFormatNanoseconds(ReadOnlySpan<byte> value, Span<byte> destination, out int written)
    {
        written = 0;
        if (!Utf8Parser.TryParse(value, out long seconds, out var consumed) || consumed == 0 || seconds < 0 || seconds > 9_000_000_000L)
        {
            return false;
        }

        long nanos = 0;
        if (consumed < value.Length)
        {
            if (value[consumed] != (byte)'.')
            {
                return false;
            }

            var digits = 0;
            for (var i = consumed + 1; i < value.Length; i++)
            {
                var d = value[i] - '0';
                if ((uint)d > 9)
                {
                    return false;
                }

                if (digits < 9)
                {
                    nanos = (nanos * 10) + d;
                }

                digits++;
            }

            while (digits < 9)
            {
                nanos *= 10;
                digits++;
            }
        }

        return Utf8Formatter.TryFormat((seconds * 1_000_000_000L) + nanos, destination, out written);
    }

    /// <summary>Index of the closing quote of a quoted field whose opening quote has been consumed; a backslash escapes the next byte.</summary>
    private static int ClosingQuote(ReadOnlySpan<byte> text)
    {
        var position = 0;
        while (true)
        {
            var i = text[position..].IndexOfAny(QuoteOrBackslash);
            if (i < 0)
            {
                return -1;
            }

            position += i;
            if (text[position] == (byte)'"')
            {
                return position;
            }

            position += 2; // the backslash and the byte it escapes
            if (position >= text.Length)
            {
                return -1;
            }
        }
    }

    private static bool IsSet(ReadOnlySpan<byte> field) => !field.IsEmpty && !(field.Length == 1 && field[0] == (byte)'-');

    /// <summary>The reject reason for a structural failure at a field of <paramref name="kind"/>.</summary>
    private static Status FailureOf(NginxVariableKind kind, Status fallback) => kind switch
    {
        NginxVariableKind.TimeLocal or NginxVariableKind.TimeIso8601 or NginxVariableKind.Msec => Status.BadTimestamp,
        NginxVariableKind.Request => Status.NoRequest,
        NginxVariableKind.Status => Status.BadStatus,
        _ => fallback,
    };

    private static ReadOnlySpan<byte> Reason(Status status) => status switch
    {
        Status.EmptyLine => "empty line"u8,
        Status.LayoutMismatch => "layout mismatch"u8,
        Status.BadTimestamp => "bad timestamp"u8,
        Status.NoRequest => "no request"u8,
        Status.UnterminatedQuote => "unterminated quote"u8,
        Status.BadStatus => "bad status"u8,
        _ => "unparseable"u8,
    };

    /// <summary>Shape check for "nginx-like, layout unknown": an address-like first token, a bracketed CLF timestamp, a quoted request after it.</summary>
    private static bool LooksNginxLike(ReadOnlySpan<byte> line)
    {
        var open = line.IndexOf((byte)'[');
        if (open < 0 || open + ClfTimestamp.Length + 1 >= line.Length || line[open + ClfTimestamp.Length + 1] != (byte)']'
            || !ClfTimestamp.TryParse(line.Slice(open + 1, ClfTimestamp.Length), out _))
        {
            return false;
        }

        var after = line[(open + ClfTimestamp.Length + 2)..];
        return after.Length > 2 && after[0] == (byte)' ' && after[1] == (byte)'"' && line.IndexOf((byte)' ') > 0;
    }

    internal enum Status : byte
    {
        Ok,
        EmptyLine,
        LayoutMismatch,
        BadTimestamp,
        NoRequest,
        UnterminatedQuote,
        BadStatus,
    }

    /// <summary>Values a line's typed fields parse to; filled by <see cref="Tokenise"/>.</summary>
    internal struct TypedValues
    {
        public long UnixMilliseconds;
        public ushort HttpStatus;

        /// <summary>Offset just past the last field and its terminator; anything after it is trailing.</summary>
        public int End;
    }

    private sealed class FileState(NginxLayout? fixedLayout)
    {
        public NginxLayout? Fixed { get; } = fixedLayout;
    }

    /// <summary>Scores sampled lines for <see cref="Detect"/>: per layout, and per line the best any layout achieves.</summary>
    private sealed class Scorer
    {
        private readonly NginxLayout? _fixed;
        private readonly int[] _starts;
        private readonly int[] _lengths;
        private readonly double[] _perLayout;
        private double _total;
        private double _unknown;

        public Scorer(NginxLayout? fixedLayout)
        {
            _fixed = fixedLayout;
            _starts = new int[NginxLayout.MaxFields];   // per Detect call, not per row
            _lengths = new int[NginxLayout.MaxFields];
            _perLayout = new double[NginxLayout.BuiltIns.Length];
        }

        public void Add(ReadOnlySpan<byte> line)
        {
            if (_fixed is not null)
            {
                if (Tokenise(line, _fixed, _starts, _lengths, out var t) == Status.Ok)
                {
                    _total += t.End == line.Length ? 1.0 : TrailingWeight;
                }

                return;
            }

            var best = 0.0;
            for (var l = 0; l < NginxLayout.BuiltIns.Length; l++)
            {
                var layout = NginxLayout.BuiltIns[l];
                if (Tokenise(line, layout, _starts, _lengths, out var t) != Status.Ok)
                {
                    continue;
                }

                var score = t.End == line.Length ? 1.0 : TrailingWeight;
                if (ReferenceEquals(layout, NginxLayout.Common))
                {
                    score *= CommonLogWeight;
                }

                _perLayout[l] += score;
                best = Math.Max(best, score);
            }

            if (best == 0 && LooksNginxLike(line))
            {
                best = UnknownLayoutWeight;
                _unknown += 1;
            }

            _total += best;
        }

        public NginxDetection Result(int lines)
        {
            if (lines == 0)
            {
                return new NginxDetection(null, 0f, false);
            }

            if (_fixed is not null)
            {
                return new NginxDetection(_fixed.Name, (float)(_total / lines), true);
            }

            var best = -1;
            var bestScore = 0.0;
            for (var l = 0; l < _perLayout.Length; l++)
            {
                if (_perLayout[l] > bestScore)
                {
                    best = l;
                    bestScore = _perLayout[l];
                }
            }

            var layoutKnown = best >= 0 && bestScore >= _unknown * UnknownLayoutWeight;
            return new NginxDetection(layoutKnown ? NginxLayout.BuiltIns[best].Name : null, (float)(_total / lines), layoutKnown);
        }
    }
}

/// <summary>
/// Result of <see cref="NginxSource.Detect"/>: the built-in (or configured) layout name when one fits, else null with
/// <see cref="LayoutKnown"/> false and a confidence of at most 0.6 ("nginx, layout unknown").
/// </summary>
public readonly record struct NginxDetection(string? Layout, float Confidence, bool LayoutKnown);
