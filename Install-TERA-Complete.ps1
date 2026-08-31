#Requires -Version 5.1

param(
    [Parameter(Mandatory = $false)]
    [ValidateSet('Apply', 'ApplyClassicPlus', 'ExportClassicPlus', 'EnableReShade', 'DisableReShade', 'RestoreReShade', 'LockConfigs', 'UnlockConfigs', 'Status')]
    [string]$Action = 'Status',

    [Parameter(Mandatory = $false)]
    [string]$TeraRoot = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

if ([string]::IsNullOrWhiteSpace($TeraRoot)) {
    $TeraRoot = if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Binaries') -PathType Container) {
        $PSScriptRoot
    } else {
        Split-Path -Parent $PSScriptRoot
    }
}
$TeraRoot = [System.IO.Path]::GetFullPath($TeraRoot)
$modulesRoot = if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'modules') -PathType Container) {
    Join-Path $PSScriptRoot 'modules'
} else {
    $TeraRoot
}
$stableEngineScript = Join-Path $modulesRoot 'apply_tera_final_stable.ps1'
$reShadeManager = Join-Path $modulesRoot 'tera_graphics_pipeline.ps1'
$portableProfilePayload = Join-Path $PSScriptRoot 'payload\classicplus'
$profilePayload = if (Test-Path -LiteralPath $portableProfilePayload -PathType Container) {
    $portableProfilePayload
} else {
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

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Restart-Elevated {
    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', ('"{0}"' -f $PSCommandPath),
        '-Action', $Action,
        '-TeraRoot', ('"{0}"' -f $TeraRoot)
    ) -join ' '
    $process = Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -Verb RunAs -Wait -PassThru -WindowStyle Hidden
    exit $process.ExitCode
}

function Assert-Closed {
    $running = Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessName -in @('TERA', 'noctenium', 'TERA Europe Classic+ Launcher', 'TCC', 'ShinraMeter')
    }
    if ($running) {
        $names = ($running.ProcessName | Sort-Object -Unique) -join ', '
        throw "Close TERA, Noctenium, TCC, Shinra Meter, and the launcher first. Running: $names"
    }
}

function Assert-ClassicPlusClosed {
    $running = Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessName -in @('TERA', 'noctenium', 'TCC', 'ShinraMeter')
    }
    if ($running) {
        $names = ($running.ProcessName | Sort-Object -Unique) -join ', '
        throw "Close TERA, Noctenium, TCC, and Shinra Meter first. Running: $names"
    }
}

function Assert-ClassicPlusPayload {
    $required = @(
        (Join-Path $profilePayload 'tcc\tcc-settings.json'),
        (Join-Path $profilePayload 'shinra\hotkeys.xml'),
        (Join-Path $profilePayload 'shinra\window.xml'),
        (Join-Path $profilePayload 'shinra\window_backup.xml'),
        (Join-Path $profilePayload 'shinra\server-overrides.txt')
    )
    foreach ($path in $required) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Classic+ profile payload is incomplete: $path"
        }
    }
}

function Assert-Files {
    foreach ($path in @($stableEngineScript, $reShadeManager) + $configFiles) {
        if (-not (Test-Path -LiteralPath $path)) { throw "Required file is missing: $path" }
    }
}

function Set-ConfigLock {
    param([bool]$Locked)
    foreach ($path in $configFiles) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            (Get-Item -LiteralPath $path).IsReadOnly = $Locked
        }
    }
}

function Get-IniValue {
    param([string]$Path, [string]$Section, [string]$Key)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    $inSection = $false
    foreach ($line in [System.IO.File]::ReadAllLines($Path)) {
        if ($line -match '^\s*\[(.+?)\]\s*$') {
            $inSection = $Matches[1] -ieq $Section
            continue
        }
        if ($inSection -and $line -match ('^\s*{0}\s*=\s*(.*?)\s*$' -f [regex]::Escape($Key))) {
            return $Matches[1]
        }
    }
    return $null
}

function Test-ExpectedSetting {
    param([string]$Path, [string]$Section, [string]$Key, [string]$Expected)
    $actual = Get-IniValue -Path $Path -Section $Section -Key $Key
    return [pscustomobject]@{
        File = Split-Path -Leaf $Path
        Section = $Section
        Key = $Key
        Expected = $Expected
        Actual = if ($null -eq $actual) { '<missing>' } else { $actual }
        Match = ($actual -ieq $Expected)
    }
}

