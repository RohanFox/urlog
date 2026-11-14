#requires -Version 5.1
<#
.SYNOPSIS
  Runs the explicit wall-clock tests (Category = Timing).

.DESCRIPTION
  A test that asserts a measured duration says as much about the box as about the code, so these are marked
  explicit: dotnet test, the pre-commit hook and tools/pre-merge.ps1 skip them, and the default run asserts the
  mechanism instead:
  - tests/Api.Tests: the login answers a known and an unknown name in the same time (the default run asserts
    one Argon2 verification at the pinned parameters on every path).
  - tests/Core.Tests: one verification costs between 50 ms and a second (the default run asserts the
    parameters, the salt and tag lengths, and that a stored hash carries them).
  - tests/Metadata.Tests: an empty metadata file migrates in under 2 s, and a million audit rows verify in
    under 10 s (the default run does both and asserts the results).
  - tests/Sources.Tests: the detection corpus is detected in under 2 s, no file in more than 50 ms (the default
    run asserts every answer; the cost is the DetectFormat benchmark).
  Run this alone on a quiet box after a change to the login path, the password hasher or its library, the
  migrations, the audit verifier or the detector.
#>
[CmdletBinding()]
param(
  [int]$Repeat = 1
)
$ErrorActionPreference = 'Stop'
$root = (& git rev-parse --show-toplevel).Trim()
Set-Location $root

$projects = @(
  'tests/Core.Tests/Core.Tests.csproj',
  'tests/Api.Tests/Api.Tests.csproj',
  'tests/Metadata.Tests/Metadata.Tests.csproj',
  'tests/Sources.Tests/Sources.Tests.csproj'
)
foreach ($project in $projects) {
  & dotnet build $project -c Release --nologo -v quiet
  if ($LASTEXITCODE -ne 0) { throw ("timing-tests: build failed: {0}" -f $project) }
}
for ($i = 1; $i -le $Repeat; $i++) {
  # One project after the other: two timed tests at once would measure each other.
  foreach ($project in $projects) {
    & dotnet test $project -c Release --no-build --nologo -- --filter-trait 'Category=Timing' --explicit only
    if ($LASTEXITCODE -ne 0) { throw ("timing-tests: {0} failed on pass {1} of {2}" -f $project, $i, $Repeat) }
  }
}
Write-Host ("timing-tests: OK ({0} pass(es))" -f $Repeat)
