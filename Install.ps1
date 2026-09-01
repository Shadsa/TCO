#Requires -Version 5.1

<#
.SYNOPSIS
Installs and manages the TERA Complete graphics package.

.DESCRIPTION
The default Apply action checks GitHub for a validated package update, applies
the engine profile, installs DXVK and ReShade, and writes a timestamped log.
TCC and Shinra profiles are included only when IncludeClassicPlus is specified.

.EXAMPLE
.\Install.ps1

.EXAMPLE
.\Install.ps1 -IncludeClassicPlus

.EXAMPLE
.\Install.ps1 -Action Status
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [ValidateSet('Apply', 'ApplyClassicPlus', 'ExportClassicPlus', 'EnableReShade', 'DisableReShade', 'RestoreReShade', 'LockConfigs', 'UnlockConfigs', 'Status')]
    [string]$Action = 'Apply',

    [Parameter(Mandatory = $false)]
    [string]$TeraRoot = '',

    [Parameter(Mandatory = $false)]
    [string]$LogPath = '',

    [Parameter(Mandatory = $false)]
    [switch]$IncludeClassicPlus,

    [Parameter(Mandatory = $false)]
    [switch]$SkipUpdate
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

if ( [string]::IsNullOrWhiteSpace($TeraRoot))
{
    $TeraRoot = if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Binaries') -PathType Container)
    {
        $PSScriptRoot
    }
    else
    {
        Split-Path -Parent $PSScriptRoot
    }
}
$TeraRoot = [System.IO.Path]::GetFullPath($TeraRoot)
$modulesRoot = if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'modules') -PathType Container)
{
    Join-Path $PSScriptRoot 'modules'
}
else
{
    $TeraRoot
}
$stableEngineScript = Join-Path $modulesRoot 'apply_tera_final_stable.ps1'
$reShadeManager = Join-Path $modulesRoot 'tera_graphics_pipeline.ps1'
$displayResolutionScript = Join-Path $modulesRoot 'display_resolution.ps1'
$engineProfileSupport = Join-Path $modulesRoot 'engine_profile.ps1'
$updateSupport = Join-Path $modulesRoot 'update_tco.ps1'
$portableEngineProfile = Join-Path $PSScriptRoot 'payload\engine-profile.json'
$engineProfilePath = if (Test-Path -LiteralPath $portableEngineProfile -PathType Leaf)
{
    $portableEngineProfile
}
else
{
    Join-Path $TeraRoot 'ReShadeTools\payload\engine-profile.json'
}
$portableProfilePayload = Join-Path $PSScriptRoot 'payload\classicplus'
$profilePayload = if (Test-Path -LiteralPath $portableProfilePayload -PathType Container)
{
    $portableProfilePayload
}
else
{
    Join-Path $TeraRoot 'ReShadeTools\payload\classicplus'
}
$classicPlusRoot = Join-Path $env:APPDATA 'Crazy-eSports-ClassicPlus\mods\external'
$tccRoot = Join-Path $classicPlusRoot 'classicplus.tcc'
$shinraRoot = Join-Path $classicPlusRoot 'classicplus.shinra'
$tccConfig = Join-Path $tccRoot 'tcc-settings.json'
$shinraConfigRoot = Join-Path $shinraRoot 'resources\config'
$binaryRoot = Join-Path $TeraRoot 'Binaries'
$configRoot = Join-Path $TeraRoot 'S1Game\Config'
$engineRoot = Join-Path $TeraRoot 'Engine\Config'
$s1Engine = Join-Path $configRoot 'S1Engine.ini'
$systemSettings = Join-Path $configRoot 'S1SystemSettings.ini'
$option = Join-Path $configRoot 'S1Option.ini'
$s1Input = Join-Path $configRoot 'S1Input.ini'
$baseInput = Join-Path $engineRoot 'BaseInput.ini'
$configFiles = @($s1Engine, $systemSettings, $option, $s1Input, $baseInput)
$engineFileMap = [ordered]@{
    'S1Engine.ini' = $s1Engine
    'S1SystemSettings.ini' = $systemSettings
    'S1Option.ini' = $option
    'BaseInput.ini' = $baseInput
    'S1Input.ini' = $s1Input
}

