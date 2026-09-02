#Requires -Version 5.1

param(
    [Parameter(Mandatory = $false)]
    [ValidateSet('Validate', 'Enable', 'Disable', 'Restore', 'Status')]
    [string]$Action = 'Status',

    [Parameter(Mandatory = $false)]
    [ValidateNotNullOrEmpty()]
    [string]$TeraRoot = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$TeraRoot = [System.IO.Path]::GetFullPath($TeraRoot)
$binaryRoot = Join-Path $TeraRoot 'Binaries'
$toolsRoot = Join-Path $TeraRoot 'ReShadeTools'
$packageRoot = Split-Path -Parent $PSScriptRoot
$portablePayload = Join-Path $packageRoot 'payload\reshade'
$payloadRoot = if (Test-Path -LiteralPath $portablePayload -PathType Container) {
    $portablePayload
} else {
    Join-Path $toolsRoot 'payload'
}
$teraExe = Join-Path $binaryRoot 'TERA.exe'
$activeD3D9 = Join-Path $binaryRoot 'd3d9.dll'
$proxyDXVK = Join-Path $binaryRoot 'd3d9_dxvk.dll'
$portableReShade = Join-Path $packageRoot 'payload\runtime\ReShade64.dll'
$installedReShade = Join-Path $toolsRoot 'runtime\ReShade64-FullAddon.dll'
$reShadeSource = if (Test-Path -LiteralPath $portableReShade -PathType Leaf) {
    $portableReShade
} else {
    $installedReShade
}
$portableDXVK = Join-Path $packageRoot 'payload\dxvk\d3d9.dll'
$installedDXVK = Join-Path $toolsRoot 'runtime\d3d9.dll'
$dxvkSource = if (Test-Path -LiteralPath $portableDXVK -PathType Leaf) {
    $portableDXVK
} else {
    $installedDXVK
}
$reShadeConfig = Join-Path $binaryRoot 'ReShade.ini'
$disabledConfig = Join-Path $binaryRoot 'ReShade.ini.proxy-disabled'
$reShadePreset = Join-Path $binaryRoot 'TERA_Natural_Clarity.ini'
$reShadeShaders = Join-Path $binaryRoot 'reshade-shaders'
$systemSettings = Join-Path $TeraRoot 'S1Game\Config\S1SystemSettings.ini'
$payloadConfig = Join-Path $payloadRoot 'ReShade.ini'
$payloadPreset = Join-Path $payloadRoot 'TERA_Natural_Clarity.ini'
$payloadShaders = Join-Path $payloadRoot 'reshade-shaders'
$portableEngineProfile = Join-Path $packageRoot 'payload\engine-profile.json'
$engineProfilePath = if (Test-Path -LiteralPath $portableEngineProfile -PathType Leaf) {
    $portableEngineProfile
} else {
    Join-Path $toolsRoot 'payload\engine-profile.json'
}
$legacyState = Join-Path $toolsRoot 'tera-reshade-state.json'
$proxyState = Join-Path $toolsRoot 'tera-reshade-proxy-state.json'
$backupRoot = Join-Path $toolsRoot 'tera-reshade-original'
$displayResolutionScript = Join-Path $PSScriptRoot 'display_resolution.ps1'
$engineProfileSupport = Join-Path $PSScriptRoot 'engine_profile.ps1'
$engineFileMap = [ordered]@{
    'S1Engine.ini' = Join-Path $TeraRoot 'S1Game\Config\S1Engine.ini'
    'S1SystemSettings.ini' = $systemSettings
    'S1Option.ini' = Join-Path $TeraRoot 'S1Game\Config\S1Option.ini'
    'BaseInput.ini' = Join-Path $TeraRoot 'Engine\Config\BaseInput.ini'
    'S1Input.ini' = Join-Path $TeraRoot 'S1Game\Config\S1Input.ini'
}
$layer64Path = 'HKLM:\SOFTWARE\Khronos\Vulkan\ImplicitLayers'
$layer32Path = 'HKLM:\SOFTWARE\WOW6432Node\Khronos\Vulkan\ImplicitLayers'
$layer64Name = 'C:\ProgramData\ReShade\ReShade64.json'
$layer32Name = 'C:\ProgramData\ReShade\ReShade32.json'

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

function Assert-TeraClosed {
    $running = Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessName -in @('TERA', 'noctenium', 'TERA Europe Classic+ Launcher')
    }
    if ($running) {
        $names = ($running.ProcessName | Sort-Object -Unique) -join ', '
        throw "Close TERA, Noctenium, and the launcher before changing the graphics chain. Running: $names"
    }
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

function Set-IniValue {
    param([string]$Path, [string]$Section, [string]$Key, [string]$Value)
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
            if ($Matches[1] -ieq $Section) { $sectionStart = $i }
        }
    }
    if ($sectionStart -lt 0) {
        [void]$lines.Add('')
        [void]$lines.Add("[$Section]")
        $sectionStart = $lines.Count - 1
        $sectionEnd = $lines.Count
    }
    $matchCount = 0
    for ($i = $sectionStart + 1; $i -lt $sectionEnd; $i++) {
        if ($lines[$i] -match ('^\s*{0}\s*=' -f [regex]::Escape($Key))) {
            $lines[$i] = "$Key=$Value"
            $matchCount++
        }
    }
    if ($matchCount -gt 1) { throw "Ambiguous duplicate $Key entries in [$Section] of $Path" }
    if ($matchCount -eq 0) { $lines.Insert($sectionEnd, "$Key=$Value") }
    [System.IO.File]::WriteAllLines($Path, $lines, [System.Text.UTF8Encoding]::new($true))
}

