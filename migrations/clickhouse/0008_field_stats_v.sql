-- 0008: field statistics are read through a view filtered by the allowed cases, like events.
-- field_stats is a plain table keyed by case, and until now the query account held SELECT on it and the raw-SQL
-- gate allowed its name: a query could read the statistics of any case, whatever case list its request carried.
-- The table was empty, since nothing wrote it. The statistics task fills it from now on, so the query account
-- reads field_stats_v instead (its grant moves in users.d) and the gate refuses the table's name.
--
-- The view shows one set per case, the newest. A refresh writes all rows of a case in one INSERT with one
-- computed_at; rows of an older refresh whose (field, value) is not in the new set stay in the table until the
-- delete that follows, and the view leaves them out. The newest computed_at is found in the same scan as the rows
-- (a window over the case), so one snapshot of the table decides: a reader gets the whole old set or the whole
-- new one. Measured on 26.8.36.3: the case filter is applied below the window and uses the primary key.
--
-- Rows of a set: (field '', value '') carries the number of events of the case; (field, value '') the number of
-- events that have a value in the field and, where it was computed, an estimate of how many distinct values;
-- (field, value) the exact count of one of the field's most frequent values.
ALTER TABLE field_stats ADD COLUMN IF NOT EXISTS distinct_estimate UInt64 DEFAULT 0;

CREATE OR REPLACE VIEW field_stats_v
SQL SECURITY DEFINER DEFINER = CURRENT_USER
AS SELECT case_id, field, value, count, distinct_estimate, computed_at
FROM
(
    SELECT
        case_id, field, value, count, distinct_estimate, computed_at,
        max(computed_at) OVER (PARTITION BY case_id) AS newest
    FROM field_stats
    WHERE has(
            arrayMap(x -> toUInt64OrZero(x), splitByChar(',', replaceRegexpAll(getSettingOrDefault('SQL_allowed_cases', ''), '[\\[\\] ]', ''))),
            case_id)
)
WHERE computed_at = newest;
