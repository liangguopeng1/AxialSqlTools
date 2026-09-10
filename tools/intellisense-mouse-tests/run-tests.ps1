<#
.SYNOPSIS
  Regression tests for "completion list mouse selection freezes the editor".

.DESCRIPTION
  Real WPF tests: compiles and drives the production class
  AxialSqlTools.IntelliSense.CompletionListWindow with a real popup, real layout and real hit-testing.
  Asserted behaviour:
    01 the item name cell hit target is a Run (ContentElement), not a Visual
    02 the naive VisualTreeHelper.GetParent walk throws on that hit target (pins the defect)
    03 production SelectItemUnderMouse handles a Run hit target without throwing and selects the item
    04 Visual hit targets (row padding / kind glyph) keep working
    05 the mouse commit is deferred until the routed event returns, and fires exactly once

  Self-contained: no NuGet restore and no main-project build required (NLog and VS SDK are shimmed
  under ProductionStubs/). Needs the .NET SDK with Windows Desktop.

.EXAMPLE
  .\tools\intellisense-mouse-tests\run-tests.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$projectPath = Join-Path $PSScriptRoot 'IntelliSenseMouseTests.csproj'
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
    throw 'dotnet SDK not found. Install the .NET SDK (with Windows Desktop) or add it to PATH.'
}

Write-Host '== IntelliSense mouse-selection tests =='
Write-Host "dotnet:  $dotnetPath"
Write-Host "project: $projectPath"

& $dotnetPath run --project $projectPath -c $Configuration
$exit = $LASTEXITCODE

if ($exit -ne 0) {
    throw "IntelliSense mouse-selection tests FAILED (exit $exit)."
}

Write-Host 'IntelliSense mouse-selection tests passed.'
