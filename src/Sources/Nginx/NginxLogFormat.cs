using System.Collections.Immutable;
using System.Text;
using Urlog.Core.Sources;

namespace Urlog.Sources.Nginx;

/// <summary>What a layout field becomes in a row. Everything not listed is an <see cref="Extra"/> under its catalog key.</summary>
public enum NginxVariableKind : byte
{
    /// <summary><c>$remote_addr</c>: <c>source_ip</c>, or <c>extras['source.address']</c> when not an address.</summary>
    SourceIp,

    /// <summary><c>$remote_user</c>: <c>user_name</c> unless <c>-</c>.</summary>
    User,

    /// <summary><c>$ident</c>, the Apache <c>%l</c> pseudo-variable: <c>extras['ident']</c> unless <c>-</c>.</summary>
    Ident,

    /// <summary><c>$time_local</c>: <c>dd/Mon/yyyy:HH:MM:SS +/-zzzz</c>, fixed 26 bytes, to <c>ts</c>.</summary>
    TimeLocal,

    /// <summary><c>$time_iso8601</c>: <c>YYYY-MM-DDTHH:MM:SS+/-HH:MM</c>, fixed 25 bytes, to <c>ts</c>.</summary>
    TimeIso8601,

    /// <summary><c>$msec</c>: epoch seconds with milliseconds, to <c>ts</c>.</summary>
    Msec,

    /// <summary><c>$request</c>: method, target and protocol to <c>url_path</c> and extras.</summary>
    Request,

    /// <summary><c>$status</c>: <c>http_status</c>, <c>event_code</c>, <c>event_outcome</c>.</summary>
    Status,

    /// <summary><c>$request_time</c> and other seconds-with-fraction durations: integer nanoseconds in extras (ECS <c>event.duration</c>).</summary>
    DurationSeconds,

    /// <summary>A promoted String or LowCardinality column (referrer, user agent, method, version, query): <see cref="NginxField.Column"/> unless <c>-</c>.</summary>
    TextColumn,

    /// <summary><c>$body_bytes_sent</c>: <c>http_response_body_bytes</c> when all digits, else <c>extras['http.response.body.bytes']</c> as logged.</summary>
    BodyBytes,

    /// <summary>Anything else: <c>extras[key]</c> unless <c>-</c>.</summary>
    Extra,
}

/// <summary>How the tokeniser finds the end of a field's value.</summary>
internal enum NginxScan : byte
{
    /// <summary>Up to the closing quote, backslash-escape aware; the terminator starts with that quote.</summary>
    Quoted,

    /// <summary>Exactly <see cref="NginxField.FixedWidth"/> bytes.</summary>
    Fixed,

    /// <summary>Up to the next space or the end of the line: the last field of the layout.</summary>
    ToEnd,

    /// <summary>Up to the first byte of the terminator.</summary>
    ToByte,
}

