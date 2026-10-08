[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release', 'Both')][string]$Configuration = 'Both',
    [string]$OutputDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'codex-release-validation'),
    [string]$CodexDirectory
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$schemaRoot = Join-Path $outputRoot 'schemas'
$cliRoot = if ([string]::IsNullOrWhiteSpace($CodexDirectory)) { Join-Path $outputRoot 'codex-cli' } else { [IO.Path]::GetFullPath($CodexDirectory) }
New-Item -ItemType Directory -Force -Path $outputRoot, $schemaRoot, $cliRoot | Out-Null
$manifestPath = Join-Path $outputRoot 'release-validation.json'
$manifestDefinition = Get-Content -LiteralPath (Join-Path $root 'app-server-contract.json') -Raw | ConvertFrom-Json
$configurations = if ($Configuration -eq 'Both') { @('Debug', 'Release') } else { @($Configuration) }
$manifest = [ordered]@{
    schemaVersion = 1
    commit = (git -C $root rev-parse HEAD 2>$null | Out-String).Trim()
    host = [ordered]@{ os = [Environment]::OSVersion.VersionString; pwsh = $PSVersionTable.PSVersion.ToString(); dotnet = ''; visualStudio = '' }
    codex = [ordered]@{
        targetVersion = $manifestDefinition.targetVersion
        assets = [System.Collections.Generic.List[object]]::new()
    }
    scenarioClass = 'Local-required'
    scenarios = [System.Collections.Generic.List[object]]::new()
    status = 'failed'
}

function Write-Manifest {
    $temporary = "$manifestPath.$([guid]::NewGuid().ToString('N')).tmp"
    [IO.File]::WriteAllText($temporary, (ConvertTo-Json $manifest -Depth 30) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $manifestPath -Force
}

function Add-Scenario([string]$Id, [string]$Command, [scriptblock]$Action, [string]$LogFile) {
    $started = [DateTime]::UtcNow
    $status = 'passed'
    $exitCode = 0
    $detail = ''
    try {
        Push-Location $root
        try {
            $text = & $Action 2>&1 | Out-String
            $exitCode = if ($null -ne $LASTEXITCODE) { $LASTEXITCODE } else { 0 }
            $detail = $text.Trim()
            if ($exitCode -ne 0) { $status = 'failed' }
        }
        finally { Pop-Location }
    }
    catch {
        $status = 'failed'
        $exitCode = if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) { $LASTEXITCODE } else { 1 }
        $detail = $_.Exception.Message
    }
    foreach ($item in Get-ChildItem Env:) {
        if ($item.Name -match '(?i)(secret|token|password|credential|api[_-]?key)' -and -not [string]::IsNullOrEmpty($item.Value)) {
            $detail = $detail.Replace([string]$item.Value, '[REDACTED]')
        }
    }
    $detail = $detail.Replace($root, '[WORKSPACE]').Replace($outputRoot, '[EVIDENCE]')
    $detail = $detail.Replace([IO.Path]::GetTempPath().TrimEnd([IO.Path]::DirectorySeparatorChar), '[TEMP]')
    if (-not [string]::IsNullOrWhiteSpace($LogFile)) {
        $logPath = Join-Path $outputRoot $LogFile
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $logPath) | Out-Null
        [IO.File]::WriteAllText($logPath, $detail + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    }
    $logHash = if (-not [string]::IsNullOrWhiteSpace($LogFile)) { (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $outputRoot $LogFile)).Hash.ToLowerInvariant() } else { $null }
    $manifest.scenarios.Add([ordered]@{
        id = $Id
        class = 'Local-required'
        status = $status
        command = $Command
        startedUtc = $started.ToString('o')
        endedUtc = [DateTime]::UtcNow.ToString('o')
        exitCode = $exitCode
        log = $LogFile
        logSha256 = $logHash
        detail = if ($detail.Length -gt 2000) { $detail.Substring(0, 2000) + '[TRUNCATED]' } else { $detail }
    })
    Write-Manifest
    return $status -eq 'passed'
}

