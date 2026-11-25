param(
    [string]$ManifestPath = 'app-server-contract.json',
    # Writes versions=<contract pin array>, schemaVersions=<pin plus minimum boundary array>,
    # regressions=<json array>, target=<version>, minimum=<version>, and boundary metadata.
    # to GITHUB_OUTPUT so workflows derive their matrices from the manifest.
    [switch]$GitHubOutput
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ContractManifest.psm1') -Force -ErrorAction Stop
$manifest = Get-Content -Raw -LiteralPath $ManifestPath | ConvertFrom-Json

function Assert-Contract([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        throw "Invalid contract manifest ${ManifestPath}: $Message"
    }
}

function ConvertTo-StableVersion([string]$Value, [string]$Field) {
    Assert-Contract ($Value -match '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') "$Field '$Value' is not a stable MAJOR.MINOR.PATCH version."
    return [version]$Value
}

Assert-Contract ($manifest.schemaVersion -eq 5) "schemaVersion must be 5 (found '$($manifest.schemaVersion)')."
Assert-Contract (-not [string]::IsNullOrWhiteSpace([string]$manifest.minimumVersion)) 'minimumVersion is required.'
Assert-Contract (-not [string]::IsNullOrWhiteSpace([string]$manifest.targetVersion)) 'targetVersion is required.'

$minimum = ConvertTo-StableVersion ([string]$manifest.minimumVersion) 'minimumVersion'
$target = ConvertTo-StableVersion ([string]$manifest.targetVersion) 'targetVersion'
$regressions = @($manifest.regressionVersions | ForEach-Object { [string]$_ })
Assert-Contract ($regressions.Count -gt 0) 'regressionVersions must not be empty.'
Assert-Contract (@($regressions | Sort-Object -Unique).Count -eq $regressions.Count) 'regressionVersions must not contain duplicates.'
Assert-Contract ($regressions -notcontains [string]$manifest.targetVersion) 'targetVersion must not be a regression version.'

$contractVersions = @($regressions) + @([string]$manifest.targetVersion)
foreach ($version in $contractVersions) {
    $parsed = ConvertTo-StableVersion $version 'Contract version'
    Assert-Contract ($parsed -ge $minimum) "Version $version is below minimumVersion $($manifest.minimumVersion)."
    Assert-Contract ($parsed -le $target) "Version $version is above targetVersion $($manifest.targetVersion)."
}

$pinned = @($manifest.releases.psobject.Properties.Name)
$boundary = $manifest.minimumBoundary
Assert-Contract ($null -ne $boundary) 'minimumBoundary is required.'
Assert-Contract ([string]$boundary.version -eq [string]$manifest.minimumVersion) 'minimumBoundary.version must equal minimumVersion.'
$boundaryVersion = ConvertTo-StableVersion ([string]$boundary.version) 'minimumBoundary.version'
Assert-Contract ($boundary.tag -eq "rust-v$($boundary.version)") 'minimumBoundary tag must match its version.'
Assert-Contract ($boundary.asset -eq 'codex-x86_64-pc-windows-msvc.exe') 'minimumBoundary executable asset is missing or incorrect.'
Assert-Contract ([string]$boundary.sha256 -match '^[0-9a-f]{64}$') 'minimumBoundary executable SHA-256 must be 64 lowercase hex characters.'
Assert-Contract ($boundary.packageAsset -eq 'codex-package-x86_64-pc-windows-msvc.tar.gz') 'minimumBoundary complete package asset is missing or incorrect.'
Assert-Contract ([string]$boundary.packageSha256 -match '^[0-9a-f]{64}$') 'minimumBoundary package SHA-256 must be 64 lowercase hex characters.'
Assert-Contract ($pinned -notcontains [string]$boundary.version) 'minimumBoundary must not also be a pinned release.'
$schemaEquivalentVersion = [string]$boundary.schemaEquivalentVersion
Assert-Contract ($contractVersions -contains $schemaEquivalentVersion) 'minimumBoundary.schemaEquivalentVersion must be a contract version.'
$equivalent = ConvertTo-StableVersion $schemaEquivalentVersion 'minimumBoundary.schemaEquivalentVersion'
Assert-Contract ($equivalent.Major -eq $boundaryVersion.Major -and $equivalent.Minor -eq $boundaryVersion.Minor -and $equivalent -gt $boundaryVersion) 'minimumBoundary schema equivalent must be a later patch in the same minor series.'
foreach ($surface in @('stable', 'experimental')) {
    Assert-Contract ($null -ne $manifest.methodsByVersion.$schemaEquivalentVersion.$surface) "methodsByVersion is missing schema equivalent $schemaEquivalentVersion/$surface."
}
foreach ($version in $pinned) {
    Assert-Contract ((ConvertTo-StableVersion $version 'Pinned release') -ge $minimum) "Pinned release $version is below minimumVersion $($manifest.minimumVersion)."
    Assert-Contract ($contractVersions -contains $version) "Pinned release $version is neither the target nor a regression version."
}
$lowestPinned = @($pinned | Sort-Object { [version]$_ })[0]
$lowestPinnedVersion = ConvertTo-StableVersion ([string]$lowestPinned) 'Lowest pinned release'
Assert-Contract (
    $minimum.Major -eq $lowestPinnedVersion.Major -and
    $minimum.Minor -eq $lowestPinnedVersion.Minor -and
    $minimum -le $lowestPinnedVersion
) "minimumVersion must be in the same minor series as and not above the lowest pinned release ($lowestPinned)."

foreach ($version in $contractVersions) {
    $release = $manifest.releases.$version
    Assert-Contract ($null -ne $release) "Release metadata for $version is missing."
    Assert-Contract ([string]$release.tag -eq "rust-v$version") "Release tag for $version must be rust-v$version."
    Assert-Contract (-not [string]::IsNullOrWhiteSpace([string]$release.asset)) "Release asset for $version is missing."
    Assert-Contract ([string]$release.sha256 -match '^[0-9a-f]{64}$') "Release SHA-256 for $version must be 64 lowercase hex characters."
    Assert-Contract ([string]$release.packageAsset -eq 'codex-package-x86_64-pc-windows-msvc.tar.gz') "Complete package asset for $version is missing or incorrect."
    Assert-Contract ([string]$release.packageSha256 -match '^[0-9a-f]{64}$') "Complete package SHA-256 for $version must be 64 lowercase hex characters."
    foreach ($surface in @('stable', 'experimental')) {
        Assert-Contract ($null -ne $manifest.methodsByVersion.$version.$surface) "methodsByVersion is missing $version/$surface."
    }
}

foreach ($version in @($manifest.methodsByVersion.psobject.Properties.Name)) {
    Assert-Contract ($contractVersions -contains $version) "methodsByVersion has an entry for unsupported version $version."
}

foreach ($version in $regressions) {
    foreach ($surface in @('stable', 'experimental')) {
        Assert-Contract ($null -ne $manifest.knownDifferences.$version.$surface) "knownDifferences is missing $version/$surface."
    }
}
foreach ($version in @($manifest.knownDifferences.psobject.Properties.Name)) {
    Assert-Contract ($regressions -contains $version) "knownDifferences has an entry for $version, which is not a regression version."
}
Assert-Contract (@($manifest.methodsByVersion.psobject.Properties.Name) -notcontains [string]$boundary.version) 'minimumBoundary must not be added to methodsByVersion.'
Assert-Contract (@($manifest.knownDifferences.psobject.Properties.Name) -notcontains [string]$boundary.version) 'minimumBoundary must not be added to knownDifferences.'

if ($GitHubOutput) {
    if ([string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
        throw '-GitHubOutput requires the GITHUB_OUTPUT environment variable.'
    }
    "versions=$(ConvertTo-Json -Compress -InputObject @($contractVersions))" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
    $schemaVersions = @(Get-CodexSchemaVersions -Manifest $manifest)
    "schemaVersions=$(ConvertTo-Json -Compress -InputObject @($schemaVersions))" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
    "regressions=$(ConvertTo-Json -Compress -InputObject @($regressions))" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
    "target=$($manifest.targetVersion)" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
    "minimum=$($manifest.minimumVersion)" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
    "minimumBoundary=$($boundary.version)" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
    "minimumBoundaryEquivalent=$schemaEquivalentVersion" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
}

Write-Host "Contract manifest is valid: minimum $($manifest.minimumVersion), target $($manifest.targetVersion), regressions $($regressions -join ', ')."
