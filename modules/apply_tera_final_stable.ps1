#Requires -Version 5.1

param(
    [Parameter(Mandatory = $false)]
    [ValidateNotNullOrEmpty()]
    [string]$TeraRoot = 'S:\TERA'
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
$binaryRoot = Join-Path $TeraRoot 'Binaries'
$s1Engine = Join-Path $configRoot 'S1Engine.ini'
$systemSettings = Join-Path $configRoot 'S1SystemSettings.ini'
$option = Join-Path $configRoot 'S1Option.ini'
$s1Input = Join-Path $configRoot 'S1Input.ini'
$baseInput = Join-Path $engineRoot 'BaseInput.ini'
$required = @($s1Engine, $systemSettings, $option, $s1Input, $baseInput)

foreach ($path in $required) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required TERA configuration file is missing: $path"
    }
}

$textureGroupsBefore = Get-TextureGroupFingerprint -Path $systemSettings

# Re-enable an already installed DXVK D3D9 wrapper. This script does not download binaries.
$dxvkActive = Join-Path $binaryRoot 'd3d9.dll'
$dxvkDisabled = "$dxvkActive.dxvk-disabled"
if (-not (Test-Path -LiteralPath $dxvkActive -PathType Leaf)) {
    if (Test-Path -LiteralPath $dxvkDisabled -PathType Leaf) {
        Move-Item -LiteralPath $dxvkDisabled -Destination $dxvkActive
        Write-Host 'DXVK D3D9 wrapper enabled.'
    }
    else {
        Write-Warning 'DXVK d3d9.dll was not found. The INI profile will still be applied.'
    }
}
else {
    Write-Host 'DXVK D3D9 wrapper is already enabled.'
}

# Frame pacing and non-texture engine behavior.
Set-IniSectionValues -Path $s1Engine -Section 'Engine.Engine' -Values ([ordered]@{
    AllowShadowVolumes = 'True'
    bSmoothFrameRate = 'True'
    MinSmoothedFrameRate = '30'
    MaxSmoothedFrameRate = '141'
})

Set-IniSectionValues -Path $s1Engine -Section 'Engine.SeqAct_Interp' -Values ([ordered]@{
    RenderingOverrides = '(bAllowAmbientOcclusion=True,bAllowDominantWholeSceneDynamicShadows=True,bAllowMotionBlurSkinning=False,bAllowTemporalAA=False,bAllowLightShafts=True)'
})

Set-IniSectionValues -Path $s1Engine -Section 'Engine.ISVHacks' -Values ([ordered]@{
    bInitializeShadersOnDemand = 'False'
    DisableATITextureFilterOptimizationChecks = 'True'
    UseMinimalNVIDIADriverShaderOptimization = 'False'
    PumpWindowMessagesWhenRenderThreadStalled = 'False'
})

Set-IniSectionValues -Path $s1Engine -Section 'Engine.GameEngine' -Values ([ordered]@{
    CacheSizeMegs = '1024'
    bClearAnimSetLinkupCachesOnLoadMap = 'False'
    bClearAnimSetLinkupCachesMap = 'False'
})

Set-IniSectionValues -Path $s1Engine -Section 'DevOptions.Shaders' -Values ([ordered]@{
    AutoReloadChangedShaders = 'False'
    bAllowMultiThreadedShaderCompile = 'True'
    bAllowDistributedShaderCompile = 'False'
    NumUnusedShaderCompilingThreads = '2'
    ThreadedShaderCompileThreshold = '4'
})

Set-IniSectionValues -Path $s1Engine -Section 'AppCompat' -Values ([ordered]@{
    CompatLevelComposite = '5'
    CompatLevelCPU = '5'
    CompatLevelGPU = '5'
    CPUNumLogicalProcessors = '24'
})

# Tested texture profile. Do not force texture-group minimum LOD or alter the LUT.
Set-IniSectionValues -Path $s1Engine -Section 'TextureStreaming' -Values ([ordered]@{
    PoolSize = '4096'
    MemoryMargin = '20'
    MinTextureResidentMipCount = '7'
    AllowStreamingLightmaps = 'True'
    UsePriorityStreaming = 'True'
    UseDynamicStreaming = 'True'
    bEnableAsyncDefrag = 'False'
    bEnableAsyncReallocation = 'False'
    BoostPlayerTextures = '3.0'
})

