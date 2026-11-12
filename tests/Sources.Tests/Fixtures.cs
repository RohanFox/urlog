using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Urlog.Core.Pipeline;
using Urlog.Core.Sources;

namespace Urlog.Sources.Tests;

/// <summary>Locates <c>fixtures/</c> from the test binary and runs a source over a fixture file through the real pipeline.</summary>
public static class Fixtures
{
    private static readonly Lazy<string> RootLazy = new(FindRoot);

    public static string Root => RootLazy.Value;

    public static string Path(string source, string file) => System.IO.Path.Combine(Root, "fixtures", source, file);

    public static byte[] Bytes(string source, string file) => File.ReadAllBytes(Path(source, file));

    /// <summary>
    /// Parses <paramref name="path"/> through <see cref="ParsePipeline"/> (compression and encoding detected from the
    /// head, as intake does), small chunks so the file spans several, and returns the rows ordered by file offset.
    /// </summary>
    public static async Task<List<ParsedRow>> ParseFileAsync(ILogSource source, string path, int chunkBytes = 4096, int parallelism = 2, IReadOnlyDictionary<string, string>? options = null, TimeSpan timeZoneHint = default, int yearHint = 1970, DateTime? fileTime = null)
    {
        var raw = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var compression = InputDecoding.DetectCompression(raw);
        var encoding = InputDecoding.DetectEncoding(raw, out _);
        var pipelineOptions = new PipelineOptions
        {
            ChunkBytes = chunkBytes,
            MaxRecordBytes = 1 << 20,
            BatchRows = 100,
            Parallelism = parallelism,
            InFlightChunks = parallelism + 2,
            InFlightBatches = 10_000, // the collecting sink keeps every batch
        };
        var buffers = new PipelineBuffers(pipelineOptions);
        var sink = new CollectingBatchSink();
        var pipeline = new ParsePipeline(buffers);
        var identity = new FileIdentity(CaseId: 1, HostId: 1, FileId: 1, ParseGeneration: 1, DefaultHost: "web-01", TimeZoneHint: timeZoneHint, YearHint: yearHint, Options: options, FileTime: fileTime);

        using var stream = InputDecoding.OpenDecoded(new MemoryStream(raw), compression, encoding);
        await pipeline.RunAsync(stream, source, identity, sink, TestContext.Current.CancellationToken);

        var rows = new List<ParsedRow>();
        foreach (var batch in sink.Batches)
        {
            for (var i = 0; i < batch.RowCount; i++)
            {
                rows.Add(ParsedRow.From(batch, i));
            }
        }

        sink.ReturnAll();
        rows.Sort((a, b) => a.FileOffset.CompareTo(b.FileOffset));
        return rows;
    }

    /// <summary>
    /// Parses <paramref name="data"/> as one file through the real batch writer on the calling thread, chunked at
    /// <paramref name="maxChunk"/> on line boundaries (a line longer than that becomes its own chunk), and returns
    /// the rows in order. For sources that need the hints or a chunking the pipeline would not produce.
    /// </summary>
    public static List<ParsedRow> ParseBytes(ILogSource source, byte[] data, int maxChunk = int.MaxValue, IReadOnlyDictionary<string, string>? options = null, string defaultHost = "web-01", TimeSpan timeZoneHint = default, int yearHint = 1970, DateTime? fileTime = null)
    {
        var pipelineOptions = new PipelineOptions { ChunkBytes = 4096, MaxRecordBytes = 1 << 20, BatchRows = 1000, Parallelism = 1, InFlightChunks = 3, InFlightBatches = 1000 };
        var buffers = new PipelineBuffers(pipelineOptions);
        var sink = new CollectingBatchSink();
        var writer = new EventBatchWriter(buffers.Batches, sink);
        var ctx = new ParseContext { CaseId = 1, HostId = 1, FileId = 1, DefaultHost = Encoding.UTF8.GetBytes(defaultHost), Options = options, TimeZoneHint = timeZoneHint, YearHint = yearHint, FileTime = fileTime };
        writer.Bind(ctx);

        var offset = 0;
        var index = 0;
        while (offset < data.Length)
        {
            var rest = data.AsSpan(offset);
            var length = rest.Length <= maxChunk ? rest.Length : source.Framing.LastRecordEnd(rest[..maxChunk]);
            if (length == 0)
            {
                var newline = rest.IndexOf((byte)'\n');
                length = newline < 0 ? rest.Length : newline + 1;
            }

            ctx.ChunkFileOffset = offset;
            ctx.ChunkIndex = index++;
            source.Parse(new System.Buffers.ReadOnlySequence<byte>(data, offset, length), ctx, writer);
            offset += length;
        }

        writer.Flush();
        var rows = new List<ParsedRow>();
        foreach (var batch in sink.Batches)
        {
            for (var i = 0; i < batch.RowCount; i++)
            {
                rows.Add(ParsedRow.From(batch, i));
            }
        }

        sink.ReturnAll();
        writer.Close();
        return rows;
    }

