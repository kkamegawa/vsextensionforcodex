[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'codex-release-validation'),
    [string[]]$Project = @(
        'tests/Codex.VisualStudio.Core.Tests/Codex.VisualStudio.Core.Tests.csproj',
        'tests/Codex.VisualStudio.Ui.Tests/Codex.VisualStudio.Ui.Tests.csproj'
    ),
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$contractManifest = Get-Content -LiteralPath (Join-Path $root 'app-server-contract.json') -Raw | ConvertFrom-Json
$evidenceRoot = [IO.Path]::GetFullPath($OutputDirectory)
$testRoot = Join-Path $evidenceRoot 'test-results'
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
$manifestPath = Join-Path $evidenceRoot 'test-results.json'
$manifest = [ordered]@{
    schemaVersion = 1
    commit = (git -C $root rev-parse HEAD 2>$null | Out-String).Trim()
    environment = [ordered]@{
        os = [Environment]::OSVersion.VersionString
        pwsh = $PSVersionTable.PSVersion.ToString()
        dotnet = ''
        configuration = $Configuration
        runnerOs = [string]$env:RUNNER_OS
    }
    workflow = [ordered]@{
        name = [string]$env:GITHUB_WORKFLOW
        event = [string]$env:GITHUB_EVENT_NAME
        runId = [string]$env:GITHUB_RUN_ID
        ref = [string]$env:GITHUB_REF
    }
    codex = [ordered]@{
        version = [string]$contractManifest.targetVersion
        sha256 = [string]$contractManifest.releases.($contractManifest.targetVersion).sha256
    }
    suites = [System.Collections.Generic.List[object]]::new()
    status = 'failed'
}
$dotnetVersion = (& dotnet --version 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw "Unable to query dotnet version: $dotnetVersion" }
$manifest.environment.dotnet = $dotnetVersion

