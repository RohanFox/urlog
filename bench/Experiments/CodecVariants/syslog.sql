-- Codec and layout variants of the pinned events schema, built from the kept no-cycle syslog table.
-- Run:  docker exec -i urlog-clickhouse clickhouse-client --password clickhouse --multiquery < bench/Experiments/CodecVariants/syslog.sql
-- Then read system.parts / system.parts_columns / system.query_log (query_id = table name) (results in the design record, "Scale, reference hardware and deployment").
-- Source table: a --keep run of bench/Experiments/InsertMatrix with --distinct-rows equal to --rows (no cycling: cycled copies sort together after merges and flatter ZSTD).
CREATE TABLE codecv_syslog_v0_pinned (
  case_id UInt64, host_id UInt64, file_id UInt64, file_offset UInt64 , parse_generation UInt32,
  ts DateTime64(3, 'UTC') CODEC(Delta, ZSTD),
  host LowCardinality(String), program LowCardinality(String), severity LowCardinality(String),
  event_code UInt32 , event_action LowCardinality(String), event_outcome LowCardinality(String),
  user_name String , source_ip IPv6 , destination_ip IPv6 , process_name LowCardinality(String),
  url_path String , http_status UInt16 ,
  message String CODEC(ZSTD),
  raw String CODEC(ZSTD), extras Map(String, String) ,
  INDEX idx_msg lower(message) TYPE text(tokenizer = 'splitByNonAlpha')
) ENGINE = MergeTree PARTITION BY (case_id, toYYYYMM(ts)) ORDER BY (case_id, host, ts);
INSERT INTO codecv_syslog_v0_pinned (case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, message, raw, extras) SELECT case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, message, raw, extras FROM codec_syslog_nocycle;

CREATE TABLE codecv_syslog_v1_lz4 (
  case_id UInt64, host_id UInt64, file_id UInt64, file_offset UInt64 , parse_generation UInt32,
  ts DateTime64(3, 'UTC') CODEC(Delta, LZ4),
  host LowCardinality(String), program LowCardinality(String), severity LowCardinality(String),
  event_code UInt32 , event_action LowCardinality(String), event_outcome LowCardinality(String),
  user_name String , source_ip IPv6 , destination_ip IPv6 , process_name LowCardinality(String),
  url_path String , http_status UInt16 ,
  message String CODEC(LZ4),
  raw String CODEC(LZ4), extras Map(String, String) ,
  INDEX idx_msg lower(message) TYPE text(tokenizer = 'splitByNonAlpha')
) ENGINE = MergeTree PARTITION BY (case_id, toYYYYMM(ts)) ORDER BY (case_id, host, ts);
INSERT INTO codecv_syslog_v1_lz4 (case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, message, raw, extras) SELECT case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, message, raw, extras FROM codec_syslog_nocycle;

CREATE TABLE codecv_syslog_v2_zstd1_all (
  case_id UInt64, host_id UInt64, file_id UInt64, file_offset UInt64 , parse_generation UInt32,
  ts DateTime64(3, 'UTC') CODEC(Delta, ZSTD),
  host LowCardinality(String), program LowCardinality(String), severity LowCardinality(String),
  event_code UInt32 , event_action LowCardinality(String), event_outcome LowCardinality(String),
  user_name String CODEC(ZSTD(1)), source_ip IPv6 CODEC(ZSTD(1)), destination_ip IPv6 CODEC(ZSTD(1)), process_name LowCardinality(String),
  url_path String CODEC(ZSTD(1)), http_status UInt16 ,
  message String CODEC(ZSTD(1)),
  raw String CODEC(ZSTD(1)), extras Map(String, String) CODEC(ZSTD(1)),
  INDEX idx_msg lower(message) TYPE text(tokenizer = 'splitByNonAlpha')
) ENGINE = MergeTree PARTITION BY (case_id, toYYYYMM(ts)) ORDER BY (case_id, host, ts);
INSERT INTO codecv_syslog_v2_zstd1_all (case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, message, raw, extras) SELECT case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, message, raw, extras FROM codec_syslog_nocycle;

CREATE TABLE codecv_syslog_v3_zstd3_all (
  case_id UInt64, host_id UInt64, file_id UInt64, file_offset UInt64 , parse_generation UInt32,
  ts DateTime64(3, 'UTC') CODEC(Delta, ZSTD),
  host LowCardinality(String), program LowCardinality(String), severity LowCardinality(String),
  event_code UInt32 , event_action LowCardinality(String), event_outcome LowCardinality(String),
  user_name String CODEC(ZSTD(3)), source_ip IPv6 CODEC(ZSTD(3)), destination_ip IPv6 CODEC(ZSTD(3)), process_name LowCardinality(String),
  url_path String CODEC(ZSTD(3)), http_status UInt16 ,
  message String CODEC(ZSTD(3)),
  raw String CODEC(ZSTD(3)), extras Map(String, String) CODEC(ZSTD(3)),
  INDEX idx_msg lower(message) TYPE text(tokenizer = 'splitByNonAlpha')
) ENGINE = MergeTree PARTITION BY (case_id, toYYYYMM(ts)) ORDER BY (case_id, host, ts);
INSERT INTO codecv_syslog_v3_zstd3_all (case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, message, raw, extras) SELECT case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, message, raw, extras FROM codec_syslog_nocycle;

