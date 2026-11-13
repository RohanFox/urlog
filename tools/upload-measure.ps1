#requires -Version 5.1
<#
.SYNOPSIS
  The done measurement of the web upload: a large resumable upload with the backend's memory sampled once a second.

.DESCRIPTION
  Starts the backend as its own Release process against a scratch pair (a metadata file and the event store
  tools/scratch-store.ps1 made for it), with the job runner off so that only the upload path is in the process,
  and runs tools/upload-measure against it: -Bytes of the source file, read again and again, as one upload in
  chunks with a SHA-256 checksum each, the connection cut in the middle of a chunk at every -CutGiB point and the upload resumed.

  Reported: bytes per second, the time of the last chunk's request (it answers after intake has hashed and
  recorded the file), the backend's working set (least, greatest, last, range), and the SHA-256 three ways:
  computed while sending, as the server recorded it, and of the stored file read back from disk here.

  The evidence root must be on a volume with room for the upload above its watermark. Nothing here touches the
  development store or its metadata file.

.EXAMPLE
  powershell -File tools/scratch-store.ps1 -MetadataFile E:\scratch\upload\metadata.db
  powershell -File tools/upload-measure.ps1 -MetadataFile E:\scratch\upload\metadata.db -EvidenceRoot E:\scratch\upload\intake
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string]$MetadataFile,
  [Parameter(Mandatory = $true)][string]$EvidenceRoot,
  [string]$Source = 'bench/data/nginx-1g.log',
  [long]$Bytes = 21474836480,
  [int]$ChunkMiB = 16,
  [string]$CutGiB = '7,14',
  [int]$Port = 5081,
  [string]$NativePort = '19000',
  [string]$OutDir = 'test-logs/measure/upload'
)
$ErrorActionPreference = 'Stop'
$root = (& git rev-parse --show-toplevel).Trim()
Set-Location $root

$dbPath = [System.IO.Path]::GetFullPath($MetadataFile)
if (-not (Test-Path -LiteralPath $dbPath)) { throw "upload-measure: $dbPath does not exist; create the scratch pair with tools/scratch-store.ps1 first" }
$sourcePath = [System.IO.Path]::GetFullPath($Source)
if (-not (Test-Path -LiteralPath $sourcePath)) { throw "upload-measure: source file $sourcePath does not exist" }
$evidence = [System.IO.Path]::GetFullPath($EvidenceRoot)
$out = [System.IO.Path]::GetFullPath($OutDir)
New-Item -ItemType Directory -Force -Path $out, $evidence | Out-Null

foreach ($project in @('src/Api', 'src/Cli', 'tools/upload-measure')) {
  & dotnet build $project -c Release --nologo -v quiet
  if ($LASTEXITCODE -ne 0) { throw "upload-measure: build of $project failed" }
}

# An account for this run alone; its password lives in this process and its children.
$user = 'measure' + (Get-Date -Format 'HHmmss')
$env:URLOG_PASSWORD = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
$env:URLOG_DB = $dbPath
$env:URLOG_CH_TCP_PORT = $NativePort
& dotnet (Join-Path $root 'src/Cli/bin/Release/net10.0/urlog.dll') user create --db $dbPath --name $user
if ($LASTEXITCODE -ne 0) { throw "upload-measure: urlog user create failed" }

$env:ASPNETCORE_ENVIRONMENT = 'Production'
$env:Urlog__Ingest__Enabled = 'false'
$env:Urlog__Ingest__EvidenceRoot = $evidence
$env:Kestrel__Endpoints__Http__Url = "http://127.0.0.1:$Port"
$apiDir = Join-Path $root 'src/Api/bin/Release/net10.0'
$apiLog = Join-Path $out 'backend.log'
$api = Start-Process -FilePath 'dotnet' -ArgumentList 'Urlog.Api.dll' -WorkingDirectory $apiDir -PassThru -WindowStyle Hidden `
  -RedirectStandardOutput $apiLog -RedirectStandardError (Join-Path $out 'backend.err.log')
try {
  $ready = $false
  for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Seconds 1
    if ($api.HasExited) { throw "upload-measure: the backend exited; see $apiLog" }
    try {
      $health = Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:$Port/api/v1/health" -TimeoutSec 5
      if ($health.StatusCode -eq 200) { $ready = $true; break }
    } catch { }
  }
  if (-not $ready) { throw "upload-measure: the backend did not answer its health route with 200 in 60 s; see $apiLog" }

  $samples = Join-Path $out 'samples.csv'
  $clientLog = Join-Path $out 'client.log'
  & dotnet (Join-Path $root 'tools/upload-measure/bin/Release/net10.0/upload-measure.dll') `
    --url "http://127.0.0.1:$Port" --user $user --source $sourcePath --bytes $Bytes --chunk-mib $ChunkMiB `
    --cut-gib $CutGiB --pid $api.Id --samples $samples | Tee-Object -FilePath $clientLog
  $clientExit = $LASTEXITCODE
}
finally {
  if (-not $api.HasExited) { Stop-Process -Id $api.Id -Force }
  Remove-Item Env:URLOG_PASSWORD
}

# The stored file read back from disk, by a hash that is neither the client's nor the server's.
$stored = (Select-String -LiteralPath $clientLog -Pattern '^stored\s+case (\d+), web/([0-9a-f]{32})$').Matches
if ($stored.Count -eq 1) {
  $file = Join-Path $evidence (Join-Path $stored[0].Groups[1].Value (Join-Path 'web' $stored[0].Groups[2].Value))
  $disk = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
  $line = "sha256    $disk (the stored file, read back from disk)"
  Write-Host $line
  Add-Content -LiteralPath $clientLog -Value $line
  $sent = (Select-String -LiteralPath $clientLog -Pattern '^sha256\s+([0-9a-f]{64}) \(computed while sending\)$').Matches[0].Groups[1].Value
  if ($disk -ne $sent) { Write-Host "result    FAILED: the stored file differs from what was sent"; exit 1 }
}
Write-Host "upload-measure: samples in $samples, client output in $clientLog, backend log in $apiLog"
exit $clientExit
