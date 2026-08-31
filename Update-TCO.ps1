#Requires -Version 5.1

param(
    [Parameter(Mandatory = $false)]
    [string]$PackageRoot = $PSScriptRoot,

    [Parameter(Mandatory = $false)]
    [string]$LogPath = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$PackageRoot = [IO.Path]::GetFullPath($PackageRoot)
if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $LogPath = Join-Path $PackageRoot 'logs\update.log'
}
$LogPath = [IO.Path]::GetFullPath($LogPath)
New-Item -ItemType Directory -Path (Split-Path -Parent $LogPath) -Force | Out-Null
$apiUrl = 'https://api.github.com/repos/Shadsa/TCO/releases/latest'
$statePath = Join-Path $PackageRoot 'logs\update-state.json'

function Write-UpdateLog {
    param([string]$Message, [ValidateSet('INFO', 'WARN', 'ERROR')][string]$Level = 'INFO')
    $line = '{0} [UPDATE] [{1}] {2}{3}' -f (Get-Date).ToString('o'), $Level, $Message, [Environment]::NewLine
    [IO.File]::AppendAllText($LogPath, $line, [Text.UTF8Encoding]::new($false))
    $color = if ($Level -eq 'ERROR') { 'Red' } elseif ($Level -eq 'WARN') { 'Yellow' } else { 'Gray' }
    Write-Host "[UPDATE] [$Level] $Message" -ForegroundColor $color
}

function Test-PathInsideRoot {
    param([string]$Root, [string]$Path)
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $pathFull = [IO.Path]::GetFullPath($Path)
    return $pathFull.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)
}

function Get-PackageManifest {
    param([string]$Root)
    $path = Join-Path $Root 'manifest.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Package manifest is missing: $path" }
    try { return [IO.File]::ReadAllText($path) | ConvertFrom-Json }
    catch { throw "Package manifest is invalid: $path. $($_.Exception.Message)" }
}

function Assert-PackageManifest {
    param([string]$Root, [object]$Manifest)
    $files = @($Manifest.Files)
    if ($files.Count -eq 0 -or $files.Count -ne [int]$Manifest.FileCount) {
        throw 'Package manifest file count is invalid.'
    }
    $seen = @{}
    foreach ($entry in $files) {
        $relative = ([string]$entry.Path).Replace('/', '\')
        if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or
            $relative -match '(^|\\)\.\.(\\|$)' -or $relative -match '^logs(\\|$)') {
            throw "Unsafe package path in manifest: $relative"
        }
        if ($seen.ContainsKey($relative)) { throw "Duplicate package path in manifest: $relative" }
        $seen[$relative] = $true
        $path = Join-Path $Root $relative
        if (-not (Test-PathInsideRoot -Root $Root -Path $path)) { throw "Package path escapes its root: $relative" }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Manifest file is missing: $relative" }
        $item = Get-Item -LiteralPath $path
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if ($item.Length -ne [long]$entry.Bytes -or $hash -ne [string]$entry.SHA256) {
            throw "Manifest validation failed for $relative"
        }
    }
    foreach ($required in @('Install.cmd', 'Start-TCO.ps1',
            'Update-TCO.ps1', 'Install-TERA-Complete.ps1')) {
        if (-not $seen.ContainsKey($required)) { throw "Release package does not manage required launcher file: $required" }
    }
}

function Get-LatestRelease {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $headers = @{
        'User-Agent' = 'TCO-Installer'
        'Accept' = 'application/vnd.github+json'
        'X-GitHub-Api-Version' = '2022-11-28'
    }
    try { return Invoke-RestMethod -Uri $apiUrl -Headers $headers -Method Get }
    catch {
        $response = $_.Exception.Response
        $statusCode = if ($null -ne $response) { [int]$response.StatusCode } else { 0 }
        if ($statusCode -eq 404) {
            Write-UpdateLog 'No published GitHub release is available; continuing with the local package.'
        } else {
            Write-UpdateLog "GitHub update check failed; continuing with the local package. $($_.Exception.Message)" 'WARN'
        }
        return $null
    }
}

function Select-ReleaseZipAsset {
    param([object]$Release)
    $zips = @($Release.assets | Where-Object { $_.name -match '\.zip$' })
    $preferred = @($zips | Where-Object { $_.name -match '^(TCO|TERA-Complete).*\.zip$' })
    if ($preferred.Count -eq 1) { return $preferred[0] }
    if ($preferred.Count -gt 1) { throw 'Latest release contains multiple matching TCO ZIP assets.' }
    if ($zips.Count -eq 1) { return $zips[0] }
    if ($zips.Count -eq 0) { throw 'Latest release contains no ZIP package asset.' }
    throw 'Latest release contains multiple ZIP assets and none has a TCO package name.'
}

