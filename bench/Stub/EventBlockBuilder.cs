using System.Buffers.Text;
using System.Net;
using System.Text;
using ClickHouse.Driver.Tcp;
using Urlog.Bench.Fixtures;
using Urlog.Core.Sources;

namespace Urlog.Bench.Stub;

/// <summary>
/// One pre-built columnar block for the pinned <c>events</c> schema (design record, "ClickHouse schema"), sorted by
/// <c>(case_id, host, ts)</c>. Built once in GlobalSetup; the benches only measure the insert.
/// </summary>
public sealed class EventBlock
{
    /// <summary>The insertable columns in table order (the <c>message</c> alias is not one), from the column metadata.</summary>
    public static readonly string[] ColumnNames = [.. EventColumns.All.ToArray().Select(EventColumns.Name)];

    public static readonly IReadOnlyDictionary<string, string> ColumnTypes = EventColumns.All.ToArray().ToDictionary(EventColumns.Name, EventColumns.ClickHouseType);

    public required int Rows { get; init; }

    public required long RawBytes { get; init; }

    public required ulong[] CaseId { get; init; }

    public required ulong[] HostId { get; init; }

    public required ulong[] FileId { get; init; }

    public required ulong[] FileOffset { get; init; }

    public required uint[] ParseGeneration { get; init; }

    public required DateTime[] Ts { get; init; }

    public required string[] Host { get; init; }

    public required string[] Program { get; init; }

    public required string[] Severity { get; init; }

    public required uint[] EventCode { get; init; }

    public required string[] EventAction { get; init; }

    public required string[] EventOutcome { get; init; }

    public required string[] UserName { get; init; }

    public required IPAddress[] SourceIp { get; init; }

    public required IPAddress[] DestinationIp { get; init; }

    public required string[] ProcessName { get; init; }

    public required string[] UrlPath { get; init; }

    public required ushort[] HttpStatus { get; init; }

    /// <summary>Message slice: the whole raw line (offset 0, the line length), as the stub source writes it.</summary>
    public required uint[] MsgOff { get; init; }

    public required uint[] MsgLen { get; init; }

    public required byte[][] MessageText { get; init; }

    public required byte[][] Raw { get; init; }

    public required KeyValuePair<string, string>[][] Extras { get; init; }

    public required string[] HttpMethod { get; init; }

    public required string[] HttpVersion { get; init; }

    public required ulong[] HttpResponseBodyBytes { get; init; }

    public required byte[][] HttpReferrer { get; init; }

    public required byte[][] UserAgent { get; init; }

    public required byte[][] UrlQuery { get; init; }

    public required string[] SyslogFacility { get; init; }

    public required uint[] ProcessPid { get; init; }

    /// <summary>The pinned schema (design record, "ClickHouse schema"), from the column metadata.</summary>
    public static string Ddl(string table) => EventSchema.CreateTable(table);