function Get-EngineChecks {
    $checks = [System.Collections.Generic.List[object]]::new()
    $specs = @(
        @($s1Engine, 'Engine.Engine', 'AllowShadowVolumes', 'True'),
        @($s1Engine, 'Engine.Engine', 'bSmoothFrameRate', 'True'),
        @($s1Engine, 'Engine.Engine', 'MaxSmoothedFrameRate', '141'),
        @($s1Engine, 'Engine.GameEngine', 'CacheSizeMegs', '1024'),
        @($s1Engine, 'DevOptions.Shaders', 'bAllowMultiThreadedShaderCompile', 'True'),
        @($s1Engine, 'AppCompat', 'CompatLevelComposite', '5'),
        @($s1Engine, 'AppCompat', 'CPUNumLogicalProcessors', '24'),
        @($s1Engine, 'TextureStreaming', 'PoolSize', '4096'),
        @($s1Engine, 'TextureStreaming', 'UsePriorityStreaming', 'True'),
        @($s1Engine, 'TextureStreaming', 'UseDynamicStreaming', 'True'),
        @($s1Engine, 'TextureStreaming', 'bEnableAsyncDefrag', 'False'),
        @($s1Engine, 'TextureStreaming', 'bEnableAsyncReallocation', 'False'),
        @($systemSettings, 'SystemSettings', 'DynamicShadows', 'True'),
        @($systemSettings, 'SystemSettings', 'MotionBlur', 'False'),
        @($systemSettings, 'SystemSettings', 'MotionBlurPause', 'False'),
        @($systemSettings, 'SystemSettings', 'MotionBlurSkinning', '0'),
        @($systemSettings, 'SystemSettings', 'DepthOfField', 'False'),
        @($systemSettings, 'SystemSettings', 'AllowRadialBlur', 'False'),
        @($systemSettings, 'SystemSettings', 'Bloom', 'True'),
        @($systemSettings, 'SystemSettings', 'LensFlares', 'True'),
        @($systemSettings, 'SystemSettings', 'bAllowTemporalAA', 'False'),
        @($systemSettings, 'SystemSettings', 'MobilePostProcessBlurAmount', '0.0'),
        @($systemSettings, 'SystemSettings', 'AmbientOcclusion', 'True'),
        @($systemSettings, 'SystemSettings', 'UseHighQualityBloom', 'True'),
        @($systemSettings, 'SystemSettings', 'FloatingPointRenderTargets', 'True'),
        @($systemSettings, 'SystemSettings', 'SkeletalMeshLODBias', '-1'),
        @($systemSettings, 'SystemSettings', 'ParticleLODBias', '-1'),
        @($systemSettings, 'SystemSettings', 'DetailMode', '3'),
        @($systemSettings, 'SystemSettings', 'MaxAnisotropy', '16'),
        @($systemSettings, 'SystemSettings', 'MaxShadowResolution', '4096'),
        @($systemSettings, 'SystemSettings', 'MaxWholeSceneDominantShadowResolution', '4096'),
        @($systemSettings, 'SystemSettings', 'bEnablePSSMShadows', 'True'),
        @($systemSettings, 'SystemSettings', 'ScreenPercentage', '100.000000'),
        @($option, 'VIDEO', 'AUTO_FRAME_RATE_OPTIMIZE_CHECK', 'False'),
        @($option, 'VIDEO', 'DISPLAY_QUALITY_PRESET_INDEX', '6'),
        @($option, 'VIDEO', 'CHARACTER_LOD', '2'),
        @($option, 'VIDEO', 'BACKGROUND_DISPLAY_DISTANCE', '6'),
        @($option, 'VIDEO', 'GLOBAL_FOLIAGE', '4'),
        @($option, 'VIDEO', 'POSTPROCESS_QUALITY_INDEX', '2'),
        @($baseInput, 'Engine.PlayerInput', 'bEnableMouseSmoothing', 'True'),
        @($s1Input, 'Engine.PlayerInput', 'bEnableMouseSmoothing', 'True')
    )
    foreach ($spec in $specs) {
        [void]$checks.Add((Test-ExpectedSetting -Path $spec[0] -Section $spec[1] -Key $spec[2] -Expected $spec[3]))
    }
    return $checks.ToArray()
}

function Get-DllKind {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return 'Missing' }
    $product = (Get-Item -LiteralPath $Path).VersionInfo.ProductName
    if ($product -eq 'DXVK') { return 'DXVK' }
    if ($product -eq 'ReShade') { return 'ReShade' }
    return "Unknown ($product)"
}

