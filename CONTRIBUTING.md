# Contributing

Requirements: .NET 10 SDK, Docker (the development ClickHouse and the integration tests), PowerShell 5.1 or later.

    powershell -File tools/install-hooks.ps1      (once per clone)
    dotnet build -warnaserror
    docker compose -f compose.dev.yml up -d clickhouse
    dotnet run --project src/Cli -- migrate --db src/Api/data/metadata.db    (once: the schema, and one store identity for the pair)
    dotnet test
    powershell -File tools/pre-merge.ps1          (before a merge: all tests, the benchmarks, the regression gate)
    cd web && npm ci && npm run check              (the web interface: type check, lint, unit tests; the hook runs it for commits that touch web/)

Commits follow Conventional Commits: `type(scope): subject`, with the scope optional. Types: feat, fix, perf,
refactor, test, docs, build, ci, style, revert, chore. The subject is imperative, lower case, without a trailing
period and at most 72 characters. The commit-msg hook checks the subject.

The design is in docs/design-record.md and the decisions in docs/adr/. A change to a parser or to the insert path
comes with benchmark numbers (bench/README.md).

## A metadata file and its event store

A metadata file belongs to one event store, and both hold the same store identity (docs/design-record.md,
"Consistency", Store identity). Every command that writes compares the two first and refuses a pair that does not
match. `urlog migrate --db <file>` gives a new metadata file and an empty store their identity; `urlog pair --db <file>`
joins a file and a store that already hold data, after showing what it joins and asking for the identity to be typed
back. A development store that was filled before this rule is paired once that way.

Never point a second metadata file at the development store. A measurement or an experiment that needs a metadata
file of its own gets an event store of its own:

    powershell -File tools/scratch-store.ps1 -MetadataFile <new file>     (a second ClickHouse on ports 19000/18123, no volume)
    powershell -File tools/scratch-store.ps1 -Down

The upload measurement runs on such a pair: the backend as its own process with the job runner off, one large
resumable upload cut and resumed, the backend's working set sampled once a second
(docs/design-record.md, "Intake", Web upload):

    powershell -File tools/upload-measure.ps1 -MetadataFile <the scratch file> -EvidenceRoot <a folder with room>

## Upgrades

The decoders for compressed input are wrapped by checks that rely on how the .NET runtime's `DeflateStream` and
the ZstdSharp and SharpCompress streams behave at the end of their input (docs/design-record.md, "Compressed
input"). None of those behaviours is a documented contract. After a bump of the .NET runtime or SDK, of
ZstdSharp.Port or of SharpCompress, run the decoder tests first and treat a failure as a change in the library,
not as a flaky test:

    dotnet test tests/Core.Tests --filter "FullyQualifiedName~CheckedDecodeStream|FullyQualifiedName~GzipMemberCut|FullyQualifiedName~TrailingBytes"

They cut files of every format at every byte, so a decoder that starts reading ahead, stops reporting an error or
hands out bytes it has not verified shows up there as a wrong classification or as content that is not a prefix of
the original.
