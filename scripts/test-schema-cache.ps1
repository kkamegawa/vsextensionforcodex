param(
    [Parameter(Mandatory = $true)][string]$CodexPath,
    [string]$TemporaryRoot = $env:RUNNER_TEMP
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($TemporaryRoot)) {
    $TemporaryRoot = [IO.Path]::GetTempPath()
}

$generator = Join-Path $PSScriptRoot 'generate-schemas.ps1'
$root = [IO.Path]::GetFullPath($TemporaryRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$testRoot = Join-Path $root ('codex-schema-cache-test-' + [guid]::NewGuid().ToString('N'))
$cache = Join-Path $testRoot '0.155.1\stable'
$metadataPath = Join-Path $cache '.schema-metadata.json'
$sentinelPath = Join-Path $cache 'codex_app_server_protocol.schemas.json'

function Invoke-Generator {
    & $generator -OutputDirectory $testRoot -Version '0.155.1' -Surface stable -CodexPath $CodexPath
    if ($LASTEXITCODE -ne 0) {
        throw "Schema generator failed with exit code $LASTEXITCODE."
    }
}

try {
    Invoke-Generator
    $firstWrite = (Get-Item -LiteralPath $sentinelPath).LastWriteTimeUtc
    Invoke-Generator
    $secondWrite = (Get-Item -LiteralPath $sentinelPath).LastWriteTimeUtc
    if ($firstWrite -ne $secondWrite) {
        throw 'Exact metadata and sentinel did not produce a schema cache hit.'
    }

    $metadata = Get-Content -Raw -LiteralPath $metadataPath | ConvertFrom-Json
    $metadata.cliVersion = '0.154.0'
    $metadata | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $metadataPath -Encoding utf8
    $marker = Join-Path $cache 'stale-marker.txt'
    Set-Content -LiteralPath $marker -Value 'stale' -Encoding utf8
    Invoke-Generator
    if (Test-Path -LiteralPath $marker) {
        throw 'CLI-version metadata mismatch did not replace the stale cache.'
    }

    $metadata = Get-Content -Raw -LiteralPath $metadataPath | ConvertFrom-Json
    $metadata.generatorArguments = @('--unexpected')
    $metadata | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $metadataPath -Encoding utf8
    Set-Content -LiteralPath $marker -Value 'stale' -Encoding utf8
    Invoke-Generator
    if (Test-Path -LiteralPath $marker) {
        throw 'Generator-argument metadata mismatch did not replace the stale cache.'
    }

    Remove-Item -LiteralPath $sentinelPath -Force
    Set-Content -LiteralPath $marker -Value 'stale' -Encoding utf8
    Invoke-Generator
    if (-not (Test-Path -LiteralPath $sentinelPath -PathType Leaf) -or (Test-Path -LiteralPath $marker)) {
        throw 'Missing schema sentinel did not replace the stale cache.'
    }

    $alphaRejected = $false
    try {
        & $generator -OutputDirectory $testRoot -Version '0.155.1-alpha.1' -Surface stable -CodexPath $CodexPath *> $null
    }
    catch {
        $alphaRejected = $true
    }
    if (-not $alphaRejected) {
        throw 'A prerelease CLI version was accepted as a stable contract.'
    }

    Write-Host 'Schema cache contract tests passed.'
}
finally {
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $testRootLeaf = Split-Path -Leaf $resolvedTestRoot
    if (
        $resolvedTestRoot.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
        $testRootLeaf.StartsWith('codex-schema-cache-test-', [StringComparison]::Ordinal)
    ) {
        if (Test-Path -LiteralPath $resolvedTestRoot) {
            Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
        }
    }
}
