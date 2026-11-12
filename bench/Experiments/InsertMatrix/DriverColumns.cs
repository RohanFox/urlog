using System.Buffers.Binary;
using System.Net;
using System.Text;
using ClickHouse.Driver.Tcp;
using Urlog.Core.Pipeline;
using Urlog.Core.Sources;

namespace Urlog.Experiments.InsertMatrix;

/// <summary>
/// <see cref="EventBatch"/> → the driver's column API, the way <c>bench/Stub/DriverColumnSink.cs</c> does it
/// (<c>string</c> per LowCardinality row from a cache, <c>byte[]</c> per String row, <c>IPAddress</c> per IP row
/// from a cache, <c>KeyValuePair&lt;string,string&gt;[]</c> per row for the map). Done once in setup here so the
/// measured cell is the driver's wire path alone; the conversion cost is reported separately.
/// </summary>
internal static class DriverColumns
{
    private static readonly KeyValuePair<string, string>[] NoExtras = [];

    public static IColumn[] Convert(EventBatch batch)
    {
        var n = batch.RowCount;
        var strings = new StringCache();
        var ips = new Dictionary<UInt128, IPAddress>();

        var host = Fill(batch.Strings(EventColumn.Host), n, strings);
        var program = Fill(batch.Strings(EventColumn.Program), n, strings);
        var severity = Fill(batch.Strings(EventColumn.Severity), n, strings);
        var action = Fill(batch.Strings(EventColumn.EventAction), n, strings);
        var outcome = Fill(batch.Strings(EventColumn.EventOutcome), n, strings);
        var process = Fill(batch.Strings(EventColumn.ProcessName), n, strings);
        var user = FillBytes(batch.Strings(EventColumn.UserName), n);
        var path = FillBytes(batch.Strings(EventColumn.UrlPath), n);
        var messageText = FillBytes(batch.Strings(EventColumn.MessageText), n);
        var method = Fill(batch.Strings(EventColumn.HttpMethod), n, strings);
        var version = Fill(batch.Strings(EventColumn.HttpVersion), n, strings);
        var referrer = FillBytes(batch.Strings(EventColumn.HttpReferrer), n);
        var userAgent = FillBytes(batch.Strings(EventColumn.UserAgent), n);
        var query = FillBytes(batch.Strings(EventColumn.UrlQuery), n);
        var facility = Fill(batch.Strings(EventColumn.SyslogFacility), n, strings);

        var raw = new byte[n][];
        var rawColumn = batch.Strings(EventColumn.Raw);
        for (var i = 0; i < n; i++)
        {
            raw[i] = rawColumn.Row(i).ToArray();
        }

        var source = FillIps(batch.SourceIp, n, ips);
        var destination = FillIps(batch.DestinationIp, n, ips);

        var extras = new KeyValuePair<string, string>[n][];
        for (var i = 0; i < n; i++)
        {
            var start = i == 0 ? 0 : batch.ExtrasOffsets[i - 1];
            var count = batch.ExtrasOffsets[i] - start;
            if (count == 0)
            {
                extras[i] = NoExtras;
                continue;
            }

            var pairs = new KeyValuePair<string, string>[count];
            for (var k = 0; k < count; k++)
            {
                pairs[k] = new(strings.Get(batch.ExtrasKeys.Row(start + k)), strings.Get(batch.ExtrasValues.Row(start + k)));
            }

            extras[i] = pairs;
        }

        var full = n == batch.Capacity;
        return
        [
            ClickHouseTcpColumn.Create("case_id", Exact(batch.CaseId, n, full)),
            ClickHouseTcpColumn.Create("host_id", Exact(batch.HostId, n, full)),
            ClickHouseTcpColumn.Create("file_id", Exact(batch.FileId, n, full)),
            ClickHouseTcpColumn.Create("file_offset", Exact(batch.FileOffset, n, full)),
            ClickHouseTcpColumn.Create("parse_generation", Exact(batch.ParseGeneration, n, full)),
            ClickHouseTcpColumn.Create("ts", Exact(batch.Ts, n, full)),
            ClickHouseTcpColumn.Create("host", host),
            ClickHouseTcpColumn.Create("program", program),
            ClickHouseTcpColumn.Create("severity", severity),
            ClickHouseTcpColumn.Create("event_code", Exact(batch.EventCode, n, full)),
            ClickHouseTcpColumn.Create("event_action", action),
            ClickHouseTcpColumn.Create("event_outcome", outcome),
            ClickHouseTcpColumn.Create("user_name", user),
            ClickHouseTcpColumn.Create("source_ip", source),
            ClickHouseTcpColumn.Create("destination_ip", destination),
            ClickHouseTcpColumn.Create("process_name", process),
            ClickHouseTcpColumn.Create("url_path", path),
            ClickHouseTcpColumn.Create("http_status", Exact(batch.HttpStatus, n, full)),
            ClickHouseTcpColumn.Create("msg_off", Exact(batch.MsgOff, n, full)),
            ClickHouseTcpColumn.Create("msg_len", Exact(batch.MsgLen, n, full)),
            ClickHouseTcpColumn.Create("message_text", messageText),
            ClickHouseTcpColumn.Create("raw", raw),
            ClickHouseTcpColumn.Create("extras", extras),
            ClickHouseTcpColumn.Create("http_method", method),
            ClickHouseTcpColumn.Create("http_version", version),
            ClickHouseTcpColumn.Create("http_response_body_bytes", Exact(batch.HttpResponseBodyBytes, n, full)),
            ClickHouseTcpColumn.Create("http_referrer", referrer),
            ClickHouseTcpColumn.Create("user_agent", userAgent),
            ClickHouseTcpColumn.Create("url_query", query),
            ClickHouseTcpColumn.Create("syslog_facility", facility),
            ClickHouseTcpColumn.Create("process_pid", Exact(batch.ProcessPid, n, full)),
        ];
    }