function Get-LayerValue {
    param([string]$RegistryPath, [string]$Name)
    if (-not (Test-Path $RegistryPath)) { return $null }
    $properties = Get-ItemProperty -Path $RegistryPath
    $property = $properties.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return [int]$property.Value
}

function Set-LayerValue {
    param([string]$RegistryPath, [string]$Name, [Nullable[int]]$Value)
    if ($null -eq $Value) { return }
    if (-not (Test-Path $RegistryPath)) { return }
    $properties = Get-ItemProperty -Path $RegistryPath
    if ($null -eq $properties.PSObject.Properties[$Name]) { return }
    New-ItemProperty -Path $RegistryPath -Name $Name -PropertyType DWord -Value $Value -Force | Out-Null
}

function Get-OriginalFXAA {
    if (Test-Path -LiteralPath $proxyState -PathType Leaf) {
        return [string](Get-Content -LiteralPath $proxyState -Raw | ConvertFrom-Json).OriginalFXAA
    }
    if (Test-Path -LiteralPath $legacyState -PathType Leaf) {
        return [string](Get-Content -LiteralPath $legacyState -Raw | ConvertFrom-Json).OriginalFXAA
    }
    return [string](Get-IniValue -Path $systemSettings -Section 'SystemSettings' -Key 'FXAA')
}

function Save-ProxyState {
    if (Test-Path -LiteralPath $proxyState -PathType Leaf) { return }
    New-Item -ItemType Directory -Path $toolsRoot -Force | Out-Null
    $activeKind = Get-DllKind $activeD3D9
    $dxvkPath = if ($activeKind -eq 'DXVK') {
        $activeD3D9
    } elseif ((Get-DllKind $proxyDXVK) -eq 'DXVK') {
        $proxyDXVK
    } else {
        $dxvkSource
    }
    if ((Get-DllKind $dxvkPath) -ne 'DXVK') { throw 'Cannot identify the original DXVK DLL.' }

    $originalBackup = $null
    if ($activeKind -like 'Unknown*') {
        New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
        $originalBackup = Join-Path $backupRoot 'd3d9.original.dll'
        Copy-Item -LiteralPath $activeD3D9 -Destination $originalBackup -Force
    }
    $state = [ordered]@{
        Schema = 2
        CreatedAt = (Get-Date).ToString('o')
        TeraRoot = $TeraRoot
        OriginalD3D9Kind = $activeKind
        OriginalD3D9Backup = $originalBackup
        OriginalFXAA = Get-OriginalFXAA
        DXVKSHA256 = (Get-FileHash -LiteralPath $dxvkPath -Algorithm SHA256).Hash
        ReShadeSHA256 = (Get-FileHash -LiteralPath $reShadeSource -Algorithm SHA256).Hash
        VulkanLayer64 = Get-LayerValue -RegistryPath $layer64Path -Name $layer64Name
        VulkanLayer32 = Get-LayerValue -RegistryPath $layer32Path -Name $layer32Name
    }
    [System.IO.File]::WriteAllText($proxyState, ($state | ConvertTo-Json), [System.Text.UTF8Encoding]::new($false))
}

