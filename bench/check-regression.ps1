#requires -Version 5.1
<#
.SYNOPSIS
  Benchmark regression gate: compares BenchmarkDotNet results against bench/baseline.json.

.DESCRIPTION
  Reads every *-report-full.json under -ResultsDir, keys each benchmark by its BenchmarkDotNet FullName
  (type, method and parameters) and compares against the baseline with a 10 % tolerance. The statistic is
  Statistics.Mean, except for benchmarks matching the baseline's "useMin" patterns, which compare
  Statistics.Min: a bench with an external component (ClickHouse on the same box for Insert*) is judged
  on its best iteration, because interference only adds time and the minimum is the stable estimator of
  the client's own cost. "ungated" patterns are reported, never compared.
  Exit code 1 on any of:
    - a benchmark slower than baseline * (1 + tolerance)
    - a benchmark that allocates per operation when its baseline allocates 0 bytes
    - a benchmark in the results with no baseline entry (add it deliberately with -UpdateBaseline)
    - a baseline entry with no result, unless -Partial (the pre-commit hook runs a subset)
  Exit code 0 when nothing regressed, including the empty-harness case (no results, no baseline).

  The tolerance is a non-negotiable (docs/design-record.md, "Throughput SLO and benchmark harness"): the script refuses to run with a tolerance above 0.10.

.PARAMETER ResultsDir
  Folder holding BenchmarkDotNet *-report-full.json files.
.PARAMETER Baseline
  Path to baseline.json.
.PARAMETER Partial
  Only compare benchmarks present in the results; baseline entries that were not measured are not an error.
.PARAMETER UpdateBaseline
  Write the measured means into the baseline instead of comparing (merged into the existing file when -Partial).
.PARAMETER Provisional
  With -UpdateBaseline: mark the written entries "provisional": true. A provisional entry is reported but is
  not a speed gate (used while a bench runs a stub tokeniser); the zero-allocation rule still applies.
  A later -UpdateBaseline without -Provisional promotes the entry to a real gate.
.PARAMETER Tolerance
  Override the baseline file's tolerance; may only be tightened, never above 0.10.
.PARAMETER SelfTest
  Run the script's own test cases on synthetic data and exit.

.EXAMPLE
  dotnet run -c Release --project bench -- --filter '*Tokenise*' --job short
  powershell -File bench/check-regression.ps1 -Partial