/// <summary>
/// One variable of a compiled layout and the literal that ends it. A struct so the per-line loop reads it by reference
/// from the layout's array with no pointer chase; the terminator's first byte and the scan kind are precomputed.
/// </summary>
public readonly struct NginxField
{
    internal NginxField(string variable, NginxVariableKind kind, string key, bool quoted, byte[] terminator, int fixedWidth)
    {
        Variable = variable;
        Kind = kind;
        Key = key;
        KeyBytes = Encoding.UTF8.GetBytes(key);
        Quoted = quoted;
        Terminator = terminator;
        FixedWidth = fixedWidth;
        TermLength = terminator.Length;
        // The last unquoted field scans to the next space like any other, but may also end with the line.
        TermFirst = terminator.Length > 0 ? terminator[0] : (byte)' ';
        TermSecond = terminator.Length > 1 ? terminator[1] : (byte)0;
        TermThird = terminator.Length > 2 ? terminator[2] : (byte)0;
        Scan = quoted ? NginxScan.Quoted : fixedWidth > 0 ? NginxScan.Fixed : terminator.Length == 0 ? NginxScan.ToEnd : NginxScan.ToByte;
        Column = kind switch
        {
            NginxVariableKind.TextColumn => key switch
            {
                "http_referrer" => EventColumn.HttpReferrer,
                "user_agent" => EventColumn.UserAgent,
                "http_method" => EventColumn.HttpMethod,
                "http_version" => EventColumn.HttpVersion,
                "url_query" => EventColumn.UrlQuery,
                _ => throw new ArgumentException($"no promoted column named {key}", nameof(key)),
            },
            NginxVariableKind.BodyBytes => EventColumn.HttpResponseBodyBytes,
            _ => EventColumn.Extras,
        };
    }

    /// <summary>The column a <see cref="NginxVariableKind.TextColumn"/> or <see cref="NginxVariableKind.BodyBytes"/> field writes; <see cref="EventColumn.Extras"/> otherwise.</summary>
    public EventColumn Column { get; }

    internal NginxScan Scan { get; }

    internal int TermLength { get; }

    internal byte TermFirst { get; }

    internal byte TermSecond { get; }

    internal byte TermThird { get; }

    /// <summary>The variable name without <c>$</c>.</summary>
    public string Variable { get; }

    public NginxVariableKind Kind { get; }

    /// <summary>Catalog key the value is stored under in <c>extras</c> (empty for typed columns; the column name for a promoted column; the extras fallback key for <see cref="NginxVariableKind.BodyBytes"/>).</summary>
    public string Key { get; }

    internal byte[] KeyBytes { get; }

    /// <summary>True when the format encloses the variable in double quotes: the value is scanned with escape handling.</summary>
    public bool Quoted { get; }

    /// <summary>The literal that follows the value, including the closing quote of a quoted field; empty at the end of the line.</summary>
    public byte[] Terminator { get; }

    /// <summary>Width of a fixed-width value (timestamps), else 0.</summary>
    public int FixedWidth { get; }

    public override string ToString() => $"${Variable}" + (Quoted ? " (quoted)" : "") + " -> " + (Kind == NginxVariableKind.Extra || Kind == NginxVariableKind.DurationSeconds ? "extras[" + Key + "]" : Kind is NginxVariableKind.TextColumn or NginxVariableKind.BodyBytes ? EventColumns.Name(Column) : Kind.ToString());
}

/// <summary>
/// An nginx <c>log_format</c> compiled into a field table: a literal prefix, then for each variable the kind it maps
/// to and the literal that terminates it. Compiled once per file (or once, for the built-ins); the tokeniser walks the
/// table per line with the same vectorised scans as the hand-written parser, allocating nothing per row.
/// <para>
/// Syntax: <c>$name</c> or <c>${name}</c> (letters, digits, underscore); everything else is literal. Two variables need a
/// literal between them. A variable between double quotes is scanned quote-aware (a backslash escapes the next
/// byte). <c>$time_local</c> and <c>$time_iso8601</c> are fixed width. An unquoted value ends at the next space (or the
/// line end); bytes after the last field are <c>extras['trailing']</c>. <c>$ident</c> is accepted as the Apache
/// <c>%l</c> field so the built-ins read Apache lines too.
/// </para>
/// </summary>
public sealed class NginxLayout
{
    /// <summary>Most fields a layout may have; the tokeniser keeps per-field positions on the stack.</summary>
    public const int MaxFields = 64;

    /// <summary>Apache/nginx <c>combined</c> (<c>$ident</c> in place of the literal <c>-</c>, so Apache's <c>%l</c> reads too).</summary>
    public static readonly NginxLayout Combined = Compile("$remote_addr $ident $remote_user [$time_local] \"$request\" $status $body_bytes_sent \"$http_referer\" \"$http_user_agent\"", "combined");

    /// <summary>Apache/nginx <c>common</c>: combined without referer and user agent.</summary>
    public static readonly NginxLayout Common = Compile("$remote_addr $ident $remote_user [$time_local] \"$request\" $status $body_bytes_sent", "common");