function Test-IsAdministrator
{
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Restart-Elevated
{
    $argumentParts = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', ('"{0}"' -f $PSCommandPath),
        '-Action', $Action,
        '-TeraRoot', ('"{0}"' -f $TeraRoot)
    )
    if ($IncludeClassicPlus)
    {
        $argumentParts += '-IncludeClassicPlus'
    }
    if ($SkipUpdate)
    {
        $argumentParts += '-SkipUpdate'
    }
    if (-not [string]::IsNullOrWhiteSpace($LogPath))
    {
        $argumentParts += @('-LogPath', ('"{0}"' -f $LogPath))
    }
    $arguments = $argumentParts -join ' '
    $process = Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -Verb RunAs -Wait -PassThru
    exit $process.ExitCode
}

function Assert-Closed
{
    param([switch]$ClassicPlus)
    $blockedNames = @('TERA', 'noctenium', 'TERA Europe Classic+ Launcher')
    if ($ClassicPlus)
    {
        $blockedNames += @('TCC', 'ShinraMeter')
    }
    $running = Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessName -in $blockedNames
    }
    if ($running)
    {
        $names = ($running.ProcessName | Sort-Object -Unique) -join ', '
        throw "Close the affected TERA/Classic+ processes first. Running: $names"
    }
}

function Assert-ClassicPlusClosed
{
    $running = Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessName -in @('TERA', 'noctenium', 'TCC', 'ShinraMeter')
    }
    if ($running)
    {
        $names = ($running.ProcessName | Sort-Object -Unique) -join ', '
        throw "Close TERA, Noctenium, TCC, and Shinra Meter first. Running: $names"
    }
}

function Assert-ClassicPlusPayload
{
    $required = @(
        (Join-Path $profilePayload 'tcc\tcc-settings.json'),
        (Join-Path $profilePayload 'shinra\hotkeys.xml'),
        (Join-Path $profilePayload 'shinra\window.xml'),
        (Join-Path $profilePayload 'shinra\window_backup.xml'),
        (Join-Path $profilePayload 'shinra\server-overrides.txt')
    )
    foreach ($path in $required)
    {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf))
        {
            throw "Classic+ profile payload is incomplete: $path"
        }
    }
}

function Assert-ClassicPlusInstalled
{
    Assert-ClassicPlusPayload
    if (-not (Test-Path -LiteralPath $tccRoot -PathType Container))
    {
        throw "TCC must be installed before running this action: $tccRoot"
    }
    if (-not (Test-Path -LiteralPath $shinraConfigRoot -PathType Container))
    {
        throw "Shinra Meter must be installed before running this action: $shinraConfigRoot"
    }
}

function Assert-Files
{
    foreach ($path in @($stableEngineScript, $reShadeManager, $displayResolutionScript,
            $engineProfileSupport, $updateSupport, $engineProfilePath) + $configFiles)
    {
        if (-not (Test-Path -LiteralPath $path))
        {
            throw "Required file is missing: $path"
        }
    }
}

function Set-ConfigLock
{
    param([bool]$Locked)
    foreach ($path in $configFiles)
    {
        if (Test-Path -LiteralPath $path -PathType Leaf)
        {
            (Get-Item -LiteralPath $path).IsReadOnly = $Locked
        }
    }
}

function Get-IniValue
{
    param([string]$Path, [string]$Section, [string]$Key)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf))
    {
        return $null
    }
    $inSection = $false
    foreach ($line in [System.IO.File]::ReadAllLines($Path))
    {
        if ($line -match '^\s*\[(.+?)\]\s*$')
        {
            $inSection = $Matches[1] -ieq $Section
            continue
        }
        if ($inSection -and $line -match ('^\s*{0}\s*=\s*(.*?)\s*$' -f [regex]::Escape($Key)))
        {
            return $Matches[1]
        }
    }
    return $null
}

