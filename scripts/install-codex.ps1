[CmdletBinding()]
param(
    # 'latest' or a version pinned in app-server-contract.json; Get-PinnedRelease rejects any other version.
    [ValidatePattern('^(latest|\d+\.\d+\.\d+)$')]
    [string]$Version = 'latest',
    [string]$OutputDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'codex-cli'),
    [switch]$GitHubEnvironment,
    [switch]$GitHubOutput
)

$ErrorActionPreference = 'Stop'

$repository = 'openai/codex'
$executableAssetName = 'codex-x86_64-pc-windows-msvc.exe'
$packageAssetName = 'codex-package-x86_64-pc-windows-msvc.tar.gz'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$manifestPath = Join-Path $root 'app-server-contract.json'

function Get-GitHubHeaders {
    $headers = @{
        Accept = 'application/vnd.github+json'
        'User-Agent' = 'codex-for-visual-studio-ci'
    }

    # Unauthenticated API calls share a small per-IP limit on hosted runners. Use the workflow
    # token when the caller provides one so the latest-release lookup does not fail intermittently.
    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) {
        $headers.Authorization = "Bearer $env:GITHUB_TOKEN"
    }

    return $headers
}

function Get-LatestRelease {
    $apiUri = "https://api.github.com/repos/$repository/releases/latest"
    $release = Invoke-RestMethod -Uri $apiUri -Headers (Get-GitHubHeaders)
    if ($release.draft -or $release.prerelease) {
        throw "GitHub latest release is not a stable published release: $($release.tag_name)"
    }

    $assets = @($release.assets)
    $executableAsset = $assets | Where-Object { $_.name -eq $executableAssetName } | Select-Object -First 1
    $packageAsset = $assets | Where-Object { $_.name -eq $packageAssetName } | Select-Object -First 1
    if ($null -eq $executableAsset -or $null -eq $packageAsset) {
        throw "The latest Codex release '$($release.tag_name)' does not contain both required Windows x64 MSVC assets."
    }

    $version = ([regex]::Match($release.tag_name, '(?<version>\d+\.\d+\.\d+)$')).Groups['version'].Value
    if ([string]::IsNullOrWhiteSpace($version)) {
        throw "The latest Codex release tag '$($release.tag_name)' does not end with a stable semantic version."
    }

    return [pscustomobject]@{
        Version = $version
        Tag = $release.tag_name
        ExecutableAsset = $executableAsset.name
        ExecutableDigest = $executableAsset.digest
        PackageAsset = $packageAsset.name
        PackageUri = $packageAsset.browser_download_url
        PackageDigest = $packageAsset.digest
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
        ExecutableAsset = $release.asset
        ExecutableDigest = "sha256:$($release.sha256)"
        PackageAsset = $release.packageAsset
        PackageUri = "https://github.com/$repository/releases/download/$($release.tag)/$($release.packageAsset)"
        PackageDigest = "sha256:$($release.packageSha256)"
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
if ($release.ExecutableAsset -ne $executableAssetName -or $release.PackageAsset -ne $packageAssetName) {
    throw "Unexpected Codex release assets. Only the Windows x64 MSVC executable and complete package are supported."
}

$downloadDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $downloadDirectory | Out-Null
$archivePath = Join-Path ([IO.Path]::GetTempPath()) ("codex-package-$([guid]::NewGuid().ToString('N')).tar.gz")

$uri = [Uri]$release.PackageUri
$escapedTag = [regex]::Escape($release.Tag)
$escapedAssetName = [regex]::Escape($packageAssetName)
if ($uri.Scheme -ne 'https' -or $uri.Host -ne 'github.com' -or
    $uri.AbsolutePath -notmatch "^/openai/codex/releases/download/$escapedTag/$escapedAssetName$") {
    throw "Refusing to download Codex from an unexpected URL: $($release.PackageUri)"
}

try {
    Invoke-WebRequest -Uri $release.PackageUri -OutFile $archivePath
    $actualPackageHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $archivePath).Hash.ToLowerInvariant()
    $expectedPackageHash = ([regex]::Match([string]$release.PackageDigest, '^sha256:(?<hash>[0-9a-fA-F]{64})$')).Groups['hash'].Value.ToLowerInvariant()
    if ([string]::IsNullOrWhiteSpace($expectedPackageHash)) {
        throw "The Codex release '$($release.Tag)' did not publish a SHA-256 digest for the complete package."
    }
    if ($actualPackageHash -ne $expectedPackageHash) {
        throw "The downloaded Codex package hash '$actualPackageHash' does not match the published SHA-256 digest '$expectedPackageHash'."
    }

    $tar = Get-Command tar.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1
    & $tar.Source -xzf $archivePath -C $downloadDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Codex package extraction failed with exit code $LASTEXITCODE."
    }

    $metadataPath = Join-Path $downloadDirectory 'codex-package.json'
    if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) {
        throw "The Codex package '$($release.PackageAsset)' does not contain codex-package.json at its root."
    }
    $packageMetadata = Get-Content -Raw -LiteralPath $metadataPath | ConvertFrom-Json
    if ([string]$packageMetadata.version -ne [string]$release.Version) {
        throw "The Codex package metadata reports version '$($packageMetadata.version)', expected '$($release.Version)'."
    }

    $entrypoint = [string]$packageMetadata.entrypoint
    if ([string]::IsNullOrWhiteSpace($entrypoint) -or
        [IO.Path]::IsPathRooted($entrypoint) -or
        $entrypoint -match '(^|[\\/])\.\.([\\/]|$)') {
        throw 'The Codex package metadata has an invalid entrypoint path.'
    }
    $entrypointPath = [IO.Path]::GetFullPath((Join-Path $downloadDirectory $entrypoint.Replace('/', [IO.Path]::DirectorySeparatorChar)))
    $packageBoundary = $downloadDirectory.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $entrypointPath.StartsWith($packageBoundary, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $entrypointPath -PathType Leaf)) {
        throw "The Codex package entrypoint is missing or outside the package directory: $entrypoint"
    }

    $actualExecutableHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $entrypointPath).Hash.ToLowerInvariant()
    $expectedExecutableHash = ([regex]::Match([string]$release.ExecutableDigest, '^sha256:(?<hash>[0-9a-fA-F]{64})$')).Groups['hash'].Value.ToLowerInvariant()
    if ([string]::IsNullOrWhiteSpace($expectedExecutableHash) -or $actualExecutableHash -ne $expectedExecutableHash) {
        throw "The packaged Codex executable hash '$actualExecutableHash' does not match its pinned release digest."
    }

    $versionOutput = (& $entrypointPath '--version' 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "The downloaded Codex CLI failed --version: $versionOutput"
    }
}
finally {
    Remove-Item -LiteralPath $archivePath -Force -ErrorAction SilentlyContinue
}
Write-Host "Codex CLI $($release.Version) ($($release.Tag)) downloaded to $entrypointPath"
Write-Host $versionOutput

Set-GitHubValue -Name 'CODEX_PATH' -Value $entrypointPath -Environment:$GitHubEnvironment
Set-GitHubValue -Name 'CODEX_VERSION' -Value $release.Version -Environment:$GitHubEnvironment
Set-GitHubValue -Name 'codex_path' -Value $entrypointPath -Output:$GitHubOutput
Set-GitHubValue -Name 'codex_version' -Value $release.Version -Output:$GitHubOutput
Set-GitHubValue -Name 'codex_tag' -Value $release.Tag -Output:$GitHubOutput
Set-GitHubValue -Name 'codex_sha256' -Value $actualExecutableHash -Output:$GitHubOutput
Set-GitHubValue -Name 'codex_package_sha256' -Value $actualPackageHash -Output:$GitHubOutput

[pscustomobject]@{
    Path = $entrypointPath
    PackageDirectory = $downloadDirectory
    PackageMetadata = $metadataPath
    Version = $release.Version
    Tag = $release.Tag
    Sha256 = $actualExecutableHash
    PackageSha256 = $actualPackageHash
} | ConvertTo-Json -Compress
