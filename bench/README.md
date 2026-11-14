# bench: throughput harness and regression gate

BenchmarkDotNet harness for the SLO in `docs/design-record.md`, "Throughput SLO and benchmark harness". Always run in Release.

## Fixtures (`bench/data/`, gitignored)

Three fixed-seed, 1 GiB files, generated on first use in about 5 s each by `bench/Fixtures/NginxGen.cs`,
`SyslogGen.cs` and `GenericGen.cs`. The RNG is an in-repo xoshiro256**, so the bytes do not depend on the .NET version.

| File | Shape | Lines | Mean B/line | Oddities (5 %) |
|---|---|---|---|---|
| `nginx-1g.log` | combined log | ~4.8 M | ~223 | IPv6 client, 1-4 KB user agent, quotes in the request, missing fields |
| `syslog-1g.log` | 88 % RFC 3164, 12 % RFC 5424 | ~7.8 M | ~137 | no PRI, non-ASCII, 1-2 KB message, escaped quotes in structured data, IPv6, CRLF |
| `generic-1g.log` | mixed application-log lines: ISO 8601 with and without a zone, bracketed BSD, CLF inside a line, epoch at the start or behind a key | ~11.8 M | ~91 | no oddity class; 31 % of lines carry no timestamp (stack-trace frames, continuation lines) and 0.8 % are blank. The sidecar's `Counts` holds the lines per kind |

Each file has a `<name>.meta.json` sidecar (seed, generator version, bytes, lines, oddity lines, SHA-256).
A fixture is regenerated when the generator version or seed changes. `tests/Bench.Tests` pins the SHA-256
of an 8 MiB sample per generator: if that test fails, every baseline number is void and must be re-measured.

## The benches

