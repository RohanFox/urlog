# fixtures/detection/artifact

Stubs of files that are not logs: a signature plus padding (nothing executable or readable as the real format),
small real zips for the zip-based documents, and text files that are artifacts by extension only.

`expected.tsv` holds what the detector must report for each file. The files are written by
`tests/Sources.Tests/Detection/FixtureWriter.cs`; change that file and write again, never a fixture by hand.