function Test-ExpectedSetting
{
    param([string]$Path, [string]$Section, [string]$Key, [string]$Expected)
    $actual = Get-IniValue -Path $Path -Section $Section -Key $Key
    return [pscustomobject]@{
        File = Split-Path -Leaf $Path
        Section = $Section
        Key = $Key
        Expected = $Expected
        Actual = if ($null -eq $actual)
        {
            '<missing>'
        }
        else
        {
            $actual
        }
        Match = ($actual -ieq $Expected)
    }
}

function Get-EngineChecks
{
    $checks = [System.Collections.Generic.List[object]]::new()
    $entries = @(Get-EngineProfileEntries -ProfilePath $engineProfilePath -FileMap $engineFileMap)
    foreach ($entry in $entries)
    {
        [void]$checks.Add((Test-ExpectedSetting -Path $entry.Path -Section $entry.Section -Key $entry.Key -Expected $entry.Value))
    }
    return $checks.ToArray()
}

function Get-DllKind
{
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf))
    {
        return 'Missing'
    }
    $product = (Get-Item -LiteralPath $Path).VersionInfo.ProductName
    if ($product -eq 'DXVK')
    {
        return 'DXVK'
    }
    if ($product -eq 'ReShade')
    {
        return 'ReShade'
    }
    return "Unknown ($product)"
}

function Read-SharedText
{
    param([string]$Path)
    $stream = [System.IO.FileStream]::new(
            $Path,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete
    )
    try
    {
        $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::UTF8, $true, 4096, $true)
        try
        {
            return $reader.ReadToEnd()
        }
        finally
        {
            $reader.Dispose()
        }
    }
    finally
    {
        $stream.Dispose()
    }
}

function Write-XmlFile
{
    param([xml]$Document, [string]$Path)
    $settings = [System.Xml.XmlWriterSettings]::new()
    $settings.Encoding = [System.Text.UTF8Encoding]::new($false)
    $settings.Indent = $true
    $settings.NewLineChars = "`r`n"
    $writer = [System.Xml.XmlWriter]::Create($Path, $settings)
    try
    {
        $Document.Save($writer)
    }
    finally
    {
        $writer.Dispose()
    }
}

function Install-ClassicPlusProfiles
{
    Assert-ClassicPlusInstalled

    Copy-Item -LiteralPath (Join-Path $profilePayload 'tcc\tcc-settings.json') -Destination $tccConfig -Force
    foreach ($name in @('hotkeys.xml', 'window.xml', 'window_backup.xml', 'server-overrides.txt'))
    {
        Copy-Item -LiteralPath (Join-Path $profilePayload "shinra\$name") -Destination (Join-Path $shinraConfigRoot $name) -Force
    }

    $documents = [Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments)
    $exportPath = (Join-Path $documents 'ShinraMeter') + [IO.Path]::DirectorySeparatorChar
    foreach ($name in @('window.xml', 'window_backup.xml')) {
    $path = Join-Path $shinraConfigRoot $name
    [xml]$xml = [IO.File]::ReadAllText($path)
    $directory = $xml.SelectSingleNode('//excel_save_directory')
    if ($null -ne $directory) {
    $directory.InnerText = $exportPath
    }
    $mute = $xml.SelectSingleNode('//mute_sound')
    if ($null -ne $mute) {
    $mute.InnerText = 'true'
    }
    Write-XmlFile -Document $xml -Path $path
    }

    $hotkeyPath = Join-Path $shinraConfigRoot 'hotkeys.xml'
    [xml]$hotkeys = [IO.File]::ReadAllText($hotkeyPath)
    $hotkeys.hotkeys.paste.ctrl = 'True'
    $hotkeys.hotkeys.paste.key = 'Home'
    Write-XmlFile -Document $hotkeys -Path $hotkeyPath
    Write-Host 'Classic+ TCC and Shinra profiles applied. ReShade uses Home; Shinra paste uses Ctrl+Home.' -ForegroundColor Green
}

