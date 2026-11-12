-- 0004: per-case field statistics for autocomplete (design record, "Autocomplete"): which fields exist in a case and
-- their top-N values with counts, refreshed after each ingest by the field statistics job. Keyed by (case, field, value); a
-- refresh writes new rows with a newer computed_at and the ReplacingMergeTree drops the old ones. Dropping a case
-- deletes its rows by DELETE WHERE case_id here (small table, not partitioned).
CREATE TABLE IF NOT EXISTS field_stats
(
    case_id     UInt64,
    field       LowCardinality(String),
    value       String,
    count       UInt64,
    computed_at DateTime64(3, 'UTC')
)
ENGINE = ReplacingMergeTree(computed_at)
ORDER BY (case_id, field, value);