CREATE TABLE codecv_syslog_v4_ints (
  case_id UInt64, host_id UInt64, file_id UInt64, file_offset UInt64 CODEC(Delta, ZSTD(1)), parse_generation UInt32,
  ts DateTime64(3, 'UTC') CODEC(DoubleDelta, ZSTD(1)),
  host LowCardinality(String), program LowCardinality(String), severity LowCardinality(String),
  event_code UInt32 CODEC(T64, ZSTD(1)), event_action LowCardinality(String), event_outcome LowCardinality(String),
  user_name String CODEC(ZSTD(1)), source_ip IPv6 CODEC(ZSTD(1)), destination_ip IPv6 CODEC(ZSTD(1)), process_name LowCardinality(String),
  url_path String CODEC(ZSTD(1)), http_status UInt16 CODEC(T64, ZSTD(1)),
  message String CODEC(ZSTD(1)),
  raw String CODEC(ZSTD(1)), extras Map(String, String) CODEC(ZSTD(1)),
  INDEX idx_msg lower(message) TYPE text(tokenizer = 'splitByNonAlpha')
) ENGINE = MergeTree PARTITION BY (case_id, toYYYYMM(ts)) ORDER BY (case_id, host, ts);
INSERT INTO codecv_syslog_v4_ints (case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, message, raw, extras) SELECT case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, message, raw, extras FROM codec_syslog_nocycle;

CREATE TABLE codecv_syslog_v4b_ints_dd (
  case_id UInt64, host_id UInt64, file_id UInt64, file_offset UInt64 CODEC(DoubleDelta, ZSTD(1)), parse_generation UInt32,
  ts DateTime64(3, 'UTC') CODEC(Delta, ZSTD(1)),
  host LowCardinality(String), program LowCardinality(String), severity LowCardinality(String),
  event_code UInt32 CODEC(ZSTD(1)), event_action LowCardinality(String), event_outcome LowCardinality(String),
  user_name String CODEC(ZSTD(1)), source_ip IPv6 CODEC(ZSTD(1)), destination_ip IPv6 CODEC(ZSTD(1)), process_name LowCardinality(String),
  url_path String CODEC(ZSTD(1)), http_status UInt16 CODEC(ZSTD(1)),
  message String CODEC(ZSTD(1)),
  raw String CODEC(ZSTD(1)), extras Map(String, String) CODEC(ZSTD(1)),
  INDEX idx_msg lower(message) TYPE text(tokenizer = 'splitByNonAlpha')
) ENGINE = MergeTree PARTITION BY (case_id, toYYYYMM(ts)) ORDER BY (case_id, host, ts);
INSERT INTO codecv_syslog_v4b_ints_dd (case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, message, raw, extras) SELECT case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, message, raw, extras FROM codec_syslog_nocycle;

CREATE TABLE codecv_syslog_v5_maplc (
  case_id UInt64, host_id UInt64, file_id UInt64, file_offset UInt64 , parse_generation UInt32,
  ts DateTime64(3, 'UTC') CODEC(Delta, ZSTD),
  host LowCardinality(String), program LowCardinality(String), severity LowCardinality(String),
  event_code UInt32 , event_action LowCardinality(String), event_outcome LowCardinality(String),
  user_name String CODEC(ZSTD(1)), source_ip IPv6 CODEC(ZSTD(1)), destination_ip IPv6 CODEC(ZSTD(1)), process_name LowCardinality(String),
  url_path String CODEC(ZSTD(1)), http_status UInt16 ,
  message String CODEC(ZSTD(1)),
  raw String CODEC(ZSTD(1)), extras Map(LowCardinality(String), String) CODEC(ZSTD(1)),
  INDEX idx_msg lower(message) TYPE text(tokenizer = 'splitByNonAlpha')
) ENGINE = MergeTree PARTITION BY (case_id, toYYYYMM(ts)) ORDER BY (case_id, host, ts);
INSERT INTO codecv_syslog_v5_maplc (case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, message, raw, extras) SELECT case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, message, raw, extras FROM codec_syslog_nocycle;

CREATE TABLE codecv_syslog_v6_msgalias (
  case_id UInt64, host_id UInt64, file_id UInt64, file_offset UInt64 , parse_generation UInt32,
  ts DateTime64(3, 'UTC') CODEC(Delta, ZSTD),
  host LowCardinality(String), program LowCardinality(String), severity LowCardinality(String),
  event_code UInt32 , event_action LowCardinality(String), event_outcome LowCardinality(String),
  user_name String CODEC(ZSTD(1)), source_ip IPv6 CODEC(ZSTD(1)), destination_ip IPv6 CODEC(ZSTD(1)), process_name LowCardinality(String),
  url_path String CODEC(ZSTD(1)), http_status UInt16 ,
  msg_off UInt32 CODEC(ZSTD(1)), msg_len UInt32 CODEC(ZSTD(1)), message String ALIAS substring(raw, msg_off + 1, msg_len),
  raw String CODEC(ZSTD(1)), extras Map(String, String) CODEC(ZSTD(1)),
  INDEX idx_msg lower(message) TYPE text(tokenizer = 'splitByNonAlpha')
) ENGINE = MergeTree PARTITION BY (case_id, toYYYYMM(ts)) ORDER BY (case_id, host, ts);
INSERT INTO codecv_syslog_v6_msgalias (case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, msg_off, msg_len, raw, extras) SELECT case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, toUInt32(position(raw, message) - 1), toUInt32(length(message)), raw, extras FROM codec_syslog_nocycle;