    public IColumn[] ToTcpColumns() =>
    [
        ClickHouseTcpColumn.Create("case_id", CaseId), ClickHouseTcpColumn.Create("host_id", HostId),
        ClickHouseTcpColumn.Create("file_id", FileId), ClickHouseTcpColumn.Create("file_offset", FileOffset),
        ClickHouseTcpColumn.Create("parse_generation", ParseGeneration), ClickHouseTcpColumn.Create("ts", Ts),
        ClickHouseTcpColumn.Create("host", Host), ClickHouseTcpColumn.Create("program", Program),
        ClickHouseTcpColumn.Create("severity", Severity), ClickHouseTcpColumn.Create("event_code", EventCode),
        ClickHouseTcpColumn.Create("event_action", EventAction), ClickHouseTcpColumn.Create("event_outcome", EventOutcome),
        ClickHouseTcpColumn.Create("user_name", UserName), ClickHouseTcpColumn.Create("source_ip", SourceIp),
        ClickHouseTcpColumn.Create("destination_ip", DestinationIp), ClickHouseTcpColumn.Create("process_name", ProcessName),
        ClickHouseTcpColumn.Create("url_path", UrlPath), ClickHouseTcpColumn.Create("http_status", HttpStatus),
        ClickHouseTcpColumn.Create("msg_off", MsgOff), ClickHouseTcpColumn.Create("msg_len", MsgLen),
        ClickHouseTcpColumn.Create("message_text", MessageText), ClickHouseTcpColumn.Create("raw", Raw),
        ClickHouseTcpColumn.Create("extras", Extras),
        ClickHouseTcpColumn.Create("http_method", HttpMethod), ClickHouseTcpColumn.Create("http_version", HttpVersion),
        ClickHouseTcpColumn.Create("http_response_body_bytes", HttpResponseBodyBytes), ClickHouseTcpColumn.Create("http_referrer", HttpReferrer),
        ClickHouseTcpColumn.Create("user_agent", UserAgent), ClickHouseTcpColumn.Create("url_query", UrlQuery),
        ClickHouseTcpColumn.Create("syslog_facility", SyslogFacility), ClickHouseTcpColumn.Create("process_pid", ProcessPid),
    ];

    /// <summary>Row-oriented view for the HTTP RowBinary path. Boxing happens here, in setup, not in the measured op.</summary>
    public object[][] ToHttpRows()
    {
        var rows = new object[Rows][];
        for (var i = 0; i < Rows; i++)
        {
            rows[i] =
            [
                CaseId[i], HostId[i], FileId[i], FileOffset[i], ParseGeneration[i], Ts[i], Host[i], Program[i], Severity[i],
                EventCode[i], EventAction[i], EventOutcome[i], UserName[i], SourceIp[i], DestinationIp[i], ProcessName[i],
                UrlPath[i], HttpStatus[i], MsgOff[i], MsgLen[i], MessageText[i], Raw[i], Extras[i],
                HttpMethod[i], HttpVersion[i], HttpResponseBodyBytes[i], HttpReferrer[i], UserAgent[i], UrlQuery[i], SyslogFacility[i], ProcessPid[i],
            ];
        }

        return rows;
    }
}

/// <summary>Deterministic nginx-shaped event rows for the insert benches. Same seed and row count = same block.</summary>
public static class EventBlockBuilder
{
    public const ulong DefaultSeed = 0x4C4F47544F4F4C10UL;

    private static readonly string[] Hosts = ["api-01", "db-01", "web-01", "web-02"]; // sorted: blocks arrive pre-sorted by (case_id, host, ts)
    private static readonly ushort[] Statuses = [200, 304, 301, 404, 403, 500, 502];
    private static readonly int[] StatusWeights = [800, 70, 20, 60, 15, 25, 10];
    private static readonly string[] UserAgents = Pools.UserAgents;
    private static readonly DateTime Epoch = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly Dictionary<(int, ulong), long> RawBytesCache = [];

