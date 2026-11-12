# fixtures/detection/adversarial

Synthetic files built to mislead the detector: logs with artifact names, an artifact with a log name, mixed formats,
binary bytes inside a log, damaged and doubled compression, empty files, text that starts like a binary signature,
and a UTF-16 Event Viewer XML export (generic until the Windows event sources exist). A gzip cut short decodes
without an error up to the cut, so `truncated.log.gz` carries no flag: the detector cannot see the cut.

`expected.tsv` holds what the detector must report for each file. The files are written by
`tests/Sources.Tests/Detection/FixtureWriter.cs`; change that file and write again, never a fixture by hand.
