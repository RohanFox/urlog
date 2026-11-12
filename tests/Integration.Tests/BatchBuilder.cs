using System.Globalization;
using System.Text;
using ClickHouse.Driver.Tcp;
using Urlog.Core.Ingest;
using Urlog.Core.Pipeline;
using Urlog.Core.Sources;

namespace Urlog.Integration.Tests;

/// <summary>Builds <see cref="EventBatch"/>es by hand through the real <see cref="EventBatchWriter"/>, for the protocol tests.</summary>
internal sealed class BatchBuilder : IDisposable
{
    private readonly PipelineBuffers _buffers;
    private readonly CollectingBatchSink _sink = new();

    public BatchBuilder(int batchRows, int batches = 4)
    {
        _buffers = new PipelineBuffers(new PipelineOptions { BatchRows = batchRows, Parallelism = 1, InFlightChunks = 3, InFlightBatches = batches + 1 });
        Writer = new EventBatchWriter(_buffers.Batches, _sink);
        Context = new ParseContext { CaseId = 1, HostId = 1, FileId = 1, ParseGeneration = 1, DefaultHost = "web-01"u8.ToArray() };
        Writer.Bind(Context);
    }

    public EventBatchWriter Writer { get; }

    public ParseContext Context { get; }

    public BatchPool Pool => _buffers.Batches;

    /// <summary>The batches flushed so far (a partial batch is flushed first).</summary>
    public IReadOnlyList<EventBatch> Flush()
    {
        Writer.Flush();
        return _sink.Batches;
    }

    public void Dispose()
    {
        Writer.Discard();
        _sink.ReturnAll();
        _buffers.Batches.Dispose();
    }

    /// <summary>The pinned schema from the column metadata (<see cref="EventSchema"/>); <paramref name="extra"/> adds constraints or replaces nothing.</summary>
    public static string Ddl(string table, string extra = "") => EventSchema.CreateTable(table, index: true, extra);

    /// <summary>The exposed columns as text: the 18 leading columns, then <c>message</c> (the alias), <c>raw</c>, <c>extras</c>, then the eight promoted columns.</summary>
    public const int ReadBackColumns = 29;

    public static NativeInserterOptions Options(ClickHouseFixture ch, string table, string user = ClickHouseFixture.AdminUser, string password = ClickHouseFixture.AdminPassword) => new()
    {
        Host = ch.Host,
        Port = ch.NativeHostPort,
        User = user,
        Password = password,
        Table = table,
        ReadTimeout = TimeSpan.FromSeconds(20),
    };

    /// <summary>Every column of every row as text, ordered by <c>file_offset</c>: the shape the round-trip tests compare.</summary>
    public static async Task<List<string[]>> ReadBackAsText(ClickHouseTcpClient client, string table, CancellationToken ct)
    {
        var rows = new List<string[]>();
        await foreach (var row in client.QueryAsync(
            "SELECT toString(case_id), toString(host_id), toString(file_id), toString(file_offset), toString(parse_generation), " +
            "toString(ts), host, program, severity, toString(event_code), event_action, event_outcome, user_name, " +
            "IPv6NumToString(source_ip), IPv6NumToString(destination_ip), process_name, url_path, toString(http_status), message, raw, toString(extras), " +
            "http_method, http_version, toString(http_response_body_bytes), http_referrer, user_agent, url_query, syslog_facility, toString(process_pid) " +
            $"FROM {table} ORDER BY file_offset", cancellationToken: ct))
        {
            var text = new string[ReadBackColumns];
            for (var i = 0; i < ReadBackColumns; i++)
            {
                text[i] = Convert.ToString(row[i], CultureInfo.InvariantCulture) ?? "";
            }

            rows.Add(text);
        }

        return rows;
    }

    public static string Utf8(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);
}
