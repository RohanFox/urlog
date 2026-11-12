# ADR 0001: Per-case isolation: `events_v` on a per-request setting, behind a mandatory gate

Status: accepted (2026-11-06). Closes the isolation spike of the design record's Security section.
Builds on ADR 0002 (driver), ADR 0003 (HTTP for the query path, `SQL_allowed_cases` as a request parameter).

## Context

An analyst with read access to some cases must not read any other case, through the SPL path or the raw-SQL
tab (design record, "Security"). The design record's mechanism: one read-only ClickHouse user, every query against a plain view
`events_v` that filters on `getSetting('SQL_allowed_cases')`, the backend sending that setting per request from
the SQLite ACL, and an `EXPLAIN AST` allowlist in front of the raw tab. The record left it as a one-day spike to
confirm on the pinned LTS (26.8.36.3), with a row policy keyed on the same setting as the fallback.

Everything below was measured on 26.8.36.3 through the driver's HTTP client (`ClickHouse.Driver` 1.5.0) as
`urlog_query`. The tests in `tests/Security/` reproduce every result
(Testcontainers, the same `deploy/clickhouse` config.d and users.d files the dev stack mounts; 128 tests).

## What was measured

### 1. The view filters on the request parameter (`CaseIsolationTests`, `UnauthorisedUserTests`)

`readonly = 2`, `custom_settings_prefixes = SQL_`, `SQL_allowed_cases` as a URL parameter of each HTTP request
(`ClickHouseCommand.CustomSettings`), six seeded (case, month) partitions of 100 rows:

| `SQL_allowed_cases` | `SELECT * FROM events_v` | SPL `\| stats count` for case 2 |
|---|---|---|
| `[1]` | 200 rows, all `case_id = 1` | 0 |
| `[1,2]` | 400 rows, cases 1 and 2 | 200 |
| `[1,2,3]` | 600 rows | 200 |
| `[]` | 0 rows | 0 |
| `[7]` (no such case) | 0 rows | 0 |
| parameter absent | 0 rows | 0 |

A case outside the list is invisible however it is named: `WHERE case_id = 2` with `[1]` counts 0, `index=2` in
SPL is refused by the transpiler before any SQL exists, and every scenario of the unauthorised-user theory (seven
SPL queries as owner, as a user with other cases, as a user with none, and as raw `SELECT * FROM events_v WHERE
case_id = 2`) returns zero events for the user without the case.

**The view is a definer view**: a view with no `SQL SECURITY` clause runs as the *invoker* on 26.8, so a user
granted `SELECT` on `events_v` alone is refused with `ACCESS_DENIED: grant SELECT ON default.events` the moment it
reads the view. The query user must never hold a grant on `events`, so migration `0005_events_v_definer.sql`
re-creates the view as `SQL SECURITY DEFINER DEFINER = CURRENT_USER`: the user needs `SELECT` on the view only and
the view reads `events` and `file_generation` as the account that ran the migration (until migration 0006 it
read a dictionary over that table; see the amendment at the end). Consequence for
operations: the migrator's admin account is the view's definer and must not be dropped or renamed.

### 2. Partition pruning survives the view and the setting (`CaseIsolationTests`)

`EXPLAIN indexes = 1` of the transpiled `host=web-01` for case 1, February, through `events_v` with
`SQL_allowed_cases=[1]` on the request: Min-Max `Parts: 1/6`, Partition keys `case_id, toYYYYMM(ts)` with
`Parts: 1/1`, PrimaryKey `case_id, host, ts` with binary search. The view's `has(arrayMap(...), case_id)` folds to
`(case_id in 1-element set)` in the condition, so the setting costs nothing at plan time. Raw
`SELECT count() ... WHERE case_id = 1 AND host = 'web-01'`: `Parts: 2/6` (both months of case 1).

### 3. The raw-SQL gate (`RawSqlGateTests` in tests/Security and tests/Query.Tests)

`src/Query/RawSqlGate.cs` walks the tree that `EXPLAIN AST` dumps (one node per line, one space per level, run as
the same read-only user, so nothing is executed) and refuses anything but a `SelectWithUnionQuery` whose only
root children are the select list; any `Set` node (a `SETTINGS` clause, wherever it sits: top level, a subquery,
a scalar, after a UNION); any `Function` under a `TableExpression` (every table function: `url`, `file`, `remote`,
`numbers`, `view`, ...); any qualified name (`system.tables`, `default.events_v`); the relations the migrations
create other than `events_v` and `field_stats` (`events`, `file_generation`, `schema_migrations`), in `FROM`, `JOIN`, a subquery or the right side of `IN`; more than 8 nested SELECTs; and,
before the server sees anything, an empty text or one over 16 KB. A text the server refuses to parse is a
rejection too: comment-split keywords (`SEL/**/ECT`, `SET/**/TINGS`) and multi-statements (`SELECT 1; SELECT 2`)
are syntax errors on 26.8, and a `SETTINGS` clause that violates a profile constraint is refused with 452 during
the `EXPLAIN` itself. The hostile corpus of 47 texts (the ones above plus `EXPLAIN`, `EXPLAIN AST`, `INSERT`,
`CREATE`, `DROP`, `ALTER`, `SYSTEM`, `KILL`, `SHOW`, `DESCRIBE`, `USE`, `INTO OUTFILE`, `FORMAT`, 1 MB of text,
nine nested selects) is refused; 17 benign shapes (CTEs, scalar CTEs, joins to `field_stats`, `IN` subqueries,
unions, window functions, `ARRAY JOIN`, the `message` alias, `SETTINGS` inside a comment, a string or an alias,
eight nested selects, a 16 KB comment, a trailing semicolon) pass and stay case-filtered; and all 46 recorded SQL
vectors of the transpiler pass the same walk.

