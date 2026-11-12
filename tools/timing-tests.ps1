#requires -Version 5.1
<#
.SYNOPSIS
  Runs the explicit wall-clock tests (Category = Timing).

.DESCRIPTION
  A test that compares two measured durations says as much about the box as about the code, so these are marked
  explicit: dotnet test, the pre-commit hook and tools/pre-merge.ps1 skip them, and the default run asserts the
  mechanism instead (for the login: one Argon2 verification at the pinned parameters on every path). Run this
  alone on a quiet box after a change to the login path or the password hasher.
#>
[CmdletBinding()]
param(
  [int]$Repeat = 1
)
$ErrorActionPreference = 'Stop'
$root = (& git rev-parse --show-toplevel).Trim()
Set-Location $root

& dotnet build tests/Api.Tests/Api.Tests.csproj -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "timing-tests: build failed" }
for ($i = 1; $i -le $Repeat; $i++) {
  & dotnet test tests/Api.Tests/Api.Tests.csproj -c Release --no-build --nologo -- --filter-trait 'Category=Timing' --explicit only
  if ($LASTEXITCODE -ne 0) { throw ("timing-tests: failed on pass {0} of {1}" -f $i, $Repeat) }
}
Write-Host ("timing-tests: OK ({0} pass(es))" -f $Repeat)