function Get-ProxyState {
    if (-not (Test-Path -LiteralPath $proxyState -PathType Leaf)) { return $null }
    return Get-Content -LiteralPath $proxyState -Raw | ConvertFrom-Json
}

function Assert-BaseFiles {
    foreach ($path in @($teraExe, $systemSettings, $reShadeSource, $dxvkSource, $payloadConfig, $payloadPreset, $payloadShaders)) {
        if (-not (Test-Path -LiteralPath $path)) { throw "Required file is missing: $path" }
    }
}

function Assert-GraphicsPayload {
    Assert-BaseFiles
    if ((Get-DllKind $reShadeSource) -ne 'ReShade') {
        throw "Bundled ReShade runtime is invalid or unrecognized: $reShadeSource"
    }
    if ((Get-DllKind $dxvkSource) -ne 'DXVK') {
        throw "Bundled DXVK runtime is invalid or unrecognized: $dxvkSource"
    }
    $resolution = Get-PrimaryDisplayResolution
    Write-Host "Graphics payload validated for primary display $($resolution.Width)x$($resolution.Height)."
}

function Assert-ProxyPipelineInstalled {
    $issues = [System.Collections.Generic.List[string]]::new()
    $state = Get-ProxyState
    if ($null -eq $state) {
        [void]$issues.Add('Proxy state was not created.')
    }
    if ((Get-DllKind $activeD3D9) -ne 'ReShade') {
        [void]$issues.Add('Binaries\d3d9.dll is not ReShade.')
    }
    if ((Get-DllKind $proxyDXVK) -ne 'DXVK') {
        [void]$issues.Add('Binaries\d3d9_dxvk.dll is not DXVK.')
    }
    if ($null -ne $state -and (Test-Path -LiteralPath $activeD3D9 -PathType Leaf)) {
        $activeHash = (Get-FileHash -LiteralPath $activeD3D9 -Algorithm SHA256).Hash
        $expectedReShadeHash = (Get-FileHash -LiteralPath $reShadeSource -Algorithm SHA256).Hash
        if ($activeHash -ne $expectedReShadeHash) {
            [void]$issues.Add('Installed ReShade hash does not match the bundled runtime.')
        }
    }
    if ($null -ne $state -and (Test-Path -LiteralPath $proxyDXVK -PathType Leaf)) {
        $proxyHash = (Get-FileHash -LiteralPath $proxyDXVK -Algorithm SHA256).Hash
        if ($proxyHash -ne [string]$state.DXVKSHA256) {
            [void]$issues.Add('Installed DXVK hash does not match the recorded source runtime.')
        }
    }
    if ((Get-IniValue -Path $reShadeConfig -Section 'PROXY' -Key 'EnableProxyLibrary') -ne '1') {
        [void]$issues.Add('ReShade proxy loading is not enabled.')
    }
    if ((Get-IniValue -Path $reShadeConfig -Section 'PROXY' -Key 'ProxyLibrary') -ine '.\d3d9_dxvk.dll') {
        [void]$issues.Add('ReShade does not target d3d9_dxvk.dll.')
    }
    $resolution = Get-PrimaryDisplayResolution
    if ((Get-IniValue -Path $reShadeConfig -Section 'DEPTH' -Key 'FilterResolutionWidth') -ne [string]$resolution.Width -or
        (Get-IniValue -Path $reShadeConfig -Section 'DEPTH' -Key 'FilterResolutionHeight') -ne [string]$resolution.Height) {
        [void]$issues.Add('Generic Depth resolution does not match the primary display.')
    }
    if ((Get-IniValue -Path $systemSettings -Section 'SystemSettings' -Key 'FXAA') -ine $managedFXAA) {
        [void]$issues.Add("TERA FXAA does not match the engine profile value '$managedFXAA'.")
    }
    foreach ($path in @($reShadeConfig, $reShadePreset, $reShadeShaders)) {
        if (-not (Test-Path -LiteralPath $path)) {
            [void]$issues.Add("Installed ReShade artifact is missing: $path")
        }
    }
    $layer64 = Get-LayerValue -RegistryPath $layer64Path -Name $layer64Name
    $layer32 = Get-LayerValue -RegistryPath $layer32Path -Name $layer32Name
    if ($null -ne $layer64 -and $layer64 -ne 1) {
        [void]$issues.Add('The global ReShade Vulkan 64-bit layer was not disabled.')
    }
    if ($null -ne $layer32 -and $layer32 -ne 1) {
        [void]$issues.Add('The global ReShade Vulkan 32-bit layer was not disabled.')
    }
    if ($issues.Count -gt 0) {
        throw "ReShade/DXVK installation validation failed:`r`n - $($issues -join "`r`n - ")"
    }
}

