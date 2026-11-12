# ADR 0002: ClickHouse .NET driver and wire protocol

Status: accepted (2026-11-04). The measured insert numbers are in ADR 0004.

## Context

The design record pinned "ClickHouse ≥ 26.3 LTS via native protocol, `ClickHouse.Client`". That statement was
wrong: `ClickHouse.Client` (DarkWanderer) speaks only the HTTP interface and its repository was archived on
22 June 2025. There was no .NET package that was both "ClickHouse.Client" and "native protocol".

The requirements that depend on this choice:

- insert path ≥ 1 M rows/s with columnar blocks and no per-row allocation (design record, "Throughput SLO and benchmark harness");
- the per-case isolation mechanism sends `SQL_allowed_cases` as a **per-query custom setting**, never by
  rewriting the analyst's SQL (design record, "Security").

## Options

| | Package | Protocol | Status (Nov 2026) |
|---|---|---|---|
| A | `ClickHouse.Driver` 1.5.0, `ClickHouseTcpClient` | native TCP 9000, columnar `IColumn[]` blocks, LZ4/zstd | official ClickHouse client; TCP part **experimental**, every use emits `CHTCP0001`; .NET 8+; supports the last 2 LTS |
| B | `ClickHouse.Driver` 1.5.0, `ClickHouseClient` / ADO.NET | HTTP 8123, `InsertBinaryAsync` with RowBinary rows as `object[]` | stable; row-oriented API allocates per row |
| C | `Octonica.ClickHouseClient` 4.1.6 | native TCP, `ClickHouseColumnWriter` | third party, long-lived, no vendor backing |

## Decision

**A for the insert path, with B kept in the same package as the fallback.** The columnar block API is what
the insert budget assumes; the experimental warning is suppressed once, in the projects that use the TCP
client, with a comment pointing here (`<NoWarn>CHTCP0001</NoWarn>`).

Requirement added for **the insert-path work**: the `Insert` bench runs **both** paths with the same pre-built blocks:

- A: `ClickHouseTcpClient.InsertAsync` with `IColumn[]`, LZ4 on the wire;
- B: `ClickHouseClient.InsertBinaryAsync` (RowBinary + LZ4 over HTTP).

The 1 M rows/s decision rests on those two numbers, not on the protocol's name. If B is within 10 % of A,
B wins on stability.

## What the driver spike measured (ClickHouse 26.8.36.3 in Docker, Windows 11, Docker Desktop 4.72)

`tests/Integration.Tests/ClickHouseNativeProtocolTests.cs` is the executable record.

1. `ClickHouseTcpClient` connects on port 9000 with `Compression=lz4`, `PingAsync` and `GetServerInfoAsync`
   succeed, `SELECT version()` returns 26.8.36.3.
2. A 10 000-row columnar insert (`UInt64`, `LowCardinality(String)`, `String`) round-trips; the
   `OnBlockWritten` callback reports every block smaller on the wire than in memory, so LZ4 is on.
3. `urlog_query` (profile `readonly=2`, `allow_ddl=0`, read-only constraints on its limits) is denied DDL and
   DML with code 164 and cannot raise `max_result_rows` (code 452). Known settings sent per query go through.
4. **Limitation found:** a custom-prefixed setting (`SQL_allowed_cases`, server config
   `custom_settings_prefixes = SQL_`) sent through `ClickHouseTcpQueryOptions.Settings` or
   `ClickHouseTcpClientOptions.CustomSettings` never reaches the server (`UNKNOWN_SETTING`, 115, at query
   analysis). The same setting works as an HTTP URL parameter, from `clickhouse-client`, as an inline
   `SETTINGS` clause, and as a `SET` statement inside a pinned TCP session (`OpenSessionAsync`). This is a
   driver behaviour, not a server one. The test asserts the failure as a tripwire so a driver upgrade that
   fixes it is noticed.

## Consequences

- The design record names `ClickHouse.Driver`.
- Decided in ADR 0003: the query path uses the HTTP client (B) with `SQL_allowed_cases` as a per-request
  parameter; a session-scoped `SET` is rejected (pooled connections would leak it); the SQL text is never
  rewritten. The ingest path keeps the TCP client and never sends custom settings.
- Pin stays `26.8.36.3` (26.8 LTS). The TCP client supports the last two LTS lines, so 26.3 also works.
- The experimental API may change between driver versions; `Directory.Packages.props` pins 1.5.0 and a bump
  is a deliberate change that re-runs `tools/pre-merge.ps1`.

## Appendix (2026-11-04): what the TCP insert API accepts per column, and what it costs

Measured against ClickHouse 26.8.36.3 with `ClickHouseTcpClient.InsertAsync(sql, IReadOnlyList<IColumn>)`, 100k rows,
`GC.GetTotalAllocatedBytes` around the second insert, hand-written `IColumn` implementations next to the driver's own
`ClickHouseTcpColumn.Create(name, T[])`. The error message of a rejected column lists the accepted element types, which
is where the middle column comes from. The table holds for driver 1.5.0 and is measured again on a driver bump.

| Target column type | Accepted element types (`T` of `IColumn<T>`) | Zero-alloc from pipeline buffers? |
|---|---|---|
| `String` | `string`, `byte[]` | **No.** One `byte[]` (or `string`) object per row is required; `ReadOnlyMemory<byte>` and `ArraySegment<byte>` are rejected. A custom `IStringColumn` (public interface with contiguous `Bytes` + `Offsets`) is accepted but is not fast-pathed: the codec falls back to `IColumn<string>` and allocates one string per row (64 B/row measured). Only the driver's internal `StringColumn` gets the span path. |
| `LowCardinality(String)` | `string` | Yes if the strings are cached by content (few distinct values); a custom `IStringColumn` costs 104 B/row (string + intern). |
| `IPv6` | `IPAddress` | Only through a cache of `IPAddress` instances keyed by the 16 bytes; `UInt128` and `byte[16]` are rejected. |
| `DateTime64(3)` | `long` (milliseconds), `DateTimeOffset`, `DateTime` | Yes: `long[]` straight from the batch. |
| `UInt64`, `UInt32`, `UInt16` | the matching value type; a custom `IColumn<T>` is read through `Values` | Yes. |
| `Map(String, String)` | `KeyValuePair<string, string>[]` | Only when empty (one shared empty array); otherwise an array of pairs of strings per row. |

With `byte[]` rows prepared in advance, the insert of 100k rows allocates ~30 KB (0.3 B/row): the driver streams
compressed frames and does not copy the payload. The floor of the public API is therefore the `byte[]` per row for
`message` and `raw` (one object shared when they are equal): about 248 B/row on the nginx fixture, measured as
275-280 B/row end to end in `EndToEnd.StubPipelineNginx1G`. Removing it means serialising `EventBatch`
(already in Native block shape) to the wire directly, which is the insert path's job; the `IColumn` route cannot do it.
Probe source: a throwaway console project with `IStringColumn` / `IColumn<T>` / `IMapColumn<string,string>` fakes
inserting into a six-column table (`id UInt64, ts DateTime64(3), host LowCardinality(String), ip IPv6, msg String, extras Map(String,String)`).
