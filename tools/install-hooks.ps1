#requires -Version 5.1
<#
.SYNOPSIS
  Point git at the repository's versioned hooks (.githooks). Run once per clone.
#>
$ErrorActionPreference = 'Stop'
$root = (& git rev-parse --show-toplevel).Trim()
if (-not $root) { throw 'not inside a git repository' }
& git -C $root config core.hooksPath .githooks
if ($LASTEXITCODE -ne 0) { throw 'git config failed' }
foreach ($name in 'pre-commit', 'commit-msg') {
  $hook = Join-Path $root ".githooks/$name"
  if (-not (Test-Path $hook)) { throw "missing $hook" }
}
Write-Host "hooks installed: core.hooksPath=.githooks"
Write-Host "  pre-commit -> build -warnaserror, unit tests, Tokenise bench for touched sources"
Write-Host "  commit-msg -> subject form and length"
Write-Host "  full gate  -> powershell -File tools/pre-merge.ps1"
