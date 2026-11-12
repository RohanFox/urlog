-- 0006: a file is visible only after its generation is committed, and a query sees one answer to "which
-- generation" for its whole run.
-- Until now events_v asked file_generation_dict per block, with generation 1 as the answer for a file without a row.
-- Two consequences. The rows of a first parse were readable while the parse was still running, and so were the rows
-- of a file whose hash had not been compared yet (the hash shares the read with the parse and is known at end of
-- file). And a dictionary reload could land while a query was scanning: measured, a count() in flight at a commit
-- read 1,408 of 4,809,713 rows, and a re-parse commit could mix two generations in one result.
-- The view now filters on a set built once per query from file_generation itself: the newest row per file, files
-- whose newest row is 0 left out. No row, nothing visible; generations start at 1; the set is a snapshot, so a
-- query started before a commit sees the old state for all of its rows and one started after sees the new.
-- Measured on 25.2 M rows (the reference stats query, 20 runs warm, three alternations): p95 212 ms against 205 ms
-- for the dictionary form; EXPLAIN indexes = 1 prunes partitions and the primary key on case_id and ts as before.
-- The definer clause, the case filter and the column list are 0005's unchanged. The view reads file_generation with
-- its definer's rights, so the query user still holds SELECT on events_v only.
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
  AND (file_id, parse_generation) IN (
        SELECT file_id, argMax(current, updated_at) AS visible
        FROM file_generation
        GROUP BY file_id
        HAVING visible > 0);

-- Nothing reads the dictionary any more. With it go its passwordless reader account (urlog_dict, removed from
-- users.d) and the write path's SYSTEM RELOAD DICTIONARY grant.
DROP DICTIONARY IF EXISTS file_generation_dict;
