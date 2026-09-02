#Requires -Version 7.0

[CmdletBinding()]
param([string]$Version = '1.0')

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$payloadRoot = Join-Path $repositoryRoot 'payload'
$manifestPath = Join-Path $payloadRoot 'manifest.json'
$textExtensions = [Collections.Generic.HashSet[string]]::new(
    [string[]]@('.fx', '.fxh', '.ini', '.json', '.md', '.txt', '.xml'),
    [StringComparer]::OrdinalIgnoreCase)

function Get-CanonicalBytes {
    param([Parameter(Mandatory)][string]$Path)

    [byte[]]$bytes = [IO.File]::ReadAllBytes($Path)
    if (-not $textExtensions.Contains([IO.Path]::GetExtension($Path))) {
        return $bytes
    }

    $output = [Collections.Generic.List[byte]]::new($bytes.Length)
    for ($index = 0; $index -lt $bytes.Length; $index++) {
        if ($bytes[$index] -eq 13 -and $index + 1 -lt $bytes.Length -and $bytes[$index + 1] -eq 10) {
            continue
        }
        $output.Add($bytes[$index])
    }
    return $output.ToArray()
}

$files = Get-ChildItem -LiteralPath $payloadRoot -File -Recurse |
    Where-Object FullName -ne $manifestPath |
    Sort-Object FullName
$entries = foreach ($file in $files) {
    [byte[]]$canonical = Get-CanonicalBytes -Path $file.FullName
    [ordered]@{
        Path = ('payload/' + [IO.Path]::GetRelativePath($payloadRoot, $file.FullName).Replace('\', '/'))
        Bytes = $canonical.LongLength
        SHA256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($canonical))
    }
}

$manifest = [ordered]@{
    Package = 'TCO embedded payload'
    Version = $Version
    FileCount = @($entries).Count
    Files = @($entries)
}
$json = ($manifest | ConvertTo-Json -Depth 5).Replace("`r`n", "`n") + "`n"
[IO.File]::WriteAllText($manifestPath, $json, [Text.UTF8Encoding]::new($false))
Write-Host "Wrote $manifestPath with $($manifest.FileCount) payload entries."
