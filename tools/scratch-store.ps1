#requires -Version 5.1
<#
.SYNOPSIS
  A throwaway event store with a metadata file of its own, for measurements and experiments.

.DESCRIPTION
  A metadata file belongs to one event store, and every writer refuses a pair whose store identities differ
  (docs/design-record.md, "Consistency", Store identity). A scratch metadata file therefore cannot be run against
  the development store, and must not be: its file ids start at 1 and would stand for other files there. This
  script gives it a store of its own.

  Up (default): starts the container urlog-clickhouse-scratch (compose.dev.yml, profile "scratch": the pinned
  image, the same accounts, native port 19000 and HTTP port 18123 on 127.0.0.1, no volume), waits until it
  answers, then runs `urlog migrate --db <MetadataFile>` against it, which creates the schema and gives the new metadata
  file and the new store one identity. It ends by printing the environment that points `urlog` at the pair.

  -Down: removes the container and what it held. The metadata file is left; with its store gone it pairs with
  nothing, so delete it, or let the next run start from a new file.

.EXAMPLE
  powershell -File tools/scratch-store.ps1 -MetadataFile E:\scratch\run1\metadata.db
  powershell -File tools/scratch-store.ps1 -Down
#>
[CmdletBinding()]
param(
  [string]$MetadataFile,
  [switch]$Down,
  [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$root = (& git rev-parse --show-toplevel).Trim()
Set-Location $root

$compose = @('compose', '-f', 'compose.dev.yml', '--profile', 'scratch')
$nativePort = '19000'
$httpPort = '18123'

if ($Down) {
  & docker @compose rm --stop --force clickhouse-scratch
  if ($LASTEXITCODE -ne 0) { throw "scratch-store: could not remove the container" }
  Write-Host "scratch-store: urlog-clickhouse-scratch removed with everything it held"
  exit 0
}

if (-not $MetadataFile) { throw "scratch-store: -MetadataFile <path of a new metadata file> is required (or -Down)" }
$dbPath = [System.IO.Path]::GetFullPath($MetadataFile)
if (Test-Path -LiteralPath $dbPath) {
  throw "scratch-store: $dbPath exists. A scratch run starts from a new metadata file: an old one carries the identity of a store that is gone."
}

& docker @compose up -d clickhouse-scratch
if ($LASTEXITCODE -ne 0) { throw "scratch-store: could not start the container" }

$healthy = $false
for ($i = 0; $i -lt 60; $i++) {
  $state = (& docker inspect --format '{{.State.Health.Status}}' urlog-clickhouse-scratch 2>$null)
  if ($state -eq 'healthy') { $healthy = $true; break }
  Start-Sleep -Seconds 1
}
if (-not $healthy) { throw "scratch-store: urlog-clickhouse-scratch did not become healthy in 60 s" }

# Always built: a binary from before a schema change would create yesterday's schema.
$cli = Join-Path $root ("src/Cli/bin/{0}/net10.0/urlog.dll" -f $Configuration)
& dotnet build src/Cli -c $Configuration --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "scratch-store: build failed" }

& dotnet $cli migrate --db $dbPath --native-port $nativePort --http-port $httpPort
if ($LASTEXITCODE -ne 0) { throw "scratch-store: urlog migrate --db failed" }

Write-Host ""
Write-Host "scratch-store: ready. For this shell:"
Write-Host ('  $env:URLOG_DB = "{0}"' -f $dbPath)
Write-Host ('  $env:URLOG_CH_TCP_PORT = "{0}"' -f $nativePort)
Write-Host ('  $env:URLOG_CH_HTTP_PORT = "{0}"' -f $httpPort)
Write-Host "then urlog user create, case create, enqueue, worker and query run against the scratch pair."
Write-Host "Remove it with: powershell -File tools/scratch-store.ps1 -Down"
