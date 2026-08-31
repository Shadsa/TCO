#Requires -Version 5.1

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$packageRoot = $PSScriptRoot
$logRoot = Join-Path $packageRoot 'logs'
New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
$logPath = Join-Path $logRoot ("install-{0}.log" -f (Get-Date).ToString('yyyyMMdd-HHmmss'))

function Write-LauncherLog {
    param([string]$Message, [string]$Level = 'INFO')
    $line = '{0} [LAUNCHER] [{1}] {2}{3}' -f (Get-Date).ToString('o'), $Level, $Message, [Environment]::NewLine
    [IO.File]::AppendAllText($logPath, $line, [Text.UTF8Encoding]::new($false))
}

try {
    Write-Host "TCO installation log: $logPath"
    Write-Host 'Checking for the latest published TCO release...'
    Write-LauncherLog "Installation started from $packageRoot"
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $packageRoot 'Update-TCO.ps1') -PackageRoot $packageRoot -LogPath $logPath
    if ($LASTEXITCODE -ne 0) {
        throw "Updater failed with exit code $LASTEXITCODE. Installation was not started."
    }

    Write-Host 'Applying the TCO graphics package...'
    # The updater may have replaced the installer, so resolve and launch it only after updating.
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $packageRoot 'Install-TERA-Complete.ps1') -Action Apply -LogPath $logPath
    $installExitCode = $LASTEXITCODE
    if ($installExitCode -ne 0) { throw "Installer failed with exit code $installExitCode." }
    Write-LauncherLog 'Installation completed successfully.'
    Write-Host 'Installation completed successfully.' -ForegroundColor Green
    exit 0
}
catch {
    Write-LauncherLog ($_ | Out-String) 'ERROR'
    Write-Host "Installation failed. Review the log: $logPath" -ForegroundColor Red
    Write-Error ($_ | Out-String) -ErrorAction Continue
    exit 1
}