function Get-AssetSHA256 {
    param([object]$Release, [object]$Asset, [string]$TemporaryRoot)
    $digestProperty = $Asset.PSObject.Properties['digest']
    if ($null -ne $digestProperty -and [string]$digestProperty.Value -match '^sha256:([A-Fa-f0-9]{64})$') {
        return $Matches[1].ToUpperInvariant()
    }
    $sidecar = $Release.assets | Where-Object { $_.name -ieq "$($Asset.name).sha256" } | Select-Object -First 1
    if ($null -eq $sidecar) { throw "Release asset has no SHA-256 digest or sidecar: $($Asset.name)" }
    $sidecarPath = Join-Path $TemporaryRoot $sidecar.name
    if (-not (Test-PathInsideRoot -Root $TemporaryRoot -Path $sidecarPath)) {
        throw "Unsafe release sidecar name: $($sidecar.name)"
    }
    Invoke-WebRequest -Uri $sidecar.browser_download_url -OutFile $sidecarPath -UseBasicParsing
    $text = [IO.File]::ReadAllText($sidecarPath)
    if ($text -notmatch '([A-Fa-f0-9]{64})') { throw "Invalid SHA-256 sidecar: $($sidecar.name)" }
    return $Matches[1].ToUpperInvariant()
}

function Assert-ZipPathsSafe {
    param([string]$ZipPath, [string]$ExtractRoot)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        foreach ($entry in $archive.Entries) {
            if ([string]::IsNullOrEmpty($entry.FullName)) { continue }
            $target = Join-Path $ExtractRoot $entry.FullName
            if (-not (Test-PathInsideRoot -Root $ExtractRoot -Path $target)) {
                throw "Unsafe path in release archive: $($entry.FullName)"
            }
        }
    } finally { $archive.Dispose() }
}

