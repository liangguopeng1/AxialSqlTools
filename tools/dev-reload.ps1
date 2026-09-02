<#
.SYNOPSIS
  Dev one-shot: build, close SSMS, install or uninstall the extension, optionally relaunch SSMS.

.DESCRIPTION
  Auto-detects SSMS 22 on C: or D: drive.
  Default -Action Reload: Release build → close SSMS → overwrite install → relaunch.
  -Action Install: same install path (build unless -SkipBuild).
  -Action Uninstall: close SSMS → VSIXInstaller /uninstall:AxialSqlTools → delete leftover folders.
  Install prefers direct overwrite of the folder SSMS actually loads (per-user AppData first);
  otherwise VSIXInstaller /quiet /force /shutdownprocesses.

  Uninstall command (script prints the resolved path):
    & "<SSMS>\Release\Common7\IDE\VSIXInstaller.exe" /quiet /uninstall:AxialSqlTools

  Install command:
    & "<SSMS>\Release\Common7\IDE\VSIXInstaller.exe" /quiet /force /shutdownprocesses "<repo>\AxialSqlTools\bin\Release\AxialSqlTools.vsix"

.EXAMPLE
  .\tools\dev-reload.ps1

.EXAMPLE
  .\tools\dev-reload.ps1 -Action Uninstall

.EXAMPLE
  .\tools\dev-reload.ps1 -Action Install

.EXAMPLE
  .\tools\dev-reload.ps1 -Action Install -SkipBuild -NoLaunch

.EXAMPLE
  .\tools\dev-reload.ps1 -Configuration Debug
#>
[CmdletBinding()]
param(
    [ValidateSet('Reload', 'Install', 'Uninstall')]
    [string]$Action = 'Reload',

    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [switch]$SkipBuild,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$packScript = Join-Path $repoRoot 'skills\axial-build-release\scripts\pack-release.ps1'
$projectDir = Join-Path $repoRoot 'AxialSqlTools'
$vsixPath = Join-Path $projectDir "bin\$Configuration\AxialSqlTools.vsix"
$builtDllPath = Join-Path $projectDir "bin\$Configuration\AxialSqlTools.dll"
$extensionId = 'AxialSqlTools'

function Write-Step([string]$Message) {
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Get-SsmsPaths {
    $candidates = @(
        'C:\Program Files\Microsoft SQL Server Management Studio 22',
        'D:\Program Files\Microsoft SQL Server Management Studio 22',
        (Join-Path $env:ProgramFiles 'Microsoft SQL Server Management Studio 22')
    ) | Select-Object -Unique

    foreach ($root in $candidates) {
        $ideDir = Join-Path $root 'Release\Common7\IDE'
        $ssmsExe = Join-Path $ideDir 'Ssms.exe'
        $vsixInstaller = Join-Path $ideDir 'VSIXInstaller.exe'
        $smoDll = Join-Path $ideDir 'Microsoft.SqlServer.Smo.dll'
        if ((Test-Path $ssmsExe) -and (Test-Path $vsixInstaller) -and (Test-Path $smoDll)) {
            return [pscustomobject]@{
                Root = $root
                IdeDir = $ideDir
                SsmsExe = $ssmsExe
                VsixInstaller = $vsixInstaller
                ExtensionDir = Join-Path $ideDir "Extensions\$extensionId"
            }
        }
    }

    throw 'SSMS 22 not found (checked C: and D: Program Files). Install SSMS 22 first.'
}

function Get-SsmsOpenDocuments {
    $progIds = @(
        'VisualStudio.DTE.17.0',
        'VisualStudio.DTE.16.0',
        'VisualStudio.DTE.15.0'
    )
    foreach ($progId in $progIds) {
        try {
            $dteType = [Type]::GetTypeFromProgID($progId)
            if (-not $dteType) { continue }
            $dte = [System.Runtime.InteropServices.Marshal]::GetActiveObject($progId)
            if (-not $dte) { continue }

            $files = New-Object 'System.Collections.Generic.List[string]'
            $activeFile = $null
            try {
                $activeFile = $dte.ActiveDocument.FullName
            }
            catch {
            }

            foreach ($doc in @($dte.Documents)) {
                try {
                    $path = $doc.FullName
                    if ($path -and (Test-Path -LiteralPath $path) -and -not $files.Contains($path)) {
                        [void]$files.Add($path)
                    }
                }
                catch {
                }
            }

            if ($files.Count -gt 0 -or $activeFile) {
                return [pscustomobject]@{
                    Files = [string[]]$files
                    ActiveFile = $activeFile
                }
            }
        }
        catch {
        }
    }

    return [pscustomobject]@{
        Files = @()
        ActiveFile = $null
    }
}

function Stop-SsmsProcesses {
    $processes = Get-Process -Name 'Ssms' -ErrorAction SilentlyContinue
    if (-not $processes) {
        Write-Host 'SSMS is not running.'
        return
    }

    $pids = ($processes | Select-Object -ExpandProperty Id) -join ', '
    Write-Host "Stopping SSMS (PID: $pids) ..."
    $processes | Stop-Process -Force
    Start-Sleep -Seconds 1

    if (Get-Process -Name 'Ssms' -ErrorAction SilentlyContinue) {
        throw 'Failed to stop SSMS. Close it manually and retry.'
    }
}

function Wait-VsixInstallerExit([int]$TimeoutSeconds = 30) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Process -Name 'VSIXInstaller' -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }
    if (Get-Process -Name 'VSIXInstaller' -ErrorAction SilentlyContinue) {
        throw 'VSIXInstaller is still running. Wait and retry.'
    }
}

