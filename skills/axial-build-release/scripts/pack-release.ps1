<#
.SYNOPSIS
  One-click Release pack for AxialSqlTools (VSIX + output ZIP).

.DESCRIPTION
  Builds AxialSqlTools.vsix (Release|AnyCPU) and ensures a ZIP beside it:
  AxialSqlTools/bin/<Config>/AxialSqlTools_SSMS22_<version>.zip

  Handles common local-machine quirks:
  - MSBuild from full VS (VSSDK) or BuildTools fallback
  - SSMS install on C: or D: (temp-remaps csproj HintPaths, always restores)
  - EnvDTE vs Microsoft.VisualStudio.Interop CS0433 (temp-removes EnvDTE refs when needed)
  - VSToolsPath from Microsoft.VSSDK.BuildTools NuGet package

.EXAMPLE
  .\skills\axial-build-release\scripts\pack-release.ps1

.EXAMPLE
  .\skills\axial-build-release\scripts\pack-release.ps1 -Configuration Release
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Platform = 'AnyCPU',
    [switch]$SkipRestore
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$scriptDir = $PSScriptRoot
$repoRoot = (Resolve-Path (Join-Path $scriptDir '..\..\..')).Path
$projectDir = Join-Path $repoRoot 'AxialSqlTools'
$projectPath = Join-Path $projectDir 'AxialSqlTools.csproj'
$manifestPath = Join-Path $projectDir 'source.extension.vsixmanifest'
$vsixPath = Join-Path $projectDir "bin\$Configuration\AxialSqlTools.vsix"
$csprojBackupPath = Join-Path $projectDir ('AxialSqlTools.csproj.bak-pack-' + [guid]::NewGuid().ToString('N'))
$csprojDirty = $false
$defaultCsprojSsmsRoot = 'C:\Program Files\Microsoft SQL Server Management Studio 22'

function Write-Step([string]$Message) {
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Get-VsWherePath {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) {
        throw "vswhere.exe not found: $vswhere"
    }
    return $vswhere
}

function Get-MSBuildInfo {
    $vswhere = Get-VsWherePath
    $withVssdk = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VSSDK -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    if ($withVssdk -and (Test-Path $withVssdk)) {
        return [pscustomobject]@{ Path = $withVssdk; HasVssdkComponent = $true }
    }
    $any = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    if ($any -and (Test-Path $any)) {
        return [pscustomobject]@{ Path = $any; HasVssdkComponent = $false }
    }
    throw 'MSBuild not found. Install Visual Studio or Build Tools with MSBuild.'
}

function Get-SsmsRoot {
    $candidates = @(
        'C:\Program Files\Microsoft SQL Server Management Studio 22',
        'D:\Program Files\Microsoft SQL Server Management Studio 22',
        (Join-Path $env:ProgramFiles 'Microsoft SQL Server Management Studio 22')
    ) | Select-Object -Unique
    foreach ($root in $candidates) {
        $smo = Join-Path $root 'Release\Common7\IDE\Microsoft.SqlServer.Smo.dll'
        if (Test-Path $smo) {
            return $root
        }
    }
    throw 'SSMS 22 not found under C:\ or D:\ Program Files. Install SSMS 22 first.'
}

function Get-PackageVersion {
    [xml]$xml = Get-Content -LiteralPath $manifestPath
    $version = $xml.PackageManifest.Metadata.Identity.Version
    if ([string]::IsNullOrWhiteSpace($version)) {
        throw "Cannot read Identity Version from $manifestPath"
    }
    return $version
}

function Get-OutputZipPath([string]$Version) {
    return Join-Path $projectDir "bin\$Configuration\AxialSqlTools_SSMS22_$Version.zip"
}

function Ensure-OutputZip([string]$Version) {
    $zipPath = Get-OutputZipPath -Version $Version
    if (Test-Path $zipPath) {
        Write-Host "Output ZIP ready: $zipPath"
        return $zipPath
    }
    if (-not (Test-Path $vsixPath)) {
        throw "VSIX missing, cannot create ZIP: $vsixPath"
    }
    Write-Host "PostBuild ZIP missing; creating: $zipPath"
    Compress-Archive -Path $vsixPath -DestinationPath $zipPath -Force
    return $zipPath
}

function Expand-MsBuildPath([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return $Value }
    $expanded = $Value.Trim()
    $expanded = $expanded.Replace('$(UserProfile)', $env:USERPROFILE)
    $expanded = $expanded.Replace('$(USERPROFILE)', $env:USERPROFILE)
    $expanded = $expanded.Replace('$(HOME)', $env:USERPROFILE)
    $expanded = [Environment]::ExpandEnvironmentVariables($expanded)
    return $expanded
}

