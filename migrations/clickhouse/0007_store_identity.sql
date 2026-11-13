-- 0007: the identity of this event store (design record, "Consistency", Store identity). One row, written once by
-- the admin account (`urlog migrate --db` for a new pair, `urlog pair` for a store that already holds data) and
-- never changed. The metadata file that belongs to this store holds the same value, and every writer compares the
-- two before it writes to either: file_generation is keyed by file id alone, so a metadata file from somewhere else
-- would overwrite the visible generation of files it has never seen. The table is created empty; a store without a
-- row has no identity yet.
CREATE TABLE IF NOT EXISTS store_identity
(
    store_id   String,
    created_at DateTime64(3, 'UTC'),
    created_by String
)
ENGINE = MergeTree
ORDER BY tuple();