    /// <summary>The default log format of the Kubernetes ingress-nginx controller.</summary>
    public static readonly NginxLayout IngressNginx = Compile(
        "$remote_addr - $remote_user [$time_local] \"$request\" $status $body_bytes_sent \"$http_referer\" \"$http_user_agent\" " +
        "$request_length $request_time [$proxy_upstream_name] [$proxy_alternative_upstream_name] $upstream_addr $upstream_response_length $upstream_response_time $upstream_status $req_id",
        "ingress-nginx");

    /// <summary>The auto-detected layouts, most specific first: a line that fits an earlier one is reported as that one.</summary>
    public static ImmutableArray<NginxLayout> BuiltIns { get; } = [IngressNginx, Combined, Common];

    private NginxLayout(string name, string format, byte[] prefix, NginxField[] fields, int messageFirst, int messageLast)
    {
        Name = name;
        Format = format;
        Prefix = prefix;
        Fields = fields;
        MessageFirst = messageFirst;
        MessageLast = messageLast;
    }

    /// <summary>Built-in name, or <c>custom</c>.</summary>
    public string Name { get; }

    /// <summary>The <c>log_format</c> string this was compiled from.</summary>
    public string Format { get; }

    /// <summary>Literal before the first variable (usually empty).</summary>
    public byte[] Prefix { get; }

    /// <summary>The variables in line order.</summary>
    public NginxField[] Fields { get; }

    /// <summary>Index of the first field of the <c>message</c> slice (<c>$request</c>, else <c>$status</c>), or -1 for the whole line.</summary>
    public int MessageFirst { get; }

    /// <summary>Index of the last field of the <c>message</c> slice: the run of request, status and bytes fields after <see cref="MessageFirst"/>.</summary>
    public int MessageLast { get; }