Two limits of the tree, recorded rather than worked around:

- The dump does not print a CTE's name (`WithElement (children 1)`), so the gate cannot tell `FROM c` (a CTE) from
  `FROM typo`. An unqualified name that is in neither list is let through; the server answers `UNKNOWN_TABLE`,
  and the two grants cover any table that is not in the denied list. `GrantTests` asserts the denied list against
  `system.tables`, so a migration that adds a relation must add it to the gate too.
- A bare Cyrillic homoglyph of `events_v` is a syntax error (unrecognised token); the backticked form is an
  unknown unqualified name and ends as `UNKNOWN_TABLE` (60). Zero rows either way.

`EXPLAIN AST` applies a `SETTINGS` clause of the explained text to the `EXPLAIN` query itself, so the gate's own
probe runs under the same constraints as the query would; it reads no table, so this is harmless.

The gate runs on **both** paths: `ClickHouseQueryClient.RunAsync`/`ExplainAsync` (transpiler output) and
`RunSqlAsync`/`ExplainSqlAsync` (the raw tab), one extra round trip per query (a few milliseconds against the
201 ms p95 measured for the first query slice). The only ungated SQL in the client is its own constant `CaseSpanAsync`.

### 4. With the gate bypassed, an inline `SETTINGS` clause overrides the request parameter (`SettingsOverrideTests`)

The driver straight, as `urlog_query`, request parameter `SQL_allowed_cases=[1]`:

| Query text | Result |
|---|---|
| `SELECT count() FROM events_v` | 200 (case 1) |
| `SELECT count() FROM events_v SETTINGS SQL_allowed_cases = '[1,2,3]'` | **600** (every case) |
| `SELECT getSetting('SQL_allowed_cases') SETTINGS SQL_allowed_cases = '[1,2,3]'` | `[1,2,3]` |
| parameter `[1,2,3]`, clause `'[2]'` | 200 (case 2): the clause is simply the last word |

With `readonly = 2` the server does not stop this: the clause is a legitimate settings change. **This is why the
gate is mandatory and not defence in depth.** The transpiler never emits `SETTINGS` and the analyst's SPL text
reaches SQL only as `param_*` values (a value containing `SETTINGS SQL_allowed_cases='[1,2,3]'` returns 0 rows and
widens nothing), but a transpiler bug that appended one would be caught by the same gate: the test edits a
`TranspileResult` to carry the clause and `RunAsync` refuses it.

### 5. The profile constraints hold (`GrantTests`)

`max_execution_time`, `max_memory_usage` and `max_result_rows` as request parameters or inline `SETTINGS`: 452
`SETTING_CONSTRAINT_VIOLATION`, raising and lowering alike (the constraint is `readonly`). `readonly = 0`: 164;
`allow_ddl = 1`: 392 `QUERY_IS_PROHIBITED`. The profile values are what `deploy/clickhouse/users.d/urlog.xml`
says (60 s, 4 GB, 1 M rows, `readonly = 2`, `allow_ddl = false`).

With the two explicit grants (`SELECT` on `events_v` and `field_stats`; a users.xml user with a `<grants>` block
has nothing else), the server refuses: `events`, `file_generation`, `schema_migrations`, `dictGet` on the
dictionary, `system.query_log`/`processes`/`users`/`grants`, `url()`, `file()`, `s3()`, `remote()`, `mysql()`,
`executable()` (all 497 `ACCESS_DENIED`, the table functions for want of `READ ON URL`/`FILE`/...), `CREATE
TEMPORARY TABLE`, every write and DDL (497 before 164 now that grants are checked first), `INTO OUTFILE` over
HTTP (358). `system.tables` and `system.databases` list only the two relations and `default`; `system.one`,
`system.settings` and `numbers()` stay readable (the gate refuses `numbers()` and `system.*` anyway). `SET` is
accepted by the server (settings may change under `readonly = 2`; outside a session it changes nothing for the
next request) and a `SET` of a constrained setting is 452: keeping `SET` out of the raw tab is the gate's job.

### The fallback, measured and not taken

