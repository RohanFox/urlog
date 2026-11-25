# urlog

Agentless, post-hoc log investigation for CSIRT and incident-response analysts. Evidence files are copied
onto the machine that runs the tool, parsed into ClickHouse, and searched with a Splunk-style query
language. Self-hosted, no collectors, Apache-2.0.

The design is in [docs/design-record.md](docs/design-record.md). v1 is interactive search: no alerting,
no detection rules, no dashboard builder.

## Status

Pre-release. What is in the tree today:

- **Sources**: nginx and Apache access logs (combined, common and custom `log_format` layouts), syslog
  (RFC 3164, ISO 8601 timestamps, RFC 5424).
- **Ingest**: a chunked, parallel parse pipeline with zero per-event allocation in the parsers, and
  inserts over the ClickHouse native protocol. Reads gzip, zstd, xz, bzip2 and UTF-16 input.
- **Query**: the SPL filter grammar with time ranges, plus `stats count by`, `sort` and `head`, transpiled
  to parameterised ClickHouse SQL. Every query runs through a per-case view as a read-only ClickHouse user.
- **Metadata**: users, sessions, cases, members, hosts, evidence files and jobs in SQLite, with a
  hash-chained audit log, a chain verifier and verified backups.
- **API**: an ASP.NET Core host with local accounts, sessions, and case and member endpoints.

- **Web interface** (`web/`): sign-in, the case list, the evidence page of a case with resumable upload,
  the file list and parse progress, and the search page (query, time range, timeline, events, one event in
  full), in light and dark. Text out of a log is drawn as text that cannot act, and every answer carries a
  strict content policy.

Not in the tree yet: the field sidebar, completion and export of the search page, the other query verbs
(`where`, full `stats`, `table`, `rex`, `timechart`), and further sources (EVTX, journald, IIS and others).

## Requirements

- .NET 10 SDK
- Docker, for the development ClickHouse (26.8 LTS) and the integration tests
- PowerShell 5.1 or later, for the hook installer, the pre-merge gate and the benchmark gate

## Quick start

```
powershell -File tools/install-hooks.ps1
dotnet build -warnaserror
docker compose -f compose.dev.yml up -d clickhouse
dotnet run --project src/Cli -- migrate
dotnet run --project src/Cli -- ingest --case 1 --host web-01 path/to/access.log
dotnet run --project src/Cli -- query --case 1 'status>=500 | stats count as n by url.path | sort -n | head 20'
```

The API host needs a first admin account:

```
dotnet run --project src/Cli -- user create --db src/Api/data/metadata.db --name alice --admin
dotnet run --project src/Api
```

It listens on http://127.0.0.1:5080 under `/api/v1`; `src/Api/api.http` has sample requests. The accounts
and passwords in `compose.dev.yml` are development stubs.

The web interface needs Node 26 or later. In development it runs beside the backend and hands `/api` on to it:

```
cd web
npm ci
npm run dev          # http://localhost:5173
```

`npm run build` writes the production build to `src/Api/wwwroot`, which the backend then serves at its own
address. A publish runs it before `dotnet publish`; `dotnet build` never needs Node.

## Tests and benchmarks

```
dotnet test
dotnet run -c Release --project bench -- --filter '*Nginx*'
powershell -File tools/pre-merge.ps1
cd web && npm run check                       # type check, lint, unit tests
powershell -File tools/web-e2e.ps1            # the interface in a browser, real backend, scratch store
```

`dotnet test` includes the integration and security suites, which start ClickHouse through Testcontainers
and need Docker. The throughput targets and the regression gate are described in
[bench/README.md](bench/README.md).

## Documentation

- [docs/design-record.md](docs/design-record.md): scope, schema, query language, security model
- [docs/adr/](docs/adr/README.md): decision records
- [CONTRIBUTING.md](CONTRIBUTING.md): requirements, build, test, commit format

## Licence

Apache-2.0. See [LICENSE](LICENSE).
