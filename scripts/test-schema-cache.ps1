param(
    [Parameter(Mandatory = $true)][string]$CodexPath,
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$TemporaryRoot = $env:RUNNER_TEMP,
    [string]$ManifestPath = (Join-Path $PSScriptRoot '..\app-server-contract.json')
)

$ErrorActionPreference = 'Stop'
$manifest = Get-Content -Raw -LiteralPath $ManifestPath | ConvertFrom-Json
$pinnedVersions = @($manifest.releases.psobject.Properties.Name)
if ($pinnedVersions -notcontains $Version) {
    throw "Codex $Version is not pinned in $ManifestPath."
}
if ([string]::IsNullOrWhiteSpace($TemporaryRoot)) {
    $TemporaryRoot = [IO.Path]::GetTempPath()
}

$generator = Join-Path $PSScriptRoot 'generate-schemas.ps1'
$root = [IO.Path]::GetFullPath($TemporaryRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$testRoot = Join-Path $root ('codex-schema-cache-test-' + [guid]::NewGuid().ToString('N'))
$cache = Join-Path $testRoot "$Version\stable"
$metadataPath = Join-Path $cache '.schema-metadata.json'
$sentinelPath = Join-Path $cache 'codex_app_server_protocol.schemas.json'

function Invoke-Generator([string]$Surface) {
    & $generator -OutputDirectory $testRoot -Version $Version -Surface $Surface -CodexPath $CodexPath
    if ($LASTEXITCODE -ne 0) {
        throw "Schema generator failed with exit code $LASTEXITCODE."
    }
}

try {
    foreach ($surface in @('stable', 'experimental')) {
        Invoke-Generator $surface
        $surfaceCache = Join-Path $testRoot "$Version\$surface"
        $surfaceSentinelPath = Join-Path $surfaceCache 'codex_app_server_protocol.schemas.json'
        $firstWrite = (Get-Item -LiteralPath $surfaceSentinelPath).LastWriteTimeUtc
        Invoke-Generator $surface
        $secondWrite = (Get-Item -LiteralPath $surfaceSentinelPath).LastWriteTimeUtc
        if ($firstWrite -ne $secondWrite) {
            throw "Exact $Version/$surface metadata and sentinel did not produce a schema cache hit."
        }
    }

    $metadata = Get-Content -Raw -LiteralPath $metadataPath | ConvertFrom-Json
    $metadata.cliVersion = @($pinnedVersions | Where-Object { $_ -ne $Version } | Select-Object -First 1)[0]
    $metadata | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $metadataPath -Encoding utf8
    $marker = Join-Path $cache 'stale-marker.txt'
    Set-Content -LiteralPath $marker -Value 'stale' -Encoding utf8
    Invoke-Generator 'stable'
    if (Test-Path -LiteralPath $marker) {
        throw 'CLI-version metadata mismatch did not replace the stale cache.'
    }

    $metadata = Get-Content -Raw -LiteralPath $metadataPath | ConvertFrom-Json
    $metadata.generatorArguments = @('--unexpected')
    $metadata | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $metadataPath -Encoding utf8
    Set-Content -LiteralPath $marker -Value 'stale' -Encoding utf8
    Invoke-Generator 'stable'
    if (Test-Path -LiteralPath $marker) {
        throw 'Generator-argument metadata mismatch did not replace the stale cache.'
    }

    $metadata = Get-Content -Raw -LiteralPath $metadataPath | ConvertFrom-Json
    $metadata.schemaVersion = 2
    $metadata | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $metadataPath -Encoding utf8
    Set-Content -LiteralPath $marker -Value 'stale' -Encoding utf8
    Invoke-Generator 'stable'
    if (Test-Path -LiteralPath $marker) {
        throw 'Cache-metadata format mismatch did not replace the stale cache.'
    }

    Remove-Item -LiteralPath $sentinelPath -Force
    Set-Content -LiteralPath $marker -Value 'stale' -Encoding utf8
    Invoke-Generator 'stable'
    if (-not (Test-Path -LiteralPath $sentinelPath -PathType Leaf) -or (Test-Path -LiteralPath $marker)) {
        throw 'Missing schema sentinel did not replace the stale cache.'
    }

    # A stub executable reports the version so the generator's own version check runs; passing an
    # invalid -Version would only exercise PowerShell parameter validation.
    function Assert-VersionRejected([string]$ReportedVersion, [string]$ExpectedMessage) {
        $stub = Join-Path $testRoot ('codex-stub-' + [guid]::NewGuid().ToString('N') + '.cmd')
        Set-Content -LiteralPath $stub -Value "@echo codex-cli $ReportedVersion" -Encoding ascii
        $failure = $null
        try {
        & $generator -OutputDirectory (Join-Path $testRoot 'stub-cache') -Version $Version -Surface stable -CodexPath $stub -Force *> $null
        }
        catch {
            $failure = $_.Exception.Message
        }

        if ($null -eq $failure) {
            throw "Codex CLI '$ReportedVersion' was accepted as the pinned stable contract."
        }

        if ($failure -notlike "*$ExpectedMessage*") {
            throw "Codex CLI '$ReportedVersion' was rejected for an unexpected reason: $failure"
        }
    }

    Assert-VersionRejected "$Version-alpha.1" 'Prerelease Codex CLI is not a stable contract'
    $otherStableVersion = @($pinnedVersions | Where-Object { $_ -ne $Version } | Select-Object -First 1)[0]
    Assert-VersionRejected $otherStableVersion "Pinned Codex $Version is required"

    Write-Host "Schema cache contract tests passed for $Version stable and experimental surfaces."
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
