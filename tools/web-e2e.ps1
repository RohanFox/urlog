#requires -Version 5.1
<#
.SYNOPSIS
  The web interface in a real browser against the real backend and a throwaway event store.

.DESCRIPTION
  Default: builds the web interface into the backend's web root, starts the scratch event store with a
  metadata file of its own (tools/scratch-store.ps1, never the development store), creates two users with
  passwords made for this run, starts the backend in Release on a port of its own, and runs the browser tests
  in web/e2e against it. Everything it started is removed at the end, also when a step fails.

  -Keep: an instance to look at by hand, which stays up until it is taken down. It shares nothing with a test
  run or a build: the backend is published with the web interface into the run's folder under
  test-logs/web-e2e/ and runs from there, on its own port (5082) and against its own store
  (tools/scratch-store.ps1 -Store kept). A build of the tree, a pre-merge run and the browser tests leave it
  alone. It prints the address and the two accounts' names; the passwords are written to a file in the run's
  folder, not printed. Run it in a terminal of its own: the backend it leaves running keeps a pipe open, and
  a caller that reads this script's output through one waits for that backend.

  -DownKept: stops the kept instance and removes its store.
  -Down: stops what an interrupted test run left behind and removes the scratch store. It does not touch a
  kept instance.

.EXAMPLE
  powershell -File tools/web-e2e.ps1
  powershell -File tools/web-e2e.ps1 -Keep
  powershell -File tools/web-e2e.ps1 -DownKept
