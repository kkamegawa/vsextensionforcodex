param(
    [string]$OutputDirectory = 'schemas',
    [ValidateSet('0.154.0', '0.155.1')][string]$Version = '0.155.1',
    [ValidateSet('stable', 'experimental')][string]$Surface = 'stable'
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$manifest = Get-Content -Raw -LiteralPath (Join-Path $root 'app-server-contract.json') | ConvertFrom-Json
$release = $manifest.releases.$Version
$surfaceDefinition = $manifest.surfaces.$Surface
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
$destination = [IO.Path]::GetFullPath((Join-Path $outputRoot "$Version\$Surface"))
$metadataPath = Join-Path $destination '.schema-metadata.json'
$sentinelPath = Join-Path $destination 'codex_app_server_protocol.schemas.json'
$expectedArguments = @($surfaceDefinition.generatorArguments)

if (($null -eq $release -or $null -eq $surfaceDefinition) -or
    (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) -or
    (-not (Test-Path -LiteralPath $sentinelPath -PathType Leaf)))
{
    Write-Output "Schema cache is missing or incomplete: $destination"
    exit 1
}

try {
    $metadata = Get-Content -Raw -LiteralPath $metadataPath | ConvertFrom-Json
    $matches = $metadata.schemaVersion -eq 1 -and
        $metadata.cliVersion -eq $Version -and
        $metadata.surface -eq $Surface -and
        (@($metadata.generatorArguments) -join "`n") -eq ($expectedArguments -join "`n") -and
        (@($metadata.generatorCommand) -join "`n") -eq ((@('app-server', 'generate-json-schema') + $expectedArguments) -join "`n") -and
        $metadata.executable -eq $release.asset -and
        $metadata.assetSha256 -eq $release.sha256
    if (-not $matches) {
        Write-Output "Schema cache metadata does not match the pinned contract: $destination"
        exit 1
    }

    Write-Output "Schema cache metadata is valid: $destination"
    exit 0
}
catch {
    Write-Output "Schema cache metadata is unreadable: $destination"
    exit 1
}