function Read-SharedText {
    param([string]$Path)
    $stream = [System.IO.FileStream]::new(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete
    )
    try {
        $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::UTF8, $true, 4096, $true)
        try { return $reader.ReadToEnd() }
        finally { $reader.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Write-XmlFile {
    param([xml]$Document, [string]$Path)
    $settings = [System.Xml.XmlWriterSettings]::new()
    $settings.Encoding = [System.Text.UTF8Encoding]::new($false)
    $settings.Indent = $true
    $settings.NewLineChars = "`r`n"
    $writer = [System.Xml.XmlWriter]::Create($Path, $settings)
    try { $Document.Save($writer) }
    finally { $writer.Dispose() }
}

function Install-ClassicPlusProfiles {
    Assert-ClassicPlusPayload
    if (-not (Test-Path -LiteralPath $tccRoot -PathType Container)) {
        throw "TCC is not installed: $tccRoot"
    }
    if (-not (Test-Path -LiteralPath $shinraConfigRoot -PathType Container)) {
        throw "Shinra Meter is not installed: $shinraConfigRoot"
    }

    Copy-Item -LiteralPath (Join-Path $profilePayload 'tcc\tcc-settings.json') -Destination $tccConfig -Force
    foreach ($name in @('hotkeys.xml', 'window.xml', 'window_backup.xml', 'server-overrides.txt')) {
        Copy-Item -LiteralPath (Join-Path $profilePayload "shinra\$name") -Destination (Join-Path $shinraConfigRoot $name) -Force
    }

    $documents = [Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments)
    $exportPath = (Join-Path $documents 'ShinraMeter') + [IO.Path]::DirectorySeparatorChar
    foreach ($name in @('window.xml', 'window_backup.xml')) {
        $path = Join-Path $shinraConfigRoot $name
        [xml]$xml = [IO.File]::ReadAllText($path)
        $directory = $xml.SelectSingleNode('//excel_save_directory')
        if ($null -ne $directory) { $directory.InnerText = $exportPath }
        $mute = $xml.SelectSingleNode('//mute_sound')
        if ($null -ne $mute) { $mute.InnerText = 'true' }
        Write-XmlFile -Document $xml -Path $path
    }

    $hotkeyPath = Join-Path $shinraConfigRoot 'hotkeys.xml'
    [xml]$hotkeys = [IO.File]::ReadAllText($hotkeyPath)
    $hotkeys.hotkeys.paste.ctrl = 'True'
    $hotkeys.hotkeys.paste.key = 'Home'
    Write-XmlFile -Document $hotkeys -Path $hotkeyPath
    Write-Host 'Classic+ TCC and Shinra profiles applied. ReShade uses Home; Shinra paste uses Ctrl+Home.' -ForegroundColor Green
}

function Export-ClassicPlusProfiles {
    foreach ($path in @($tccConfig, (Join-Path $shinraConfigRoot 'hotkeys.xml'), (Join-Path $shinraConfigRoot 'window.xml'),
        (Join-Path $shinraConfigRoot 'window_backup.xml'), (Join-Path $shinraConfigRoot 'server-overrides.txt'))) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Current Classic+ config is missing: $path" }
    }
    $payloadTcc = Join-Path $profilePayload 'tcc'
    $payloadShinra = Join-Path $profilePayload 'shinra'
    New-Item -ItemType Directory -Path $payloadTcc,$payloadShinra -Force | Out-Null

    $tcc = Read-SharedText -Path $tccConfig | ConvertFrom-Json
    $accountHash = $tcc.PSObject.Properties['LastAccountNameHash']
    if ($null -ne $accountHash) { $accountHash.Value = '' }
    [IO.File]::WriteAllText(
        (Join-Path $payloadTcc 'tcc-settings.json'),
        ($tcc | ConvertTo-Json -Depth 100),
        [Text.UTF8Encoding]::new($false)
    )

    foreach ($name in @('hotkeys.xml', 'window.xml', 'window_backup.xml')) {
        [xml]$xml = Read-SharedText -Path (Join-Path $shinraConfigRoot $name)
        foreach ($node in $xml.SelectNodes('//token | //username')) { $node.InnerText = '' }
        $directory = $xml.SelectSingleNode('//excel_save_directory')
        if ($null -ne $directory) { $directory.InnerText = '__DOCUMENTS__\ShinraMeter\' }
        if ($name -eq 'hotkeys.xml') {
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

function Show-UnifiedStatus {
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
    $logBytes = if (Test-Path -LiteralPath $reShadeLog) { (Get-Item -LiteralPath $reShadeLog).Length } else { 0 }
    $runtimeConfirmed = $false
    $runtimeModule = 'Not confirmed'
    $renderAPI = 'Not confirmed'
    if ($logBytes -gt 0) {
        $logText = Read-SharedText -Path $reShadeLog
        $runtimeConfirmed = $logText.Contains("Initializing crosire's ReShade")
        if ($logText -match "loaded from '([^']+)'") { $runtimeModule = Split-Path -Leaf $Matches[1] }
        if ($logText.Contains('Direct3DCreate9')) { $renderAPI = 'D3D9' }
        elseif ($logText.Contains('D3D11CreateDevice')) { $renderAPI = 'D3D11' }
    }
    $dxvkRuntimeConfirmed = $false
    $dxvkLog = Join-Path $binaryRoot 'TERA_d3d9.log'
    if (Test-Path -LiteralPath $dxvkLog -PathType Leaf) {
        $tera = Get-Process -Name TERA -ErrorAction SilentlyContinue | Select-Object -First 1
        $dxvkRuntimeConfirmed = ($null -eq $tera -or (Get-Item -LiteralPath $dxvkLog).LastWriteTime -ge $tera.StartTime)
    }

    $d3d9Kind = Get-DllKind $activeD3D9
    $proxyKind = Get-DllKind $proxyD3D9
    $pipeline = if ($d3d9Kind -eq 'ReShade' -and $proxyKind -eq 'DXVK') {
        'ReShade D3D9 -> DXVK -> Vulkan'
    } elseif ($d3d9Kind -eq 'DXVK') {
        'DXVK only (D3D9)'
    } else { 'Original or incomplete' }

    $presetPath = Join-Path $binaryRoot 'TERA_Natural_Clarity.ini'
    $cinematicDOFEnabled = $false
    if (Test-Path -LiteralPath $presetPath -PathType Leaf) {
        $firstLine = [IO.File]::ReadLines($presetPath) | Select-Object -First 1
        $cinematicDOFEnabled = $firstLine.Contains('CinematicDOF@CinematicDOF.fx')
    }
    $shinraPaste = '<missing>'
    $shinraMuted = $false
    try {
        [xml]$hotkeys = Read-SharedText -Path (Join-Path $shinraConfigRoot 'hotkeys.xml')
        $shinraPaste = if ($hotkeys.hotkeys.paste.ctrl -ieq 'True') { "Ctrl+$($hotkeys.hotkeys.paste.key)" } else { [string]$hotkeys.hotkeys.paste.key }
        [xml]$window = Read-SharedText -Path (Join-Path $shinraConfigRoot 'window.xml')
        $shinraMuted = $window.SelectSingleNode('//mute_sound').InnerText -ieq 'true'
    } catch {}

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
        GenericDepthResolution = "$(Get-IniValue -Path $reShadeConfig -Section 'DEPTH' -Key 'FilterResolutionWidth')x$(Get-IniValue -Path $reShadeConfig -Section 'DEPTH' -Key 'FilterResolutionHeight')"
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

    if ($failed.Count -gt 0) {
        Write-Host 'Engine profile mismatches:' -ForegroundColor Yellow
        $failed | Format-Table File, Section, Key, Expected, Actual -AutoSize
    }
    Write-Host 'Config lock state:'
    $locks | Format-Table -AutoSize
}

function Invoke-ReShadeAction {
    param([ValidateSet('Enable', 'Disable', 'Restore')][string]$ReShadeAction)
    & $reShadeManager -Action $ReShadeAction -TeraRoot $TeraRoot
    if ($LASTEXITCODE -notin @($null, 0)) { throw "ReShade manager failed with exit code $LASTEXITCODE" }
}

if ($Action -ne 'Status' -and -not (Test-IsAdministrator)) { Restart-Elevated }

try {
    Assert-Files
    switch ($Action) {
        'Apply' {
            Assert-Closed
            Set-ConfigLock -Locked $false
            try {
                & $stableEngineScript -TeraRoot $TeraRoot
                if ($LASTEXITCODE -notin @($null, 0)) { throw "Stable engine script failed with exit code $LASTEXITCODE" }
                Invoke-ReShadeAction -ReShadeAction Enable
                Install-ClassicPlusProfiles
            }
            finally {
                Set-ConfigLock -Locked $true
            }
            Write-Host 'Complete TERA engine, DXVK, ReShade, TCC, and Shinra profile applied.' -ForegroundColor Green
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
            Assert-Closed
            Set-ConfigLock -Locked $false
            try { Invoke-ReShadeAction -ReShadeAction Enable }
            finally { Set-ConfigLock -Locked $true }
            Show-UnifiedStatus
        }
        'DisableReShade' {
            Assert-Closed
            Set-ConfigLock -Locked $false
            try { Invoke-ReShadeAction -ReShadeAction Disable }
            finally { Set-ConfigLock -Locked $true }
            Show-UnifiedStatus
        }
        'RestoreReShade' {
            Assert-Closed
            Set-ConfigLock -Locked $false
            try { Invoke-ReShadeAction -ReShadeAction Restore }
            finally { Set-ConfigLock -Locked $true }
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
catch {
    $message = $_ | Out-String
    $logPath = Join-Path $TeraRoot 'ReShadeTools\complete-manager-error.log'
    try { [System.IO.File]::WriteAllText($logPath, $message, [System.Text.UTF8Encoding]::new($false)) } catch {}
    Write-Error $message
    exit 1
}
