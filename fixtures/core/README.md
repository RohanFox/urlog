# fixtures/core

Tiny inputs for the core pipeline tests (`tests/Core.Tests`): `sample.log` plus the same bytes compressed with
the `xz`, `gzip` and `bzip2` command-line tools (zstd is produced in-process by the test, there is no CLI on the
dev box). The decoder tests require every variant to decode to `sample.log` byte for byte.
