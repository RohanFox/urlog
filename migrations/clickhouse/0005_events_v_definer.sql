-- 0005: events_v runs with the definer's rights (docs/adr/0001-case-isolation.md).
-- Measured on 26.8.36.3: a view without a SQL SECURITY clause runs as the invoker, so a user granted SELECT on
-- events_v alone is refused with ACCESS_DENIED on `events` the moment it reads the view. The query path's user must
-- never hold a grant on `events` (design record, "Security"), so the view is re-created as SQL SECURITY DEFINER: the user needs
-- SELECT on events_v only, and the view reads `events` and the generation dictionary as the user that ran this
-- migration. That user is the migrator's admin account and must stay (the view breaks if it is dropped or renamed).
-- The SELECT is 0003's unchanged: the same two filters, the same column list; CREATE OR REPLACE swaps the definition
-- in place (Atomic database) and changes nothing stored.
CREATE OR REPLACE VIEW events_v
SQL SECURITY DEFINER DEFINER = CURRENT_USER
AS SELECT
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