function Export-ClassicPlusProfiles
{
    foreach ($path in @($tccConfig, (Join-Path $shinraConfigRoot 'hotkeys.xml'), (Join-Path $shinraConfigRoot 'window.xml'),
    (Join-Path $shinraConfigRoot 'window_backup.xml'), (Join-Path $shinraConfigRoot 'server-overrides.txt')))
    {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf))
        {
            throw "Current Classic+ config is missing: $path"
        }
    }
    $payloadTcc = Join-Path $profilePayload 'tcc'
    $payloadShinra = Join-Path $profilePayload 'shinra'
    New-Item -ItemType Directory -Path $payloadTcc,$payloadShinra -Force | Out-Null

    $tcc = Read-SharedText -Path $tccConfig | ConvertFrom-Json
    $accountHash = $tcc.PSObject.Properties['LastAccountNameHash']
    if ($null -ne $accountHash)
    {
        $accountHash.Value = ''
    }
    [IO.File]::WriteAllText(
            (Join-Path $payloadTcc 'tcc-settings.json'),
            ($tcc | ConvertTo-Json -Depth 100),
            [Text.UTF8Encoding]::new($false)
    )

    foreach ($name in @('hotkeys.xml', 'window.xml', 'window_backup.xml'))
    {
        [xml]$xml = Read-SharedText -Path (Join-Path $shinraConfigRoot $name)
        foreach ($node in $xml.SelectNodes('//token | //username'))
        {
            $node.InnerText = ''
        }
        $directory = $xml.SelectSingleNode('//excel_save_directory')
        if ($null -ne $directory)
        {
            $directory.InnerText = '__DOCUMENTS__\ShinraMeter\'
        }
        if ($name -eq 'hotkeys.xml')
        {
            $xml.hotkeys.paste.ctrl = 'True'
            $xml.hotkeys.paste.key = 'Home'
        }
        Write-XmlFile -Document $xml -Path (Join-Path $payloadShinra $name)
    }
    [IO.File]::WriteAllText(
            (Join-Path $payloadShinra 'server-overrides.txt'),
            (Read-SharedText -Path (Join-Path $shinraConfigRoot 'server-overrides.txt')),
            [Text.UTF8Encoding]::new($false)
    )
    Write-Host "Sanitized Classic+ profiles exported to $profilePayload" -ForegroundColor Green
}