    /// <summary>One expected row per line of <c>expected.jsonl</c>; keys absent from a line take the column default.</summary>
    public static List<ParsedRow> ReadExpected(string source) => ReadExpectedFile(Path(source, "expected.jsonl"));

    public static List<ParsedRow> ReadExpectedFile(string path)
    {
        var rows = new List<ParsedRow>();
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(line);
            var o = doc.RootElement;
            var extras = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (o.TryGetProperty("extras", out var e))
            {
                foreach (var p in e.EnumerateObject())
                {
                    extras[p.Name] = p.Value.GetString() ?? "";
                }
            }

            rows.Add(new ParsedRow(
                FileOffset: o.GetProperty("file_offset").GetUInt64(),
                Ts: o.TryGetProperty("ts", out var ts) && ts.ValueKind == JsonValueKind.String ? ts.GetString()! : ParsedRow.Epoch,
                Host: Str(o, "host"),
                Program: Str(o, "program"),
                Severity: Str(o, "severity"),
                UserName: Str(o, "user_name"),
                SourceIp: o.TryGetProperty("source_ip", out var ip) ? ip.GetString()! : "::",
                UrlPath: Str(o, "url_path"),
                HttpStatus: o.TryGetProperty("http_status", out var st) ? st.GetUInt16() : (ushort)0,
                EventCode: o.TryGetProperty("event_code", out var ec) ? ec.GetUInt32() : 0u,
                EventOutcome: Str(o, "event_outcome"),
                Message: Str(o, "message"),
                Raw: Str(o, "raw"),
                HttpMethod: Str(o, "http_method"),
                HttpVersion: Str(o, "http_version"),
                HttpResponseBodyBytes: o.TryGetProperty("http_response_body_bytes", out var bb) ? bb.GetUInt64() : 0UL,
                HttpReferrer: Str(o, "http_referrer"),
                UserAgent: Str(o, "user_agent"),
                UrlQuery: Str(o, "url_query"),
                SyslogFacility: Str(o, "syslog_facility"),
                ProcessPid: o.TryGetProperty("process_pid", out var pid) ? pid.GetUInt32() : 0U,
                Extras: extras));
        }

        return rows;

        static string Str(JsonElement o, string name) => o.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "urlog.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("urlog.sln not found above " + AppContext.BaseDirectory);
    }
}

