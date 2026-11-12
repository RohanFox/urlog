# fixtures/detection/generic

Synthetic application logs that no source claims: each timestamp shape the generic source reads, stack traces,
lines with no timestamp at all, Latin-1, UTF-16, and the formats that get their own source later (JSON lines, CSV).
All of them must fall back to `generic`.

`expected.tsv` holds what the detector must report for each file. The files are written by
`tests/Sources.Tests/Detection/FixtureWriter.cs`; change that file and write again, never a fixture by hand.