function Show-UnifiedStatus
{
    Assert-Files
    $checks = @(Get-EngineChecks)
    $failed = @($checks | Where-Object { -not $_.Match })
    $locks = @($configFiles | ForEach-Object {
        $item = Get-Item -LiteralPath $_
        [pscustomobject]@{ File = $item.Name; ReadOnly = $item.IsReadOnly }
    })
    $activeD3D9 = Join-Path $binaryRoot 'd3d9.dll'
    $proxyD3D9 = Join-Path $binaryRoot 'd3d9_dxvk.dll'
    $reShadeConfig = Join-Path $binaryRoot 'ReShade.ini'
    $reShadeLog = Join-Path $binaryRoot 'ReShade.log'
    $proxyEnabled = Get-IniValue -Path $reShadeConfig -Section 'PROXY' -Key 'EnableProxyLibrary'
    $proxyLibrary = Get-IniValue -Path $reShadeConfig -Section 'PROXY' -Key 'ProxyLibrary'
    $logBytes = if (Test-Path -LiteralPath $reShadeLog)
    {
        (Get-Item -LiteralPath $reShadeLog).Length
    }
    else
    {
        0
    }
    $runtimeConfirmed = $false
    $runtimeModule = 'Not confirmed'
    $renderAPI = 'Not confirmed'
    if ($logBytes -gt 0)
    {
        $logText = Read-SharedText -Path $reShadeLog
        $runtimeConfirmed = $logText.Contains("Initializing crosire's ReShade")
        if ($logText -match "loaded from '([^']+)'")
        {
            $runtimeModule = Split-Path -Leaf $Matches[1]
        }
        if ( $logText.Contains('Direct3DCreate9'))
        {
            $renderAPI = 'D3D9'
        }
        elseif ($logText.Contains('D3D11CreateDevice'))
        {
            $renderAPI = 'D3D11'
        }
    }
    $dxvkRuntimeConfirmed = $false
    $dxvkLog = Join-Path $binaryRoot 'TERA_d3d9.log'
    if (Test-Path -LiteralPath $dxvkLog -PathType Leaf)
    {
        $tera = Get-Process -Name TERA -ErrorAction SilentlyContinue | Select-Object -First 1
        $dxvkRuntimeConfirmed = ($null -eq $tera -or (Get-Item -LiteralPath $dxvkLog).LastWriteTime -ge $tera.StartTime)
    }

    $d3d9Kind = Get-DllKind $activeD3D9
    $proxyKind = Get-DllKind $proxyD3D9
    $pipeline = if ($d3d9Kind -eq 'ReShade' -and $proxyKind -eq 'DXVK')
    {
        'ReShade D3D9 -> DXVK -> Vulkan'
    }
    elseif ($d3d9Kind -eq 'DXVK')
    {
        'DXVK only (D3D9)'
    }
    else
    {
        'Original or incomplete'
    }

    $presetPath = Join-Path $binaryRoot 'TERA_Natural_Clarity.ini'
    $cinematicDOFEnabled = $false
    if (Test-Path -LiteralPath $presetPath -PathType Leaf)
    {
        $firstLine = [IO.File]::ReadLines($presetPath) | Select-Object -First 1
        $cinematicDOFEnabled = $firstLine.Contains('CinematicDOF@CinematicDOF.fx')
    }
    $primaryResolution = Get-PrimaryDisplayResolution
    $shinraPaste = '<missing>'
    $shinraMuted = $false
    try
    {
        [xml]$hotkeys = Read-SharedText -Path (Join-Path $shinraConfigRoot 'hotkeys.xml')
        $shinraPaste = if ($hotkeys.hotkeys.paste.ctrl -ieq 'True')
        {
            "Ctrl+$( $hotkeys.hotkeys.paste.key )"
        }
        else
        {
            [string]$hotkeys.hotkeys.paste.key
        }
        [xml]$window = Read-SharedText -Path (Join-Path $shinraConfigRoot 'window.xml')
        $shinraMuted = $window.SelectSingleNode('//mute_sound').InnerText -ieq 'true'
    }
    catch
    {
    }

    [pscustomobject]@{
        TeraRoot = $TeraRoot
        EngineProfileHealthy = ($failed.Count -eq 0)
        EngineChecksPassed = $checks.Count - $failed.Count
        EngineChecksTotal = $checks.Count
        TexturePoolMB = Get-IniValue -Path $s1Engine -Section 'TextureStreaming' -Key 'PoolSize'
        ConfiguredPipeline = $pipeline
        ActiveD3D9 = $d3d9Kind
        D3D9ProxyTarget = $proxyKind
        ReShadeProxyEnabled = ($proxyEnabled -eq '1' -and $proxyLibrary -ieq '.\d3d9_dxvk.dll')
        ReShadeHomeKey = (Get-IniValue -Path $reShadeConfig -Section 'INPUT' -Key 'KeyOverlay')
        GenericDepthFormat = Get-IniValue -Path $reShadeConfig -Section 'DEPTH' -Key 'FilterFormat'
        GenericDepthResolution = "$( Get-IniValue -Path $reShadeConfig -Section 'DEPTH' -Key 'FilterResolutionWidth' )x$( Get-IniValue -Path $reShadeConfig -Section 'DEPTH' -Key 'FilterResolutionHeight' )"
        PrimaryDisplayResolution = "$( $primaryResolution.Width )x$( $primaryResolution.Height )"
        GenericDepthMatchesPrimaryDisplay = (
        (Get-IniValue -Path $reShadeConfig -Section 'DEPTH' -Key 'FilterResolutionWidth') -eq [string]$primaryResolution.Width -and
                (Get-IniValue -Path $reShadeConfig -Section 'DEPTH' -Key 'FilterResolutionHeight') -eq [string]$primaryResolution.Height
        )
        GenericDepthExactResolution = ((Get-IniValue -Path $reShadeConfig -Section 'DEPTH' -Key 'UseAspectRatioHeuristics') -eq '3')
        CinematicDOFInstalled = (Test-Path -LiteralPath (Join-Path $binaryRoot 'reshade-shaders\Shaders\OtisFX\CinematicDOF.fx') -PathType Leaf)
        CinematicDOFEnabled = $cinematicDOFEnabled
        TeraFXAA = Get-IniValue -Path $systemSettings -Section 'SystemSettings' -Key 'FXAA'
        ConfigsLocked = (@($locks | Where-Object { -not $_.ReadOnly }).Count -eq 0)
        DetectedRenderAPI = $renderAPI
        ReShadeRuntimeConfirmed = $runtimeConfirmed
        ReShadeRuntimeModule = $runtimeModule
        DXVKRuntimeConfirmed = $dxvkRuntimeConfirmed
        ReShadeLogBytes = $logBytes
        TCCProfileInstalled = (Test-Path -LiteralPath $tccConfig -PathType Leaf)
        ShinraProfileInstalled = (Test-Path -LiteralPath (Join-Path $shinraConfigRoot 'window.xml') -PathType Leaf)
        ShinraPasteShortcut = $shinraPaste
        ShinraAudioMuted = $shinraMuted
    } | Format-List

    if ($failed.Count -gt 0)
    {
        Write-Host 'Engine profile mismatches:' -ForegroundColor Yellow
        $failed | Format-Table File, Section, Key, Expected, Actual -AutoSize
    }
    Write-Host 'Config lock state:'
    $locks | Format-Table -AutoSize
}