function Enable-ProxyPipeline {
    Assert-TeraClosed
    Assert-GraphicsPayload
    Save-ProxyState

    $activeKind = Get-DllKind $activeD3D9
    if ($activeKind -eq 'DXVK') {
        if (Test-Path -LiteralPath $proxyDXVK) {
            if ((Get-DllKind $proxyDXVK) -ne 'DXVK') { throw "Unexpected file at $proxyDXVK" }
            $activeHash = (Get-FileHash -LiteralPath $activeD3D9 -Algorithm SHA256).Hash
            $proxyHash = (Get-FileHash -LiteralPath $proxyDXVK -Algorithm SHA256).Hash
            if ($activeHash -ne $proxyHash) {
                Copy-Item -LiteralPath $activeD3D9 -Destination $proxyDXVK -Force
            }
        }
        else {
            Copy-Item -LiteralPath $activeD3D9 -Destination $proxyDXVK
        }
    }
    elseif ($activeKind -eq 'ReShade') {
        if ((Get-DllKind $proxyDXVK) -ne 'DXVK') {
            Copy-Item -LiteralPath $dxvkSource -Destination $proxyDXVK -Force
        }
    }
    else {
        Copy-Item -LiteralPath $dxvkSource -Destination $proxyDXVK -Force
    }

    Copy-Item -LiteralPath $reShadeSource -Destination $activeD3D9 -Force

    if ((Get-DllKind $activeD3D9) -ne 'ReShade') { throw 'Failed to activate the ReShade D3D9 proxy.' }
    if ((Get-DllKind $proxyDXVK) -ne 'DXVK') { throw 'Failed to preserve DXVK as the proxy target.' }

    Copy-Item -LiteralPath $payloadConfig -Destination $reShadeConfig -Force
    Copy-Item -LiteralPath $payloadPreset -Destination $reShadePreset -Force
    if (-not (Test-Path -LiteralPath $reShadeShaders -PathType Container)) {
        New-Item -ItemType Directory -Path $reShadeShaders -Force | Out-Null
    }
    Copy-Item -Path (Join-Path $payloadShaders '*') -Destination $reShadeShaders -Recurse -Force

    Set-IniValue -Path $reShadeConfig -Section 'PROXY' -Key 'EnableProxyLibrary' -Value '1'
    Set-IniValue -Path $reShadeConfig -Section 'PROXY' -Key 'ProxyLibrary' -Value '.\d3d9_dxvk.dll'
    $primaryResolution = Get-PrimaryDisplayResolution
    Set-IniValue -Path $reShadeConfig -Section 'DEPTH' -Key 'FilterResolutionWidth' -Value ([string]$primaryResolution.Width)
    Set-IniValue -Path $reShadeConfig -Section 'DEPTH' -Key 'FilterResolutionHeight' -Value ([string]$primaryResolution.Height)
    Set-IniValue -Path $systemSettings -Section 'SystemSettings' -Key 'FXAA' -Value $managedFXAA

    Set-LayerValue -RegistryPath $layer64Path -Name $layer64Name -Value 1
    Set-LayerValue -RegistryPath $layer32Path -Name $layer32Name -Value 1

    if (Test-Path -LiteralPath (Join-Path $binaryRoot 'ReShade.log')) {
        Remove-Item -LiteralPath (Join-Path $binaryRoot 'ReShade.log') -Force
    }

    Assert-ProxyPipelineInstalled
    Write-Host "ReShade D3D9 -> DXVK -> Vulkan proxy chain enabled for $($primaryResolution.Width)x$($primaryResolution.Height)." -ForegroundColor Green
}