#>
[CmdletBinding()]
param(
  [string]$ResultsDir = "",
  [string]$Baseline = "",
  [switch]$Partial,
  [switch]$UpdateBaseline,
  [switch]$Provisional,
  [double]$Tolerance = -1,
  [switch]$SelfTest
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# Hard ceiling. Raising this constant is "weakening a CI gate" and is forbidden (docs/design-record.md, "Throughput SLO and benchmark harness").
$script:MaxTolerance = 0.10

# $PSScriptRoot is not reliable inside the param block on Windows PowerShell 5.1, so defaults resolve here.
$script:BenchDir = $PSScriptRoot
if ([string]::IsNullOrEmpty($script:BenchDir)) { $script:BenchDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
if ([string]::IsNullOrEmpty($ResultsDir)) { $ResultsDir = Join-Path $script:BenchDir "BenchmarkDotNet.Artifacts/results" }
if ([string]::IsNullOrEmpty($Baseline)) { $Baseline = Join-Path $script:BenchDir "baseline.json" }

function Get-PropNames($obj) {
  # Strict-mode safe: an empty PSCustomObject has no .Name on its (empty) property collection.
  $names = @()
  if ($null -eq $obj) { return $names }
  foreach ($p in $obj.PSObject.Properties) { $names += $p.Name }
  return $names
}

function Read-Baseline([string]$path) {
  if (-not (Test-Path $path)) {
    return [pscustomobject]@{ schema = 1; tolerance = $script:MaxTolerance; updated = $null; machine = $null; benchmarks = [pscustomobject]@{} }
  }
  $b = Get-Content -Raw -Path $path | ConvertFrom-Json
  if (-not ((Get-PropNames $b) -contains 'benchmarks') -or $null -eq $b.benchmarks) {
    $b | Add-Member -NotePropertyName benchmarks -NotePropertyValue ([pscustomobject]@{}) -Force
  }
  return $b
}

function Read-Results([string]$dir) {
  # Returns an ordered hashtable FullName -> @{ meanNs; allocatedBytes; machine }
  $out = [ordered]@{}
  if (-not (Test-Path $dir)) { return $out }
  $files = Get-ChildItem -Path $dir -Filter '*-report-full.json' -File | Sort-Object Name
  foreach ($f in $files) {
    $report = Get-Content -Raw -Path $f.FullName | ConvertFrom-Json
    $machine = $null
    if ((Get-PropNames $report) -contains 'HostEnvironmentInfo') {
      $h = $report.HostEnvironmentInfo
      $machine = @{ cpu = $h.ProcessorName; runtime = $h.RuntimeVersion; os = $h.OsVersion }
    }
    foreach ($bm in $report.Benchmarks) {
      $alloc = $null
      if (((Get-PropNames $bm) -contains 'Memory') -and $null -ne $bm.Memory) {
        $alloc = [double]$bm.Memory.BytesAllocatedPerOperation
      }
      $min = $null
      if ((Get-PropNames $bm.Statistics) -contains 'Min' -and $null -ne $bm.Statistics.Min) { $min = [double]$bm.Statistics.Min }
      $out[$bm.FullName] = @{ meanNs = [double]$bm.Statistics.Mean; minNs = $min; allocatedBytes = $alloc; machine = $machine }
    }
  }
  return $out
}

function Get-Ungated($baseline) {
  # Wildcard patterns (PowerShell -like) of benchmarks the gate reports but never compares or baselines.
  if ((Get-PropNames $baseline) -contains 'ungated' -and $null -ne $baseline.ungated) { return @($baseline.ungated) }
  return @()
}

function Test-Ungated([string]$name, [string[]]$patterns) {
  foreach ($p in $patterns) { if ($name -like $p) { return $true } }
  return $false
}

function Get-MinPatterns($baseline) {
  # Benchmarks compared on Statistics.Min instead of Mean. Rule: a bench with an external component
  # (ClickHouse on the same box for Insert*) is compared on the best iteration, because server-side
  # interference (merges, index builds, other load) only ever adds time, so the minimum is the stable
  # estimator of the client's own cost. Benches with no external component (Tokenise*) stay on Mean.
  if ((Get-PropNames $baseline) -contains 'useMin' -and $null -ne $baseline.useMin) { return @($baseline.useMin) }
  return @()
}

function Format-Alloc($bytes) {
  if ($null -eq $bytes) { return 'n/a' }
  return ('{0:N0} B' -f [double]$bytes)
}

function Compare-Benchmarks($baseline, $results, [double]$tol, [bool]$partial) {
  # Returns @{ rows = [list of row objects]; failures = [list of strings] }
  $rows = New-Object System.Collections.Generic.List[object]
  $failures = New-Object System.Collections.Generic.List[string]
  $baseNames = Get-PropNames $baseline.benchmarks
  $ungated = Get-Ungated $baseline
  $minPatterns = Get-MinPatterns $baseline

  foreach ($name in $results.Keys) {
    $r = $results[$name]
    $stat = 'mean'
    if (Test-Ungated $name $minPatterns) { $stat = 'min' }
    $nowVal = $r.meanNs
    if ($stat -eq 'min') { $nowVal = $r.minNs }
    if (Test-Ungated $name $ungated) {
      # Reported, never gated: EndToEnd is run by hand and its numbers go in the description of the change, not the baseline.
      $rows.Add([pscustomobject]@{ Bench = $name; Stat = $stat; Baseline = '-'; Now = ('{0:N1} ns' -f $nowVal); Delta = '-'; Alloc = (Format-Alloc $r.allocatedBytes); Status = 'ungated' })
      continue
    }
    if ($baseNames -notcontains $name) {
      $failures.Add("NEW     $name : no baseline entry. Add it deliberately with -UpdateBaseline and commit with URLOG_BASELINE_UPDATE=1.")
      $rows.Add([pscustomobject]@{ Bench = $name; Stat = $stat; Baseline = '-'; Now = ('{0:N1} ns' -f $nowVal); Delta = 'new'; Alloc = (Format-Alloc $r.allocatedBytes); Status = 'NEW' })
      continue
    }
    $b = $baseline.benchmarks.$name
    $baseVal = $null
    if ($stat -eq 'min') {
      if ((Get-PropNames $b) -contains 'minNs' -and $null -ne $b.minNs) { $baseVal = [double]$b.minNs }
    }
    else {
      $baseVal = [double]$b.meanNs
    }
    if ($null -eq $baseVal -or $null -eq $nowVal) {
      $failures.Add("NOSTAT  $name : compared on '$stat' but the baseline entry or the result has no such statistic. Re-take the baseline with -UpdateBaseline.")
      $rows.Add([pscustomobject]@{ Bench = $name; Stat = $stat; Baseline = '?'; Now = '?'; Delta = '?'; Alloc = (Format-Alloc $r.allocatedBytes); Status = 'NOSTAT' })
      continue
    }
    $delta = 0.0
    if ($baseVal -gt 0) { $delta = ($nowVal - $baseVal) / $baseVal }
    # A provisional entry (a bench still on a stub tokeniser) records a number but is not a speed gate:
    # the real tokeniser will be slower than a stub that only finds delimiters. The allocation rule still applies.
    $isProvisional = ((Get-PropNames $b) -contains 'provisional') -and ($b.provisional -eq $true)
    $status = 'ok'
    if ($isProvisional) {
      $status = 'provisional'
    }
    elseif ($delta -gt $tol) {
      $status = 'REGRESSION'
      $failures.Add(("SLOWER  {0} : {5} {1:N1} ns -> {2:N1} ns ({3:+0.0%;-0.0%}) exceeds {4:P0}" -f $name, $baseVal, $nowVal, $delta, $tol, $stat))
    }
    $baseAlloc = $null
    if ((Get-PropNames $b) -contains 'allocatedBytes') { $baseAlloc = $b.allocatedBytes }
    if ($null -ne $baseAlloc -and [double]$baseAlloc -eq 0 -and $null -ne $r.allocatedBytes -and $r.allocatedBytes -gt 0) {
      $status = 'ALLOC'
      $failures.Add(("ALLOC   {0} : baseline allocates 0 B/op, now {1} B/op (zero per-event allocation is non-negotiable)" -f $name, $r.allocatedBytes))
    }
    $rows.Add([pscustomobject]@{ Bench = $name; Stat = $stat; Baseline = ('{0:N1} ns' -f $baseVal); Now = ('{0:N1} ns' -f $nowVal); Delta = ('{0:+0.0%;-0.0%}' -f $delta); Alloc = (Format-Alloc $r.allocatedBytes); Status = $status })
  }

  if (-not $partial) {
    foreach ($name in $baseNames) {
      if (-not $results.Contains($name)) {
        $failures.Add("MISSING $name : in baseline but not measured. Run the full suite or update the baseline deliberately.")
      }
    }
  }
  return @{ rows = $rows; failures = $failures }
}

function Write-Table($rows) {
  if ($rows.Count -eq 0) { Write-Host 'bench gate: nothing measured.'; return }
  Write-Host ''
  Write-Host '| Bench | Stat | Baseline | Now | Delta | Alloc/op | Status |'
  Write-Host '|---|---|---|---|---|---|---|'
  foreach ($r in $rows) { Write-Host ("| {0} | {1} | {2} | {3} | {4} | {5} | {6} |" -f $r.Bench, $r.Stat, $r.Baseline, $r.Now, $r.Delta, $r.Alloc, $r.Status) }
  Write-Host ''
}

function Resolve-Tolerance($baseline, [double]$override) {
  $t = $script:MaxTolerance
  if ((Get-PropNames $baseline) -contains 'tolerance' -and $null -ne $baseline.tolerance) { $t = [double]$baseline.tolerance }
  if ($override -ge 0) { $t = $override }
  if ($t -gt $script:MaxTolerance) {
    throw ("tolerance {0} exceeds the hard ceiling {1}; the gate cannot be weakened." -f $t, $script:MaxTolerance)
  }
  return $t
}

function Update-BaselineFile($baseline, $results, [string]$path, [bool]$partial, [bool]$provisional) {
  $bench = [ordered]@{}
  $ungated = Get-Ungated $baseline
  $minPatterns = Get-MinPatterns $baseline
  if ($partial) {
    # Keep what was not measured, except entries that are now ungated: ungated means not in the baseline.
    foreach ($p in $baseline.benchmarks.PSObject.Properties) { if (-not (Test-Ungated $p.Name $ungated)) { $bench[$p.Name] = $p.Value } }
  }
  $machine = $null
  foreach ($name in $results.Keys) {
    $r = $results[$name]
    if (Test-Ungated $name $ungated) { continue }
    $entry = [ordered]@{ meanNs = [math]::Round($r.meanNs, 3); allocatedBytes = $r.allocatedBytes }
    if ($null -ne $r.minNs) { $entry['minNs'] = [math]::Round($r.minNs, 3) }
    if ($provisional) { $entry['provisional'] = $true }   # written without -Provisional = promoted to a real gate
    $bench[$name] = $entry
    if ($null -ne $r.machine) { $machine = $r.machine }
  }
  $tol = $script:MaxTolerance
  if ((Get-PropNames $baseline) -contains 'tolerance' -and $null -ne $baseline.tolerance) { $tol = [double]$baseline.tolerance }
  $doc = [ordered]@{
    schema     = 1
    tolerance  = $tol
    updated    = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')
    machine    = $machine
    ungated    = @($ungated)
    useMin     = @($minPatterns)
    benchmarks = $bench
  }
  $json = $doc | ConvertTo-Json -Depth 6
  [System.IO.File]::WriteAllText($path, ($json -replace "`r`n", "`n") + "`n", (New-Object System.Text.UTF8Encoding($false)))
  Write-Host ("baseline written: {0} ({1} benchmarks)" -f $path, $bench.Count)
}

function Invoke-Gate([string]$resultsDir, [string]$baselinePath, [bool]$partial, [bool]$update, [double]$tolOverride, [bool]$provisional = $false) {
  $baseline = Read-Baseline $baselinePath
  $tol = Resolve-Tolerance $baseline $tolOverride
  $results = Read-Results $resultsDir
  if ($update) {
    Update-BaselineFile $baseline $results $baselinePath $partial $provisional
    return 0
  }
  $cmp = Compare-Benchmarks $baseline $results $tol $partial
  Write-Table $cmp.rows
  if ($cmp.failures.Count -gt 0) {
    foreach ($f in $cmp.failures) { Write-Host ("FAIL {0}" -f $f) }
    Write-Host ("bench gate: FAILED ({0} problem(s), tolerance {1:P0})" -f $cmp.failures.Count, $tol)
    return 1
  }
  Write-Host ("bench gate: OK ({0} benchmark(s) compared, tolerance {1:P0})" -f $results.Count, $tol)
  return 0
}

function New-SyntheticReport([string]$dir, [string]$type, [hashtable[]]$benches) {
  $list = @()
  foreach ($b in $benches) {
    $min = $b.mean
    if ($b.ContainsKey('min')) { $min = $b.min }
    $list += [ordered]@{
      FullName   = $b.name
      Statistics = @{ Mean = $b.mean; Min = $min }
      Memory     = @{ BytesAllocatedPerOperation = $b.alloc }
    }
  }
  $doc = [ordered]@{ Title = $type; HostEnvironmentInfo = @{ ProcessorName = 'synthetic'; RuntimeVersion = 'synthetic'; OsVersion = 'synthetic' }; Benchmarks = $list }
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  ($doc | ConvertTo-Json -Depth 6) | Set-Content -Path (Join-Path $dir "$type-report-full.json") -Encoding utf8
}

function Invoke-SelfTest {
  $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("urlog-bench-selftest-" + [guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Force -Path $tmp | Out-Null
  $script:stPass = 0
  $script:stFail = 0
  function Assert-Exit([string]$case, [int]$expected, [int]$actual) {
    if ($expected -eq $actual) { Write-Host ("  pass  {0}" -f $case); $script:stPass++ }
    else { Write-Host ("  FAIL  {0}: expected exit {1}, got {2}" -f $case, $expected, $actual); $script:stFail++ }
  }
  $bl0 = '{ "schema": 1, "tolerance": 0.10, "benchmarks": {} }'
  $blA = '{ "schema": 1, "tolerance": 0.10, "benchmarks": { "A.Run": { "meanNs": 1000.0, "allocatedBytes": 0 } } }'
  $blAB = '{ "schema": 1, "tolerance": 0.10, "benchmarks": { "A.Run": { "meanNs": 1000.0, "allocatedBytes": 0 }, "B.Run": { "meanNs": 1000.0, "allocatedBytes": 512 } } }'
  $blAB0 = '{ "schema": 1, "tolerance": 0.10, "benchmarks": { "A.Run": { "meanNs": 1000.0, "allocatedBytes": 0 }, "B.Run": { "meanNs": 1000.0, "allocatedBytes": 0 } } }'
  try {
    # 1: empty harness, empty baseline -> green
    $d = Join-Path $tmp 'empty'; New-Item -ItemType Directory -Force -Path (Join-Path $d 'results') | Out-Null
    $bl = Join-Path $d 'baseline.json'; $bl0 | Set-Content $bl -Encoding utf8
    Assert-Exit 'empty harness is green' 0 (Invoke-Gate (Join-Path $d 'results') $bl $false $false -1)

    # 2: +9.9 % and exactly +10 % -> green
    $d = Join-Path $tmp 'ok'; New-Item -ItemType Directory -Force -Path $d | Out-Null
    $bl = Join-Path $d 'baseline.json'; $blAB | Set-Content $bl -Encoding utf8
    New-SyntheticReport (Join-Path $d 'results') 'A' @(@{ name = 'A.Run'; mean = 1099.0; alloc = 0 }, @{ name = 'B.Run'; mean = 1100.0; alloc = 512 })
    Assert-Exit 'within and at tolerance passes' 0 (Invoke-Gate (Join-Path $d 'results') $bl $false $false -1)

    # 3: +10.5 % -> fail
    $d = Join-Path $tmp 'slow'; New-Item -ItemType Directory -Force -Path $d | Out-Null
    $bl = Join-Path $d 'baseline.json'; $blA | Set-Content $bl -Encoding utf8
    New-SyntheticReport (Join-Path $d 'results') 'A' @(@{ name = 'A.Run'; mean = 1105.0; alloc = 0 })
    Assert-Exit 'regression over tolerance fails' 1 (Invoke-Gate (Join-Path $d 'results') $bl $false $false -1)

    # 4: allocation appears where baseline is 0 B -> fail even though faster
    $d = Join-Path $tmp 'alloc'; New-Item -ItemType Directory -Force -Path $d | Out-Null
    $bl = Join-Path $d 'baseline.json'; $blA | Set-Content $bl -Encoding utf8
    New-SyntheticReport (Join-Path $d 'results') 'A' @(@{ name = 'A.Run'; mean = 900.0; alloc = 24 })
    Assert-Exit 'new per-op allocation fails' 1 (Invoke-Gate (Join-Path $d 'results') $bl $false $false -1)

    # 5: new benchmark without baseline -> fail; after -UpdateBaseline -> green
    $d = Join-Path $tmp 'new'; New-Item -ItemType Directory -Force -Path $d | Out-Null
    $bl = Join-Path $d 'baseline.json'; $bl0 | Set-Content $bl -Encoding utf8
    New-SyntheticReport (Join-Path $d 'results') 'A' @(@{ name = 'A.Run'; mean = 1000.0; alloc = 0 })
    Assert-Exit 'unbaselined benchmark fails' 1 (Invoke-Gate (Join-Path $d 'results') $bl $false $false -1)
    Assert-Exit 'update baseline succeeds' 0 (Invoke-Gate (Join-Path $d 'results') $bl $false $true -1)
    Assert-Exit 'baselined benchmark passes' 0 (Invoke-Gate (Join-Path $d 'results') $bl $false $false -1)

    # 6: baseline entry not measured -> fail in full mode, green with -Partial
    $d = Join-Path $tmp 'missing'; New-Item -ItemType Directory -Force -Path $d | Out-Null
    $bl = Join-Path $d 'baseline.json'; $blAB0 | Set-Content $bl -Encoding utf8
    New-SyntheticReport (Join-Path $d 'results') 'A' @(@{ name = 'A.Run'; mean = 1000.0; alloc = 0 })
    Assert-Exit 'missing benchmark fails in full mode' 1 (Invoke-Gate (Join-Path $d 'results') $bl $false $false -1)
    Assert-Exit 'missing benchmark allowed in partial mode' 0 (Invoke-Gate (Join-Path $d 'results') $bl $true $false -1)

    # 7: tolerance cannot be weakened
    $d = Join-Path $tmp 'weak'; New-Item -ItemType Directory -Force -Path $d | Out-Null
    $bl = Join-Path $d 'baseline.json'
    '{ "schema": 1, "tolerance": 0.25, "benchmarks": {} }' | Set-Content $bl -Encoding utf8
    $threw = 0
    try { Invoke-Gate (Join-Path $d 'results') $bl $false $false -1 | Out-Null } catch { $threw = 1 }
    Assert-Exit 'tolerance above 10 % is refused' 1 $threw
    $threw = 0
    try { Invoke-Gate (Join-Path $d 'results') (Join-Path $tmp 'empty/baseline.json') $false $false 0.2 | Out-Null } catch { $threw = 1 }
    Assert-Exit '-Tolerance above 10 % is refused' 1 $threw

    # 8: provisional entry ignores speed, keeps the allocation rule, and is promoted by a plain -UpdateBaseline
    $d = Join-Path $tmp 'prov'; New-Item -ItemType Directory -Force -Path $d | Out-Null
    $bl = Join-Path $d 'baseline.json'; $bl0 | Set-Content $bl -Encoding utf8
    New-SyntheticReport (Join-Path $d 'results') 'A' @(@{ name = 'A.Run'; mean = 1000.0; alloc = 0 })
    Assert-Exit 'provisional baseline written' 0 (Invoke-Gate (Join-Path $d 'results') $bl $false $true -1 $true)
    $written = Get-Content -Raw $bl | ConvertFrom-Json
    Assert-Exit 'provisional flag present' 1 ([int]($written.benchmarks.'A.Run'.provisional -eq $true))
    New-SyntheticReport (Join-Path $d 'results') 'A' @(@{ name = 'A.Run'; mean = 3000.0; alloc = 0 })
    Assert-Exit 'provisional entry: 3x slower still passes' 0 (Invoke-Gate (Join-Path $d 'results') $bl $false $false -1)
    New-SyntheticReport (Join-Path $d 'results') 'A' @(@{ name = 'A.Run'; mean = 900.0; alloc = 16 })
    Assert-Exit 'provisional entry: new allocation still fails' 1 (Invoke-Gate (Join-Path $d 'results') $bl $false $false -1)
    New-SyntheticReport (Join-Path $d 'results') 'A' @(@{ name = 'A.Run'; mean = 2000.0; alloc = 0 })
    Assert-Exit 'promotion with plain -UpdateBaseline' 0 (Invoke-Gate (Join-Path $d 'results') $bl $true $true -1)
    $written = Get-Content -Raw $bl | ConvertFrom-Json
    Assert-Exit 'promoted entry has no provisional flag' 0 ([int]((Get-PropNames $written.benchmarks.'A.Run') -contains 'provisional'))
    New-SyntheticReport (Join-Path $d 'results') 'A' @(@{ name = 'A.Run'; mean = 2300.0; alloc = 0 })
    Assert-Exit 'promoted entry gates speed again' 1 (Invoke-Gate (Join-Path $d 'results') $bl $false $false -1)

    # 9: ungated patterns are reported, never compared, never written
    $d = Join-Path $tmp 'ungated'; New-Item -ItemType Directory -Force -Path $d | Out-Null
    $bl = Join-Path $d 'baseline.json'
    '{ "schema": 1, "tolerance": 0.10, "ungated": ["X.EndToEnd.*"], "benchmarks": {} }' | Set-Content $bl -Encoding utf8
    New-SyntheticReport (Join-Path $d 'results') 'E' @(@{ name = 'X.EndToEnd.Run'; mean = 1000.0; alloc = 99 })
    Assert-Exit 'ungated result passes without a baseline entry' 0 (Invoke-Gate (Join-Path $d 'results') $bl $false $false -1)
    Assert-Exit 'update ignores ungated' 0 (Invoke-Gate (Join-Path $d 'results') $bl $true $true -1)
    $written = Get-Content -Raw $bl | ConvertFrom-Json
    Assert-Exit 'ungated entry not written' 0 ([int]((Get-PropNames $written.benchmarks) -contains 'X.EndToEnd.Run'))
    Assert-Exit 'ungated list preserved' 1 ([int](@($written.ungated) -contains 'X.EndToEnd.*'))

    # 10: useMin patterns compare Statistics.Min; Mean noise is ignored, Min regressions are not
    $d = Join-Path $tmp 'usemin'; New-Item -ItemType Directory -Force -Path $d | Out-Null
    $bl = Join-Path $d 'baseline.json'
    '{ "schema": 1, "tolerance": 0.10, "useMin": ["X.Insert.*"], "benchmarks": {} }' | Set-Content $bl -Encoding utf8
    New-SyntheticReport (Join-Path $d 'results') 'I' @(@{ name = 'X.Insert.Tcp'; mean = 1200.0; min = 1000.0; alloc = 5 })
    Assert-Exit 'useMin baseline written' 0 (Invoke-Gate (Join-Path $d 'results') $bl $false $true -1)
    $written = Get-Content -Raw $bl | ConvertFrom-Json
    Assert-Exit 'minNs recorded' 1 ([int]($written.benchmarks.'X.Insert.Tcp'.minNs -eq 1000.0))
    New-SyntheticReport (Join-Path $d 'results') 'I' @(@{ name = 'X.Insert.Tcp'; mean = 1600.0; min = 1050.0; alloc = 5 })
    Assert-Exit 'useMin: mean +33 % but min +5 % passes' 0 (Invoke-Gate (Join-Path $d 'results') $bl $false $false -1)
    New-SyntheticReport (Join-Path $d 'results') 'I' @(@{ name = 'X.Insert.Tcp'; mean = 1150.0; min = 1120.0; alloc = 5 })
    Assert-Exit 'useMin: mean -4 % but min +12 % fails' 1 (Invoke-Gate (Join-Path $d 'results') $bl $false $false -1)
    '{ "schema": 1, "tolerance": 0.10, "useMin": ["X.Insert.*"], "benchmarks": { "X.Insert.Tcp": { "meanNs": 1200.0, "allocatedBytes": 5 } } }' | Set-Content $bl -Encoding utf8
    Assert-Exit 'useMin: baseline without minNs fails until re-taken' 1 (Invoke-Gate (Join-Path $d 'results') $bl $false $false -1)

    # 11: an entry that becomes ungated is dropped from the baseline on the next partial update
    $d = Join-Path $tmp 'drop'; New-Item -ItemType Directory -Force -Path $d | Out-Null
    $bl = Join-Path $d 'baseline.json'
    '{ "schema": 1, "tolerance": 0.10, "ungated": ["B.*"], "benchmarks": { "A.Run": { "meanNs": 1000.0, "allocatedBytes": 0 }, "B.Run": { "meanNs": 1000.0, "allocatedBytes": 0 } } }' | Set-Content $bl -Encoding utf8
    New-SyntheticReport (Join-Path $d 'results') 'A' @(@{ name = 'A.Run'; mean = 1000.0; alloc = 0 })
    Assert-Exit 'partial update with ungated entry present' 0 (Invoke-Gate (Join-Path $d 'results') $bl $true $true -1)
    $written = Get-Content -Raw $bl | ConvertFrom-Json
    Assert-Exit 'ungated entry dropped from baseline' 0 ([int]((Get-PropNames $written.benchmarks) -contains 'B.Run'))
  }
  finally {
    Remove-Item -Recurse -Force -Path $tmp -ErrorAction SilentlyContinue
  }
  Write-Host ("self-test: {0} passed, {1} failed" -f $script:stPass, $script:stFail)
  if ($script:stFail -gt 0) { return 1 }
  return 0
}

if ($SelfTest) {
  exit (Invoke-SelfTest)
}
exit (Invoke-Gate $ResultsDir $Baseline $Partial.IsPresent $UpdateBaseline.IsPresent $Tolerance $Provisional.IsPresent)
