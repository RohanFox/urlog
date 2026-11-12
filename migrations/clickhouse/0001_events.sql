-- 0001: the events table. Design record, "ClickHouse schema".
-- One wide table for every case, never one table per case. Monthly partitions per case; time pruning comes from
-- ts in the sort key. LowCardinality strings are lowercased at ingest. The text index needs tokenizer
-- 'splitByNonAlpha' on 26.8 ('default' is rejected).
--
-- Re-cut once from the per-column measurement (design record, "Scale, reference hardware and deployment"), before anything was
-- deployed: ZSTD(1) on every String, IPv6 and the Map, Delta on file_offset, T64 on the small integers; the logical
-- `message` is an ALIAS over `raw` located by msg_off/msg_len (message_text is the stored fallback for a source whose
-- message is not a byte slice of its record, empty otherwise); the Map keys are LowCardinality; the eight columns after
-- extras were extras keys before the re-cut. Column order is the Native block layout (src/Core/Sources/EventColumn.cs).
-- Additive-only from here on: a later migration may ADD COLUMN with a default; a column never changes meaning.
CREATE TABLE IF NOT EXISTS events
(
    case_id                  UInt64,
    host_id                  UInt64,
    file_id                  UInt64,
    file_offset              UInt64 CODEC(Delta, ZSTD(1)),
    parse_generation         UInt32,
    ts                       DateTime64(3, 'UTC') CODEC(Delta, ZSTD),
    host                     LowCardinality(String),
    program                  LowCardinality(String),
    severity                 LowCardinality(String),
    event_code               UInt32 CODEC(T64, ZSTD(1)),
    event_action             LowCardinality(String),
    event_outcome            LowCardinality(String),
    user_name                String CODEC(ZSTD(1)),
    source_ip                IPv6 CODEC(ZSTD(1)),
    destination_ip           IPv6 CODEC(ZSTD(1)),
    process_name             LowCardinality(String),
    url_path                 String CODEC(ZSTD(1)),
    http_status              UInt16 CODEC(T64, ZSTD(1)),
    msg_off                  UInt32 CODEC(ZSTD(1)),
    msg_len                  UInt32 CODEC(ZSTD(1)),
    message_text             String CODEC(ZSTD(1)),
    raw                      String CODEC(ZSTD(1)),
    extras                   Map(LowCardinality(String), String) CODEC(ZSTD(1)),
    http_method              LowCardinality(String),
    http_version             LowCardinality(String),
    http_response_body_bytes UInt64 CODEC(T64, ZSTD(1)),
    http_referrer            String CODEC(ZSTD(1)),
    user_agent               String CODEC(ZSTD(1)),
    url_query                String CODEC(ZSTD(1)),
    syslog_facility          LowCardinality(String),
    process_pid              UInt32 CODEC(T64, ZSTD(1)),
    message                  String ALIAS if(msg_len = 0, message_text, substring(raw, msg_off + 1, msg_len)),
    INDEX idx_msg lower(message) TYPE text(tokenizer = 'splitByNonAlpha')
)
ENGINE = MergeTree
PARTITION BY (case_id, toYYYYMM(ts))
ORDER BY (case_id, host, ts);