function Write-AtomicJson([string]$Path, [object]$Value) {
    $temporaryPath = "$Path.$([guid]::NewGuid().ToString('N')).tmp"
    $json = ConvertTo-Json -InputObject $Value -Depth 30
    [IO.File]::WriteAllText($temporaryPath, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporaryPath -Destination $Path -Force
}

function Protect-EvidenceText([string]$Text) {
    $safeText = $Text
    $safeText = $safeText.Replace($root, '[WORKSPACE]')
    $safeText = $safeText.Replace([IO.Path]::GetTempPath().TrimEnd([IO.Path]::DirectorySeparatorChar), '[TEMP]')
    foreach ($item in Get-ChildItem Env:) {
        if ($item.Name -match '(?i)(secret|token|password|credential|api[_-]?key)' -and
            -not [string]::IsNullOrEmpty($item.Value)) {
            $safeText = $safeText.Replace([string]$item.Value, '[REDACTED]')
        }
    }
    $safeText = [regex]::Replace($safeText, '(?i)(authorization\s*[:=]\s*bearer\s+)[^\s<"'']+', '$1[REDACTED]')
    $safeText = [regex]::Replace($safeText, '(?i)("(?:access_token|refresh_token|id_token|client_secret|api_key)"\s*:\s*")[^"\\]*(?:\\.[^"\\]*)*"', '$1[REDACTED]"')
    return $safeText
}

function Read-Trx([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return [pscustomobject]@{ Available = $false; Results = @(); Counts = [ordered]@{ passed = 0; failed = 0; skipped = 0; total = 0 } }
    }
    $xml = [xml](Get-Content -LiteralPath $Path -Raw)
    $results = @($xml.SelectNodes("//*[local-name()='UnitTestResult']"))
    $tests = @($xml.SelectNodes("//*[local-name()='UnitTest']"))
    $definitions = @{}
    foreach ($test in $tests) {
        $method = $test.SelectSingleNode("./*[local-name()='TestMethod']")
        if ($null -ne $method) {
            $definitions[[string]$test.id] = [pscustomobject]@{
                FullyQualifiedName = ([string]$method.className + '.' + [string]$method.name)
                MethodName = [string]$method.name
                ClassName = [string]$method.className
            }
        }
    }
    $parsed = foreach ($result in $results) {
        $definition = $definitions[[string]$result.testId]
        $reason = $result.SelectSingleNode("./*[local-name()='Output']/*[local-name()='ErrorInfo']/*[local-name()='Message']")
        [pscustomobject]@{
            Id = [string]$result.testId
            Name = [string]$result.testName
            Outcome = [string]$result.outcome
            FullyQualifiedName = if ($definition) { $definition.FullyQualifiedName } else { '' }
            MethodName = if ($definition) { $definition.MethodName } else { '' }
            Reason = if ($reason) { Protect-EvidenceText ([string]$reason.InnerText) } else { '' }
        }
    }
    $failed = @($parsed | Where-Object Outcome -eq 'Failed').Count
    $passed = @($parsed | Where-Object Outcome -eq 'Passed').Count
    $skipped = @($parsed | Where-Object { $_.Outcome -in @('NotExecuted', 'Inconclusive', 'Timeout', 'Aborted') }).Count
    $caseCountByMethod = @{}
    foreach ($group in @($parsed | Where-Object { -not [string]::IsNullOrWhiteSpace($_.FullyQualifiedName) } | Group-Object FullyQualifiedName)) {
        $caseCountByMethod[$group.Name] = $group.Count
    }
    return [pscustomobject]@{
        Available = $true
        Results = @($parsed)
        CaseCountByMethod = $caseCountByMethod
        Counts = [ordered]@{ passed = $passed; failed = $failed; skipped = $skipped; total = $parsed.Count }
    }
}

function Invoke-DotnetTest([string]$ProjectPath, [string]$SuiteName, [string]$TrxName, [string]$LogName, [string]$Filter, [switch]$IncludePassed) {
    $trxPath = Join-Path $testRoot $TrxName
    $logPath = Join-Path $testRoot $LogName
    $arguments = @('test', $ProjectPath, '-c', $Configuration, '-v', 'minimal', '--logger', "trx;LogFileName=$TrxName", '--results-directory', $testRoot)
    if ($NoBuild) { $arguments += '--no-build' }
    if (-not [string]::IsNullOrWhiteSpace($Filter)) { $arguments += @('--filter', $Filter) }
    $started = [DateTime]::UtcNow
    $previousCodexPath = $env:CODEX_PATH
    Remove-Item Env:CODEX_PATH -ErrorAction SilentlyContinue
    try {
        $output = & dotnet @arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        if ($null -eq $previousCodexPath) { Remove-Item Env:CODEX_PATH -ErrorAction SilentlyContinue }
        else { $env:CODEX_PATH = $previousCodexPath }
    }
    $text = Protect-EvidenceText (($output | Out-String) + [Environment]::NewLine)
    [IO.File]::WriteAllText($logPath, $text, [Text.UTF8Encoding]::new($false))
    if (Test-Path -LiteralPath $trxPath -PathType Leaf) {
        $trxContent = Protect-EvidenceText ([IO.File]::ReadAllText($trxPath))
        [IO.File]::WriteAllText($trxPath, $trxContent, [Text.UTF8Encoding]::new($false))
    }
    $parsed = Read-Trx $trxPath
    $visibleResults = if ($IncludePassed) { @($parsed.Results) } else { @($parsed.Results | Where-Object Outcome -ne 'Passed') }
    $trxHash = if (Test-Path -LiteralPath $trxPath -PathType Leaf) { (Get-FileHash -Algorithm SHA256 -LiteralPath $trxPath).Hash.ToLowerInvariant() } else { $null }
    $logHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $logPath).Hash.ToLowerInvariant()
    return [pscustomobject]@{
        startedUtc = $started.ToString('o')
        endedUtc = [DateTime]::UtcNow.ToString('o')
        command = 'dotnet test ' + [IO.Path]::GetRelativePath($root, $ProjectPath) + ' -c ' + $Configuration +
            $(if ($NoBuild) { ' --no-build' } else { '' }) + $(if ($Filter) { " --filter $Filter" } else { '' })
        exitCode = $exitCode
        trx = [IO.Path]::GetRelativePath($evidenceRoot, $trxPath)
        trxSha256 = $trxHash
        log = [IO.Path]::GetRelativePath($evidenceRoot, $logPath)
        logSha256 = $logHash
        counts = $parsed.Counts
        trxAvailable = $parsed.Available
        results = $visibleResults
        resultCountByMethod = $parsed.CaseCountByMethod
    }
}