A row policy on `events` keyed on the same `getSetting` works (10/20/0 rows for `[1]`/`[2]`/absent in the probe,
partition pruning intact) and loses to an inline `SETTINGS` clause exactly as the view does (30 rows). Marking
`SQL_allowed_cases` `READONLY` in a settings profile refuses the clause **and the request parameter** alike
(452, `SettingsOverrideTests`), so the readonly constraint cannot coexist with per-request delivery; it would need
one ClickHouse user or profile per analyst with the case list baked in and re-synced from SQLite on every share,
a second source of truth for the ACL. `CHANGEABLE_IN_READONLY` lets the parameter through but then the clause
too. The row policy therefore buys nothing over the view and the gate, and the fallback is recorded as not taken.

## Decision

1. **`events_v` on `getSettingOrDefault('SQL_allowed_cases', '')`, as `SQL SECURITY DEFINER`** (migration 0005),
   with the backend sending the setting as a per-request HTTP parameter (ADR 0003). No row policy.
2. **`urlog_query` holds exactly two grants**, `SELECT` on `events_v` and `field_stats`, plus the constrained
   profile; nothing on `events`, no `dictGet`, no table-function sources, no `system.*` beyond what every user
   reads. Declared in `deploy/clickhouse/users.d/urlog.xml`; `GrantTests` pins the list.
3. **The raw-SQL gate is mandatory on both paths** and lives in `src/Query/RawSqlGate.cs`, applied by
   `ClickHouseQueryClient` before any text reaches the server. Its rules are the ones listed under 3 above; the
   recorded tree shapes in `tests/Query.Tests/RawSqlGateTests.cs` pin the dump format of 26.8, and
   `LiveTreesMatchTheRecordedShapes` in tests/Security says when a server upgrade moves it.
4. **Every scenario runs as an unauthorised user** and must return zero events, through the SPL path and the raw
   tab (`UnauthorisedUserTests`); `scenarios/` plugs into that theory when it exists.

## Consequences

- `tests/Security` is a Testcontainers project like `tests/Integration.Tests`: `tools/pre-merge.ps1` runs it, the
  pre-commit hook skips it. None of its tests is marked slow or skipped.
- A migration that creates a relation adds it to `RawSqlGate.DeniedRelations` (or `AllowedRelations` with a
  grant); `GrantTests.EveryRelationTheMigrationsCreateIsKnownToTheGate` fails otherwise.
- The migrator's admin account is the definer of `events_v`; dropping or renaming it breaks every query. The
  deployment notes must say so.
- The design record's Security section states the measured mechanism, the gate rules and the grants list.
- Open questions: the gate's rejection reasons are the messages the raw tab shows; the audit row for a query
  (text, SHA-256, user, case, duration) is written whether the gate accepted it or not, so a rejected query is
  visible in the audit chain.

## Amendment, 2026-11-12: the generation filter is a per-query set, the dictionary is gone

The isolation mechanism above is unchanged: the definer view, `SQL_allowed_cases`, the two grants, the gate. What
changed is the view's second filter, which says which parse of a file is the visible one.

**Before.** `parse_generation = dictGetOrDefault('file_generation_dict', 'current', file_id, 1)`: a dictionary over
`file_generation`, loaded through a passwordless account `urlog_dict` and reloaded by the write path after every
commit (`SYSTEM RELOAD DICTIONARY ON *.*`, the only form the server grants).

**What was measured.** A file without a row resolved to generation 1, so a parse in progress was readable. And the
dictionary answers per block: in a test that kills an ingesting worker and restarts it, a `count()` in flight when
the restart committed read 1,408 of 4,809,713 rows, once in nine restarts. A re-parse commit could mix two
generations in one result the same way.

**Now** (migration 0006): `(file_id, parse_generation) IN (SELECT file_id, argMax(current, updated_at) AS visible
FROM file_generation GROUP BY file_id HAVING visible > 0)`. The set is built once per query from a snapshot of the
table. No row, nothing visible; a query sees one generation per file for all of its rows. The view reads
`file_generation` with its definer's rights, so `urlog_query` still holds `SELECT` on `events_v` and `field_stats`
only, and `SELECT count() FROM file_generation` as that user is still 497.

**Removed with it:** the dictionary, the `urlog_dict` account and its profile, and `SYSTEM RELOAD DICTIONARY` from
`urlog_ingest`. One account fewer without a password, one global grant fewer on the write path. `GrantTests` pins
both grant lists, the two remaining `urlog_*` accounts and the absence of any dictionary; `UncommittedFileTests`
pins that uncommitted rows are invisible through SPL and raw SQL.

**Cost.** The reference query `src_ip=X | stats count by url.path` on 25.2 M rows, 20 runs warm, three alternating
passes on the dev box: p95 206 / 217 / 212 ms against 200 / 205 / 206 ms for the dictionary form (median +3.4 %),
p50 191 to 193 ms for both. `EXPLAIN indexes = 1` on a case and time query: partition pruning to the case's month
and primary-key use on `case_id` and `ts`, as in decision 2 above. The set costs one small aggregation over
`file_generation` (one row per file) per query.
