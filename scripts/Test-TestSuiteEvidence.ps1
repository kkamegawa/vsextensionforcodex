[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("test-suite-evidence-" + [guid]::NewGuid().ToString('N'))
$stubRoot = Join-Path $temporaryRoot 'bin'
$fakeDotnetPath = Join-Path $stubRoot 'fake-dotnet.ps1'
$shimPath = Join-Path $stubRoot 'dotnet.cmd'
$project = 'tests/Codex.VisualStudio.Core.Tests/Codex.VisualStudio.Core.Tests.csproj'

$fakeDotnet = @'
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$ArgumentList)
$ErrorActionPreference = 'Stop'
if ($ArgumentList -contains '--version') { Write-Output '10.0.0'; exit 0 }
$calls = 0
if (Test-Path -LiteralPath $env:FAKE_DOTNET_CALL_FILE) { $calls = [int](Get-Content $env:FAKE_DOTNET_CALL_FILE -Raw) }
$calls++
[IO.File]::WriteAllText($env:FAKE_DOTNET_CALL_FILE, [string]$calls)
if ($env:FAKE_DOTNET_MODE -eq 'host-crash') { exit 1 }
$resultsIndex = [Array]::IndexOf($ArgumentList, '--results-directory')
$logger = @($ArgumentList | Where-Object { $_ -like 'trx;LogFileName=*' }) | Select-Object -First 1
if ($resultsIndex -lt 0 -or -not $logger) { exit 2 }
$resultsDirectory = $ArgumentList[$resultsIndex + 1]
$trxName = $logger.Substring('trx;LogFileName='.Length)
New-Item -ItemType Directory -Force -Path $resultsDirectory | Out-Null
$definitions = ''
$results = ''
if ($env:FAKE_DOTNET_MODE -eq 'data-driven') {
    foreach ($number in 1..2) {
        $id = "case-$number"
        $definitions += "<UnitTest id=`"$id`" name=`"Sample row $number`"><TestMethod className=`"Fixture.Tests`" name=`"Sample`" /></UnitTest>"
        $results += "<UnitTestResult testId=`"$id`" testName=`"Sample row $number`" outcome=`"Failed`" />"
    }
}
elseif ($env:FAKE_DOTNET_MODE -eq 'empty') {
    # No definitions or results: discovery found nothing yet vstest still exits 0.
}
elseif ($env:FAKE_DOTNET_MODE -eq 'skip-and-flaky') {
    $definitions = '<UnitTest id="case-1" name="Sample"><TestMethod className="Fixture.Tests" name="Sample" /></UnitTest>' +
        '<UnitTest id="case-2" name="Unstable"><TestMethod className="Fixture.Tests" name="Unstable" /></UnitTest>'
    $unstableOutcome = if ($calls -eq 2) { 'Passed' } else { 'Failed' }
    $results = '<UnitTestResult testId="case-1" testName="Sample" outcome="NotExecuted"><Output><ErrorInfo><Message>Required capability was unavailable.</Message></ErrorInfo></Output></UnitTestResult>' +
        "<UnitTestResult testId=`"case-2`" testName=`"Unstable`" outcome=`"$unstableOutcome`" />"
}
elseif ($env:FAKE_DOTNET_MODE -eq 'skip') {
    $definitions = '<UnitTest id="case-1" name="Sample"><TestMethod className="Fixture.Tests" name="Sample" /></UnitTest>'
    $results = '<UnitTestResult testId="case-1" testName="Sample" outcome="NotExecuted"><Output><ErrorInfo><Message>Required capability was unavailable.</Message></ErrorInfo></Output></UnitTestResult>'
}
else {
    $definitions = '<UnitTest id="case-1" name="Sample"><TestMethod className="Fixture.Tests" name="Sample" /></UnitTest>'
    $outcome = if ($env:FAKE_DOTNET_MODE -eq 'flaky' -and $calls -eq 2) { 'Passed' } elseif ($env:FAKE_DOTNET_MODE -eq 'flaky') { 'Failed' } else { 'Passed' }
    $results = "<UnitTestResult testId=`"case-1`" testName=`"Sample`" outcome=`"$outcome`" />"
}
$trx = "<TestRun><TestDefinitions>$definitions</TestDefinitions><Results>$results</Results></TestRun>"
[IO.File]::WriteAllText((Join-Path $resultsDirectory $trxName), $trx)
if ($env:FAKE_DOTNET_MODE -in @('flaky', 'skip-and-flaky') -and $calls -eq 1) { exit 1 }
if ($env:FAKE_DOTNET_MODE -eq 'data-driven') { exit 1 }
exit 0
'@

$oldPath = $env:PATH
$oldScript = $env:FAKE_DOTNET_SCRIPT
$oldCallFile = $env:FAKE_DOTNET_CALL_FILE
$oldMode = $env:FAKE_DOTNET_MODE
$oldCodexPath = $env:CODEX_PATH
try {
    New-Item -ItemType Directory -Force -Path $stubRoot | Out-Null
    [IO.File]::WriteAllText($fakeDotnetPath, $fakeDotnet, [Text.UTF8Encoding]::new($true))
    [IO.File]::WriteAllText($shimPath, '@echo off' + "`r`n" + 'pwsh -NoProfile -File "%FAKE_DOTNET_SCRIPT%" %*' + "`r`n" + 'exit /b %ERRORLEVEL%' + "`r`n", [Text.UTF8Encoding]::new($true))
    $env:PATH = "$stubRoot;$oldPath"
    $env:FAKE_DOTNET_SCRIPT = $fakeDotnetPath
    Remove-Item Env:CODEX_PATH -ErrorAction SilentlyContinue

    $scenarios = @(
        [pscustomobject]@{ Name = 'passed'; Mode = 'passed'; ExpectedExit = 0; ExpectedStatus = 'passed'; ExpectedRetry = 'not-needed'; ExpectedCalls = 1 },
        [pscustomobject]@{ Name = 'flaky'; Mode = 'flaky'; ExpectedExit = 1; ExpectedStatus = 'flaky'; ExpectedRetry = 'flaky-failure-gate'; ExpectedCalls = 2 },
        [pscustomobject]@{ Name = 'required-skip'; Mode = 'skip'; ExpectedExit = 1; ExpectedStatus = 'blocked'; ExpectedRetry = 'not-retried-required-skip'; ExpectedCalls = 1 },
        [pscustomobject]@{ Name = 'skip-and-flaky'; Mode = 'skip-and-flaky'; ExpectedExit = 1; ExpectedStatus = 'flaky'; ExpectedRetry = 'flaky-failure-gate'; ExpectedCalls = 2 },
        [pscustomobject]@{ Name = 'zero-tests'; Mode = 'empty'; ExpectedExit = 1; ExpectedStatus = 'failed'; ExpectedRetry = 'no-tests-executed'; ExpectedCalls = 1 },
        [pscustomobject]@{ Name = 'data-driven'; Mode = 'data-driven'; ExpectedExit = 1; ExpectedStatus = 'failed'; ExpectedRetry = 'not-retryable-non-isolatable-test-case'; ExpectedCalls = 1 },
        [pscustomobject]@{ Name = 'abnormal-host'; Mode = 'host-crash'; ExpectedExit = 1; ExpectedStatus = 'blocked'; ExpectedRetry = 'no-trx-result'; ExpectedCalls = 1 }
    )

    foreach ($scenario in $scenarios) {
        $caseRoot = Join-Path $temporaryRoot $scenario.Name
        New-Item -ItemType Directory -Force -Path $caseRoot | Out-Null
        $env:FAKE_DOTNET_MODE = $scenario.Mode
        $env:FAKE_DOTNET_CALL_FILE = Join-Path $caseRoot 'calls.txt'
        $manifestPath = Join-Path $caseRoot 'test-results.json'
        & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Invoke-TestSuitesWithEvidence.ps1') -Configuration Release -OutputDirectory $caseRoot -Project $project -NoBuild *> $null
        $actualExit = $LASTEXITCODE
        if ($actualExit -ne $scenario.ExpectedExit) { throw "$($scenario.Name): expected exit $($scenario.ExpectedExit), got $actualExit." }
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if ($manifest.suites.Count -ne 1 -or $manifest.suites[0].status -ne $scenario.ExpectedStatus) {
            throw "$($scenario.Name): expected suite status '$($scenario.ExpectedStatus)', got '$($manifest.suites[0].status)'."
        }
        if ($manifest.suites[0].retryStatus -ne $scenario.ExpectedRetry) {
            throw "$($scenario.Name): expected retry status '$($scenario.ExpectedRetry)', got '$($manifest.suites[0].retryStatus)'."
        }
        $actualCalls = [int](Get-Content -LiteralPath $env:FAKE_DOTNET_CALL_FILE -Raw)
        if ($actualCalls -ne $scenario.ExpectedCalls) { throw "$($scenario.Name): expected $($scenario.ExpectedCalls) dotnet calls, got $actualCalls." }
    }
    Write-Host 'Test-suite evidence behavior passed: pass, sticky flaky, required skip, skip with retried failure, zero tests, non-isolatable data row, and abnormal host.'
}
finally {
    if ($null -eq $oldPath) { Remove-Item Env:PATH -ErrorAction SilentlyContinue } else { $env:PATH = $oldPath }
    if ($null -eq $oldScript) { Remove-Item Env:FAKE_DOTNET_SCRIPT -ErrorAction SilentlyContinue } else { $env:FAKE_DOTNET_SCRIPT = $oldScript }
    if ($null -eq $oldCallFile) { Remove-Item Env:FAKE_DOTNET_CALL_FILE -ErrorAction SilentlyContinue } else { $env:FAKE_DOTNET_CALL_FILE = $oldCallFile }
    if ($null -eq $oldMode) { Remove-Item Env:FAKE_DOTNET_MODE -ErrorAction SilentlyContinue } else { $env:FAKE_DOTNET_MODE = $oldMode }
    if ($null -eq $oldCodexPath) { Remove-Item Env:CODEX_PATH -ErrorAction SilentlyContinue } else { $env:CODEX_PATH = $oldCodexPath }
    if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
}