try {
    foreach ($projectPath in $Project) {
        $resolvedProject = [IO.Path]::GetFullPath((Join-Path $root $projectPath))
        if (-not (Test-Path -LiteralPath $resolvedProject -PathType Leaf)) { throw "Test project was not found: $projectPath" }
        $suiteName = [IO.Path]::GetFileNameWithoutExtension($resolvedProject)
        $initial = Invoke-DotnetTest $resolvedProject $suiteName "$suiteName-initial.trx" "$suiteName-initial.log" ''
        $suite = [ordered]@{
            project = [IO.Path]::GetRelativePath($root, $resolvedProject)
            initial = $initial
            retries = [System.Collections.Generic.List[object]]::new()
            skips = @($initial.results | Where-Object Outcome -in @('NotExecuted', 'Inconclusive', 'Timeout', 'Aborted') | ForEach-Object {
                [ordered]@{ testId = $_.Id; testName = $_.Name; outcome = $_.Outcome; reason = $_.Reason; classification = 'blocked-required-unclassified' }
            })
            retryStatus = 'not-needed'
            status = 'passed'
        }

        if (-not $initial.trxAvailable) {
            $suite.status = 'blocked'
            $suite.retryStatus = 'no-trx-result'
        }
        elseif ($initial.Counts.skipped -gt 0) {
            $suite.status = 'blocked'
            $suite.retryStatus = 'not-retried-required-skip'
        }
        elseif ($initial.exitCode -ne 0 -or $initial.Counts.failed -gt 0) {
            $suite.status = 'failed'
            $failedResults = @($initial.results | Where-Object Outcome -eq 'Failed')
            if ($failedResults.Count -eq 0) {
                $suite.retryStatus = 'not-retryable-abnormal-host-or-no-test-identity'
            }
            else {
                $retryable = [System.Collections.Generic.List[object]]::new()
                $blocked = $false
                foreach ($failure in $failedResults) {
                    if ([string]::IsNullOrWhiteSpace($failure.FullyQualifiedName)) { $blocked = $true; break }
                    if ($initial.resultCountByMethod[$failure.FullyQualifiedName] -ne 1) { $blocked = $true; break }
                    $retryable.Add($failure)
                }
                if ($blocked) {
                    $suite.retryStatus = 'not-retryable-non-isolatable-test-case'
                }
                else {
                    $suite.retryStatus = 'attempted'
                    $retryIndex = 0
                    foreach ($failure in $retryable) {
                        $retryIndex++
                        $filter = "FullyQualifiedName=$($failure.FullyQualifiedName)"
                        $retry = Invoke-DotnetTest $resolvedProject $suiteName "$suiteName-retry-$retryIndex.trx" "$suiteName-retry-$retryIndex.log" $filter -IncludePassed
                        $matching = @($retry.results | Where-Object FullyQualifiedName -eq $failure.FullyQualifiedName)
                        $retryOutcome = if ($retry.exitCode -eq 0 -and $retry.trxAvailable -and $matching.Count -eq 1 -and $matching[0].Outcome -eq 'Passed') { 'flaky' } else { 'failed' }
                        $suite.retries.Add([ordered]@{
                            testId = $failure.Id
                            testName = $failure.Name
                            filter = $filter
                            outcome = $retryOutcome
                            attempt = $retry
                        })
                    }
                    if (@($suite.retries | Where-Object outcome -eq 'flaky').Count -gt 0) {
                        $suite.status = 'flaky'
                        $suite.retryStatus = 'flaky-failure-gate'
                    }
                }
            }
        }
        $manifest.suites.Add($suite)
    }
}
finally {
    if (@($manifest.suites | Where-Object status -in @('failed', 'flaky')).Count -gt 0) {
        $manifest.status = 'failed'
    }
    elseif (@($manifest.suites | Where-Object status -eq 'blocked').Count -gt 0) {
        $manifest.status = 'blocked'
    }
    else { $manifest.status = 'passed' }
    Write-AtomicJson $manifestPath $manifest
}

if ($manifest.status -ne 'passed') {
    throw "One or more test suites failed or were flaky. See sanitized evidence: $manifestPath"
}
Write-Host "All test suites passed. Evidence: $manifestPath"