function Find-AxialExtensionDirs([string]$IdeDir) {
    $dirs = New-Object 'System.Collections.Generic.List[string]'

    $ssmsUserRoot = Join-Path $env:LOCALAPPDATA 'Microsoft\SSMS'
    if (Test-Path $ssmsUserRoot) {
        Get-ChildItem -Path $ssmsUserRoot -Recurse -Filter 'AxialSqlTools.pkgdef' -ErrorAction SilentlyContinue |
            ForEach-Object {
                $p = $_.Directory.FullName
                if (-not $dirs.Contains($p)) { [void]$dirs.Add($p) }
            }
    }

    $extensionsRoot = Join-Path $IdeDir 'Extensions'
    if (Test-Path $extensionsRoot) {
        Get-ChildItem -Path $extensionsRoot -Recurse -Filter 'AxialSqlTools.pkgdef' -ErrorAction SilentlyContinue |
            ForEach-Object {
                $p = $_.Directory.FullName
                if (-not $dirs.Contains($p)) { [void]$dirs.Add($p) }
            }
    }

    return @($dirs)
}

function Get-BuildTimeLabel([string]$BuildInfoPath) {
    if (-not (Test-Path $BuildInfoPath)) { return $null }
    $content = Get-Content -LiteralPath $BuildInfoPath -Raw -Encoding UTF8
    if ($content -match 'BuildTime\s*=\s*"([^"]+)"') { return $Matches[1] }
    return $null
}

function Get-PrimaryExtensionDir([string[]]$ExtensionDirs) {
    foreach ($dir in $ExtensionDirs) {
        if ($dir -like "*\AppData\Local\Microsoft\SSMS\*") { return $dir }
    }
    if ($ExtensionDirs.Count -gt 0) { return $ExtensionDirs[0] }
    return $null
}