    public static EventBlock Build(int rows, ulong seed = DefaultSeed)
    {
        var rng = new Xoshiro256(seed);
        var ipText = Pools.Ipv4Pool(rng, 5_000);
        var ips = ipText.Select(b => IPAddress.Parse(Encoding.ASCII.GetString(b)).MapToIPv6()).ToArray();
        var destination = IPAddress.Parse("10.0.0.5").MapToIPv6();
        var pathText = Pools.PathPool(rng, 2_000);
        var paths = pathText.Select(Encoding.ASCII.GetString).ToArray();

        var block = new EventBlock
        {
            Rows = rows,
            RawBytes = 0,
            CaseId = new ulong[rows], HostId = new ulong[rows], FileId = new ulong[rows], FileOffset = new ulong[rows],
            ParseGeneration = new uint[rows], Ts = new DateTime[rows], Host = new string[rows], Program = new string[rows],
            Severity = new string[rows], EventCode = new uint[rows], EventAction = new string[rows], EventOutcome = new string[rows],
            UserName = new string[rows], SourceIp = new IPAddress[rows], DestinationIp = new IPAddress[rows],
            ProcessName = new string[rows], UrlPath = new string[rows], HttpStatus = new ushort[rows],
            MsgOff = new uint[rows], MsgLen = new uint[rows], MessageText = new byte[rows][], Raw = new byte[rows][],
            Extras = new KeyValuePair<string, string>[rows][],
            HttpMethod = new string[rows], HttpVersion = new string[rows], HttpResponseBodyBytes = new ulong[rows],
            HttpReferrer = new byte[rows][], UserAgent = new byte[rows][], UrlQuery = new byte[rows][],
            SyslogFacility = new string[rows], ProcessPid = new uint[rows],
        };
        var empty = Array.Empty<byte>();
        var userAgentBytes = UserAgents.Select(Encoding.ASCII.GetBytes).ToArray();

        long rawBytes = 0;
        var perHost = Math.Max(1, rows / Hosts.Length);
        Span<byte> line = stackalloc byte[512];
        ulong offset = 0;
        for (var i = 0; i < rows; i++)
        {
            var hostIndex = Math.Min(i / perHost, Hosts.Length - 1);
            var ts = Epoch.AddMilliseconds((i % perHost) * 3L);
            var ip = rng.NextSkewed(ips.Length);
            var path = rng.NextSkewed(paths.Length);
            var status = rng.NextWeighted(StatusWeights);
            var bytes = (ulong)Math.Exp(rng.NextDouble() * 12.0);
            var ua = rng.NextSkewed(UserAgents.Length);

            var len = FormatRaw(line, ipText[ip], ts, pathText[path], Statuses[status], bytes, UserAgents[ua]);
            var raw = line[..len].ToArray();
            rawBytes += len;

            block.CaseId[i] = 1;
            block.HostId[i] = (ulong)hostIndex + 1;
            block.FileId[i] = (ulong)hostIndex + 1;
            block.FileOffset[i] = offset;
            offset += (ulong)len + 1;
            block.ParseGeneration[i] = 1;
            block.Ts[i] = ts;
            block.Host[i] = Hosts[hostIndex];
            block.Program[i] = "nginx";
            block.Severity[i] = "info";
            block.EventCode[i] = 0;
            block.EventAction[i] = "access";
            block.EventOutcome[i] = Statuses[status] < 400 ? "success" : "failure";
            block.UserName[i] = "-";
            block.SourceIp[i] = ips[ip];
            block.DestinationIp[i] = destination;
            block.ProcessName[i] = "nginx";
            block.UrlPath[i] = paths[path];
            block.HttpStatus[i] = Statuses[status];
            block.MsgOff[i] = 0;
            block.MsgLen[i] = (uint)len;
            block.MessageText[i] = empty;
            block.Raw[i] = raw;
            block.Extras[i] = [new("upstream.addr", "10.0.0.5:8080")];
            block.HttpMethod[i] = "get";
            block.HttpVersion[i] = "1.1";
            block.HttpResponseBodyBytes[i] = bytes;
            block.HttpReferrer[i] = empty;
            block.UserAgent[i] = userAgentBytes[ua];
            block.UrlQuery[i] = empty;
            block.SyslogFacility[i] = "";
            block.ProcessPid[i] = 0;
        }

        lock (RawBytesCache)
        {
            RawBytesCache[(rows, seed)] = rawBytes;
        }

        return new EventBlock
        {
            Rows = block.Rows, RawBytes = rawBytes, CaseId = block.CaseId, HostId = block.HostId, FileId = block.FileId,
            FileOffset = block.FileOffset, ParseGeneration = block.ParseGeneration, Ts = block.Ts, Host = block.Host,
            Program = block.Program, Severity = block.Severity, EventCode = block.EventCode, EventAction = block.EventAction,
            EventOutcome = block.EventOutcome, UserName = block.UserName, SourceIp = block.SourceIp,
            DestinationIp = block.DestinationIp, ProcessName = block.ProcessName, UrlPath = block.UrlPath,
            HttpStatus = block.HttpStatus, MsgOff = block.MsgOff, MsgLen = block.MsgLen, MessageText = block.MessageText,
            Raw = block.Raw, Extras = block.Extras, HttpMethod = block.HttpMethod, HttpVersion = block.HttpVersion,
            HttpResponseBodyBytes = block.HttpResponseBodyBytes, HttpReferrer = block.HttpReferrer, UserAgent = block.UserAgent,
            UrlQuery = block.UrlQuery, SyslogFacility = block.SyslogFacility, ProcessPid = block.ProcessPid,
        };
    }

