# ADR 0004: The insert data plane is our own native-protocol client

Status: accepted (2026-11-05). Complements ADR 0002 (driver) and ADR 0003 (transport per path).

## Context

The insert budget is ≥ 1 M rows/s with no per-row allocation (design record, "Throughput SLO and benchmark harness"). `EventBatch` is laid out as
a ClickHouse Native block: struct of arrays, strings as one byte buffer plus offsets. The pinned driver's column
API cannot consume that layout (ADR 0002 appendix: one `byte[]` or `string` per String row, `IPAddress` per IP row,
`KeyValuePair<string,string>[]` per Map row), which the pipeline bench measured as a 248 B/row floor, and the insert matrix measured the
conversion at 630 B/row and 340-1,470 ms per block before any byte reaches the socket, plus 65-420 B/row inside the
driver's wire path (its Map codec allocates per pair). The driver exposes no raw connection: `ClickHouseTcpConnection`,
`ClickHouseBinaryWriter` and its compressed-frame code are internal in 1.5.0 (checked by reflection).

The numbers (32-cell matrix plus supplements, measured with `bench/Experiments/InsertMatrix`): a Native writer fed straight from
`EventBatch` reaches 0.99-1.21 M rows/s with the text index on at 4-8 connections at 0.1 B/row and about one client
core; the driver's API reaches 0.9-1.1 M at 1.5× the client CPU and the allocations above. The dev box's ceiling at
~1.2 M rows/s is its Docker Desktop loopback (uncompressed blocks move at 0.23-0.32 M rows/s, ~140-200 MB/s on the
wire), not either client and not ClickHouse.

## Decision

- **The insert data plane is a protocol client of our own** (`src/Core/Ingest/Native/`): `NativeConnection`
  (Hello, Ping, INSERT with blocks streamed as Data packets), `ProtocolWriter` (LZ4 frames with CityHash128
  checksums), `PacketReader`/`FrameSource`, `NativeBlockSerializer` (`EventBatch` → Data packet). About 900 lines.
  It uses one public piece of the driver, `Lz4Compressor.Encode`/`Decode` (block LZ4, method byte `0x82`); no new
  package is pinned.
- **The driver's TCP client stays for the control plane**: DDL, migrations (`ClickHouseMigrator`), lightweight
  deletes and generation commits (`EventStoreAdmin`), `system.*` reads, the benches' setup. ADR 0003 is unchanged:
  native TCP for inserts, HTTP for queries; `src/Core/Ingest` depends on the TCP client only.
- **Protocol revision pinned at 54449** (`NativeConnection.ClientRevision`): new enough for LowCardinality on the
  wire (54405) and string-encoded settings (54429); old enough that the server expects no handshake addendum (54458),
  no per-column custom-serialisation flag (54454), no chunked packets (54470), and sends no ProfileEvents during
  inserts (54456). The server negotiates down to the client's revision, so the packet layout is fixed by the client,
  not by the server version. The client refuses a server below 26.3 or one that cannot offer 54449.
- **Frames**: 1 MiB of payload per compressed frame (`max_compress_block_size`, what clickhouse-client sends),
  LZ4 fast, CityHash128 v1.0.2 over `method | compressed size | decompressed size | data`, checksum written as
  low 64 then high 64 bits. Frames received from the server are checksum-verified too.
- **Column serialisation**: `UInt64/32/16` and `DateTime64(3)` as the batch arrays; `IPv6` as 16-byte rows in
  network order (IPv4 mapped, as `EventBatch` stores it); `String` as varint length + bytes cut from the column
  buffer by its offsets; `LowCardinality(String)` as a per-block dictionary (`SharedDictionariesWithAdditionalKeys`,
  flags `HasAdditionalKeys | NeedUpdateDictionary`, key width UInt8/16/32 by dictionary size); `Map(String, String)`
  as cumulative pair offsets then the key and value string columns. An optional row permutation (`BlockOrder`)
  writes an unsorted block in `(case_id, host, ts, file_offset)` order as a gather. It is off by default
  (`SortBlocks`): measured at -1 % to +4.5 % end to end for twice the client CPU, below the 10 % win it needed.
- **Settings**: none are sent on this path. The isolation setting `SQL_allowed_cases` belongs to the query path
  (ADR 0003) and is never written here.

## Tests (the component's own, beyond the byte-identical fingerprint of the matrix)

`tests/Integration.Tests/NativeProtocolTests.cs`, against the pinned image through Testcontainers:

- Hello negotiates ≥ 54449 against the pinned LTS; Ping round-trips.
- Every column of the pinned schema round-trips and is read back equal through the driver: empty String, empty
  Map and a Map with an empty value, IPv4-mapped and full IPv6 addresses, `DateTime64` at the epoch, at the 2038
  boundary and in 2100, `UInt64`/`UInt32`/`UInt16` at their maxima, a 256-byte value with every byte, a 100 KB
  string, non-ASCII UTF-8, and the ASCII-lowercasing of LowCardinality columns.
- LowCardinality dictionaries of 200, 1,000 and 70,000 distinct values in one block (UInt8, UInt16 and UInt32 key
  widths) come back with every value in place.
- Server-side failures surface as `ClickHouseInsertException` with the server's code and name, never as a hang:
  missing table (60 `UNKNOWN_TABLE`), read-only user (164 `READONLY`), wrong column type, and a `CHECK` constraint
  violated by the second block of an INSERT (469 `VIOLATED_CONSTRAINT`, the mid-stream case).
- A 6 MB block whose frame boundaries fall inside string values arrives intact (count, per-row hashes).

`tests/Core.Tests/NativeFramingTests.cs` covers the framing layer alone (values straddling frames read back by our
own frame reader, checksum rejection, varint encoding, the serialised block header and column order, the sort
permutation). `NativeInserterTests` and `SchemaTests` cover the sink, the migrator and the view.

## Rules

- **A ClickHouse LTS bump re-runs `NativeProtocolTests` and `SchemaTests` against the new image before the pin in
  `compose.dev.yml`, `ClickHouseFixture.Image` and `CoreInfo.ClickHouseLts` moves.** Server versions tested so far:
  26.8.36.3.
- The client revision is bumped only with a reason written here (a feature the insert path needs), and every
  packet layout difference between the old and the new revision is listed in the same change.
- No per-row allocation in the serialiser (design record, "Parser plugin contract", insert path); the Insert bench's B/row is the tripwire.

## Consequences

- `src/Core/Ingest/Native` owns a wire protocol. Its surface is small (inserts only) and the server validates every
  frame, so a mistake fails loudly; the cost is that nobody upstream maintains it for us.
- The driver's experimental warning stays suppressed in `Core`, `bench` and the tests for the control plane.
- If a future driver release exposes a zero-copy block API, the matrix harness (`bench/Experiments/InsertMatrix`)
  is the yardstick for retiring this client; until then this ADR stands.