function Invoke-ReShadeAction
{
    param([ValidateSet('Validate', 'Enable', 'Disable', 'Restore')][string]$ReShadeAction)
    & $reShadeManager -Action $ReShadeAction -TeraRoot $TeraRoot
}

if ([string]::IsNullOrWhiteSpace($LogPath))
{
    $LogPath = Join-Path $PSScriptRoot ("logs\install-{0}.log" -f (Get-Date).ToString('yyyyMMdd-HHmmss'))
}
$LogPath = [IO.Path]::GetFullPath($LogPath)

if ($Action -ne 'Status' -and -not (Test-IsAdministrator))
{
    Restart-Elevated
}

New-Item -ItemType Directory -Path (Split-Path -Parent $LogPath) -Force | Out-Null

Write-Host 'TERA Complete installer' -ForegroundColor Cyan
Write-Host "Action: $Action"
Write-Host "Log: $LogPath"
$bootstrapLine = '{0} [INSTALLER] Action={1}; TeraRoot={2}; IncludeClassicPlus={3}{4}' -f `
    (Get-Date).ToString('o'), $Action, $TeraRoot, [bool]$IncludeClassicPlus, [Environment]::NewLine
[IO.File]::AppendAllText($LogPath, $bootstrapLine, [Text.UTF8Encoding]::new($false))

if ($Action -eq 'Apply' -and -not $SkipUpdate)
{
    if (-not (Test-Path -LiteralPath $updateSupport -PathType Leaf))
    {
        $missingUpdateModule = "Update module is missing: $updateSupport"
        [IO.File]::AppendAllText($LogPath, $missingUpdateModule + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
        Write-Error $missingUpdateModule -ErrorAction Continue
        exit 1
    }
    . $updateSupport
    try
    {
        $updateResult = Invoke-TCOSelfUpdate -PackageRoot $PSScriptRoot -LogPath $LogPath
    }
    catch
    {
        $updateFailure = $_ | Out-String
        [IO.File]::AppendAllText($LogPath, $updateFailure, [Text.UTF8Encoding]::new($false))
        Write-Error $updateFailure -ErrorAction Continue
        exit 1
    }

    if ($updateResult.Updated)
    {
        Write-Host "Restarting with TCO release $($updateResult.Tag)..." -ForegroundColor Cyan
        $restartArguments = @(
            '-NoProfile',
            '-ExecutionPolicy', 'Bypass',
            '-File', $PSCommandPath,
            '-Action', $Action,
            '-TeraRoot', $TeraRoot,
            '-LogPath', $LogPath,
            '-SkipUpdate'
        )
        if ($IncludeClassicPlus)
        {
            $restartArguments += '-IncludeClassicPlus'
        }
        & powershell.exe @restartArguments
        exit $LASTEXITCODE
    }
    $SkipUpdate = $true
}

$transcriptStarted = $false
Start-Transcript -Path $LogPath -Append | Out-Null
$transcriptStarted = $true

try
{
    Assert-Files
    . $displayResolutionScript
    . $engineProfileSupport
    switch ($Action)
    {
        'Apply' {
            Assert-Closed -ClassicPlus:$IncludeClassicPlus
            if ($IncludeClassicPlus)
            {
                Assert-ClassicPlusInstalled
            }
            Invoke-ReShadeAction -ReShadeAction Validate
            Set-ConfigLock -Locked $false
            try
            {
                & $stableEngineScript -TeraRoot $TeraRoot -EngineProfilePath $engineProfilePath
                Invoke-ReShadeAction -ReShadeAction Enable
                if ($IncludeClassicPlus)
                {
                    Install-ClassicPlusProfiles
                }
            }
            finally
            {
                Set-ConfigLock -Locked $true
            }
            if ($IncludeClassicPlus)
            {
                Write-Host 'TERA engine, DXVK, ReShade, TCC, and Shinra profiles applied.' -ForegroundColor Green
            }
            else
            {
                Write-Host 'TERA engine, DXVK, and ReShade profile applied.' -ForegroundColor Green
            }
            Show-UnifiedStatus
        }
        'ApplyClassicPlus' {
            Assert-ClassicPlusClosed
            Install-ClassicPlusProfiles
            Show-UnifiedStatus
        }
        'ExportClassicPlus' {
            Export-ClassicPlusProfiles
            Write-Host 'Export excludes the last-account hash, usernames, tokens, and absolute user paths.' -ForegroundColor Yellow
        }
        'EnableReShade' {
            Assert-Closed -ClassicPlus:$IncludeClassicPlus
            if ($IncludeClassicPlus)
            {
                Assert-ClassicPlusInstalled
            }
            Invoke-ReShadeAction -ReShadeAction Validate
            Set-ConfigLock -Locked $false
            try
            {
                Invoke-ReShadeAction -ReShadeAction Enable
                if ($IncludeClassicPlus)
                {
                    Install-ClassicPlusProfiles
                }
            }
            finally
            {
                Set-ConfigLock -Locked $true
            }
            Show-UnifiedStatus
        }
        'DisableReShade' {
            Assert-Closed
            Set-ConfigLock -Locked $false
            try
            {
                Invoke-ReShadeAction -ReShadeAction Disable
            }
            finally
            {
                Set-ConfigLock -Locked $true
            }
            Show-UnifiedStatus
        }
        'RestoreReShade' {
            Assert-Closed
            Set-ConfigLock -Locked $false
            try
            {
                Invoke-ReShadeAction -ReShadeAction Restore
            }
            finally
            {
                Set-ConfigLock -Locked $true
            }
            Show-UnifiedStatus
        }
        'LockConfigs' {
            Assert-Closed
            Set-ConfigLock -Locked $true
            Show-UnifiedStatus
        }
        'UnlockConfigs' {
            Assert-Closed
            Set-ConfigLock -Locked $false
            Show-UnifiedStatus
        }
        'Status' {
            Show-UnifiedStatus
        }
    }
}
catch
{
    $message = $_ | Out-String
    $errorLogPath = Join-Path $TeraRoot 'ReShadeTools\complete-manager-error.log'
    try
    {
        New-Item -ItemType Directory -Path (Split-Path -Parent $errorLogPath) -Force | Out-Null
        [System.IO.File]::WriteAllText($errorLogPath, $message,[System.Text.UTF8Encoding]::new($false))
    }
    catch
    {
    }
    Write-Error $message -ErrorAction Continue
    if ($transcriptStarted)
    {
        Stop-Transcript | Out-Null
    }
    exit 1
}

if ($transcriptStarted)
{
    Stop-Transcript | Out-Null
}
