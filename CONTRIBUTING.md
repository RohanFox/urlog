# Contributing

Requirements: .NET 10 SDK, Docker (the development ClickHouse and the integration tests), PowerShell 5.1 or later.

    powershell -File tools/install-hooks.ps1      (once per clone)
    dotnet build -warnaserror
    docker compose -f compose.dev.yml up -d clickhouse
    dotnet test
    powershell -File tools/pre-merge.ps1          (before a merge: all tests, the benchmarks, the regression gate)

Commits follow Conventional Commits: `type(scope): subject`, with the scope optional. Types: feat, fix, perf,
refactor, test, docs, build, ci, style, revert, chore. The subject is imperative, lower case, without a trailing
period and at most 72 characters. The commit-msg hook checks the subject.

The design is in docs/design-record.md and the decisions in docs/adr/. A change to a parser or to the insert path
comes with benchmark numbers (bench/README.md).

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