# Maximize non-texture visuals while preserving every TEXTUREGROUP_* line.
Set-IniSectionValues -Path $systemSettings -Section 'SystemSettings' -Values ([ordered]@{
    StaticDecals = 'True'
    DynamicDecals = 'True'
    UnbatchedDecals = 'True'
    DynamicLights = 'True'
    DynamicShadows = 'True'
    LightEnvironmentShadows = 'True'
    CompositeDynamicLights = 'False'
    DirectionalLightmaps = 'True'
    MotionBlur = 'False'
    MotionBlurPause = 'False'
    MotionBlurSkinning = '0'
    DepthOfField = 'False'
    AmbientOcclusion = 'True'
    Bloom = 'True'
    UseHighQualityBloom = 'True'
    bAllowLightShafts = 'True'
    Distortion = 'True'
    FilteredDistortion = 'True'
    DropParticleDistortion = 'False'
    AllowDistortionAndColorInSameMaterial = 'True'
    SpeedTreeLeaves = 'True'
    SpeedTreeFronds = 'True'
    SpeedTreeBranches = 'True'
    SpeedTreeBillboards = 'True'
    SpeedTreeLeafQuality = '2'
    SpeedTreeLeafShadows = 'True'
    SpeedTreeLeafWind = 'True'
    OnlyStreamInTextures = 'False'
    LensFlares = 'True'
    FogVolumes = 'True'
    FloatingPointRenderTargets = 'True'
    OneFrameThreadLag = 'True'
    UseVsync = 'False'
    AllowRadialBlur = 'False'
    bAllowTemporalAA = 'False'
    MobilePostProcessBlurAmount = '0.0'
    AllowSubsurfaceScattering = 'True'
    AllowImageReflections = 'True'
    AllowImageReflectionShadowing = 'True'
    bAllowSeparateTranslucency = 'True'
    bAllowHighQualityMaterials = 'True'
    MaxFilterBlurSampleCount = '16'
    SkeletalMeshLODBias = '-1'
    ParticleLODBias = '-1'
    DetailMode = '3'
    MaxDrawDistanceScale = '2.000000'
    ShadowFilterQualityBias = '-1'
    MaxAnisotropy = '16'
    MaxMultiSamples = '1'
    MinShadowResolution = '256'
    MaxShadowResolution = '4096'
    MaxWholeSceneDominantShadowResolution = '4096'
    ShadowFadeResolution = '64'
    ShadowTexelsPerPixel = '2.000000'
    bEnableForegroundShadowsOnWorld = 'True'
    bEnableForegroundSelfShadowing = 'True'
    bAllowWholeSceneDominantShadows = 'True'
    bEnableVSMShadows = 'True'
    bAllowBetterModulatedShadows = 'True'
    bEnablePSSMShadows = 'True'
    HighPrecisionGBuffers = 'True'
    ScreenPercentage = '100.000000'
    FoliageDrawRadiusMultiplier = '1.500000'
    FXAA = 'False'
    MinTextureResidentMipCount = '7'
    AllowD3D10 = 'False'
    AllowD3D11 = 'False'
    ImageReflectionTextureSize = '1024'
})

# Keep the high preset and prevent runtime quality reduction. Texture LOD is intentionally untouched.
Set-IniSectionValues -Path $option -Section 'VIDEO' -Values ([ordered]@{
    AUTO_FRAME_RATE_OPTIMIZE_CHECK = 'False'
    OPTIMIZING_FOR_MANY_USERS = 'False'
    OPTIMIZING_FOR_PEGASUS = 'False'
    EFFECT_CLIENT_LOD = '0'
    DISPLAY_QUALITY_PRESET_INDEX = '6'
    CHARACTER_LOD = '2'
    CHARACTER_SHADOW_QUALITY = '4'
    BACKGROUND_DISPLAY_DISTANCE = '6'
    GLOBAL_FOLIAGE = '4'
    SKY_DETAIL = '2'
    LOWEND_LIGHTING = 'False'
    BACKGROUND_EFFECT_CULL_DISTANCE_INDEX = '4'
    BACKGROUND_EFFECT_LOD_INDEX = '2'
    CHARACTER_EFFECT_CULL_DISTANCE_INDEX = '4'
    POSTPROCESS_QUALITY_INDEX = '2'
    REALTIME_OPTIMIZE = '0'
    ENVCASE_EFFECT = 'True'
    EFFECT_CLIENT_LOD_DEFAULT = '2'
    EFFECT_CLIENT_LOD_IN_DUNGEON = '2'
    HIDE_WEAPON_EFFECT = 'False'
    CHARACTER_CULL_DISTANCE_LIMIT_INDEX = '6'
    CHARACTER_COUNT_LIMIT_INDEX = '4'
})

Set-IniSectionValues -Path $option -Section 'CityWar' -Values ([ordered]@{
    CITYWAR_USE_AUTO_OPTIMIZE = 'False'
})

Set-IniSectionValues -Path $baseInput -Section 'Engine.PlayerInput' -Values ([ordered]@{
    bEnableMouseSmoothing = 'True'
})
Set-IniSectionValues -Path $s1Input -Section 'Engine.PlayerInput' -Values ([ordered]@{
    bEnableMouseSmoothing = 'True'
})

$textureGroupsAfter = Get-TextureGroupFingerprint -Path $systemSettings
if ($textureGroupsBefore -cne $textureGroupsAfter) {
    throw 'Texture-group integrity check failed. A TEXTUREGROUP entry changed unexpectedly.'
}

Write-Host ''
Write-Host 'TERA stable optimization profile applied successfully.' -ForegroundColor Green
Write-Host 'Texture pool: 4096 MB'
Write-Host 'Texture groups and ColorLookupTable: unchanged'
Write-Host "DXVK active: $(Test-Path -LiteralPath $dxvkActive -PathType Leaf)"
Write-Host 'Backup created: False'
