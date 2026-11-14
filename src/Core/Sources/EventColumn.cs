namespace Urlog.Core.Sources;

/// <summary>
/// The pinned <c>events</c> columns (design record, "ClickHouse schema";
/// <c>migrations/clickhouse/0001_events.sql</c>). Plugins address <see cref="IBatchWriter"/> by this enum, never by
/// name, so a column write costs an array index and nothing else. Order is the table's column order and is part of
/// the Native block layout: do not reorder. The logical <c>message</c> is not a stored column: it is an ALIAS over
/// <c>raw</c> through <see cref="MsgOff"/> and <see cref="MsgLen"/>, with <see cref="MessageText"/> as the stored
/// fallback for a source whose message is not a byte slice of its record.
/// </summary>
public enum EventColumn : byte
{
    CaseId,
    HostId,
    FileId,
    FileOffset,
    ParseGeneration,
    Ts,
    Host,
    Program,
    Severity,
    EventCode,
    EventAction,
    EventOutcome,
    UserName,
    SourceIp,
    DestinationIp,
    ProcessName,
    UrlPath,
    HttpStatus,

    /// <summary>Byte offset of the message inside <see cref="Raw"/>; set through <see cref="IBatchWriter.AppendMessage"/>.</summary>
    MsgOff,

    /// <summary>Byte length of the message inside <see cref="Raw"/>; 0 means "use <see cref="MessageText"/>".</summary>
    MsgLen,

    /// <summary>The message when it is not a slice of <see cref="Raw"/> (EVTX rendering, JSON with escapes); empty otherwise.</summary>
    MessageText,
    Raw,
    Extras,
    HttpMethod,
    HttpVersion,
    HttpResponseBodyBytes,
    HttpReferrer,
    UserAgent,
    UrlQuery,
    SyslogFacility,
    ProcessPid,
}

/// <summary>Storage class of a column inside an <see cref="Urlog.Core.Pipeline.EventBatch"/>.</summary>
public enum EventColumnKind : byte
{
    Unsigned64,
    Unsigned32,
    Unsigned16,
    Timestamp,
    Text,
    LowCardinalityText,
    IpAddress,
    Map,
}

/// <summary>Static facts about the pinned columns: names, ClickHouse types, codecs, storage kind, string-column slots.</summary>
public static class EventColumns
{
    public const int Count = 31;

    /// <summary>Number of columns stored as a byte buffer plus offsets (<see cref="EventColumnKind.Text"/> and <see cref="EventColumnKind.LowCardinalityText"/>).</summary>
    public const int StringColumnCount = 16;

    /// <summary>The logical message column of the table and of <c>events_v</c>: an ALIAS, never written by the insert path.</summary>
    public const string MessageAlias = "message";

    /// <summary>The ALIAS expression of <see cref="MessageAlias"/> in the table DDL.</summary>
    public const string MessageExpression = "if(msg_len = 0, message_text, substring(raw, msg_off + 1, msg_len))";

    private static readonly string[] Names =
    [
        "case_id", "host_id", "file_id", "file_offset", "parse_generation", "ts", "host", "program", "severity",
        "event_code", "event_action", "event_outcome", "user_name", "source_ip", "destination_ip", "process_name",
        "url_path", "http_status", "msg_off", "msg_len", "message_text", "raw", "extras",
        "http_method", "http_version", "http_response_body_bytes", "http_referrer", "user_agent", "url_query",
        "syslog_facility", "process_pid",
    ];

    private static readonly string[] Types =
    [
        "UInt64", "UInt64", "UInt64", "UInt64", "UInt32", "DateTime64(3, 'UTC')", "LowCardinality(String)",
        "LowCardinality(String)", "LowCardinality(String)", "UInt32", "LowCardinality(String)", "LowCardinality(String)",
        "String", "IPv6", "IPv6", "LowCardinality(String)", "String", "UInt16", "UInt32", "UInt32", "String", "String",
        "Map(LowCardinality(String), String)", "LowCardinality(String)", "LowCardinality(String)", "UInt64", "String",
        "String", "String", "LowCardinality(String)", "UInt32",
    ];

    // From the per-column measurement: ZSTD(1) on every String, IPv6 and the Map; Delta on file_offset; T64 on the small integers; ts keeps Delta.
    private static readonly string[] Codecs =
    [
        "", "", "", "CODEC(Delta, ZSTD(1))", "", "CODEC(Delta, ZSTD)", "", "", "",
        "CODEC(T64, ZSTD(1))", "", "", "CODEC(ZSTD(1))", "CODEC(ZSTD(1))", "CODEC(ZSTD(1))", "",
        "CODEC(ZSTD(1))", "CODEC(T64, ZSTD(1))", "CODEC(ZSTD(1))", "CODEC(ZSTD(1))", "CODEC(ZSTD(1))", "CODEC(ZSTD(1))",
        "CODEC(ZSTD(1))", "", "", "CODEC(T64, ZSTD(1))", "CODEC(ZSTD(1))", "CODEC(ZSTD(1))", "CODEC(ZSTD(1))", "",
        "CODEC(T64, ZSTD(1))",
    ];

