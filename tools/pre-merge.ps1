#requires -Version 5.1
<#
.SYNOPSIS
  Full gate before merging a branch. Requires Docker (integration tests, Insert benches).

.DESCRIPTION
  0. quiet precheck: warns when free RAM < 8 GB or when the ClickHouse container is above 20 % CPU before
     the benches start, so a noisy run is labelled as such in the output. With -Quiet those warnings stop
     the run instead (use it when you are about to take a baseline). A WSL distro that is running but idle
     is left running: stopping it takes Docker Desktop's link to the distro with it, and the two figures
     above already say whether the machine is quiet.
     The ClickHouse figure is the median of eight docker stats samples about four seconds apart, and all
     eight are printed. One sample is not representative: an idle server flushes its system log tables
     (asynchronous_metric_log, trace_log, metric_log, part_log) every 7.5 seconds and merges the parts it
     wrote, a burst of 30 to 55 % of one core for under two seconds over an idle level near 3 %. Measured on
     the dev box with no merge, mutation or query running, one sample in four landed on a burst. Real load
     (merges after an ingest, another client) holds for longer than half of the 30 seconds and moves the
     median.
  1. dotnet build -warnaserror (Release)
  2. every test project, one after another: the unit projects, then tests/Integration.Tests, then
     tests/Security (both Testcontainers). Not `dotnet test urlog.sln`, which runs the projects side by side:
     tests/Integration.Tests ingests the 1 GiB fixture six times (the worker kill tests) and takes every core
     while it does, and a test in another project that was running at the same moment failed on its timing
     with nothing wrong in the code under test. One project at a time, a failure belongs to the project that
     reports it. No project and no test is left out: every tests/*/*.csproj runs, and the step fails if there
     are fewer than the three groups expect.
     A failing project prints the names of its failing tests, and the whole test output of the run is kept
     in test-logs/pre-merge.log (untracked, overwritten by the next run).
  3. bench/check-regression.ps1 -SelfTest, then the commit-msg hook against accepted and rejected messages,
     then the hygiene check of the pre-commit hook against added lines it must refuse and lines it must pass
  4. DiskRead, Tokenise*, DetectFormat and Insert* benches, full BenchmarkDotNet job
  5. bench/check-regression.ps1 in full mode: every baseline entry must be measured and within 10 %
  After recreating the dev database, run one throwaway 1 GiB ingest before this script (bench/README.md,
  "Running"): a fresh ClickHouse volume measured InsertNative 11 % slow and within 1 % once settled.
  There are no skip switches on purpose. If a stage cannot run, the branch is not ready to merge.
#>
[CmdletBinding()]
param(
  [switch]$Quiet
)
$ErrorActionPreference = 'Stop'
$root = (& git rev-parse --show-toplevel).Trim()
Set-Location $root

function Invoke-Step([string]$title, [scriptblock]$body) {
  Write-Host ""
  Write-Host ("=== {0}" -f $title)
  & $body
  if ($LASTEXITCODE -ne 0) { throw ("pre-merge: step failed: {0}" -f $title) }
}

# The commit-msg hook against messages it must accept and messages it must reject. Runs the hook the way git
# does (git hook run), so it needs no shell on PATH.
function Test-CommitMsgHook {
  $long = 'feat(api): ' + ('x' * 62)
  $cases = @(
    @{ ok = $true;  name = 'type(scope): subject';   text = "feat(api): add the export endpoint`n`nBody line.`n" },
    @{ ok = $true;  name = 'type: subject';          text = "docs: fix a typo`n" },
    @{ ok = $true;  name = 'comment lines skipped';  text = "# a comment`nfix(core): handle an empty chunk`n# another`n" },
    @{ ok = $true;  name = '72 characters';          text = ($long.Substring(0, 72) + "`n") },
    @{ ok = $true;  name = 'Merge subject';          text = ("Merge branch 'topic-example': " + ('x' * 80) + "`n") },
    @{ ok = $true;  name = 'Revert subject';         text = "Revert `"feat(api): add the export endpoint`"`n`nThis reverts commit 0123abc.`n" },
    @{ ok = $true;  name = 'fixup! subject';         text = "fixup! feat(api): add the export endpoint`n" },
    @{ ok = $true;  name = 'squash! subject';        text = "squash! not a conventional subject`n" },
    @{ ok = $true;  name = 'scissors block ignored'; text = "fix: one`n`n# ------------------------ >8 ------------------------`n+ not a subject`n" },
    @{ ok = $true;  name = 'body is not checked';    text = "docs: fine`n`nAny text: here.`nSigned-off-by: Someone <s@example.com>`n" },
    @{ ok = $false; name = 'no type';                text = "Add the export endpoint`n" },
    @{ ok = $false; name = 'unknown type';           text = "feature(api): add the export endpoint`n" },
    @{ ok = $false; name = 'no space after colon';   text = "feat:add the export endpoint`n" },
    @{ ok = $false; name = '73 characters';          text = ($long + "`n") },
    @{ ok = $false; name = 'comments only';          text = "# nothing here`n" }
  )
  $file = Join-Path ([System.IO.Path]::GetTempPath()) ("urlog-commit-msg-{0}.txt" -f $PID)
  $utf8 = New-Object System.Text.UTF8Encoding $false
  $failed = 0
  try {
    foreach ($c in $cases) {
      [System.IO.File]::WriteAllText($file, $c.text, $utf8)
      $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
      & git -c core.hooksPath=.githooks hook run commit-msg -- $file 2>$null | Out-Null
      $accepted = ($LASTEXITCODE -eq 0)
      $ErrorActionPreference = $old
      if ($accepted -ne $c.ok) {
        $failed++
        Write-Host ("  FAIL {0}: expected {1}" -f $c.name, $(if ($c.ok) { 'accepted' } else { 'rejected' }))
      }
    }
  }
  finally { Remove-Item -Force -ErrorAction SilentlyContinue $file }
  Write-Host ("commit-msg self-test: {0} cases, {1} failed" -f $cases.Count, $failed)
  if ($failed -gt 0) { $global:LASTEXITCODE = 1 } else { $global:LASTEXITCODE = 0 }
}

# The hygiene check of the pre-commit hook (.githooks/hygiene) against added lines it must refuse and added
# lines it must let through. The ids are put together here from two pieces, so that this file passes the check
# it tests.
function Test-HygieneCheck {
  $t = 'T'; $m = 'M'; $mile = 'mile' + 'stone'
  $cases = @(
    @{ ok = $false; name = 'work-item id in a comment';      path = 'src/Core/A.cs';        line = ("    // {0}07 (a): chosen by measurement" -f $t) },
    @{ ok = $false; name = 'work-item id in a doc';          path = 'docs/design-record.md'; line = ("Decided in {0}19." -f $t) },
    @{ ok = $false; name = 'work-item id in a test name';    path = 'tests/X/Y.cs';         line = ("    public void {0}14_RequeuesTheJob()" -f $t) },
    @{ ok = $false; name = 'id range';                       path = 'docs/a.md';            line = ("({0}09-{0}17)" -f $t) },
    @{ ok = $false; name = 'id beside the codec';            path = 'src/Core/A.cs';        line = ("// {0}07: {0}64 on the small integers" -f $t) },
    @{ ok = $false; name = 'branch name with an id';         path = 'tools/a.ps1';          line = ("git merge --squash t{0}-case-lifecycle" -f 19) },
    @{ ok = $false; name = 'named stage with a letter';      path = 'docs/a.md';            line = ("Planned for {0}1a." -f $m) },
    @{ ok = $false; name = 'named stage by number';          path = 'compose.dev.yml';      line = ("# joins in {0} 1" -f $mile) },
    @{ ok = $false; name = 'named stage, capitalised';       path = 'docs/a.md';            line = ("## {0} 0 and cut list" -f ($mile.Substring(0, 1).ToUpper() + $mile.Substring(1))) },
    @{ ok = $true;  name = 'the codec alone';                path = 'src/Core/A.cs';        line = ("    event_code UInt32 CODEC({0}64, ZSTD(1))," -f $t) },
    @{ ok = $true;  name = 'ISO timestamp';                  path = 'tests/X/Y.cs';         line = 'var at = "2026-11-14T10:00:00Z";' },
    @{ ok = $true;  name = 'timestamp format';               path = 'src/Core/A.cs';        line = "ToString(`"yyyyMMdd'T'HHmmss'Z'`")" },
    @{ ok = $true;  name = 'generic type parameter';         path = 'src/Core/A.cs';        line = 'public static T Parse<T>(string text) where T : struct' },
    @{ ok = $true;  name = 'longer identifier';              path = 'src/Core/A.cs';        line = 'var INT64 = 1; var UTF16 = 2; var T640 = 3; var XT07 = 4;' },
    @{ ok = $true;  name = 'megabytes and millions';         path = 'docs/a.md';            line = '1 M rows/s at 64 MiB, 100 MB/s' },
    @{ ok = $true;  name = 'the analyst''s own ticket';      path = 'docs/a.md';            line = 'record the head outside this machine (case notebook, ticket)' },
    @{ ok = $true;  name = 'a fixture line is never read';   path = 'fixtures/nginx/sample.log'; line = ("GET /{0}19/{1} HTTP/1.1" -f $t, $mile) },
    @{ ok = $true;  name = 'a removed line is not an added one'; path = 'src/Core/A.cs';    line = $null; removed = ("    // {0}07 (a): chosen by measurement" -f $t) }
  )
  $file = Join-Path ([System.IO.Path]::GetTempPath()) ("urlog-hygiene-{0}.diff" -f $PID)
  $utf8 = New-Object System.Text.UTF8Encoding $false
  $failed = 0
  try {
    foreach ($c in $cases) {
      $body = if ($null -ne $c.line) { "+" + $c.line } else { "-" + $c.removed }
      $text = "diff --git a/{0} b/{0}`n--- a/{0}`n+++ b/{0}`n@@ -1,0 +1 @@`n{1}`n" -f $c.path, $body
      [System.IO.File]::WriteAllText($file, $text, $utf8)
      $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
      & git -c core.hooksPath=.githooks hook run hygiene -- $file 2>$null | Out-Null
      $accepted = ($LASTEXITCODE -eq 0)
      $ErrorActionPreference = $old
      if ($accepted -ne $c.ok) {
        $failed++
        Write-Host ("  FAIL {0}: expected {1}" -f $c.name, $(if ($c.ok) { 'accepted' } else { 'refused' }))
      }
    }
  }
  finally { Remove-Item -Force -ErrorAction SilentlyContinue $file }
  Write-Host ("hygiene self-test: {0} cases, {1} failed" -f $cases.Count, $failed)
  if ($failed -gt 0) { $global:LASTEXITCODE = 1 } else { $global:LASTEXITCODE = 0 }
}

function Get-NoiseWarnings {
  $warnings = New-Object System.Collections.Generic.List[string]
  try {
    $freeGb = (Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory / 1MB
    if ($freeGb -lt 8) { $warnings.Add(("free RAM {0:N1} GB < 8 GB: fixtures and ClickHouse will fight for the page cache" -f $freeGb)) }
  }
  catch { $warnings.Add("could not read free RAM: " + $_.Exception.Message) }
  try {
    # Eight samples, the median decides: see the header for why one sample is not enough.
    $samples = New-Object System.Collections.Generic.List[double]
    $running = $true
    for ($i = 0; $i -lt 8 -and $running; $i++) {
      if ($i -gt 0) { Start-Sleep -Seconds 4 }
      $stats = & docker stats --no-stream --format '{{.Name}} {{.CPUPerc}}' 2>$null
      $ch = @($stats | Where-Object { $_ -match '^urlog-clickhouse\s+([\d.]+)%' })
      if ($ch.Count -eq 0) { $running = $false }
      else { $samples.Add([double]::Parse($Matches[1], [System.Globalization.CultureInfo]::InvariantCulture)) }
    }
    if (-not $running) { $warnings.Add("ClickHouse container urlog-clickhouse not running: start compose.dev.yml") }
    else {
      $sorted = @($samples | Sort-Object)
      $median = ($sorted[3] + $sorted[4]) / 2
      $shown = (@($samples | ForEach-Object { $_.ToString('0.0', [System.Globalization.CultureInfo]::InvariantCulture) }) -join ', ')
      Write-Host ("ClickHouse CPU %, eight samples four seconds apart: {0}; median {1}" -f $shown, $median.ToString('0.0', [System.Globalization.CultureInfo]::InvariantCulture))
      if ($median -gt 20) { $warnings.Add(("ClickHouse at a median of {0:N0} % CPU before the run (merges or another client); Insert numbers will be noisy" -f $median)) }
    }
  }
  catch { $warnings.Add("could not read docker stats: " + $_.Exception.Message) }
  return $warnings
}

Write-Host "=== quiet precheck"
$noise = Get-NoiseWarnings
$label = 'quiet'
if ($noise.Count -gt 0) {
  $label = 'NOISY'
  foreach ($w in $noise) { Write-Host ("WARNING {0}" -f $w) }
  if ($Quiet) { throw "pre-merge: -Quiet requested and the machine is not quiet; fix the warnings above and rerun" }
}
else {
  Write-Host "machine is quiet (free RAM >= 8 GB, ClickHouse idle)"
}

Invoke-Step 'build -warnaserror (Release)' { dotnet build urlog.sln -c Release -warnaserror --nologo -v quiet }
$containerProjects = @('tests/Integration.Tests/Integration.Tests.csproj', 'tests/Security/Security.Tests.csproj')
$allProjects = @(Get-ChildItem -Path tests -Filter *.csproj -Recurse -Depth 1 | ForEach-Object { 'tests/{0}/{1}' -f $_.Directory.Name, $_.Name } | Sort-Object)
$unitProjects = @($allProjects | Where-Object { $containerProjects -notcontains $_ })
foreach ($p in $containerProjects) { if ($allProjects -notcontains $p) { throw ("pre-merge: test project not found: {0}" -f $p) } }
if ($unitProjects.Count -lt 1) { throw "pre-merge: no unit test project found under tests/" }
# The whole test output of this run is kept in test-logs/pre-merge.log (untracked, overwritten by the next run),
# and a failing step names its failing tests: a failure that does not come back can still be read.
$exclude = (& git rev-parse --git-path info/exclude).Trim()
if (-not (Test-Path $exclude) -or -not (@(Get-Content $exclude) -contains 'test-logs/')) { Add-Content -Path $exclude -Value 'test-logs/' -Encoding ascii }
New-Item -ItemType Directory -Force -Path 'test-logs' | Out-Null
$testLog = 'test-logs/pre-merge.log'
$testLogPath = Join-Path $root $testLog
[System.IO.File]::WriteAllText($testLogPath, '')
foreach ($p in ($unitProjects + $containerProjects)) {
  $project = $p
  Write-Host ""
  Write-Host ("=== tests: {0}" -f $project)
  $output = @(& dotnet test $project -c Release --no-build --nologo | ForEach-Object { Write-Host $_; $_ })
  $exit = $LASTEXITCODE
  [System.IO.File]::AppendAllLines($testLogPath, [string[]](@("=== {0}" -f $project) + $output))
  if ($exit -ne 0) {
    Write-Host ("pre-merge: failing tests in {0}:" -f $project)
    $failing = @($output | Where-Object { $_ -match '^failed ' })
    if ($failing.Count -gt 0) { $failing | ForEach-Object { Write-Host $_ } } else { Write-Host "  (no test named: the run itself failed, see the log)" }
    Write-Host ("pre-merge: full test output kept in {0}" -f $testLog)
    throw ("pre-merge: step failed: tests: {0}" -f $project)
  }
}
Write-Host ("tests: {0} projects run one after another ({1} unit, then Integration.Tests, then Security)" -f $allProjects.Count, $unitProjects.Count)
Invoke-Step 'gate self-test'               { & powershell -NoProfile -ExecutionPolicy Bypass -File bench/check-regression.ps1 -SelfTest }
Invoke-Step 'commit-msg hook self-test'    { Test-CommitMsgHook }
Invoke-Step 'hygiene check self-test'      { Test-HygieneCheck }
Invoke-Step 'bench: DiskRead + Tokenise* + DetectFormat + Insert* (full job)' {
  if (Test-Path 'bench/BenchmarkDotNet.Artifacts/results') { Remove-Item -Recurse -Force 'bench/BenchmarkDotNet.Artifacts/results' }
  dotnet run -c Release --project bench --no-build -- --filter '*DiskRead*' '*Tokenise*' '*DetectFormat*' '*Insert*'
}
Invoke-Step 'regression gate (full)' { & powershell -NoProfile -ExecutionPolicy Bypass -File bench/check-regression.ps1 }
Write-Host ""
Write-Host ("pre-merge: OK (run conditions: {0})" -f $label)
if ($noise.Count -gt 0) { foreach ($w in $noise) { Write-Host ("  noted: {0}" -f $w) } }
