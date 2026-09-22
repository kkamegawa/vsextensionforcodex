[CmdletBinding()]
param(
    [ValidateSet('latest', '0.154.0', '0.155.1')]
    [string]$Version = 'latest',
    [string]$OutputDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'codex-cli'),
    [switch]$GitHubEnvironment,
    [switch]$GitHubOutput
)

$ErrorActionPreference = 'Stop'

$repository = 'openai/codex'
$assetName = 'codex-x86_64-pc-windows-msvc.exe'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$manifestPath = Join-Path $root 'app-server-contract.json'

function Get-GitHubHeaders {
    return @{
        Accept = 'application/vnd.github+json'
        'User-Agent' = 'codex-for-visual-studio-ci'
    }
}

function Get-LatestRelease {
    $apiUri = "https://api.github.com/repos/$repository/releases/latest"
    $release = Invoke-RestMethod -Uri $apiUri -Headers (Get-GitHubHeaders)
    if ($release.draft -or $release.prerelease) {
        throw "GitHub latest release is not a stable published release: $($release.tag_name)"
    }

    $asset = @($release.assets) | Where-Object { $_.name -eq $assetName } | Select-Object -First 1
    if ($null -eq $asset) {
        throw "The latest Codex release '$($release.tag_name)' does not contain '$assetName'."
    }

    $version = ([regex]::Match($release.tag_name, '(?<version>\d+\.\d+\.\d+)$')).Groups['version'].Value
    if ([string]::IsNullOrWhiteSpace($version)) {
        throw "The latest Codex release tag '$($release.tag_name)' does not end with a stable semantic version."
    }

    return [pscustomobject]@{
        Version = $version
        Tag = $release.tag_name
        Asset = $asset.name
        Uri = $asset.browser_download_url
        Digest = $asset.digest
    }
}

function Get-PinnedRelease([string]$PinnedVersion) {
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    $release = $manifest.releases.$PinnedVersion
    if ($null -eq $release) {
        throw "The pinned Codex version '$PinnedVersion' is not present in $manifestPath."
    }

    return [pscustomobject]@{
        Version = $PinnedVersion
        Tag = $release.tag
        Asset = $release.asset
        Uri = "https://github.com/$repository/releases/download/$($release.tag)/$($release.asset)"
        Digest = "sha256:$($release.sha256)"
    }
}

function Set-GitHubValue([string]$Name, [string]$Value, [switch]$Environment, [switch]$Output) {
    if ($Environment) {
        if ([string]::IsNullOrWhiteSpace($env:GITHUB_ENV)) {
            throw '-GitHubEnvironment requires the GITHUB_ENV environment variable.'
        }
        "$Name=$Value" | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append
    }
    if ($Output) {
        if ([string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
            throw '-GitHubOutput requires the GITHUB_OUTPUT environment variable.'
        }
        "$Name=$Value" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
    }
}

$release = if ($Version -eq 'latest') { Get-LatestRelease } else { Get-PinnedRelease $Version }
if ($release.Asset -ne $assetName) {
    throw "Unexpected Codex asset '$($release.Asset)'. Only the Windows x64 MSVC asset is supported."
}

$downloadDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $downloadDirectory | Out-Null
$downloadPath = Join-Path $downloadDirectory $release.Asset

$uri = [Uri]$release.Uri
$escapedAssetName = [regex]::Escape($assetName)
if ($uri.Scheme -ne 'https' -or $uri.Host -ne 'github.com' -or
    $uri.AbsolutePath -notmatch "^/openai/codex/releases/download/[^/]+/$escapedAssetName$") {
    throw "Refusing to download Codex from an unexpected URL: $($release.Uri)"
}

Invoke-WebRequest -Uri $release.Uri -OutFile $downloadPath
$actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $downloadPath).Hash.ToLowerInvariant()
$expectedHash = ([regex]::Match([string]$release.Digest, '^sha256:(?<hash>[0-9a-fA-F]{64})$')).Groups['hash'].Value.ToLowerInvariant()
if ([string]::IsNullOrWhiteSpace($expectedHash)) {
    throw "The Codex release '$($release.Tag)' did not publish a SHA-256 asset digest."
}
if ($actualHash -ne $expectedHash) {
    throw "The downloaded Codex CLI hash '$actualHash' does not match the published SHA-256 digest '$expectedHash'."
}

$versionOutput = (& $downloadPath '--version' 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0) {
    throw "The downloaded Codex CLI failed --version: $versionOutput"
}
Write-Host "Codex CLI $($release.Version) ($($release.Tag)) downloaded to $downloadPath"
Write-Host $versionOutput

Set-GitHubValue -Name 'CODEX_PATH' -Value $downloadPath -Environment:$GitHubEnvironment
Set-GitHubValue -Name 'CODEX_VERSION' -Value $release.Version -Environment:$GitHubEnvironment
Set-GitHubValue -Name 'codex_path' -Value $downloadPath -Output:$GitHubOutput
Set-GitHubValue -Name 'codex_version' -Value $release.Version -Output:$GitHubOutput
Set-GitHubValue -Name 'codex_tag' -Value $release.Tag -Output:$GitHubOutput
Set-GitHubValue -Name 'codex_sha256' -Value $actualHash -Output:$GitHubOutput

[pscustomobject]@{
    Path = $downloadPath
    Version = $release.Version
    Tag = $release.Tag
    Sha256 = $actualHash
} | ConvertTo-Json -Compress
