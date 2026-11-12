-- 0002: which parse generation of each file is the visible one. SQLite is the source of truth; the backend writes a
-- row here on every re-parse commit and the startup reconciler re-syncs the two (design record, "ClickHouse
-- schema"). ReplacingMergeTree keyed on file_id; the newest updated_at wins.
CREATE TABLE IF NOT EXISTS file_generation
(
    file_id    UInt64,
    current    UInt32,
    updated_at DateTime64(3, 'UTC')
)
ENGINE = ReplacingMergeTree(updated_at)
ORDER BY file_id;

-- The dictionary events_v reads. Its source query collapses the ReplacingMergeTree so a not-yet-merged duplicate
-- can never surface an old generation. Short lifetime, and the backend runs SYSTEM RELOAD DICTIONARY after a commit.
-- A local ClickHouse source authenticates as a named user; urlog_dict (deploy/clickhouse/users.d/urlog.xml) has no
-- password, is read-only and may SELECT this table only, so no credential lives in this file.
CREATE DICTIONARY IF NOT EXISTS file_generation_dict
(
    file_id UInt64,
    current UInt32
)
PRIMARY KEY file_id
SOURCE(CLICKHOUSE(USER 'urlog_dict' QUERY 'SELECT file_id, argMax(current, updated_at) AS current FROM file_generation GROUP BY file_id'))
LAYOUT(HASHED())
LIFETIME(MIN 1 MAX 5);