$completed = $false
try {
    $dotnetText =(& dotnet --version 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "Unable to query dotnet SDK: $dotnetText" }
    $manifest.host.dotnet = $dotnetText
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) { $manifest.host.visualStudio = (& $vswhere -latest -property installationVersion 2>$null | Out-String).Trim() }

    $cliPaths = @{}
    $allCliInstalled = $true
    foreach ($version in @($manifestDefinition.regressionVersion, $manifestDefinition.targetVersion)) {
        $versionCopy = $version
        $versionCliRoot = Join-Path $cliRoot $versionCopy
        $versionAsset = $manifestDefinition.releases.$versionCopy
        $versionCliPath = Join-Path $versionCliRoot $versionAsset.asset
        $installed = Add-Scenario "cli.pinned-download-hash.$version" "pwsh scripts/install-codex.ps1 -Version $version" {
            & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'install-codex.ps1') -Version $versionCopy -OutputDirectory $versionCliRoot
        } "cli-install-$version.log"
        if ($installed) {
            $cliPaths[$versionCopy] = $versionCliPath
            $manifest.codex.assets.Add([ordered]@{ version = $versionCopy; sha256 = $versionAsset.sha256; status = 'passed' })
        }
        else {
            $allCliInstalled = $false
            $manifest.codex.assets.Add([ordered]@{ version = $versionCopy; sha256 = $versionAsset.sha256; status = 'failed' })
        }
    }
    $codexPath = if ($cliPaths.ContainsKey([string]$manifestDefinition.targetVersion)) { $cliPaths[[string]$manifestDefinition.targetVersion] } else { '' }

    $schemaGenerationOk = $allCliInstalled
    if ($allCliInstalled) {
        foreach ($version in @($manifestDefinition.regressionVersion, $manifestDefinition.targetVersion)) {
            foreach ($surface in @('stable', 'experimental')) {
                $id = "schema.generate.$version.$surface"
                $versionCopy = $version
                $versionCodexPath = $cliPaths[$versionCopy]
                $surfaceCopy = $surface
                $ok = Add-Scenario $id "generate-schemas.ps1 -Version $version -Surface $surface" {
                    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'generate-schemas.ps1') -OutputDirectory $schemaRoot -Version $versionCopy -Surface $surfaceCopy -CodexPath $versionCodexPath
                } "schema/$version-$surface.log"
                if (-not $ok) { $schemaGenerationOk = $false }
            }
        }
        $cacheOk = Add-Scenario 'schema.cache.contract' 'test-schema-cache.ps1' {
            & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'test-schema-cache.ps1') -CodexPath $codexPath
        } 'schema/cache-contract.log'
        if (-not $cacheOk) { $schemaGenerationOk = $false }
    }
    else {
        foreach ($version in @($manifestDefinition.regressionVersion, $manifestDefinition.targetVersion)) {
            foreach ($surface in @('stable', 'experimental')) {
                $manifest.scenarios.Add([ordered]@{ id = "schema.generate.$version.$surface"; class = 'Local-required'; status = 'not-run'; command = 'blocked by pinned CLI download failure'; exitCode = $null; log = $null })
            }
        }
    }

    if ($schemaGenerationOk) {
        foreach ($surface in @('stable', 'experimental')) {
            $surfaceCopy = $surface
            [void](Add-Scenario "schema.compare.$surface" "compare-schemas.ps1 -Surface $surface" {
                & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'compare-schemas.ps1') `
                    -BaselineDirectory (Join-Path $schemaRoot "$($manifestDefinition.regressionVersion)/$surfaceCopy") `
                    -TargetDirectory (Join-Path $schemaRoot "$($manifestDefinition.targetVersion)/$surfaceCopy") -Surface $surfaceCopy
            } "schema/compare-$surface.log")
            [void](Add-Scenario "schema.method-surface.$surface" "verify-contract-surface.ps1 -Surface $surface" {
                & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'verify-contract-surface.ps1') -SchemaDirectory (Join-Path $schemaRoot "$($manifestDefinition.targetVersion)/$surfaceCopy")
            } "schema/method-surface-$surface.log")
        }
    }

    [void](Add-Scenario 'tests.evidence-policy' 'Test-TestSuiteEvidence.ps1' {
        & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Test-TestSuiteEvidence.ps1')
    } 'test-evidence-policy.log')

    $linkPath = Join-Path $outputRoot 'link-capabilities.json'
    $linkCapabilityPassed = Add-Scenario 'environment.link-capability' 'Test-LinkCapabilities.ps1' {
        & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Test-LinkCapabilities.ps1') -OutputPath $linkPath
    } 'link-capability.log'
    if (-not $linkCapabilityPassed) {
        $manifest.scenarios[-1].status = 'blocked'
        if (Test-Path -LiteralPath $linkPath) { $manifest.scenarios[-1].detail = (Get-Content -LiteralPath $linkPath -Raw | ConvertFrom-Json).symlink.file.detail }
    }

    foreach ($config in $configurations) {
        $configurationCopy = $config
        [void](Add-Scenario "build.$config" "dotnet build CodexForVisualStudio.slnx -c $config" {
            $oldCodexPath = $env:CODEX_PATH
            try {
                $env:CODEX_PATH = $codexPath
                & dotnet build CodexForVisualStudio.slnx -c $configurationCopy -v minimal
            }
            finally { if ($null -eq $oldCodexPath) { Remove-Item Env:CODEX_PATH -ErrorAction SilentlyContinue } else { $env:CODEX_PATH = $oldCodexPath } }
        } "build/$config.log")
    }

    foreach ($config in $configurations) {
        $configurationCopy = $config
        $testsDirectory = Join-Path $outputRoot "tests/$config"
        $testSuitesPassed = Add-Scenario "test.suites.$config" "Invoke-TestSuitesWithEvidence.ps1 -Configuration $config" {
            & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Invoke-TestSuitesWithEvidence.ps1') -Configuration $configurationCopy -OutputDirectory $testsDirectory -NoBuild
        } "tests/$config-runner.log"
        if (-not $testSuitesPassed) {
            $testManifestPath = Join-Path $testsDirectory 'test-results.json'
            if (Test-Path -LiteralPath $testManifestPath) {
                $testManifest = Get-Content -LiteralPath $testManifestPath -Raw | ConvertFrom-Json
                if ($testManifest.status -eq 'blocked') { $manifest.scenarios[-1].status = 'blocked' }
                $manifest.scenarios[-1].detail = ($testManifest.suites | ForEach-Object { "$($_.project): $($_.status); skipped=$($_.initial.counts.skipped); retry=$($_.retryStatus)" }) -join '; '
            }
        }
    }

    if ($configurations -contains 'Release') {
        [void](Add-Scenario 'package.vsix.release' 'Test-VsixContents.ps1 -Configuration Release' {
            & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Test-VsixContents.ps1') -Configuration Release -OutputPath (Join-Path $outputRoot 'vsix-inspection.json')
        } 'vsix-inspection.log')
    }
    $completed = $true
}
finally {
    # An exception that escapes before every scenario is recorded must never read as a pass.
    if (-not $completed -or @($manifest.scenarios | Where-Object status -in @('failed', 'flaky')).Count -gt 0) {
        $manifest.status = 'failed'
    }
    elseif (@($manifest.scenarios | Where-Object status -in @('blocked', 'not-run')).Count -gt 0) {
        $manifest.status = 'blocked'
    }
    else { $manifest.status = 'passed' }
    $manifest.completedUtc = [DateTime]::UtcNow.ToString('o')
    Write-Manifest
}

if ($manifest.status -ne 'passed') { throw "Release validation did not pass. Review evidence in $outputRoot" }
Write-Host "Release validation passed. Evidence: $manifestPath"
