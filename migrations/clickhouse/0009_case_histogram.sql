-- 0009: the stored histogram of a case, for the timeline of a search that narrows nothing.
-- Read from the events, that timeline is a scan of every row's time and severity (355 ms at 25 M rows, 1,439 ms at
-- 100 M). case_histogram holds the same counts per minute: one row per file, parse generation, severity and UTC
-- minute, written by one INSERT ... SELECT over the rows of a parse attempt after its last insert and before
-- its commit.
--
-- A row carries the generation it was counted from, and the view filters on the committed generation of each file
-- with the filter of events_v (0006), word for word. The file_generation row that makes a parse's events visible
-- therefore makes its buckets visible in the same statement, and the buckets of an attempt that failed or that a
-- re-parse has replaced are hidden like its events until their delete has run. A plain MergeTree and a sum at
-- read: no engine may fold rows of two generations together.
--
-- The host is not a dimension. Measured with it on a syslog file of 7.8 M events from 24 hosts: 3.14 M rows, a fill
-- of 11.5 to 12.8 % of the parse job and 833 MiB for its GROUP BY. A timeline per host needs a design of its own.
--
-- The sort key has the file and the generation before the minute. Measured on 3.5 M rows (a case of 100 M
-- events): the interior of the read took 50 to 58 ms with the minute first and 44 to 52 ms this way. bucket has
-- the type of events.ts. As a DateTime the read took 37 to 43 ms, and a day cut in a zone east of UTC then
-- started at another instant for rows at the epoch than it does over events.ts, so the type stays.
-- The bucket function returns a DateTime, 1970 to 2106: a parse with a time outside that writes no rows here,
-- and its case reads its timeline from the events.
-- Partitioned by case: archive and purge drop the partition, as they drop the case's partitions of events.
CREATE TABLE IF NOT EXISTS case_histogram
(
    case_id          UInt64,
    file_id          UInt64,
    parse_generation UInt32,
    severity         LowCardinality(String),
    bucket           DateTime64(3, 'UTC') CODEC(Delta, ZSTD(1)),
    events           UInt64 CODEC(T64, ZSTD(1))
)
ENGINE = MergeTree
PARTITION BY case_id
ORDER BY (case_id, file_id, parse_generation, bucket, severity);

-- Read through the view only: the allowed cases of the request, the committed generation of each file.
CREATE OR REPLACE VIEW case_histogram_v
SQL SECURITY DEFINER DEFINER = CURRENT_USER
AS SELECT case_id, severity, bucket, events
FROM case_histogram
WHERE has(
        arrayMap(x -> toUInt64OrZero(x), splitByChar(',', replaceRegexpAll(getSettingOrDefault('SQL_allowed_cases', ''), '[\\[\\] ]', ''))),
        case_id)
  AND (file_id, parse_generation) IN (
        SELECT file_id, argMax(current, updated_at) AS visible
        FROM file_generation
        GROUP BY file_id
        HAVING visible > 0);
