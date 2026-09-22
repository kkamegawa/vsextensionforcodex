param(
    [string]$OutputDirectory = 'schemas',
    [ValidateSet('0.154.0', '0.155.1')][string]$Version = '0.155.1',
    [ValidateSet('stable', 'experimental')][string]$Surface = 'stable',
    [string]$CodexPath,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$manifest = Get-Content -Raw -LiteralPath (Join-Path $root 'app-server-contract.json') | ConvertFrom-Json
$release = $manifest.releases.$Version
$surfaceDefinition = $manifest.surfaces.$Surface
if ($null -eq $release -or $null -eq $surfaceDefinition) { throw "Unsupported schema contract: $Version/$Surface" }
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
$destination = [IO.Path]::GetFullPath((Join-Path $outputRoot "$Version\$Surface"))
$metadataPath = Join-Path $destination '.schema-metadata.json'
$sentinelPath = Join-Path $destination 'codex_app_server_protocol.schemas.json'
$arguments = @($surfaceDefinition.generatorArguments)

function Resolve-CodexExecutable {
    param([string]$RequestedPath)
    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        if (-not (Test-Path -LiteralPath $RequestedPath -PathType Leaf)) { throw "CODEX_PATH does not point to a file: $RequestedPath" }
        return (Resolve-Path -LiteralPath $RequestedPath).Path
    }
    $command = Get-Command codex -CommandType Application -All -ErrorAction SilentlyContinue |
        Where-Object { $_.Source -notmatch '[\\/]WindowsApps[\\/]' } | Select-Object -First 1
    if ($null -eq $command) { throw 'Codex CLI was not found. Set -CodexPath to the pinned release executable.' }
    return $command.Source
}

function Get-CliVersion([string]$Executable) {
    $text = (& $Executable '--version' 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "Unable to query Codex CLI version: $text" }
    $match = [regex]::Match($text, '(?<version>\d+\.\d+\.\d+)(?<suffix>[^\s]*)')
    if (-not $match.Success) { throw "Codex CLI version was not recognized: $text" }
    if (-not [string]::IsNullOrEmpty($match.Groups['suffix'].Value)) { throw "Prerelease Codex CLI is not a stable contract: $text" }
    return $match.Groups['version'].Value
}

$codex = Resolve-CodexExecutable $CodexPath
$actualVersion = Get-CliVersion $codex
if ($actualVersion -ne $Version) { throw "Pinned Codex $Version is required; executable reports $actualVersion." }
$metadata = [ordered]@{
    schemaVersion = 1
    cliVersion = $Version
    surface = $Surface
    generatorArguments = @($arguments)
    generatorCommand = @('app-server', 'generate-json-schema') + $arguments
    executable = $release.asset
    assetSha256 = $release.sha256
}

$cacheHit = (-not $Force) -and (Test-Path -LiteralPath $metadataPath) -and (Test-Path -LiteralPath $sentinelPath)
if ($cacheHit) {
    try {
        $existing = Get-Content -Raw -LiteralPath $metadataPath | ConvertFrom-Json
        $cacheHit = ($existing.schemaVersion -eq $metadata.schemaVersion -and $existing.cliVersion -eq $metadata.cliVersion -and $existing.surface -eq $metadata.surface -and
            (@($existing.generatorArguments) -join "`n") -eq (@($metadata.generatorArguments) -join "`n") -and
            (@($existing.generatorCommand) -join "`n") -eq (@($metadata.generatorCommand) -join "`n") -and
            $existing.executable -eq $metadata.executable -and
            $existing.assetSha256 -eq $metadata.assetSha256)
    } catch { $cacheHit = $false }
}
if ($cacheHit) { Write-Host "Schema cache hit: $destination"; return }

$parent = Split-Path -Parent $destination
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$temporary = Join-Path $parent ('.tmp-' + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Force -Path $temporary | Out-Null
    $cliArguments = @('app-server', 'generate-json-schema') + $arguments + @('--out', $temporary)
    & $codex @cliArguments
    if ($LASTEXITCODE -ne 0) { throw "Codex schema generation failed with exit code $LASTEXITCODE." }
    $metadata | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $temporary '.schema-metadata.json') -Encoding utf8
    $sentinel = Join-Path $temporary 'codex_app_server_protocol.schemas.json'
    if (-not (Test-Path -LiteralPath $sentinel -PathType Leaf)) { throw "Codex did not emit the required schema sentinel: $sentinel" }
    $resolvedDestination = $destination.TrimEnd([IO.Path]::DirectorySeparatorChar)
    if (-not $resolvedDestination.StartsWith($outputRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        $resolvedDestination -notmatch '\\(0\.154\.0|0\.155\.1)\\(stable|experimental)$') { throw "Refusing to replace schema path outside the versioned schema cache: $destination" }
    $backup = "$destination.backup-$([guid]::NewGuid().ToString('N'))"
    $hadDestination = Test-Path -LiteralPath $destination
    if ($hadDestination) { Move-Item -LiteralPath $destination -Destination $backup }
    try {
        Move-Item -LiteralPath $temporary -Destination $destination
        if ($hadDestination) { Remove-Item -LiteralPath $backup -Recurse -Force }
    } catch {
        if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
        if ($hadDestination -and (Test-Path -LiteralPath $backup)) { Move-Item -LiteralPath $backup -Destination $destination }
        throw
    }
    Write-Host "Generated $Version/$Surface schemas in $destination"
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
