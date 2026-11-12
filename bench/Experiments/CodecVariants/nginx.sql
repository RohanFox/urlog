-- Codec and layout variants of the pinned events schema, built from the kept no-cycle nginx table.
-- Run:  docker exec -i urlog-clickhouse clickhouse-client --password clickhouse --multiquery < bench/Experiments/CodecVariants/nginx.sql
-- Then read system.parts / system.parts_columns / system.query_log (query_id = table name) (results in the design record, "Scale, reference hardware and deployment").
-- Source table: a --keep run of bench/Experiments/InsertMatrix with --distinct-rows equal to --rows (no cycling: cycled copies sort together after merges and flatter ZSTD).
CREATE TABLE codecv_nginx_v0_pinned (
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
INSERT INTO codecv_nginx_v0_pinned (case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, message, raw, extras) SELECT case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, message, raw, extras FROM codec_nginx_kept;

CREATE TABLE codecv_nginx_v1_lz4 (
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
INSERT INTO codecv_nginx_v1_lz4 (case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, message, raw, extras) SELECT case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, message, raw, extras FROM codec_nginx_kept;

CREATE TABLE codecv_nginx_v2_zstd1_all (
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
INSERT INTO codecv_nginx_v2_zstd1_all (case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, message, raw, extras) SELECT case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, message, raw, extras FROM codec_nginx_kept;

CREATE TABLE codecv_nginx_v3_zstd3_all (
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
INSERT INTO codecv_nginx_v3_zstd3_all (case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, message, raw, extras) SELECT case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, message, raw, extras FROM codec_nginx_kept;

CREATE TABLE codecv_nginx_v4_ints (
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
INSERT INTO codecv_nginx_v4_ints (case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, message, raw, extras) SELECT case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, message, raw, extras FROM codec_nginx_kept;

CREATE TABLE codecv_nginx_v4b_ints_dd (
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
INSERT INTO codecv_nginx_v4b_ints_dd (case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, message, raw, extras) SELECT case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, message, raw, extras FROM codec_nginx_kept;

CREATE TABLE codecv_nginx_v5_maplc (
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
INSERT INTO codecv_nginx_v5_maplc (case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, message, raw, extras) SELECT case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, message, raw, extras FROM codec_nginx_kept;

CREATE TABLE codecv_nginx_v6_msgalias (
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
INSERT INTO codecv_nginx_v6_msgalias (case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, msg_off, msg_len, raw, extras) SELECT case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, toUInt32(position(raw, message) - 1), toUInt32(length(message)), raw, extras FROM codec_nginx_kept;

CREATE TABLE codecv_nginx_v7_promoted (
  case_id UInt64, host_id UInt64, file_id UInt64, file_offset UInt64 , parse_generation UInt32,
  ts DateTime64(3, 'UTC') CODEC(Delta, ZSTD),
  host LowCardinality(String), program LowCardinality(String), severity LowCardinality(String),
  event_code UInt32 , event_action LowCardinality(String), event_outcome LowCardinality(String),
  user_name String CODEC(ZSTD(1)), source_ip IPv6 CODEC(ZSTD(1)), destination_ip IPv6 CODEC(ZSTD(1)), process_name LowCardinality(String),
  url_path String CODEC(ZSTD(1)), http_status UInt16 ,
  message String CODEC(ZSTD(1)),
  raw String CODEC(ZSTD(1)), extras Map(String, String) CODEC(ZSTD(1)),
  http_method LowCardinality(String), http_version LowCardinality(String), http_response_body_bytes UInt64 CODEC(T64, ZSTD(1)),
  http_referrer String CODEC(ZSTD(1)), user_agent String CODEC(ZSTD(1)), url_query String CODEC(ZSTD(1)),
  INDEX idx_msg lower(message) TYPE text(tokenizer = 'splitByNonAlpha')
) ENGINE = MergeTree PARTITION BY (case_id, toYYYYMM(ts)) ORDER BY (case_id, host, ts);
INSERT INTO codecv_nginx_v7_promoted (case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, message, raw, extras, http_method, http_version, http_response_body_bytes, http_referrer, user_agent, url_query) SELECT case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, message, raw, mapFilter((k, v) -> k NOT IN ('http.request.method','http.version','http.response.body.bytes','http.request.referrer','user_agent.original','url.query'), extras),

CREATE TABLE codecv_nginx_v7b_promoted_ualc (
  case_id UInt64, host_id UInt64, file_id UInt64, file_offset UInt64 , parse_generation UInt32,
  ts DateTime64(3, 'UTC') CODEC(Delta, ZSTD),
  host LowCardinality(String), program LowCardinality(String), severity LowCardinality(String),
  event_code UInt32 , event_action LowCardinality(String), event_outcome LowCardinality(String),
  user_name String CODEC(ZSTD(1)), source_ip IPv6 CODEC(ZSTD(1)), destination_ip IPv6 CODEC(ZSTD(1)), process_name LowCardinality(String),
  url_path String CODEC(ZSTD(1)), http_status UInt16 ,
  message String CODEC(ZSTD(1)),
  raw String CODEC(ZSTD(1)), extras Map(String, String) CODEC(ZSTD(1)),
  http_method LowCardinality(String), http_version LowCardinality(String), http_response_body_bytes UInt64 CODEC(T64, ZSTD(1)),
  http_referrer String CODEC(ZSTD(1)), user_agent LowCardinality(String) CODEC(ZSTD(1)), url_query String CODEC(ZSTD(1)),
  INDEX idx_msg lower(message) TYPE text(tokenizer = 'splitByNonAlpha')
) ENGINE = MergeTree PARTITION BY (case_id, toYYYYMM(ts)) ORDER BY (case_id, host, ts);
INSERT INTO codecv_nginx_v7b_promoted_ualc (case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, message, raw, extras, http_method, http_version, http_response_body_bytes, http_referrer, user_agent, url_query) SELECT case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, message, raw, mapFilter((k, v) -> k NOT IN ('http.request.method','http.version','http.response.body.bytes','http.request.referrer','user_agent.original','url.query'), extras),


-- Combined variants (v9 = every lever, v10 = v9 plus a second text index on lower(raw)).
DROP TABLE IF EXISTS codecv_nginx_v9_combo;
CREATE TABLE codecv_nginx_v9_combo (
  case_id UInt64, host_id UInt64, file_id UInt64, file_offset UInt64 CODEC(Delta, ZSTD(1)), parse_generation UInt32,
  ts DateTime64(3, 'UTC') CODEC(Delta, ZSTD(1)),
  host LowCardinality(String), program LowCardinality(String), severity LowCardinality(String),
  event_code UInt32 CODEC(T64, ZSTD(1)), event_action LowCardinality(String), event_outcome LowCardinality(String),
  user_name String CODEC(ZSTD(1)), source_ip IPv6 CODEC(ZSTD(1)), destination_ip IPv6 CODEC(ZSTD(1)), process_name LowCardinality(String),
  url_path String CODEC(ZSTD(1)), http_status UInt16 CODEC(T64, ZSTD(1)),
  msg_off UInt32 CODEC(ZSTD(1)), msg_len UInt32 CODEC(ZSTD(1)), message String ALIAS substring(raw, msg_off + 1, msg_len),
  raw String CODEC(ZSTD(1)), extras Map(LowCardinality(String), String) CODEC(ZSTD(1)),
  http_method LowCardinality(String), http_version LowCardinality(String), http_response_body_bytes UInt64 CODEC(T64, ZSTD(1)),
  http_referrer String CODEC(ZSTD(1)), user_agent String CODEC(ZSTD(1)), url_query String CODEC(ZSTD(1)),
  INDEX idx_msg lower(message) TYPE text(tokenizer = 'splitByNonAlpha')
) ENGINE = MergeTree PARTITION BY (case_id, toYYYYMM(ts)) ORDER BY (case_id, host, ts);
DROP TABLE IF EXISTS codecv_nginx_v10_combo_rawidx;
CREATE TABLE codecv_nginx_v10_combo_rawidx AS codecv_nginx_v9_combo;
ALTER TABLE codecv_nginx_v10_combo_rawidx ADD INDEX idx_raw lower(raw) TYPE text(tokenizer = 'splitByNonAlpha');
DROP TABLE IF EXISTS codecv_syslog_v7_promoted;
CREATE TABLE codecv_syslog_v7_promoted (
  case_id UInt64, host_id UInt64, file_id UInt64, file_offset UInt64, parse_generation UInt32,
  ts DateTime64(3, 'UTC') CODEC(Delta, ZSTD),
  host LowCardinality(String), program LowCardinality(String), severity LowCardinality(String),
  event_code UInt32, event_action LowCardinality(String), event_outcome LowCardinality(String),
  user_name String CODEC(ZSTD(1)), source_ip IPv6 CODEC(ZSTD(1)), destination_ip IPv6 CODEC(ZSTD(1)), process_name LowCardinality(String),
  url_path String CODEC(ZSTD(1)), http_status UInt16,
  message String CODEC(ZSTD(1)), raw String CODEC(ZSTD(1)), extras Map(String, String) CODEC(ZSTD(1)),
  syslog_facility LowCardinality(String), process_pid UInt32 CODEC(T64, ZSTD(1)),
  INDEX idx_msg lower(message) TYPE text(tokenizer = 'splitByNonAlpha')
) ENGINE = MergeTree PARTITION BY (case_id, toYYYYMM(ts)) ORDER BY (case_id, host, ts);
DROP TABLE IF EXISTS codecv_syslog_v9_combo;
CREATE TABLE codecv_syslog_v9_combo (
  case_id UInt64, host_id UInt64, file_id UInt64, file_offset UInt64 CODEC(Delta, ZSTD(1)), parse_generation UInt32,
  ts DateTime64(3, 'UTC') CODEC(Delta, ZSTD(1)),
  host LowCardinality(String), program LowCardinality(String), severity LowCardinality(String),
  event_code UInt32 CODEC(T64, ZSTD(1)), event_action LowCardinality(String), event_outcome LowCardinality(String),
  user_name String CODEC(ZSTD(1)), source_ip IPv6 CODEC(ZSTD(1)), destination_ip IPv6 CODEC(ZSTD(1)), process_name LowCardinality(String),
  url_path String CODEC(ZSTD(1)), http_status UInt16 CODEC(T64, ZSTD(1)),
  msg_off UInt32 CODEC(ZSTD(1)), msg_len UInt32 CODEC(ZSTD(1)), message String ALIAS substring(raw, msg_off + 1, msg_len),
  raw String CODEC(ZSTD(1)), extras Map(LowCardinality(String), String) CODEC(ZSTD(1)),
  syslog_facility LowCardinality(String), process_pid UInt32 CODEC(T64, ZSTD(1)),
  INDEX idx_msg lower(message) TYPE text(tokenizer = 'splitByNonAlpha')
) ENGINE = MergeTree PARTITION BY (case_id, toYYYYMM(ts)) ORDER BY (case_id, host, ts);
DROP TABLE IF EXISTS codecv_syslog_v10_combo_rawidx;
CREATE TABLE codecv_syslog_v10_combo_rawidx AS codecv_syslog_v9_combo;
ALTER TABLE codecv_syslog_v10_combo_rawidx ADD INDEX idx_raw lower(raw) TYPE text(tokenizer = 'splitByNonAlpha');
