<#
.SYNOPSIS
  Regression tests for the completion matcher (scoring + highlight indices).

.DESCRIPTION
  Compiles and drives the real production class
  AxialSqlTools.IntelliSense.CompletionMatcher (pure functions, no catalog / connection / VS API).
  Asserted behaviour:
    01 reported case: in a WHERE clause, typing h / h_ must match CG_H_ID (its H_ID segment)
    08 single-char and underscore prefixes match any identifier segment (k_id / h_id / k_)
    04 no over-matching: no contains/fuzzy for one char, no segment match for object names,
       and h_ must not match HX_ID (the separator has to exist)
    05 the pre-existing rules still hold (fe / hid / h_id / cg_h / clause keywords)
    06 ranking: a column segment hit scores 100, in the same tier as plain keyword hits
    07 text helpers (GetLastSegment / UnbracketIdentifier / IsNumericOrIpPrefix)

  Self-contained: no NuGet restore and no main-project build required.

.EXAMPLE
  .\tools\intellisense-matcher-tests\run-tests.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$projectPath = Join-Path $PSScriptRoot 'IntelliSenseMatcherTests.csproj'
if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "Test project not found: $projectPath"
}

$dotnetPath = $null
$cmd = Get-Command dotnet -ErrorAction SilentlyContinue
if ($cmd) { $dotnetPath = $cmd.Source }

if (-not $dotnetPath -or -not (Test-Path -LiteralPath $dotnetPath)) {
    $candidates = @(
        (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'dotnet\dotnet.exe'),
        'C:\Program Files\dotnet\dotnet.exe',
        (Join-Path $env:DOTNET_ROOT 'dotnet.exe')
    )
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            $dotnetPath = $candidate
            break
        }
    }
}

if (-not $dotnetPath) {
    throw 'dotnet SDK not found. Install the .NET SDK or add it to PATH.'
}

Write-Host '== IntelliSense matcher tests =='
Write-Host "dotnet:  $dotnetPath"
Write-Host "project: $projectPath"

& $dotnetPath run --project $projectPath -c $Configuration
$exit = $LASTEXITCODE

if ($exit -ne 0) {
    throw "IntelliSense matcher tests FAILED (exit $exit)."
}

Write-Host 'IntelliSense matcher tests passed.'
