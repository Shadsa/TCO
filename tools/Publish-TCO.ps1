[CmdletBinding()]
param(
    [switch]$NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$publishDirectory = [IO.Path]::GetFullPath((Join-Path $artifactsRoot 'release'))
$expectedPrefix = $artifactsRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

if (-not $publishDirectory.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean a publish directory outside $artifactsRoot."
}

if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}

$arguments = @(
    'publish',
    (Join-Path $repositoryRoot 'frontend\TcoInstaller\TcoInstaller.csproj'),
    '--configuration', 'Release',
    '--runtime', 'win-x64',
    '--output', $publishDirectory
)

if ($NoRestore) {
    $arguments += '--no-restore'
}

& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$publishedFiles = @(Get-ChildItem -LiteralPath $publishDirectory -File -Recurse)
if ($publishedFiles.Count -ne 1 -or $publishedFiles[0].Extension -ne '.exe') {
    throw "Expected exactly one published EXE, found $($publishedFiles.Count) file(s)."
}

$hash = Get-FileHash -LiteralPath $publishedFiles[0].FullName -Algorithm SHA256
Write-Host "Single-file release: $($publishedFiles[0].FullName)"
Write-Host "Size: $([Math]::Round($publishedFiles[0].Length / 1MB, 2)) MiB"
Write-Host "SHA-256: $($hash.Hash)"
