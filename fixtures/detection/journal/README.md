# fixtures/detection/journal

Header-only stubs of recognised log formats this build does not parse: the binary systemd journal and, until its
source exists, the Windows event log. Kind `unparsed`, each with a message.

`expected.tsv` holds what the detector must report for each file. The files are written by
`tests/Sources.Tests/Detection/FixtureWriter.cs`; change that file and write again, never a fixture by hand.