/// <summary>A row in canonical, comparable form: the columns a source writes plus host and extras. <c>Message</c> is the logical message (the slice of raw, or the stored text).</summary>
public sealed record ParsedRow(
    ulong FileOffset, string Ts, string Host, string Program, string Severity, string UserName, string SourceIp, string UrlPath,
    ushort HttpStatus, uint EventCode, string EventOutcome, string Message, string Raw,
    string HttpMethod, string HttpVersion, ulong HttpResponseBodyBytes, string HttpReferrer, string UserAgent, string UrlQuery,
    string SyslogFacility, uint ProcessPid, SortedDictionary<string, string> Extras)
{
    public const string Epoch = "1970-01-01T00:00:00.000Z";

    public static ParsedRow From(EventBatch batch, int i)
    {
        var extras = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var from = i == 0 ? 0 : batch.ExtrasOffsets[i - 1];
        for (var k = from; k < batch.ExtrasOffsets[i]; k++)
        {
            extras[batch.ExtrasKeys.RowString(k)] = batch.ExtrasValues.RowString(k);
        }

        return new ParsedRow(
            FileOffset: batch.FileOffset[i],
            Ts: DateTimeOffset.FromUnixTimeMilliseconds(batch.Ts[i]).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            Host: batch.Strings(EventColumn.Host).RowString(i),
            Program: batch.Strings(EventColumn.Program).RowString(i),
            Severity: batch.Strings(EventColumn.Severity).RowString(i),
            UserName: batch.Strings(EventColumn.UserName).RowString(i),
            SourceIp: new IPAddress(batch.SourceIp.AsSpan(i * 16, 16)).ToString(),
            UrlPath: batch.Strings(EventColumn.UrlPath).RowString(i),
            HttpStatus: batch.HttpStatus[i],
            EventCode: batch.EventCode[i],
            EventOutcome: batch.Strings(EventColumn.EventOutcome).RowString(i),
            Message: batch.MsgLen[i] == 0
                ? batch.Strings(EventColumn.MessageText).RowString(i)
                : Encoding.UTF8.GetString(batch.Strings(EventColumn.Raw).Row(i).Slice((int)batch.MsgOff[i], (int)batch.MsgLen[i])),
            Raw: batch.Strings(EventColumn.Raw).RowString(i),
            HttpMethod: batch.Strings(EventColumn.HttpMethod).RowString(i),
            HttpVersion: batch.Strings(EventColumn.HttpVersion).RowString(i),
            HttpResponseBodyBytes: batch.HttpResponseBodyBytes[i],
            HttpReferrer: batch.Strings(EventColumn.HttpReferrer).RowString(i),
            UserAgent: batch.Strings(EventColumn.UserAgent).RowString(i),
            UrlQuery: batch.Strings(EventColumn.UrlQuery).RowString(i),
            SyslogFacility: batch.Strings(EventColumn.SyslogFacility).RowString(i),
            ProcessPid: batch.ProcessPid[i],
            Extras: extras)
        {
            RawBytes = batch.Strings(EventColumn.Raw).Row(i).ToArray(),
        };
    }

    /// <summary>The raw column's bytes, for the byte-for-byte check against the file (not part of equality).</summary>
    public ReadOnlyMemory<byte> RawBytes { get; init; }

    public bool IsReject => Extras.ContainsKey("reject_reason");

    public bool Equals(ParsedRow? other) =>
        other is not null && FileOffset == other.FileOffset && Ts == other.Ts && Host == other.Host && Program == other.Program
        && Severity == other.Severity && UserName == other.UserName && SourceIp == other.SourceIp && UrlPath == other.UrlPath && HttpStatus == other.HttpStatus
        && EventCode == other.EventCode && EventOutcome == other.EventOutcome && Message == other.Message && Raw == other.Raw
        && Extras.SequenceEqual(other.Extras);

    public override int GetHashCode() => HashCode.Combine(FileOffset, Raw);

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"@{FileOffset} ts={Ts} host={Host} program={Program} severity={Severity} user={UserName} ip={SourceIp} path={UrlPath} status={HttpStatus} code={EventCode} outcome={EventOutcome}\n");
        sb.Append(CultureInfo.InvariantCulture, $"  message={Message}\n  raw={Raw}\n  extras={{{string.Join(", ", Extras.Select(p => p.Key + "=" + p.Value))}}}");
        return sb.ToString();
    }
}