function Install-ReleasePackage {
    param([string]$SourceRoot, [object]$Manifest)
    $backupRoot = Join-Path ([IO.Path]::GetTempPath()) ('tco-update-backup-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
    $changes = [System.Collections.Generic.List[object]]::new()
    try {
        $copyEntries = @($Manifest.Files) + @([pscustomobject]@{ Path = 'manifest.json'; Bytes = $null; SHA256 = $null })
        foreach ($entry in $copyEntries) {
            $relative = ([string]$entry.Path).Replace('/', '\')
            $source = Join-Path $SourceRoot $relative
            $destination = Join-Path $PackageRoot $relative
            if (-not (Test-PathInsideRoot -Root $PackageRoot -Path $destination)) { throw "Unsafe update destination: $relative" }
            $existed = Test-Path -LiteralPath $destination -PathType Leaf
            if ($existed) {
                $backup = Join-Path $backupRoot $relative
                New-Item -ItemType Directory -Path (Split-Path -Parent $backup) -Force | Out-Null
                Copy-Item -LiteralPath $destination -Destination $backup -Force
            }
            [void]$changes.Add([pscustomobject]@{ Relative = $relative; Existed = $existed })
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            Copy-Item -LiteralPath $source -Destination $destination -Force
            if ($relative -ne 'manifest.json') {
                $item = Get-Item -LiteralPath $destination
                $hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
                if ($item.Length -ne [long]$entry.Bytes -or $hash -ne [string]$entry.SHA256) {
                    throw "Updated file verification failed: $relative"
                }
            }
        }
    }
    catch {
        $updateError = $_
        $rollbackErrors = [System.Collections.Generic.List[string]]::new()
        for ($index = $changes.Count - 1; $index -ge 0; $index--) {
            $change = $changes[$index]
            try {
                $destination = Join-Path $PackageRoot $change.Relative
                if ($change.Existed) {
                    Copy-Item -LiteralPath (Join-Path $backupRoot $change.Relative) -Destination $destination -Force
                } elseif (Test-Path -LiteralPath $destination -PathType Leaf) {
                    Remove-Item -LiteralPath $destination -Force
                }
            } catch { [void]$rollbackErrors.Add($_.Exception.Message) }
        }
        if ($rollbackErrors.Count -gt 0) {
            throw "FATAL: update failed and rollback was incomplete. Update error: $updateError Rollback: $($rollbackErrors -join '; ')"
        }
        try {
            $restoredManifest = Get-PackageManifest -Root $PackageRoot
            Assert-PackageManifest -Root $PackageRoot -Manifest $restoredManifest
        }
        catch {
            throw "FATAL: update failed and the rolled-back package did not pass validation. Update error: $updateError Validation: $($_.Exception.Message)"
        }
        throw "Update failed and was rolled back: $updateError"
    }
    finally {
        $systemTemporaryRoot = [IO.Path]::GetTempPath()
        if ((Test-PathInsideRoot -Root $systemTemporaryRoot -Path $backupRoot) -and
            (Test-Path -LiteralPath $backupRoot)) {
            Remove-Item -LiteralPath $backupRoot -Recurse -Force
        }
    }
}

function Invoke-TCOSelfUpdate {
    try {
        $localManifest = Get-PackageManifest -Root $PackageRoot
        Assert-PackageManifest -Root $PackageRoot -Manifest $localManifest
    }
    catch {
        throw "FATAL: local package validation failed. $($_.Exception.Message)"
    }

    Write-UpdateLog "Checking $apiUrl"
    $release = Get-LatestRelease
    if ($null -eq $release) { return }
    $tag = [string]$release.tag_name
    if ([string]::IsNullOrWhiteSpace($tag)) { throw 'Latest GitHub release has no tag.' }

    if (Test-Path -LiteralPath $statePath -PathType Leaf) {
        try {
            $state = [IO.File]::ReadAllText($statePath) | ConvertFrom-Json
            if ([string]$state.Tag -eq $tag) {
                Write-UpdateLog "Release $tag is already installed."
                return
            }
        } catch { Write-UpdateLog "Ignoring invalid update state: $($_.Exception.Message)" 'WARN' }
    }

    $localVersion = ([string]$localManifest.Version).TrimStart('v', 'V')
    if ($localVersion -eq $tag.TrimStart('v', 'V')) {
        Write-UpdateLog "Local package already matches release $tag."
        [IO.File]::WriteAllText($statePath, ([ordered]@{ Tag = $tag; InstalledAt = (Get-Date).ToString('o') } | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
        return
    }
    $asset = Select-ReleaseZipAsset -Release $release
    $temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('tco-update-' + [guid]::NewGuid().ToString('N'))
    $extractRoot = Join-Path $temporaryRoot 'extract'
    New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null
    try {
        $expectedHash = Get-AssetSHA256 -Release $release -Asset $asset -TemporaryRoot $temporaryRoot
        $zipPath = Join-Path $temporaryRoot $asset.name
        if (-not (Test-PathInsideRoot -Root $temporaryRoot -Path $zipPath)) {
            throw "Unsafe release asset name: $($asset.name)"
        }
        Write-UpdateLog "Downloading release $tag asset $($asset.name)."
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zipPath -UseBasicParsing
        $actualHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
        if ($actualHash -ne $expectedHash) { throw 'Downloaded release asset SHA-256 does not match GitHub metadata.' }
        Assert-ZipPathsSafe -ZipPath $zipPath -ExtractRoot $extractRoot
        Expand-Archive -LiteralPath $zipPath -DestinationPath $extractRoot -Force
        $candidates = @(Get-ChildItem -LiteralPath $extractRoot -Filter manifest.json -File -Recurse | Where-Object {
            Test-Path -LiteralPath (Join-Path $_.DirectoryName 'Install-TERA-Complete.ps1') -PathType Leaf
        })
        if ($candidates.Count -ne 1) { throw "Release ZIP must contain exactly one TCO package root; found $($candidates.Count)." }
        $sourceRoot = $candidates[0].DirectoryName
        $remoteManifest = Get-PackageManifest -Root $sourceRoot
        Assert-PackageManifest -Root $sourceRoot -Manifest $remoteManifest
        Install-ReleasePackage -SourceRoot $sourceRoot -Manifest $remoteManifest
        [IO.File]::WriteAllText($statePath, ([ordered]@{
            Tag = $tag
            Asset = [string]$asset.name
            AssetSHA256 = $actualHash
            InstalledAt = (Get-Date).ToString('o')
        } | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
        Write-UpdateLog "Updated local package successfully to release $tag."
    }
    finally {
        $systemTemporaryRoot = [IO.Path]::GetTempPath()
        if ((Test-PathInsideRoot -Root $systemTemporaryRoot -Path $temporaryRoot) -and
            (Test-Path -LiteralPath $temporaryRoot)) {
            Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
        }
    }
}

try {
    Invoke-TCOSelfUpdate
    exit 0
}
catch {
    Write-UpdateLog "Update was not applied: $($_ | Out-String)" 'ERROR'
    if ($_.Exception.Message -like 'FATAL:*') { exit 1 }
    Write-UpdateLog 'The validated local package will be used.' 'WARN'
    exit 0
}