    private static T[] Exact<T>(T[] array, int n, bool full) => full ? array : array[..n];

    private static string[] Fill(StringColumnBuffer column, int n, StringCache cache)
    {
        var dest = new string[n];
        for (var i = 0; i < n; i++)
        {
            dest[i] = cache.Get(column.Row(i));
        }

        return dest;
    }

    private static byte[][] FillBytes(StringColumnBuffer column, int n)
    {
        var dest = new byte[n][];
        var cache = new Dictionary<int, byte[]>();
        for (var i = 0; i < n; i++)
        {
            var row = column.Row(i);
            if (row.IsEmpty)
            {
                dest[i] = [];
                continue;
            }

            if (row.Length > 64)
            {
                dest[i] = row.ToArray();
                continue;
            }

            var hash = Hash(row);
            if (cache.TryGetValue(hash, out var hit) && hit.AsSpan().SequenceEqual(row))
            {
                dest[i] = hit;
                continue;
            }

            var copy = row.ToArray();
            if (cache.Count < 4096)
            {
                cache[hash] = copy;
            }

            dest[i] = copy;
        }

        return dest;
    }

    private static IPAddress[] FillIps(byte[] packed, int n, Dictionary<UInt128, IPAddress> cache)
    {
        var dest = new IPAddress[n];
        for (var i = 0; i < n; i++)
        {
            var bytes = packed.AsSpan(i * 16, 16);
            var key = new UInt128(BinaryPrimitives.ReadUInt64BigEndian(bytes), BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]));
            if (!cache.TryGetValue(key, out var ip))
            {
                ip = new IPAddress(bytes);
                if (cache.Count < 65_536)
                {
                    cache[key] = ip;
                }
            }

            dest[i] = ip;
        }

        return dest;
    }

    private static int Hash(ReadOnlySpan<byte> bytes)
    {
        var h = default(HashCode);
        h.AddBytes(bytes);
        return h.ToHashCode();
    }

    private sealed class StringCache
    {
        private readonly Dictionary<int, (byte[] Key, string Value)> _entries = [];

        public string Get(ReadOnlySpan<byte> utf8)
        {
            if (utf8.IsEmpty)
            {
                return "";
            }

            var hash = Hash(utf8);
            if (_entries.TryGetValue(hash, out var hit) && hit.Key.AsSpan().SequenceEqual(utf8))
            {
                return hit.Value;
            }

            var value = Encoding.UTF8.GetString(utf8);
            if (_entries.Count < 8192)
            {
                _entries[hash] = (utf8.ToArray(), value);
            }

            return value;
        }
    }
}
