-- user_agent as String (pinned) against LowCardinality(String), on the scratch cases a corpus ingest left in events.
-- Run as the admin account on the dev stack only:
--   docker exec -i urlog-clickhouse clickhouse-client --password clickhouse --multiquery < bench/Experiments/CodecVariants/corpus-user-agent.sql
-- These are direct reads of events (not through events_v): a measurement by the admin on scratch cases, as the fixture variants were.
-- Both tables are filled by the same INSERT ... SELECT so their part structure is the same; compare them with each other,
-- not with the partitions of events. Read system.parts / system.parts_columns afterwards (result in the design record, "Scale, reference hardware and deployment"),
-- then DROP both tables. Every query against the rows returns counts, lengths and byte sizes only.
-- Scratch cases: 9131, 9132 (urlog ingest --case). Nothing in this file comes from the corpus.
DROP TABLE IF EXISTS corpus_nginx_v0;
CREATE TABLE corpus_nginx_v0 AS events;

DROP TABLE IF EXISTS corpus_nginx_ua_lc;
CREATE TABLE corpus_nginx_ua_lc AS events;
ALTER TABLE corpus_nginx_ua_lc MODIFY COLUMN user_agent LowCardinality(String) CODEC(ZSTD(1));

INSERT INTO corpus_nginx_v0 (case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, msg_off, msg_len, message_text, raw, extras, http_method, http_version, http_response_body_bytes, http_referrer, user_agent, url_query, syslog_facility, process_pid)
SELECT case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, msg_off, msg_len, message_text, raw, extras, http_method, http_version, http_response_body_bytes, http_referrer, user_agent, url_query, syslog_facility, process_pid
FROM events WHERE case_id IN (9131, 9132)
SETTINGS log_comment = 'corpus_nginx_v0';

INSERT INTO corpus_nginx_ua_lc (case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, msg_off, msg_len, message_text, raw, extras, http_method, http_version, http_response_body_bytes, http_referrer, user_agent, url_query, syslog_facility, process_pid)
SELECT case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, msg_off, msg_len, message_text, raw, extras, http_method, http_version, http_response_body_bytes, http_referrer, user_agent, url_query, syslog_facility, process_pid
FROM events WHERE case_id IN (9131, 9132)
SETTINGS log_comment = 'corpus_nginx_ua_lc';