    /// <summary>A built-in by name (case-insensitive): <c>combined</c>, <c>common</c>, <c>ingress-nginx</c>.</summary>
    public static NginxLayout? BuiltIn(string name)
    {
        foreach (var layout in BuiltIns)
        {
            if (string.Equals(layout.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return layout;
            }
        }

        return null;
    }

    /// <summary>A built-in by name, else the compiled <paramref name="formatOrName"/>; throws <see cref="FormatException"/> for an invalid format.</summary>
    public static NginxLayout Resolve(string formatOrName)
    {
        ArgumentNullException.ThrowIfNull(formatOrName);
        return BuiltIn(formatOrName.Trim()) ?? Compile(formatOrName);
    }

    /// <summary>Compiles a <c>log_format</c> string; throws <see cref="FormatException"/> with the reason when it cannot be used.</summary>
    public static NginxLayout Compile(string format, string name = "custom")
    {
        if (!TryCompile(format, out var layout, out var error, name))
        {
            throw new FormatException(error);
        }

        return layout;
    }

    /// <summary>Compiles a <c>log_format</c> string without throwing.</summary>
    public static bool TryCompile(string format, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out NginxLayout? layout, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error, string name = "custom")
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(name);
        layout = null;
        error = null;

        // Tokenise into alternating literal / variable parts.
        var literals = new List<string>();
        var variables = new List<string>();
        var literal = new StringBuilder();
        var i = 0;
        while (i < format.Length)
        {
            var c = format[i];
            if (c != '$')
            {
                literal.Append(c);
                i++;
                continue;
            }

            i++;
            var braced = i < format.Length && format[i] == '{';
            if (braced)
            {
                i++;
            }

            var start = i;
            while (i < format.Length && (char.IsAsciiLetterOrDigit(format[i]) || format[i] == '_'))
            {
                i++;
            }

            if (i == start)
            {
                error = $"log_format: '$' at position {start - 1} is not followed by a variable name";
                return false;
            }

            if (braced)
            {
                if (i >= format.Length || format[i] != '}')
                {
                    error = $"log_format: '${{' at position {start - 2} is not closed";
                    return false;
                }

                i++;
            }

            if (variables.Count > 0 && literal.Length == 0)
            {
                error = $"log_format: ${variables[^1]} and ${format[start..i].TrimEnd('}')} have no literal between them, so the line cannot be split";
                return false;
            }

            literals.Add(literal.ToString());
            literal.Clear();
            variables.Add(format[start..i].TrimEnd('}'));
        }

        literals.Add(literal.ToString()); // the literal after the last variable (may be empty)

        if (variables.Count == 0)
        {
            error = "log_format: no variables";
            return false;
        }

        if (variables.Count > MaxFields)
        {
            error = $"log_format: {variables.Count} variables, at most {MaxFields} are supported";
            return false;
        }

        var fields = new NginxField[variables.Count];
        for (var f = 0; f < fields.Length; f++)
        {
            var before = literals[f];
            var after = literals[f + 1];
            var quoted = before.EndsWith('"') && after.StartsWith('"');
            var (kind, key, width) = Map(variables[f]);
            if (quoted && width > 0)
            {
                width = 0; // a quoted timestamp is scanned like any quoted field
            }

            fields[f] = new NginxField(variables[f], kind, key, quoted, Encoding.UTF8.GetBytes(after), width);
        }

        // message = the run of request / status / bytes fields starting at $request (else $status).
        var first = IndexOfKind(fields, NginxVariableKind.Request);
        if (first < 0)
        {
            first = IndexOfKind(fields, NginxVariableKind.Status);
        }

        var last = first;
        while (last >= 0 && last + 1 < fields.Length && IsMessageField(fields[last + 1]))
        {
            last++;
        }

        // Typed fields by index (the last occurrence wins, as a repeated column write would), extras as an index list:
        // the writer runs straight-line code for the typed columns and one tight loop for extras, with no dispatch per field.
        var extras = new List<int>();
        var durations = new List<int>();
        var columns = new List<int>();
        var bodyBytes = new List<int>();
        for (var f = 0; f < fields.Length; f++)
        {
            switch (fields[f].Kind)
            {
                case NginxVariableKind.Extra:
                case NginxVariableKind.Ident:
                    extras.Add(f);
                    break;
                case NginxVariableKind.DurationSeconds:
                    durations.Add(f);
                    break;
                case NginxVariableKind.TextColumn:
                    columns.Add(f);
                    break;
                case NginxVariableKind.BodyBytes:
                    bodyBytes.Add(f);
                    break;
                default:
                    break;
            }
        }

        layout = new NginxLayout(name, format, Encoding.UTF8.GetBytes(literals[0]), fields, first, last)
        {
            TimeIndex = LastIndexOfKind(fields, NginxVariableKind.TimeLocal, NginxVariableKind.TimeIso8601, NginxVariableKind.Msec),
            StatusIndex = LastIndexOfKind(fields, NginxVariableKind.Status, NginxVariableKind.Status, NginxVariableKind.Status),
            SourceIpIndex = LastIndexOfKind(fields, NginxVariableKind.SourceIp, NginxVariableKind.SourceIp, NginxVariableKind.SourceIp),
            UserIndex = LastIndexOfKind(fields, NginxVariableKind.User, NginxVariableKind.User, NginxVariableKind.User),
            RequestIndex = LastIndexOfKind(fields, NginxVariableKind.Request, NginxVariableKind.Request, NginxVariableKind.Request),
            ExtraIndexes = [.. extras],
            DurationIndexes = [.. durations],
            ColumnIndexes = [.. columns],
            BodyBytesIndexes = [.. bodyBytes],
        };
        return true;
    }

    /// <summary>Index of the field that gives <c>ts</c> (the last timestamp variable), or -1.</summary>
    internal int TimeIndex { get; private init; } = -1;

    /// <summary>Index of the last <c>$status</c> field, or -1.</summary>
    internal int StatusIndex { get; private init; } = -1;

    internal int SourceIpIndex { get; private init; } = -1;

    internal int UserIndex { get; private init; } = -1;

    internal int RequestIndex { get; private init; } = -1;

    /// <summary>Fields written to <c>extras</c> as logged (<see cref="NginxVariableKind.Extra"/> and <see cref="NginxVariableKind.Ident"/>), in layout order.</summary>
    internal int[] ExtraIndexes { get; private init; } = [];