    private static readonly EventColumnKind[] Kinds =
    [
        EventColumnKind.Unsigned64, EventColumnKind.Unsigned64, EventColumnKind.Unsigned64, EventColumnKind.Unsigned64,
        EventColumnKind.Unsigned32, EventColumnKind.Timestamp, EventColumnKind.LowCardinalityText,
        EventColumnKind.LowCardinalityText, EventColumnKind.LowCardinalityText, EventColumnKind.Unsigned32,
        EventColumnKind.LowCardinalityText, EventColumnKind.LowCardinalityText, EventColumnKind.Text,
        EventColumnKind.IpAddress, EventColumnKind.IpAddress, EventColumnKind.LowCardinalityText, EventColumnKind.Text,
        EventColumnKind.Unsigned16, EventColumnKind.Unsigned32, EventColumnKind.Unsigned32, EventColumnKind.Text,
        EventColumnKind.Text, EventColumnKind.Map, EventColumnKind.LowCardinalityText, EventColumnKind.LowCardinalityText,
        EventColumnKind.Unsigned64, EventColumnKind.Text, EventColumnKind.Text, EventColumnKind.Text,
        EventColumnKind.LowCardinalityText, EventColumnKind.Unsigned32,
    ];

    // Slot of each string column in EventBatch.Strings; -1 for non-string columns. Order follows EventColumn.
    private static readonly sbyte[] StringSlots =
    [
        -1, -1, -1, -1, -1, -1, 0, 1, 2, -1, 3, 4, 5, -1, -1, 6, 7, -1, -1, -1, 8, 9, -1, 10, 11, -1, 12, 13, 14, 15, -1,
    ];

    private static readonly EventColumn[] StringColumnsBySlot =
    [
        EventColumn.Host, EventColumn.Program, EventColumn.Severity, EventColumn.EventAction, EventColumn.EventOutcome,
        EventColumn.UserName, EventColumn.ProcessName, EventColumn.UrlPath, EventColumn.MessageText, EventColumn.Raw,
        EventColumn.HttpMethod, EventColumn.HttpVersion, EventColumn.HttpReferrer, EventColumn.UserAgent, EventColumn.UrlQuery,
        EventColumn.SyslogFacility,
    ];

    public static string Name(EventColumn column) => Names[(int)column];

    public static string ClickHouseType(EventColumn column) => Types[(int)column];

    /// <summary>The <c>CODEC(...)</c> clause of the column in the pinned DDL, or empty for the default (LZ4).</summary>
    public static string Codec(EventColumn column) => Codecs[(int)column];

    public static EventColumnKind Kind(EventColumn column) => Kinds[(int)column];

    /// <summary>True for the columns stored as bytes plus offsets, including the LowCardinality ones.</summary>
    public static bool IsString(EventColumn column) => StringSlots[(int)column] >= 0;

    /// <summary>Slot of a string column inside the batch (0..15); throws for a non-string column.</summary>
    public static int StringSlot(EventColumn column)
    {
        var slot = StringSlots[(int)column];
        return slot >= 0 ? slot : throw new ArgumentException($"{Name(column)} is not a string column", nameof(column));
    }

    public static EventColumn StringColumnAt(int slot) => StringColumnsBySlot[slot];

    /// <summary>Every column in table order.</summary>
    public static ReadOnlySpan<EventColumn> All =>
    [
        EventColumn.CaseId, EventColumn.HostId, EventColumn.FileId, EventColumn.FileOffset, EventColumn.ParseGeneration,
        EventColumn.Ts, EventColumn.Host, EventColumn.Program, EventColumn.Severity, EventColumn.EventCode,
        EventColumn.EventAction, EventColumn.EventOutcome, EventColumn.UserName, EventColumn.SourceIp,
        EventColumn.DestinationIp, EventColumn.ProcessName, EventColumn.UrlPath, EventColumn.HttpStatus,
        EventColumn.MsgOff, EventColumn.MsgLen, EventColumn.MessageText, EventColumn.Raw, EventColumn.Extras,
        EventColumn.HttpMethod, EventColumn.HttpVersion, EventColumn.HttpResponseBodyBytes, EventColumn.HttpReferrer,
        EventColumn.UserAgent, EventColumn.UrlQuery, EventColumn.SyslogFacility, EventColumn.ProcessPid,
    ];

    /// <summary>The stored columns that <c>events_v</c> exposes: everything but the three the <c>message</c> alias is built from.</summary>
    public static bool IsExposed(EventColumn column) => column is not (EventColumn.MsgOff or EventColumn.MsgLen or EventColumn.MessageText);
}

/// <summary>
/// DDL for a table in the pinned shape, from the column metadata above: the benches' and tests' scratch tables. The
/// shipped table is <c>migrations/clickhouse/0001_events.sql</c>, kept by hand; <c>SchemaTests</c> inserts through both.
/// </summary>
public static class EventSchema
{
    public const string TextIndex = "INDEX idx_msg lower(message) TYPE text(tokenizer = 'splitByNonAlpha')";

    /// <summary>A <c>CREATE TABLE</c> with every pinned column, codec and the <c>message</c> alias; <paramref name="index"/> adds the text index.</summary>
    public static string CreateTable(string table, bool index = true, string extra = "")
    {
        ArgumentException.ThrowIfNullOrEmpty(table);
        var sb = new System.Text.StringBuilder();
        sb.Append("CREATE TABLE ").Append(table).Append(" (\n");
        foreach (var column in EventColumns.All)
        {
            sb.Append("  ").Append(EventColumns.Name(column)).Append(' ').Append(EventColumns.ClickHouseType(column));
            var codec = EventColumns.Codec(column);
            if (codec.Length > 0)
            {
                sb.Append(' ').Append(codec);
            }

            sb.Append(",\n");
        }

        sb.Append("  ").Append(EventColumns.MessageAlias).Append(" String ALIAS ").Append(EventColumns.MessageExpression);
        if (index)
        {
            sb.Append(",\n  ").Append(TextIndex);
        }

        sb.Append(extra);
        sb.Append("\n) ENGINE = MergeTree\nPARTITION BY (case_id, toYYYYMM(ts))\nORDER BY (case_id, host, ts)");
        return sb.ToString();
    }
}
