#Requires -Version 5.1

param(
    [Parameter(Mandatory = $false)]
    [ValidateNotNullOrEmpty()]
    [string]$TeraRoot = 'S:\TERA',

    [Parameter(Mandatory = $false)]
    [string]$EngineProfilePath = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

if (Get-Process -Name 'TERA' -ErrorAction SilentlyContinue) {
    throw 'Close TERA before applying the optimization profile.'
}

function Set-IniSectionValues {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Section,
        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$Values
    )

    $lines = [System.Collections.Generic.List[string]]::new()
    [System.IO.File]::ReadAllLines($Path) | ForEach-Object { [void]$lines.Add($_) }

    $sectionStart = -1
    $sectionEnd = $lines.Count
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\s*\[(.+?)\]\s*$') {
            if ($sectionStart -ge 0) {
                $sectionEnd = $i
                break
            }
            if ($Matches[1] -ieq $Section) {
                $sectionStart = $i
            }
        }
    }

    if ($sectionStart -lt 0) {
        [void]$lines.Add('')
        [void]$lines.Add("[$Section]")
        $sectionStart = $lines.Count - 1
        $sectionEnd = $lines.Count
    }

    foreach ($entry in $Values.GetEnumerator()) {
        $key = [regex]::Escape([string]$entry.Key)
        $replacement = "{0}={1}" -f $entry.Key, $entry.Value
        $found = $false

        for ($i = $sectionStart + 1; $i -lt $sectionEnd; $i++) {
            if ($lines[$i] -match "^\s*$key\s*=") {
                $lines[$i] = $replacement
                $found = $true
            }
        }

        if (-not $found) {
            $lines.Insert($sectionEnd, $replacement)
            $sectionEnd++
        }
    }

    [System.IO.File]::WriteAllLines($Path, $lines, [System.Text.UTF8Encoding]::new($false))
}

function Get-TextureGroupFingerprint {
    param([Parameter(Mandatory = $true)][string]$Path)

    $textureGroups = [System.IO.File]::ReadAllLines($Path) |
        Where-Object { $_ -match '^TEXTUREGROUP_' }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes(($textureGroups -join "`n"))
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [Convert]::ToBase64String($sha.ComputeHash($bytes))
    }
    finally {
        $sha.Dispose()
    }
}

$configRoot = Join-Path $TeraRoot 'S1Game\Config'
$engineRoot = Join-Path $TeraRoot 'Engine\Config'
$s1Engine = Join-Path $configRoot 'S1Engine.ini'
$systemSettings = Join-Path $configRoot 'S1SystemSettings.ini'
$option = Join-Path $configRoot 'S1Option.ini'
$s1Input = Join-Path $configRoot 'S1Input.ini'
$baseInput = Join-Path $engineRoot 'BaseInput.ini'
$engineFileMap = [ordered]@{
    'S1Engine.ini' = $s1Engine
    'S1SystemSettings.ini' = $systemSettings
    'S1Option.ini' = $option
    'BaseInput.ini' = $baseInput
    'S1Input.ini' = $s1Input
}
$packageRoot = Split-Path -Parent $PSScriptRoot
$portableEngineProfile = Join-Path $packageRoot 'payload\engine-profile.json'
if ([string]::IsNullOrWhiteSpace($EngineProfilePath)) {
    $EngineProfilePath = if (Test-Path -LiteralPath $portableEngineProfile -PathType Leaf) {
        $portableEngineProfile
    } else {
        Join-Path $TeraRoot 'ReShadeTools\payload\engine-profile.json'
    }
}
$EngineProfilePath = [IO.Path]::GetFullPath($EngineProfilePath)
$engineProfileSupport = Join-Path $PSScriptRoot 'engine_profile.ps1'
$required = @($engineFileMap.Values) + @($EngineProfilePath, $engineProfileSupport)

foreach ($path in $required) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required TERA configuration file is missing: $path"
    }
}

. $engineProfileSupport
$engineProfileEntries = @(Get-EngineProfileEntries -ProfilePath $EngineProfilePath -FileMap $engineFileMap)

$textureGroupsBefore = Get-TextureGroupFingerprint -Path $systemSettings

# Apply every JSON entry. Existing keys are replaced and missing sections or keys are inserted.
foreach ($fileName in $engineFileMap.Keys) {
    $fileEntries = @($engineProfileEntries | Where-Object { $_.File -ieq $fileName })
    $sectionNames = @($fileEntries | Select-Object -ExpandProperty Section -Unique)
    foreach ($sectionName in $sectionNames) {
        $values = [ordered]@{}
        foreach ($entry in @($fileEntries | Where-Object { $_.Section -ieq $sectionName })) {
            $values[$entry.Key] = $entry.Value
        }
        Set-IniSectionValues -Path $engineFileMap[$fileName] -Section $sectionName -Values $values
    }
}

$textureGroupsAfter = Get-TextureGroupFingerprint -Path $systemSettings
if ($textureGroupsBefore -cne $textureGroupsAfter) {
    throw 'Texture-group integrity check failed. A TEXTUREGROUP entry changed unexpectedly.'
}

Write-Host ''
Write-Host 'TERA stable optimization profile applied successfully.' -ForegroundColor Green
Write-Host "Engine settings applied: $($engineProfileEntries.Count) from $EngineProfilePath"
$texturePool = $engineProfileEntries | Where-Object {
    $_.File -ieq 'S1Engine.ini' -and
    $_.Section -ieq 'TextureStreaming' -and
    $_.Key -ieq 'PoolSize'
} | Select-Object -First 1
Write-Host $(if ($null -eq $texturePool) { 'Texture pool: not managed' } else { "Texture pool: $($texturePool.Value) MB" })
Write-Host 'Texture groups and ColorLookupTable: unchanged'
Write-Host 'Backup created: False'