function Disable-ProxyPipeline {
    Assert-TeraClosed
    $state = Get-ProxyState
    if ($null -eq $state) { throw 'Proxy state is missing; refusing an unsafe disable operation.' }
    if ((Get-DllKind $proxyDXVK) -ne 'DXVK') { throw 'Preserved DXVK proxy DLL is missing or invalid.' }

    Copy-Item -LiteralPath $proxyDXVK -Destination $activeD3D9 -Force
    if ((Get-FileHash -LiteralPath $activeD3D9 -Algorithm SHA256).Hash -ne [string]$state.DXVKSHA256) {
        throw 'Restored DXVK hash does not match the recorded original.'
    }

    if (Test-Path -LiteralPath $reShadeConfig) {
        if (Test-Path -LiteralPath $disabledConfig) { Remove-Item -LiteralPath $disabledConfig -Force }
        Move-Item -LiteralPath $reShadeConfig -Destination $disabledConfig
    }
    Set-IniValue -Path $systemSettings -Section 'SystemSettings' -Key 'FXAA' -Value ([string]$state.OriginalFXAA)
    Set-LayerValue -RegistryPath $layer64Path -Name $layer64Name -Value ([Nullable[int]]$state.VulkanLayer64)
    Set-LayerValue -RegistryPath $layer32Path -Name $layer32Name -Value ([Nullable[int]]$state.VulkanLayer32)
    Write-Host 'ReShade disabled; the original DXVK entry point and FXAA value are restored.' -ForegroundColor Yellow
}

function Restore-OriginalPipeline {
    Disable-ProxyPipeline
    $state = Get-ProxyState
    $originalKind = if ($null -ne $state.PSObject.Properties['OriginalD3D9Kind']) {
        [string]$state.OriginalD3D9Kind
    } else {
        'DXVK'
    }
    if ($originalKind -eq 'Missing') {
        Remove-Item -LiteralPath $activeD3D9 -Force
    } elseif ($originalKind -like 'Unknown*') {
        $originalBackup = [string]$state.OriginalD3D9Backup
        if (-not (Test-Path -LiteralPath $originalBackup -PathType Leaf)) {
            throw 'The original D3D9 backup is missing; refusing an unsafe restore.'
        }
        Copy-Item -LiteralPath $originalBackup -Destination $activeD3D9 -Force
    } elseif ((Get-DllKind $activeD3D9) -ne 'DXVK') {
        throw 'DXVK restoration validation failed.'
    }
    foreach ($path in @($proxyDXVK, $disabledConfig, $reShadePreset, $reShadeShaders,
        (Join-Path $binaryRoot 'ReShade.log'), (Join-Path $binaryRoot 'ReShadePreset.ini'))) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }
    Write-Host 'Proxy artifacts removed. The pre-ReShade DXVK pipeline is restored.' -ForegroundColor Green
}