#>
[CmdletBinding()]
param(
  [int]$Port = 5081,
  [int]$KeepPort = 5082,
  [switch]$Keep,
  [switch]$Down,
  [switch]$DownKept,
  [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$root = (& git rev-parse --show-toplevel).Trim()
Set-Location $root

$runs = Join-Path $root 'test-logs/web-e2e'
$scratchStore = Join-Path $root 'tools/scratch-store.ps1'

# One instance of each kind at a time: the tests' and the kept one. Each has its pid file, port and store.
if ($Keep -or $DownKept) { $kind = 'kept'; $store = 'kept'; $nativePort = '19001'; $httpPort = '18124'; $listen = $KeepPort }
else { $kind = 'tests'; $store = 'scratch'; $nativePort = '19000'; $httpPort = '18123'; $listen = $Port }
$pidFile = Join-Path $runs "$kind.pid"

function Stop-Backend {
  if (Test-Path -LiteralPath $pidFile) {
    $id = [int](Get-Content -LiteralPath $pidFile)
    $process = Get-Process -Id $id -ErrorAction SilentlyContinue
    if ($process -and $process.ProcessName -eq 'dotnet') { Stop-Process -Id $id -Force; $process.WaitForExit(10000) | Out-Null }
    Remove-Item -LiteralPath $pidFile -Force
  }
}

function Remove-Everything {
  Stop-Backend
  & powershell -NoProfile -ExecutionPolicy Bypass -File $scratchStore -Store $store -Down | Out-Host
}

if ($Down -or $DownKept) {
  Remove-Everything
  exit 0
}

# test-logs/ is this clone's own folder for run output and is kept out of the tree.
$exclude = (& git rev-parse --git-path info/exclude).Trim()
if (-not (Select-String -LiteralPath $exclude -Pattern '^test-logs/$' -Quiet -ErrorAction SilentlyContinue)) { Add-Content -LiteralPath $exclude -Value 'test-logs/' }

$run = Join-Path $runs ("{0}-{1}" -f $kind, (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force -Path $run | Out-Null
$db = Join-Path $run 'metadata.db'
$status = 1
$kept = $false

try {
  # A second instance of the same kind would meet the first one's port and store.
  Remove-Everything

  Write-Host "web-e2e: building the web interface"
  Push-Location (Join-Path $root 'web')
  try {
    if (-not (Test-Path 'node_modules')) { & npm ci --no-audit --no-fund; if ($LASTEXITCODE -ne 0) { throw "web-e2e: npm ci failed" } }
    & npm run build --silent
    if ($LASTEXITCODE -ne 0) { throw "web-e2e: the web build failed" }
    if (-not $Keep) {
      & npx playwright install chromium | Out-Host
      if ($LASTEXITCODE -ne 0) { throw "web-e2e: the browser could not be installed" }
    }
  } finally { Pop-Location }

  Write-Host "web-e2e: event store ($store)"
  & powershell -NoProfile -ExecutionPolicy Bypass -File $scratchStore -Store $store -MetadataFile $db -Configuration $Configuration | Out-Host
  if ($LASTEXITCODE -ne 0) { throw "web-e2e: the store did not start" }

  if ($Keep) {
    # A copy of the backend with the web interface in it, in the run's folder: nothing under bin/ is held open.
    $backendHome = Join-Path $run 'backend'
    & dotnet publish src/Api -c $Configuration -o $backendHome --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "web-e2e: the backend could not be published" }
    # The settings of this box are not part of a publish; the copy reads them where they are.
    $local = Join-Path $root 'src/Api/appsettings.Local.json'
    if (Test-Path -LiteralPath $local) { $env:Urlog__LocalSettings = $local }
  }
  else {
    $backendHome = Join-Path $root 'src/Api'
    & dotnet build src/Api -c $Configuration --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "web-e2e: the backend build failed" }
  }

  # Two accounts for this run alone. The passwords never reach the console.
  $cli = Join-Path $root ("src/Cli/bin/{0}/net10.0/urlog.dll" -f $Configuration)
  $owner = 'e2e-owner'
  $other = 'e2e-other'
  $ownerPassword = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
  $otherPassword = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
  $env:URLOG_PASSWORD = $ownerPassword
  & dotnet $cli user create --db $db --name $owner --admin | Out-Host
  if ($LASTEXITCODE -ne 0) { throw "web-e2e: could not create $owner" }
  $env:URLOG_PASSWORD = $otherPassword
  & dotnet $cli user create --db $db --name $other | Out-Host
  if ($LASTEXITCODE -ne 0) { throw "web-e2e: could not create $other" }
  Remove-Item Env:URLOG_PASSWORD

  $url = "http://127.0.0.1:$listen"
  $env:Kestrel__Endpoints__Http__Url = $url
  $env:Urlog__Metadata__Path = $db
  $env:Urlog__ClickHouse__NativePort = $nativePort
  $env:Urlog__ClickHouse__HttpPort = $httpPort
  $env:Urlog__Ingest__EvidenceRoot = (Join-Path $run 'intake')
  $env:ASPNETCORE_ENVIRONMENT = 'Production'
  # Every browser test signs in from this one address within the same minute; the sign-in throttle would stop
  # the run. A kept instance keeps the throttle a person would meet.
  if (-not $Keep) { $env:Urlog__Auth__LoginThrottle__Attempts = '100' }

  Write-Host "web-e2e: backend on $url"
  if ($Keep) { $api = Join-Path $backendHome 'Urlog.Api.dll' } else { $api = Join-Path $root ("src/Api/bin/{0}/net10.0/Urlog.Api.dll" -f $Configuration) }
  $backend = Start-Process -FilePath 'dotnet' -ArgumentList @("`"$api`"") -WorkingDirectory $backendHome -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $run 'backend.log') -RedirectStandardError (Join-Path $run 'backend.err.log')
  Set-Content -LiteralPath $pidFile -Value $backend.Id

  $ready = $false
  for ($i = 0; $i -lt 60; $i++) {
    try {
      $health = Invoke-WebRequest -Uri "$url/api/v1/health" -UseBasicParsing -TimeoutSec 2
      if ($health.StatusCode -eq 200) { $ready = $true; break }
    } catch { }
    if ($backend.HasExited) { throw "web-e2e: the backend stopped; see $run\backend.log" }
    Start-Sleep -Seconds 1
  }
  if (-not $ready) { throw "web-e2e: the backend did not become ready in 60 s; see $run\backend.log" }

  if ($Keep) {
    Set-Content -LiteralPath (Join-Path $run 'accounts.txt') -Value @("$owner $ownerPassword", "$other $otherPassword")
    Write-Host ""
    Write-Host "web-e2e: kept instance at $url"
    Write-Host "  accounts: $owner (admin), $other; passwords in $run\accounts.txt"
    Write-Host "  take it down with: powershell -File tools/web-e2e.ps1 -DownKept"
    $kept = $true
    $status = 0
  }
  else {
    $env:URLOG_E2E_URL = $url
    $env:URLOG_E2E_OWNER = $owner
    $env:URLOG_E2E_OWNER_PASSWORD = $ownerPassword
    $env:URLOG_E2E_OTHER = $other
    $env:URLOG_E2E_OTHER_PASSWORD = $otherPassword
    Push-Location (Join-Path $root 'web')
    try {
      & npx playwright test
      $status = $LASTEXITCODE
    } finally { Pop-Location }
    if ($status -ne 0) { Write-Host "web-e2e: browser tests failed; backend log in $run\backend.log" }
  }
}
finally {
  foreach ($name in 'URLOG_PASSWORD', 'URLOG_E2E_OWNER_PASSWORD', 'URLOG_E2E_OTHER_PASSWORD') { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
  if (-not $kept) { Remove-Everything }
}
exit $status
