# ADR 0003: Transport per path: HTTP for queries, native TCP for inserts

Status: accepted (2026-11-04, after the driver spike). Supersedes the open question in ADR 0002, "Consequences".

## Context

ADR 0002 pinned `ClickHouse.Driver` 1.5.0 and measured that its native TCP client (`ClickHouseTcpClient`)
drops custom-prefixed settings sent out-of-band (`ClickHouseTcpQueryOptions.Settings`,
`ClickHouseTcpClientOptions.CustomSettings`), while the same setting works as an HTTP request parameter, as
an inline `SETTINGS` clause, and as a `SET` statement inside a pinned TCP session.

The per-case isolation mechanism (design record, "Security") depends on the backend delivering `SQL_allowed_cases`
with every query without touching the analyst's SQL text. The insert path never sends custom settings and
needs columnar blocks for the 1 M rows/s budget.

## Options for delivering `SQL_allowed_cases`

| | Mechanism | Verdict |
|---|---|---|
| 1 | HTTP client, setting as a per-request URL parameter | works; stateless per request; nothing touches the SQL text |
| 2 | Pinned TCP session, `SET SQL_allowed_cases = ...` before each query | works, but the setting is session state on a pooled connection; a missed reset or a reused session would leak one user's case list to the next. Rejected as an isolation mechanism. |
| 3 | Append `SETTINGS SQL_allowed_cases = ...` to the analyst's SQL | rewrites user SQL; rejected by the design record |
| 4 | Wait for a driver fix | unknown timeline; the tripwire test in `tests/Integration.Tests` will tell us when it lands |

## Decision

- **Query path** (SPL transpiler output and the raw-SQL tab): `ClickHouse.Driver`'s **HTTP client**
  (`ClickHouseClient` / ADO.NET), user `urlog_query`, `SQL_allowed_cases` as a per-request setting
  parameter. Every request is self-contained; there is no session state to leak.
- **Insert path**: `ClickHouseTcpClient` columnar blocks with LZ4, as in ADR 0002. The insert bench still
  measures the HTTP RowBinary path alongside it.
- The TCP tripwire test stays: if out-of-band custom settings start working over TCP, this ADR is
  revisited on measured numbers, not before.

## Consequences for case isolation

Delivering the setting per request leaves two things for the isolation tests to show, next to `events_v`
filtering with `readonly=2`:

- **(a)** a query carrying a user-written `SETTINGS` clause is **rejected by the raw-SQL gate** (`EXPLAIN AST`
  allowlist), through both the transpiled path and the raw-SQL tab;
- **(b)** with the gate bypassed in a test harness, a query-level `SETTINGS SQL_allowed_cases = ...` clause
  **does override** the URL parameter on the pinned LTS. This is why the gate is mandatory and not a
  defence in depth: with `readonly=2` the server itself does not stop it.

Both results are in `docs/adr/0001-case-isolation.md` and the Security section of the design record.

## Consequences elsewhere

- `src/Api` and `src/Query` take a dependency on the HTTP client only; `src/Core/Ingest` on the TCP client
  only. No component holds both.
- The HTTP client's `max_result_rows`, `max_execution_time` and `max_memory_usage` are sent per request as
  well, but the profile constraints on `urlog_query` (readonly) are what make them unraisable.
- Connection pooling for the HTTP client is per process and stateless with respect to settings; no
  per-user pool is needed.
