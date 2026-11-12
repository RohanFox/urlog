#requires -Version 5.1
<#
.SYNOPSIS
  Runs the explicit pipeline stress test: 2,000 runs of a parser that throws while the others work.

.DESCRIPTION
  The test (ParsePipelineFailureTests.StressAThrowingParserIsTheFailureReportedEveryTime) is marked explicit, so
  dotnet test, the pre-commit hook and tools/pre-merge.ps1 skip it. Run this once before merging a change to
  src/Core/Pipeline/ParsePipeline.cs. On the hand-off defect it was written for, 60 of 10,000 runs reported the
  wrong failure in this configuration, so a pass over 2,000 runs is not luck.
#>
[CmdletBinding()]
param(
  [int]$Repeat = 1
)
$ErrorActionPreference = 'Stop'
$root = (& git rev-parse --show-toplevel).Trim()
Set-Location $root

& dotnet build tests/Core.Tests/Core.Tests.csproj -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "stress-pipeline: build failed" }
for ($i = 1; $i -le $Repeat; $i++) {
  & dotnet test tests/Core.Tests/Core.Tests.csproj -c Release --no-build --nologo -- --filter-method '*StressAThrowingParser*' --explicit only
  if ($LASTEXITCODE -ne 0) { throw ("stress-pipeline: failed on pass {0} of {1}" -f $i, $Repeat) }
}
Write-Host ("stress-pipeline: OK ({0} pass(es) of 2,000 runs)" -f $Repeat)