    /// <summary>Raw bytes a block of <paramref name="rows"/> represents, without materialising the arrays (used by the host for MB/s).</summary>
    public static long RawBytes(int rows, ulong seed = DefaultSeed)
    {
        lock (RawBytesCache)
        {
            if (RawBytesCache.TryGetValue((rows, seed), out var cached))
            {
                return cached;
            }
        }

        var rng = new Xoshiro256(seed);
        var ipText = Pools.Ipv4Pool(rng, 5_000);
        var pathText = Pools.PathPool(rng, 2_000);
        var perHost = Math.Max(1, rows / Hosts.Length);
        Span<byte> line = stackalloc byte[512];
        long total = 0;
        for (var i = 0; i < rows; i++)
        {
            var ts = Epoch.AddMilliseconds((i % perHost) * 3L);
            var ip = rng.NextSkewed(ipText.Length);
            var path = rng.NextSkewed(pathText.Length);
            var status = rng.NextWeighted(StatusWeights);
            var bytes = (ulong)Math.Exp(rng.NextDouble() * 12.0);
            var ua = rng.NextSkewed(UserAgents.Length);
            total += FormatRaw(line, ipText[ip], ts, pathText[path], Statuses[status], bytes, UserAgents[ua]);
        }

        lock (RawBytesCache)
        {
            RawBytesCache[(rows, seed)] = total;
        }

        return total;
    }

    private static int FormatRaw(Span<byte> dest, byte[] ip, DateTime ts, byte[] path, ushort status, ulong bytes, string ua)
    {
        var pos = 0;
        ip.CopyTo(dest[pos..]);
        pos += ip.Length;
        pos += Append(dest[pos..], " - - ["u8);
        pos += AppendPadded(dest[pos..], ts.Day, 2);
        dest[pos++] = (byte)'/';
        pos += Append(dest[pos..], Pools.Months[ts.Month - 1]);
        dest[pos++] = (byte)'/';
        pos += AppendPadded(dest[pos..], ts.Year, 4);
        dest[pos++] = (byte)':';
        pos += AppendPadded(dest[pos..], ts.Hour, 2);
        dest[pos++] = (byte)':';
        pos += AppendPadded(dest[pos..], ts.Minute, 2);
        dest[pos++] = (byte)':';
        pos += AppendPadded(dest[pos..], ts.Second, 2);
        pos += Append(dest[pos..], " +0000] \"GET "u8);
        path.CopyTo(dest[pos..]);
        pos += path.Length;
        pos += Append(dest[pos..], " HTTP/1.1\" "u8);
        Utf8Formatter.TryFormat(status, dest[pos..], out var n);
        pos += n;
        dest[pos++] = (byte)' ';
        Utf8Formatter.TryFormat(bytes, dest[pos..], out n);
        pos += n;
        pos += Append(dest[pos..], " \"-\" \""u8);
        pos += Encoding.ASCII.GetBytes(ua, dest[pos..]);
        dest[pos++] = (byte)'"';
        return pos;
    }

    private static int Append(Span<byte> dest, ReadOnlySpan<byte> src)
    {
        src.CopyTo(dest);
        return src.Length;
    }

    private static int AppendPadded(Span<byte> dest, int value, int width)
    {
        for (var i = width - 1; i >= 0; i--)
        {
            dest[i] = (byte)('0' + (value % 10));
            value /= 10;
        }

        return width;
    }
}
