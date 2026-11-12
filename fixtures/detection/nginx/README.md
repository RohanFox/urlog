# fixtures/detection/nginx

Synthetic nginx and Apache access logs in the shapes real exports have: the built-in layouts, rotated and
compressed names, CRLF, a BOM, IPv6 clients, and a custom `log_format` no built-in fits (probable, layout unknown).

`expected.tsv` holds what the detector must report for each file. The files are written by
`tests/Sources.Tests/Detection/FixtureWriter.cs`; change that file and write again, never a fixture by hand.
