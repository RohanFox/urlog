# fixtures/detection/syslog

Synthetic syslog files: RFC 3164 with and without a PRI, the rsyslog ISO form, RFC 5424 with structured data, a
sequence-numbered network device, the usual file names and rotations, three compressions, CRLF, a BOM and UTF-16
with and without one.

`expected.tsv` holds what the detector must report for each file. The files are written by
`tests/Sources.Tests/Detection/FixtureWriter.cs`; change that file and write again, never a fixture by hand.