| Class | What one op does | Needs ClickHouse | In the gate |
|---|---|---|---|
| `DiskRead` | sequential 1 MB reads of the 1 GiB nginx fixture (GlobalSetup reads it once to warm the page cache) | no | no: hardware attribution only, see below |
| `TokeniseNginx`, `TokeniseSyslog` | the whole 1 GiB fixture from memory, 64 MB newline-aligned chunks, single thread, through `IBatchWriter` into a `NullBatchWriter` | no | yes (both run the real sources) |
| `TokeniseGeneric` | the same on the generic fixture through `GenericSource`, a fresh parse context per op | no | no: reported, `ungated` (the generic source is outside the SLO) |
| `DetectFormat` | the detector on one sample (first 64 KB, last 4 KB) with every registered source; `Case` = `nginx`, `syslog`, `unclaimed-text` (every source reads its lines and refuses them), `utf16-syslog`, `signature` | no | no: reported, `ungated`. Its syslog case measured 145.9 and 118.6 µs in two runs of the same code, so a 10 % gate on it would measure the mode. The 50 ms budget per file is a test (`DetectorCorpusTests`) |
| `Insert` | one pre-built block of `BlockSize` rows (100k / 250k / 500k) into the pinned `events` schema through the driver: `Tcp` = columnar blocks over native TCP + LZ4, `Http` = RowBinary over HTTP + LZ4, same block (ADR 0002's two transports, kept for the record) | yes | yes |
| `InsertNative` | **the real insert path**: 2 M rows of real `EventBatch`es of `BatchRows` rows (100k / 250k / 500k, parsed once from the nginx fixture by `NginxSource`) through `NativeInserter` with `Writers` = 4 or 6 into the pinned schema with the text index, LZ4, 500k rows per INSERT; one op ends when every INSERT is acknowledged | yes | yes for 4 writers (the SLO configuration, compared on `Min`); 6 writers is ungated and reported for the curve |
| `EndToEnd.PipelineNginx1G` | the chunk pipeline on the 1 GiB nginx fixture (pooled 64 MB chunks from disk, line framing, 8 parsers, 100k-row `EventBatch`es) into a counting sink: the pipeline's own cost; **≤ 50 B/row is the pipeline's bound** | yes (setup only) | no, run by hand |
| `EndToEnd.StubPipelineNginx1G` | the same pipeline through the real `NativeInserter` (4 writers) into the pinned schema with the text index; the name of the first stub pipeline, kept so the numbers compare over time (driver column API: 470k rows/s, then 627k through the pooled pipeline; now the Native writer) | yes | no, run by hand |

`TokeniseNginx` and `EndToEnd` run the real `NginxSource` (1,273 MB/s, 5.7 M rows/s, 0 B/row on this box);
`TokeniseSyslog` runs the real `SyslogSource` (1,390 MB/s, 10.1 M rows/s, 0 B/row).
`TokeniseGeneric` runs the real `GenericSource`: 754 MB/s, 8.2 M rows/s, 0 B/row (256 B per file) on this box, with
the timestamp regex on every line. `DetectFormat` measured 0.11 to 0.15 ms per file for the text cases and 0.16 µs
for a file decided by its signature, against a budget of 50 ms. Before them both ran
`bench/Stub/StubSource.cs`, an `ILogSource` that frames lines, walks the delimiters with `SearchValues` (zero
allocation) and writes each line as `raw`/`message` with constant typed columns; it stays as the harness's own
test subject (`tests/Bench.Tests`). Bench class and method names did not change when the real code arrived, so
baseline keys did not move.
The bench-only `DriverColumnSink` is gone, replaced by the production `NativeInserter`
(`src/Core/Ingest`), which serialises `EventBatch` straight to Native blocks and removed the 250 B/row floor the
driver's String codec imposed. `bench/Fixtures/EventBatchSet.cs` builds the real batches both `InsertNative` and the
insert matrix harness (`bench/Experiments/InsertMatrix`, a standalone console outside the solution) insert.

`EndToEnd` sizes the pipeline for the SLO (`EndToEnd.Options`): `min(cores, 8)` parsers, `parsers + 2` in-flight chunks (the pool holds one more for the reader's hand-off),
4 writers with one batch in flight each. At those settings the pools hold about 1.4 GB on nginx-shaped data
(`PipelineOptions.EstimatePoolBytes`); the memory formula and the 8 GB-box settings are in `PipelineOptions`.

ClickHouse defaults to the compose stack (`127.0.0.1`, 9000/8123, `default`/`clickhouse`); override with
`URLOG_CH_HOST`, `URLOG_CH_TCP_PORT`, `URLOG_CH_HTTP_PORT`, `URLOG_CH_ADMIN_USER`, `URLOG_CH_ADMIN_PASSWORD` (the admin account: the benches create scratch tables; `URLOG_CH_USER` is the ingest account and is not read by the benches).
The HTTP client turns `async_insert` on by default; the harness turns it off (`set_async_insert=0`), as the
insert budget requires.

## Running

```
dotnet run -c Release --project bench -- --list flat                              # what exists (also generates fixtures)
dotnet run -c Release --project bench -- --filter '*TokeniseNginx*'               # one bench, full job
dotnet run -c Release --project bench -- --filter '*Tokenise*' --job short        # quick look (what the hook does)
dotnet run -c Release --project bench -- --filter '*Insert*'                      # needs compose.dev.yml up
dotnet run -c Release --project bench -- --filter '*EndToEnd*'
dotnet run -c Release --project bench -- --filter '*Insert*' '*EndToEnd*' -Slo   # pinned to 8 threads, see below
```

After recreating the dev database (a new ClickHouse volume), run one throwaway 1 GiB ingest before measuring:

```
dotnet run -c Release --project src/Cli -- migrate
dotnet run -c Release --project src/Cli -- ingest --case 999999 --host warmup bench/data/nginx-1g.log
```

A fresh volume measured `InsertNative` 11 % slow; once one ingest had settled it was within 1 %. The benches
write to their own tables, so the scratch case's rows can stay or be dropped.

Results land in `bench/BenchmarkDotNet.Artifacts/results/` (gitignored) as markdown, CSV, HTML and a
`*-report-full.json` per benchmark class. The JSON is what the gate reads.

### `-Slo`: comparable to the reference box

The SLO is defined on 8 cores. A `-Slo` argument pins the benchmark process to 8 threads
(`--affinity 0xFF`) so `EndToEnd` and `Insert` numbers are comparable to the reference machine; the
single-threaded `Tokenise*` benches are unaffected. `-Slo` writes to `bench/BenchmarkDotNet.Artifacts-slo/`
so the two runs never mix. The gate reads the unpinned folder; point it at the other one with
`-ResultsDir bench/BenchmarkDotNet.Artifacts-slo/results` when you want to compare pinned numbers.
When ClickHouse runs in Docker on the same machine it shares the CPU with the client during `Insert` and
`EndToEnd`; the affinity mask only constrains the client. Record both numbers in the description of the change.

## Reading results

| Column | Meaning |
|---|---|
| `Mean` | time per op; what the gate compares |
| `Allocated` | managed bytes per op (`MemoryDiagnoser`) |
| `MB/s` | input bytes per second, derived from `Mean` and the workload (fixture bytes, or the block's raw bytes for `Insert`) |
| `rows/s` | rows (lines) per second, same derivation |
| `B/row` | `Allocated` divided by rows; tokenisers must show 0 |

The three derived columns come from `bench/Reporting/ThroughputColumns.cs`: every bench class exposes
`static Workload WorkloadFor(ParameterInstances)` so the host process can compute them without the child's
state. Paste `MB/s`, `rows/s` and `B/row` in the PR.

## The gate: `check-regression.ps1`

```
powershell -File bench/check-regression.ps1               # full comparison, every baseline entry must be measured
powershell -File bench/check-regression.ps1 -Partial      # subset run: only measured benchmarks are compared
powershell -File bench/check-regression.ps1 -SelfTest     # the script's own 18 cases on synthetic data
```

Rules it enforces (exit 1 on any):

1. The compared statistic more than **10 %** above `bench/baseline.json`. The ceiling is a constant in the
   script; it cannot be raised from the command line or the baseline file. The statistic is `Mean`, except
   for benches listed under `useMin` in the baseline (`Insert*`), which compare `Min`, the best measured
   iteration. Reason: `Insert` has an external component, ClickHouse, and on a shared box its merges, index
   builds and other load only ever **add** time, so `Mean` swings 20 % between runs while the minimum is the
   stable estimator of the client's own cost. `Tokenise*` has no external component and stays on `Mean`.
2. Per-op allocation where the baseline records 0 B, even when the run is faster, and even for provisional entries.
3. A measured benchmark with no baseline entry. New benches are added deliberately, never silently.
4. In full mode, a baseline entry that was not measured (a bench quietly deleted or renamed).

An empty harness with an empty baseline is green.

### Provisional entries

A baseline entry with `"provisional": true` is reported but **not** a speed gate. A `Tokenise*` entry is
provisional while its bench runs the stub (as `TokeniseNginx` and `TokeniseSyslog` did before their sources existed): a stub that
only finds delimiters is not a number a real tokeniser must beat, so the real number must not "regress" against it.
The allocation rule still applies to provisional entries (0 B/row is a non-negotiable, not a speed comparison).
No entry is provisional any more; the mechanism stays for the next source.

```
powershell -File bench/check-regression.ps1 -UpdateBaseline -Partial -Provisional   # write stub numbers, flagged
powershell -File bench/check-regression.ps1 -UpdateBaseline -Partial                # the real source landed: promote to a real gate
```

`Insert` entries at 100k and 250k rows are real gates, compared on `Min`. The 500k block is in
the `ungated` list until it is re-measured on a quiet machine: even its minimum moved more than 10 % between
runs here. `InsertNative` entries with 4 writers are gates (compared on `Min`, like every bench with
ClickHouse in the loop); the 6-writer cells are `ungated` and reported for the curve. `DiskRead` is in the `ungated` list too: on this box it measured 214, 356 and 505 ms on three consecutive runs depending on how much of the fixture the OS kept in the page cache (ClickHouse in Docker competes for RAM), so a 10 % gate on it only measures the cache, not code.

## Where it runs

| Stage | What runs | Job |
|---|---|---|
| `git commit` (pre-commit hook, `.githooks/pre-commit`) | `dotnet build -warnaserror`, unit tests; if the commit touches `src/Sources/<Name>/`, the `*Tokenise<Name>*` benches only, then the gate in `-Partial` mode | `--job short` |
| `tools/pre-merge.ps1` (run before merging a branch) | build, unit tests, integration tests (Testcontainers), gate self-test, **all** `DiskRead`, `Tokenise*`, `DetectFormat` and `Insert*` benches, gate in full mode | default (full) |

Install the hook once per clone: `powershell -File tools/install-hooks.ps1`. Integration tests, `Insert*`
and `EndToEnd` need Docker and are never part of pre-commit.

`tools/pre-merge.ps1` starts with a **quiet precheck**: it warns when free RAM is below 8 GB or when the
ClickHouse container is above 20 % CPU (the median of eight samples four seconds apart, all printed: an idle
server bursts every few seconds while it flushes its system log tables, so one sample says little), and labels the final line `run conditions: NOISY` so a noisy run is
never mistaken for a clean one. `-Quiet` turns those warnings into a stop; use it before taking a baseline.

## Updating the baseline

The baseline changes only on purpose, with numbers from the full job on the reference box or a documented
machine (`machine` is recorded in the file).

```
dotnet run -c Release --project bench -- --filter '*DiskRead*' '*Tokenise*' '*DetectFormat*' '*Insert*'
powershell -File bench/check-regression.ps1 -UpdateBaseline            # rewrites every entry
powershell -File bench/check-regression.ps1 -UpdateBaseline -Partial   # merges only what was measured
URLOG_BASELINE_UPDATE=1 git commit -m "bench: baseline for <reason>"
```

The pre-commit hook refuses a staged `bench/baseline.json` unless `URLOG_BASELINE_UPDATE=1` is set; that is
the local stand-in for a labelled pull request. Say in the commit message why the numbers moved.

## Baseline format

```json
{
  "schema": 1,
  "tolerance": 0.10,
  "updated": "2026-11-04",
  "machine": { "cpu": "...", "runtime": "...", "os": "..." },
  "ungated": [ "Urlog.Bench.Benches.EndToEnd.*", "Urlog.Bench.Benches.DiskRead.*", "Urlog.Bench.Benches.Insert.*(BlockSize: 500000)" ],
  "useMin": [ "Urlog.Bench.Benches.Insert.*" ],
  "benchmarks": {
    "Urlog.Bench.Benches.TokeniseNginx.Parse": { "meanNs": 123456789.0, "allocatedBytes": 0, "provisional": true },
    "Urlog.Bench.Benches.Insert.Tcp(BlockSize: 250000)": { "meanNs": 234567890.0, "minNs": 222222222.0, "allocatedBytes": 1024 }
  }
}
```

Keys are BenchmarkDotNet `FullName`s, which include `[Params]` values, so each block size is its own entry.
`ungated` lists wildcard patterns the gate reports but never compares or writes (`EndToEnd`), so a
`--filter '*'` run followed by the gate is still green.