    internal int[] DurationIndexes { get; private init; } = [];

    /// <summary>Fields written to a promoted String/LowCardinality column (<see cref="NginxVariableKind.TextColumn"/>), in layout order.</summary>
    internal int[] ColumnIndexes { get; private init; } = [];

    /// <summary>Fields of <see cref="NginxVariableKind.BodyBytes"/>, in layout order.</summary>
    internal int[] BodyBytesIndexes { get; private init; } = [];

    private static int LastIndexOfKind(NginxField[] fields, NginxVariableKind a, NginxVariableKind b, NginxVariableKind c)
    {
        for (var i = fields.Length - 1; i >= 0; i--)
        {
            var kind = fields[i].Kind;
            if (kind == a || kind == b || kind == c)
            {
                return i;
            }
        }

        return -1;
    }

    public override string ToString() => Name + ": " + Format;

    private static bool IsMessageField(NginxField field) =>
        field.Kind is NginxVariableKind.Request or NginxVariableKind.Status or NginxVariableKind.BodyBytes
        || field.Key is "http.response.bytes";

    private static int IndexOfKind(NginxField[] fields, NginxVariableKind kind)
    {
        for (var i = 0; i < fields.Length; i++)
        {
            if (fields[i].Kind == kind)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Variable name to (kind, extras key, fixed width). ECS names where one exists; unknown variables keep their name.</summary>
    private static (NginxVariableKind Kind, string Key, int Width) Map(string variable)
    {
        switch (variable)
        {
            case "remote_addr": return (NginxVariableKind.SourceIp, "", 0);
            case "remote_user": return (NginxVariableKind.User, "", 0);
            case "ident": return (NginxVariableKind.Ident, "ident", 0);
            case "time_local": return (NginxVariableKind.TimeLocal, "", ClfTimestamp.Length);
            case "time_iso8601": return (NginxVariableKind.TimeIso8601, "", 25);
            case "msec": return (NginxVariableKind.Msec, "", 0);
            case "request": return (NginxVariableKind.Request, "", 0);
            case "status": return (NginxVariableKind.Status, "", 0);
            case "request_time": return (NginxVariableKind.DurationSeconds, "event.duration", 0);
            case "body_bytes_sent": return (NginxVariableKind.BodyBytes, "http.response.body.bytes", 0);
            case "bytes_sent": return (NginxVariableKind.Extra, "http.response.bytes", 0);
            case "request_length": return (NginxVariableKind.Extra, "http.request.bytes", 0);
            case "http_referer": return (NginxVariableKind.TextColumn, "http_referrer", 0);
            case "http_user_agent": return (NginxVariableKind.TextColumn, "user_agent", 0);
            case "http_x_forwarded_for": return (NginxVariableKind.Extra, "network.forwarded_ip", 0);
            case "host": return (NginxVariableKind.Extra, "url.domain", 0);
            case "server_name": return (NginxVariableKind.Extra, "server.domain", 0);
            case "server_protocol": return (NginxVariableKind.TextColumn, "http_version", 0);
            case "request_method": return (NginxVariableKind.TextColumn, "http_method", 0);
            case "request_uri": return (NginxVariableKind.Extra, "url.original", 0);
            case "args":
            case "query_string": return (NginxVariableKind.TextColumn, "url_query", 0);
            case "scheme": return (NginxVariableKind.Extra, "url.scheme", 0);
            case "req_id":
            case "request_id": return (NginxVariableKind.Extra, "http.request.id", 0);
            case "geoip_country_code":
            case "geoip2_data_country_code": return (NginxVariableKind.Extra, "source.geo.country_iso_code", 0);
            default:
                if (variable.StartsWith("upstream_", StringComparison.Ordinal))
                {
                    return (NginxVariableKind.Extra, "upstream." + variable["upstream_".Length..], 0);
                }

                return (NginxVariableKind.Extra, variable, 0);
        }
    }
}
