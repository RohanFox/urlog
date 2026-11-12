-- 0003: the only relation the query path may read (design record, "Security"; ADR 0001).
-- Two filters:
--   1. the allowed-case list, sent by the backend as the per-query custom setting SQL_allowed_cases ('[1,7]' form)
--      and never written into the analyst's SQL. No setting means no cases: an unauthorised user reads zero rows.
--      This form is confirmed on the pinned LTS (docs/adr/0001-case-isolation.md); the row-policy fallback was not taken.
--   2. the generation filter: a row is visible only while its parse_generation is the file's current one, so a
--      half-finished re-parse (new rows inserted, old rows still waiting for the lightweight delete) is never
--      visible as duplicates. A file without a file_generation row is on its first parse (generation 1).
-- The columns are named: `message` is an ALIAS on events, which SELECT * leaves out, and msg_off, msg_len and
-- message_text are its building blocks, not fields. The catalog and the transpiler know exactly one `message`.
CREATE VIEW IF NOT EXISTS events_v AS
SELECT
    case_id, host_id, file_id, file_offset, parse_generation, ts,
    host, program, severity, event_code, event_action, event_outcome,
    user_name, source_ip, destination_ip, process_name, url_path, http_status,
    message, raw, extras,
    http_method, http_version, http_response_body_bytes, http_referrer, user_agent, url_query,
    syslog_facility, process_pid
FROM events
WHERE has(
        arrayMap(x -> toUInt64OrZero(x), splitByChar(',', replaceRegexpAll(getSettingOrDefault('SQL_allowed_cases', ''), '[\\[\\] ]', ''))),
        case_id)
  AND parse_generation = dictGetOrDefault('file_generation_dict', 'current', file_id, toUInt32(1));