function Test-InstalledMatchesBuild([string]$BuiltDll, [string[]]$ExtensionDirs) {
    if (-not (Test-Path $BuiltDll)) { return $false, @('Built DLL not found') }
    $primary = Get-PrimaryExtensionDir -ExtensionDirs $ExtensionDirs
    if (-not $primary) { return $false, @('No extension folder found') }

    $targets = @($primary)
    $builtHash = (Get-FileHash -LiteralPath $BuiltDll -Algorithm SHA256).Hash
    $mismatches = New-Object 'System.Collections.Generic.List[string]'
    foreach ($dir in $targets) {
        $installed = Join-Path $dir 'AxialSqlTools.dll'
        if (-not (Test-Path $installed)) {
            [void]$mismatches.Add("$installed (missing)")
            continue
        }
        $installedHash = (Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash
        if ($installedHash -ne $builtHash) {
            [void]$mismatches.Add("$installed (hash mismatch)")
        }
    }
    return ($mismatches.Count -eq 0), $mismatches
}

function Expand-VsixToTemp([string]$VsixFile) {
    $tempDir = Join-Path $env:TEMP ("axial-vsix-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($VsixFile, $tempDir)
    return $tempDir
}

function Install-VsixDirectCopy([string]$VsixFile, [string]$TargetDir) {
    $tempDir = $null
    try {
        $tempDir = Expand-VsixToTemp -VsixFile $VsixFile
        $dll = Get-ChildItem -Path $tempDir -Recurse -Filter 'AxialSqlTools.dll' -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if (-not $dll) { return $false }
        $sourceRoot = $dll.Directory.FullName
        $copiedMain = $false
        $failed = New-Object 'System.Collections.Generic.List[string]'
        Get-ChildItem -Path $sourceRoot -Recurse -File | ForEach-Object {
            $relative = $_.FullName.Substring($sourceRoot.Length).TrimStart('\')
            $dest = Join-Path $TargetDir $relative
            try {
                $destParent = Split-Path $dest -Parent
                if (-not (Test-Path $destParent)) {
                    New-Item -ItemType Directory -Path $destParent -Force | Out-Null
                }
                Copy-Item -LiteralPath $_.FullName -Destination $dest -Force -ErrorAction Stop
                if ($_.Name -eq 'AxialSqlTools.dll') { $copiedMain = $true }
            }
            catch {
                [void]$failed.Add($relative)
            }
        }
        if ($failed.Count -gt 0) {
            Write-Host "  partial copy ($($failed.Count) skipped): $TargetDir" -ForegroundColor DarkYellow
        }
        return $copiedMain
    }
    catch {
        Write-Host "Direct copy failed: $($_.Exception.Message)" -ForegroundColor Yellow
        return $false
    }
    finally {
        if ($tempDir -and (Test-Path $tempDir)) {
            Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Write-VsixCommand([string]$Kind, [string]$InstallerPath, [string[]]$InstallerArgs) {
    $quoted = $InstallerArgs | ForEach-Object {
        if ($_ -match '\s') { "`"$_`"" } else { $_ }
    }
    Write-Host "$Kind command:"
    Write-Host "  `"$InstallerPath`" $($quoted -join ' ')"
}

function Invoke-VsixInstaller([string]$InstallerPath, [string]$VsixFile, [switch]$Admin) {
    $installArgs = @('/quiet', '/force', '/shutdownprocesses', "/logFile:$env:TEMP\axial-vsix-install.log", $VsixFile)
    if ($Admin) { $installArgs = @('/admin') + $installArgs }
    $label = if ($Admin) { 'per-machine (/admin)' } else { 'per-user' }
    Write-VsixCommand -Kind "Install ($label)" -InstallerPath $InstallerPath -InstallerArgs $installArgs
    $install = Start-Process -FilePath $InstallerPath -ArgumentList $installArgs -Wait -PassThru
    if ($install.ExitCode -ne 0) {
        throw "VSIX install failed ($label) with exit code $($install.ExitCode). Log: $env:TEMP\axial-vsix-install.log"
    }
    Wait-VsixInstallerExit
    Start-Sleep -Seconds 1
}

function Uninstall-VsixQuiet([string]$InstallerPath, [string]$IdeDir) {
    Wait-VsixInstallerExit
    $log = Join-Path $env:TEMP 'axial-vsix-uninstall.log'
    $uninstallArgs = @('/quiet', "/uninstall:$extensionId", "/logFile:$log")
    Write-VsixCommand -Kind 'Uninstall' -InstallerPath $InstallerPath -InstallerArgs $uninstallArgs
    $uninstall = Start-Process -FilePath $InstallerPath -ArgumentList $uninstallArgs -Wait -PassThru
    if ($uninstall.ExitCode -ne 0) {
        Write-Host "Uninstall exited $($uninstall.ExitCode) (may already be gone). Log: $log" -ForegroundColor DarkYellow
    }
    Wait-VsixInstallerExit

    $extDirs = Find-AxialExtensionDirs -IdeDir $IdeDir
    if ($extDirs.Count -eq 0) {
        Write-Host 'Extension uninstalled (no leftover folders).'
        return
    }

    foreach ($dir in $extDirs) {
        Write-Host "Remove leftover: $dir"
        Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
    }

    $left = Find-AxialExtensionDirs -IdeDir $IdeDir
    if ($left.Count -eq 0) {
        Write-Host 'Extension uninstalled.'
        return
    }

    Write-Host 'Leftover folders remain; trying /admin uninstall ...' -ForegroundColor Yellow
    $adminArgs = @('/admin') + $uninstallArgs
    Write-VsixCommand -Kind 'Uninstall (/admin)' -InstallerPath $InstallerPath -InstallerArgs $adminArgs
    [void](Start-Process -FilePath $InstallerPath -ArgumentList $adminArgs -Wait -PassThru)
    Wait-VsixInstallerExit
    foreach ($dir in $left) {
        Write-Host "Remove leftover: $dir"
        Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
    }

    $left = Find-AxialExtensionDirs -IdeDir $IdeDir
    if ($left.Count -gt 0) {
        throw "Uninstall leftover: $($left -join '; ')"
    }
    Write-Host 'Extension uninstalled (/admin).'
}

function Install-VsixQuiet([string]$InstallerPath, [string]$VsixFile, [string]$IdeDir, [string]$BuiltDll) {
    if (-not (Test-Path $VsixFile)) {
        throw "VSIX not found: $VsixFile. Build first or omit -SkipBuild."
    }

    Write-VsixCommand -Kind 'Install' -InstallerPath $InstallerPath -InstallerArgs @('/quiet', '/force', '/shutdownprocesses', $VsixFile)
    Wait-VsixInstallerExit

    $buildTime = Get-BuildTimeLabel -BuildInfoPath (Join-Path $projectDir 'BuildInfo.cs')
    if ($buildTime) {
        Write-Host "Build time: $buildTime"
    }

    $extDirs = Find-AxialExtensionDirs -IdeDir $IdeDir
    if ($extDirs.Count -gt 0) {
        Write-Host "Found $($extDirs.Count) extension folder(s)."
        $primary = Get-PrimaryExtensionDir -ExtensionDirs $extDirs
        if ($primary) {
            Write-Host "SSMS loads: $primary"
        }
        $anyCopied = $false
        foreach ($extDir in $extDirs) {
            Write-Host "Direct overwrite: $extDir"
            if (Install-VsixDirectCopy -VsixFile $VsixFile -TargetDir $extDir) {
                $anyCopied = $true
            }
        }
        if ($anyCopied) {
            $ok, $bad = Test-InstalledMatchesBuild -BuiltDll $BuiltDll -ExtensionDirs $extDirs
            if ($ok) {
                Write-Host 'Extension installed (direct copy verified).'
                return
            }
            Write-Host 'Primary extension verification failed:' -ForegroundColor Yellow
            $bad | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
        }
        else {
            Write-Host 'Direct copy unavailable for all folders.' -ForegroundColor Yellow
        }
    }

    Invoke-VsixInstaller -InstallerPath $InstallerPath -VsixFile $VsixFile
    $extDirs = Find-AxialExtensionDirs -IdeDir $IdeDir
    $ok, $bad = Test-InstalledMatchesBuild -BuiltDll $BuiltDll -ExtensionDirs $extDirs
    if ($ok) {
        Write-Host 'Extension installed (per-user VSIX verified).'
        return
    }

    Write-Host 'Per-user install did not update all copies; trying /admin ...' -ForegroundColor Yellow
    Invoke-VsixInstaller -InstallerPath $InstallerPath -VsixFile $VsixFile -Admin
    $extDirs = Find-AxialExtensionDirs -IdeDir $IdeDir
    $ok, $bad = Test-InstalledMatchesBuild -BuiltDll $BuiltDll -ExtensionDirs $extDirs
    if (-not $ok) {
        $details = ($bad | ForEach-Object { "  $_" }) -join [Environment]::NewLine
        throw "Install verification failed. SSMS loads the per-user copy under AppData; built DLL was not deployed.$([Environment]::NewLine)$details"
    }
    Write-Host 'Extension installed (/admin fallback verified).'
}

function Start-SsmsApp([string]$SsmsExe, [string[]]$SqlFiles) {
    if (-not (Test-Path -LiteralPath $SsmsExe)) {
        throw "SSMS executable not found: $SsmsExe"
    }

    $workDir = Split-Path -Parent $SsmsExe
    $launchFiles = New-Object 'System.Collections.Generic.List[string]'
    foreach ($file in $SqlFiles) {
        if ($file -and (Test-Path -LiteralPath $file)) {
            [void]$launchFiles.Add($file)
        }
    }

    Write-Host "Launching SSMS: $SsmsExe"
    if ($launchFiles.Count -gt 0) {
        Write-Host "Restoring query windows: $($launchFiles -join '; ')"
    }

    $argumentString = ''
    if ($launchFiles.Count -gt 0) {
        $argumentString = ($launchFiles | ForEach-Object {
            if ($_ -match '\s') { "`"$_`"" } else { $_ }
        }) -join ' '
    }

    $started = $false
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $SsmsExe
        $psi.WorkingDirectory = $workDir
        $psi.UseShellExecute = $true
        $psi.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Normal
        if ($argumentString) {
            $psi.Arguments = $argumentString
        }
        [void][System.Diagnostics.Process]::Start($psi)
        $started = $true
    }
    catch {
        Write-Host "ShellExecute launch failed: $($_.Exception.Message)" -ForegroundColor Yellow
    }

    if (-not $started) {
        if ($launchFiles.Count -gt 0) {
            Start-Process -FilePath $SsmsExe -ArgumentList $launchFiles.ToArray() -WorkingDirectory $workDir -WindowStyle Normal | Out-Null
        }
        else {
            Start-Process -FilePath $SsmsExe -WorkingDirectory $workDir -WindowStyle Normal | Out-Null
        }
    }

    Start-Sleep -Seconds 3
    $ssmsProc = Get-Process -Name 'Ssms' -ErrorAction SilentlyContinue
    if (-not $ssmsProc) {
        throw 'Failed to launch SSMS. Please start it manually from the Start menu.'
    }

    $ssmsPid = ($ssmsProc | Select-Object -First 1 -ExpandProperty Id)
    Write-Host "SSMS is running (PID: $ssmsPid)."
}

$ssms = Get-SsmsPaths
Write-Host "SSMS: $($ssms.Root)"
Write-Host "Action: $Action"
Write-Host "Configuration: $Configuration"

if ($Action -eq 'Uninstall') {
    Write-Step 'Stop SSMS'
    Stop-SsmsProcesses
    Write-Step 'Uninstall extension'
    Uninstall-VsixQuiet -InstallerPath $ssms.VsixInstaller -IdeDir $ssms.IdeDir
    Write-Host ''
    Write-Host 'Done. Extension uninstalled.' -ForegroundColor Green
    return
}

if (-not $SkipBuild) {
    Write-Step 'Build extension'
    if (-not (Test-Path $packScript)) {
        throw "Pack script not found: $packScript"
    }
    & $packScript -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed with exit code $LASTEXITCODE"
    }
}
else {
    Write-Step 'Skip build (-SkipBuild)'
}

Write-Step 'Save open query documents'
$savedDocs = Get-SsmsOpenDocuments
if ($savedDocs.Files.Count -gt 0) {
    Write-Host "Saved $($savedDocs.Files.Count) document(s) to restore."
}
else {
    Write-Host 'No open SQL documents detected (SSMS not running or DTE unavailable).'
}

Write-Step 'Stop SSMS'
Stop-SsmsProcesses

Write-Step 'Install extension'
Install-VsixQuiet -InstallerPath $ssms.VsixInstaller -VsixFile $vsixPath -IdeDir $ssms.IdeDir -BuiltDll $builtDllPath

if ($NoLaunch) {
    Write-Host ''
    Write-Host 'Done. Installed without launching SSMS (-NoLaunch).' -ForegroundColor Green
}
else {
    Write-Step 'Launch SSMS'
    Start-SsmsApp -SsmsExe $ssms.SsmsExe -SqlFiles $savedDocs.Files
    Write-Host ''
    Write-Host 'Done. SSMS relaunched with previous query windows.' -ForegroundColor Green
}