function Find-VsSdkToolsPath {
    $searchRoots = New-Object System.Collections.Generic.List[string]
    $nugetProps = Join-Path $projectDir 'obj\AxialSqlTools.csproj.nuget.g.props'
    if (Test-Path $nugetProps) {
        [xml]$propsXml = Get-Content -LiteralPath $nugetProps
        $ns = New-Object System.Xml.XmlNamespaceManager($propsXml.NameTable)
        $ns.AddNamespace('m', 'http://schemas.microsoft.com/developer/msbuild/2003')
        $pkgNode = $propsXml.SelectSingleNode('//m:PkgMicrosoft_VSSDK_BuildTools', $ns)
        if ($pkgNode -and $pkgNode.InnerText) {
            $searchRoots.Add((Expand-MsBuildPath $pkgNode.InnerText))
        }
        $rootNode = $propsXml.SelectSingleNode('//m:NuGetPackageRoot', $ns)
        if ($rootNode -and $rootNode.InnerText) {
            $nugetRoot = (Expand-MsBuildPath $rootNode.InnerText).TrimEnd('\')
            $searchRoots.Add((Join-Path $nugetRoot 'microsoft.vssdk.buildtools'))
        }
    }
    $searchRoots.Add((Join-Path $env:USERPROFILE '.nuget\packages\microsoft.vssdk.buildtools'))
    $targets = @()
    foreach ($root in ($searchRoots | Select-Object -Unique)) {
        if (-not (Test-Path $root)) { continue }
        if (Test-Path (Join-Path $root 'tools\vssdk\Microsoft.VsSDK.targets')) {
            return (Join-Path $root 'tools')
        }
        $targets += Get-ChildItem -Path $root -Directory -ErrorAction SilentlyContinue |
            ForEach-Object { Join-Path $_.FullName 'tools\vssdk\Microsoft.VsSDK.targets' } |
            Where-Object { Test-Path $_ }
    }
    if ($targets.Count -eq 0) {
        return $null
    }
    $latest = $targets | Sort-Object -Descending | Select-Object -First 1
    return (Split-Path (Split-Path $latest -Parent) -Parent)
}

function Get-NuGetPackageRoot {
    $nugetProps = Join-Path $projectDir 'obj\AxialSqlTools.csproj.nuget.g.props'
    if (Test-Path $nugetProps) {
        [xml]$propsXml = Get-Content -LiteralPath $nugetProps
        $ns = New-Object System.Xml.XmlNamespaceManager($propsXml.NameTable)
        $ns.AddNamespace('m', 'http://schemas.microsoft.com/developer/msbuild/2003')
        $rootNode = $propsXml.SelectSingleNode('//m:NuGetPackageRoot', $ns)
        if ($rootNode -and $rootNode.InnerText) {
            $root = Expand-MsBuildPath $rootNode.InnerText
            if (-not $root.EndsWith('\')) { $root += '\' }
            return $root
        }
    }
    return (Join-Path $env:USERPROFILE '.nuget\packages\')
}

function Backup-ProjectFile {
    Copy-Item -LiteralPath $projectPath -Destination $csprojBackupPath -Force
    $script:csprojDirty = $true
}

function Restore-ProjectFile {
    if (-not $csprojDirty) { return }
    if (Test-Path $csprojBackupPath) {
        Copy-Item -LiteralPath $csprojBackupPath -Destination $projectPath -Force
        Remove-Item -LiteralPath $csprojBackupPath -Force -ErrorAction SilentlyContinue
        Write-Host "Restored csproj from backup."
    }
    $script:csprojDirty = $false
}

function Set-SsmsHintPaths([string]$SsmsRoot) {
    if ($SsmsRoot -eq $defaultCsprojSsmsRoot) {
        Write-Host "SSMS path matches csproj default: $SsmsRoot"
        return $false
    }
    Write-Host "Remapping SSMS HintPaths: '$defaultCsprojSsmsRoot' -> '$SsmsRoot'"
    if (-not $csprojDirty) { Backup-ProjectFile }
    $content = Get-Content -LiteralPath $projectPath -Raw
    $updated = $content.Replace($defaultCsprojSsmsRoot, $SsmsRoot)
    if ($updated -eq $content) {
        throw "Expected csproj to contain '$defaultCsprojSsmsRoot' but it was not found."
    }
    Set-Content -LiteralPath $projectPath -Value $updated -NoNewline
    return $true
}

function Remove-EnvDteReferences {
    Write-Host 'Temporarily removing EnvDTE/EnvDTE80 references to avoid CS0433 with Microsoft.VisualStudio.Interop.'
    if (-not $csprojDirty) { Backup-ProjectFile }
    [xml]$xml = Get-Content -LiteralPath $projectPath
    $ns = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
    $ns.AddNamespace('m', 'http://schemas.microsoft.com/developer/msbuild/2003')
    $removed = 0
    $refNodes = @($xml.SelectNodes('//m:Reference', $ns))
    foreach ($ref in $refNodes) {
        $include = $ref.GetAttribute('Include')
        if ($include -eq 'EnvDTE' -or $include -eq 'EnvDTE80' -or $include.StartsWith('EnvDTE,') -or $include.StartsWith('EnvDTE80,')) {
            [void]$ref.ParentNode.RemoveChild($ref)
            $removed++
        }
    }
    if ($removed -eq 0) {
        Write-Warning 'No EnvDTE references found to remove.'
        return
    }
    $xml.Save($projectPath)
    Write-Host "Removed $removed EnvDTE reference(s)."
}

function Assert-BuildPrerequisites {
    if (Test-Path $vsixPath) {
        try {
            Remove-Item -LiteralPath $vsixPath -Force
        }
        catch {
            $deadline = (Get-Date).AddSeconds(10)
            while ((Get-Date) -lt $deadline) {
                Start-Sleep -Milliseconds 250
                try {
                    Remove-Item -LiteralPath $vsixPath -Force
                    break
                }
                catch {
                }
            }
            if (Test-Path $vsixPath) {
                throw "Existing VSIX is locked: $vsixPath. Close SSMS/VSIXInstaller, then rerun."
            }
        }
    }
    if (Get-Process -Name 'VSIXInstaller' -ErrorAction SilentlyContinue) {
        Write-Warning 'VSIXInstaller process detected but output VSIX is not locked; continuing build.'
    }
}

function Invoke-MsBuild {
    param(
        [Parameter(Mandatory = $true)][string]$MsBuildPath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )
    Write-Host "MSBuild: $MsBuildPath"
    Write-Host ("Args: " + ($Arguments -join ' '))
    & $MsBuildPath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "MSBuild failed with exit code $LASTEXITCODE"
    }
}

try {
    if (-not (Test-Path $projectPath)) {
        throw "Project not found: $projectPath"
    }
    if (-not (Test-Path $manifestPath)) {
        throw "Manifest not found: $manifestPath"
    }

    $version = Get-PackageVersion
    Write-Step "Pack AxialSqlTools $version ($Configuration|$Platform)"
    Write-Host "Repo: $repoRoot"

    Write-Step 'Resolve tools and SSMS'
    $msbuild = Get-MSBuildInfo
    $ssmsRoot = Get-SsmsRoot
    Write-Host "MSBuild: $($msbuild.Path) (VSSDK component: $($msbuild.HasVssdkComponent))"
    Write-Host "SSMS: $ssmsRoot"

    Assert-BuildPrerequisites

    Write-Step 'Prepare project (temporary local fixes)'
    if (-not $msbuild.HasVssdkComponent) {
        Remove-EnvDteReferences
    }

    $msbuildArgsBase = @(
        $projectPath,
        "/p:Configuration=$Configuration",
        "/p:Platform=$Platform"
    )

    if (-not $SkipRestore) {
        Write-Step 'Restore'
        Invoke-MsBuild -MsBuildPath $msbuild.Path -Arguments ($msbuildArgsBase + @('/t:Restore'))
    }

    $extraProps = @()
    $vsSdkTools = Find-VsSdkToolsPath
    if ($vsSdkTools) {
        $vsSdkInstall = Join-Path $vsSdkTools 'vssdk'
        $vsSdkBin = Join-Path $vsSdkInstall 'bin'
        Write-Host "VSToolsPath: $vsSdkTools"
        Write-Host "VsSDKToolsPath: $vsSdkBin"
        $extraProps += "/p:VSToolsPath=$vsSdkTools"
        $extraProps += "/p:VsSDKInstall=$vsSdkInstall"
        $extraProps += "/p:VsSDKToolsPath=$vsSdkBin"
    }
    elseif (-not $msbuild.HasVssdkComponent) {
        throw 'Microsoft.VSSDK.BuildTools NuGet package not found after restore. Run restore with network, then retry.'
    }
    $nugetRoot = Get-NuGetPackageRoot
    Write-Host "NuGetPackageRoot: $nugetRoot"
    $extraProps += "/p:NuGetPackageRoot=$nugetRoot"
    $extraProps += "/p:SsmsRoot=$ssmsRoot"

    Write-Step 'Build Release VSIX'
    Invoke-MsBuild -MsBuildPath $msbuild.Path -Arguments ($msbuildArgsBase + $extraProps + @('/t:Build', '/m'))

    if (-not (Test-Path $vsixPath)) {
        throw "Build completed but VSIX not found: $vsixPath"
    }

    Write-Step 'Package ZIP'
    $zipPath = Ensure-OutputZip -Version $version
    $vsixItem = Get-Item -LiteralPath $vsixPath
    $zipItem = Get-Item -LiteralPath $zipPath

    Write-Step 'Done'
    Write-Host ("VSIX : {0} ({1:N0} bytes)" -f $vsixItem.FullName, $vsixItem.Length)
    Write-Host ("ZIP  : {0} ({1:N0} bytes)" -f $zipItem.FullName, $zipItem.Length)
    Write-Host ("Version: $version")
}
catch {
    Write-Host ""
    Write-Error $_.Exception.Message
    exit 1
}
finally {
    Restore-ProjectFile
}
