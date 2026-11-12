# fixtures/detection/container

Small archives of synthetic logs. The detector reports the chain; intake expands them and detects each member.

`expected.tsv` holds what the detector must report for each file. The files are written by
`tests/Sources.Tests/Detection/FixtureWriter.cs`; change that file and write again, never a fixture by hand.