function Show-Status {
    $activeKind = Get-DllKind $activeD3D9
    $proxyKind = Get-DllKind $proxyDXVK
    $proxyEnabled = Get-IniValue -Path $reShadeConfig -Section 'PROXY' -Key 'EnableProxyLibrary'
    $proxyLibrary = Get-IniValue -Path $reShadeConfig -Section 'PROXY' -Key 'ProxyLibrary'
    $fxaa = Get-IniValue -Path $systemSettings -Section 'SystemSettings' -Key 'FXAA'
    $layer64 = Get-LayerValue -RegistryPath $layer64Path -Name $layer64Name
    $layer32 = Get-LayerValue -RegistryPath $layer32Path -Name $layer32Name
    $depthFormat = Get-IniValue -Path $reShadeConfig -Section 'DEPTH' -Key 'FilterFormat'
    $depthWidth = Get-IniValue -Path $reShadeConfig -Section 'DEPTH' -Key 'FilterResolutionWidth'
    $depthHeight = Get-IniValue -Path $reShadeConfig -Section 'DEPTH' -Key 'FilterResolutionHeight'
    $depthAspect = Get-IniValue -Path $reShadeConfig -Section 'DEPTH' -Key 'UseAspectRatioHeuristics'
    $logPath = Join-Path $binaryRoot 'ReShade.log'
    $logLength = if (Test-Path -LiteralPath $logPath) { (Get-Item -LiteralPath $logPath).Length } else { 0 }
    $runtimeConfirmed = $false
    if ($logLength -gt 0) {
        $runtimeConfirmed = (Read-SharedText -Path $logPath).Contains("Initializing crosire's ReShade")
    }
    $primaryResolution = Get-PrimaryDisplayResolution
    [pscustomobject]@{
        TeraRoot = $TeraRoot
        ActiveD3D9 = $activeKind
        ProxyTarget = $proxyKind
        ProxyEnabled = $proxyEnabled
        ProxyLibrary = $proxyLibrary
        VulkanGlobalLayer64Disabled = ($layer64 -eq 1)
        VulkanGlobalLayer32Disabled = ($layer32 -eq 1)
        TeraFXAA = $fxaa
        ExpectedTeraFXAA = $managedFXAA
        ConfigActive = (Test-Path -LiteralPath $reShadeConfig -PathType Leaf)
        PresetInstalled = (Test-Path -LiteralPath $reShadePreset -PathType Leaf)
        ShadersInstalled = (Test-Path -LiteralPath $reShadeShaders -PathType Container)
        GenericDepthFilter = "Format=$depthFormat Resolution=${depthWidth}x${depthHeight} AspectMode=$depthAspect"
        PrimaryDisplayResolution = "$($primaryResolution.Width)x$($primaryResolution.Height)"
        GenericDepthMatchesPrimaryDisplay = (
            $depthWidth -eq [string]$primaryResolution.Width -and
            $depthHeight -eq [string]$primaryResolution.Height
        )
        ProxyPipelineEnabled = ($activeKind -eq 'ReShade' -and $proxyKind -eq 'DXVK' -and $proxyEnabled -eq '1' -and $proxyLibrary -ieq '.\d3d9_dxvk.dll' -and $fxaa -ieq $managedFXAA -and ($null -eq $layer64 -or $layer64 -eq 1) -and ($null -eq $layer32 -or $layer32 -eq 1))
        RuntimeConfirmed = $runtimeConfirmed
        ReShadeLogBytes = $logLength
    }
}

if ($Action -in @('Enable', 'Disable', 'Restore') -and -not (Test-IsAdministrator)) { Restart-Elevated }

try {
    foreach ($path in @($displayResolutionScript, $engineProfileSupport, $engineProfilePath)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Required file is missing: $path"
        }
    }
    . $displayResolutionScript
    . $engineProfileSupport
    $engineProfileEntries = @(Get-EngineProfileEntries -ProfilePath $engineProfilePath -FileMap $engineFileMap)
    $fxaaEntry = $engineProfileEntries | Where-Object {
        $_.File -ieq 'S1SystemSettings.ini' -and
        $_.Section -ieq 'SystemSettings' -and
        $_.Key -ieq 'FXAA'
    } | Select-Object -First 1
    if ($null -eq $fxaaEntry) {
        throw 'The engine profile must define S1SystemSettings.ini [SystemSettings] FXAA.'
    }
    $managedFXAA = [string]$fxaaEntry.Value
    switch ($Action) {
        'Validate' {
            Assert-GraphicsPayload
            Write-Host 'ReShade and DXVK prerequisites are ready.' -ForegroundColor Green
        }
        'Enable' {
            Enable-ProxyPipeline
            Show-Status | Format-List
        }
        'Disable' {
            Disable-ProxyPipeline
            Show-Status | Format-List
        }
        'Restore' {
            Restore-OriginalPipeline
            Show-Status | Format-List
        }
        'Status' {
            Show-Status | Format-List
        }
    }
}
catch {
    $message = $_ | Out-String
    $errorLog = Join-Path $toolsRoot 'proxy-manager-error.log'
    try {
        New-Item -ItemType Directory -Path $toolsRoot -Force | Out-Null
        [System.IO.File]::WriteAllText($errorLog, $message, [System.Text.UTF8Encoding]::new($false))
    } catch {}
    throw
}
