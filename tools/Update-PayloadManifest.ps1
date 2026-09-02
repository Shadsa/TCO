#Requires -Version 7.0

[CmdletBinding()]
param([string]$Version = '1.0')

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$payloadRoot = Join-Path $repositoryRoot 'payload'
$manifestPath = Join-Path $payloadRoot 'manifest.json'
$executableExtensions = [Collections.Generic.HashSet[string]]::new(
    [string[]]@('.dll', '.exe'),
    [StringComparer]::OrdinalIgnoreCase)

$files = Get-ChildItem -LiteralPath $payloadRoot -File -Recurse |
    Where-Object FullName -ne $manifestPath |
    Where-Object { $executableExtensions.Contains($_.Extension) } |
    Sort-Object FullName
$entries = foreach ($file in $files) {
    [byte[]]$bytes = [IO.File]::ReadAllBytes($file.FullName)
    [ordered]@{
        Path = ('payload/' + [IO.Path]::GetRelativePath($payloadRoot, $file.FullName).Replace('\', '/'))
        Bytes = $bytes.LongLength
        SHA256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
    }
}

$manifest = [ordered]@{
    Package = 'TCO executable payload integrity'
    Version = $Version
    FileCount = @($entries).Count
    Files = @($entries)
}
$json = ($manifest | ConvertTo-Json -Depth 5).Replace("`r`n", "`n") + "`n"
[IO.File]::WriteAllText($manifestPath, $json, [Text.UTF8Encoding]::new($false))
Write-Host "Wrote $manifestPath with $($manifest.FileCount) payload entries."
