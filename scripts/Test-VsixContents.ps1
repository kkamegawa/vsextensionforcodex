[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$ExpectedVersion,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$extensionOutput = Join-Path $root "src/Codex.VisualStudio.Extension/bin/$Configuration/net8.0-windows10.0.22621.0"
$workerOutput = Join-Path $root "src/Codex.VisualStudio.Worker/bin/$Configuration/net8.0"
$contractsOutput = Join-Path $root "src/Codex.VisualStudio.Contracts/bin/$Configuration/net8.0"
$package = Get-ChildItem -LiteralPath (Join-Path $root "src/Codex.VisualStudio.Extension/bin/$Configuration") -Filter '*.vsix' -Recurse |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if ($null -eq $package) { throw "No VSIX was produced for $Configuration." }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
$checks = [System.Collections.Generic.List[object]]::new()
try {
    $entries = @{}
    foreach ($entry in $archive.Entries) { $entries[$entry.FullName.Replace('\', '/')] = $entry }
    $manifestEntry = $entries['extension.vsixmanifest']
    if ($null -eq $manifestEntry) { throw 'VSIX does not contain extension.vsixmanifest.' }
    $reader = [IO.StreamReader]::new($manifestEntry.Open())
    try { $vsixManifest = [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
    $namespaces = [Xml.XmlNamespaceManager]::new($vsixManifest.NameTable)
    $namespaces.AddNamespace('v', 'http://schemas.microsoft.com/developer/vsx-schema/2011')
    $identity = $vsixManifest.SelectSingleNode('//v:PackageManifest/v:Metadata/v:Identity', $namespaces)
    if ($null -eq $identity) { throw 'VSIX identity is missing.' }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion) -and $identity.Version -ne $ExpectedVersion) {
        throw "VSIX version '$($identity.Version)' does not match expected '$ExpectedVersion'."
    }

    $pairs = @(
        [pscustomobject]@{ Entry = 'Codex.VisualStudio.Extension.dll'; Output = Join-Path $extensionOutput 'Codex.VisualStudio.Extension.dll' }
    )
    foreach ($pair in $pairs) {
        $entry = $entries[$pair.Entry]
        if ($null -eq $entry -or -not (Test-Path -LiteralPath $pair.Output -PathType Leaf)) {
            throw "Required build or VSIX payload is missing: $($pair.Entry)"
        }
        $stream = $entry.Open()
        try {
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $packagedHash = [Convert]::ToHexString($sha.ComputeHash($stream)).ToLowerInvariant() } finally { $sha.Dispose() }
        }
        finally { $stream.Dispose() }
        $builtHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $pair.Output).Hash.ToLowerInvariant()
        if ($packagedHash -ne $builtHash) { throw "VSIX payload '$($pair.Entry)' differs from build output." }
        $checks.Add([ordered]@{ entry = $pair.Entry; sha256 = $packagedHash; status = 'passed' })
    }

    $allWorkerFiles = @(Get-ChildItem -LiteralPath $workerOutput -File -Recurse | Where-Object Extension -ne '.pdb')
    $workerFiles = @($allWorkerFiles | Where-Object {
        [IO.Path]::GetRelativePath($workerOutput, $_.FullName) -notmatch '[\\/]'
    })
    if ($workerFiles.Count -eq 0) { throw "Worker build output is empty: $workerOutput" }
    foreach ($workerFile in $workerFiles) {
        $relativeWorkerPath = [IO.Path]::GetRelativePath($workerOutput, $workerFile.FullName).Replace('\', '/')
        $entryName = "Worker/$relativeWorkerPath"
        $entry = $entries[$entryName]
        if ($null -eq $entry -and $relativeWorkerPath -match '^[^/]+/.+\.resources\.dll$') {
            $entryName = $relativeWorkerPath
            $entry = $entries[$entryName]
        }
        if ($null -eq $entry) { throw "VSIX is missing Worker build output '$relativeWorkerPath'." }
        $stream = $entry.Open()
        try {
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $packagedHash = [Convert]::ToHexString($sha.ComputeHash($stream)).ToLowerInvariant() } finally { $sha.Dispose() }
        }
        finally { $stream.Dispose() }
        $builtHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $workerFile.FullName).Hash.ToLowerInvariant()
        if ($packagedHash -ne $builtHash) { throw "VSIX Worker payload '$entryName' differs from build output." }
        $checks.Add([ordered]@{ entry = $entryName; sha256 = $packagedHash; status = 'passed' })
    }

    $xamlPath = Join-Path $root 'src/Codex.VisualStudio.Extension/ToolWindows/ChatToolWindowContent.xaml'
    $extensionDll = Join-Path $extensionOutput 'Codex.VisualStudio.Extension.dll'
    if (-not (Test-Path -LiteralPath $xamlPath -PathType Leaf)) { throw 'Remote UI XAML source file is missing.' }
    $extensionAssembly = [Reflection.Assembly]::LoadFrom($extensionDll)
    $resourceName = @($extensionAssembly.GetManifestResourceNames() | Where-Object { $_.EndsWith('ToolWindows.ChatToolWindowContent.xaml', [StringComparison]::Ordinal) }) | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($resourceName)) { throw 'Built Extension assembly does not contain the embedded Remote UI XAML resource.' }
    $resourceStream = $extensionAssembly.GetManifestResourceStream($resourceName)
    if ($null -eq $resourceStream) { throw "Unable to read embedded XAML resource '$resourceName'." }
    try {
        $resourceBuffer = [IO.MemoryStream]::new()
        try { $resourceStream.CopyTo($resourceBuffer); $embeddedXaml = $resourceBuffer.ToArray() } finally { $resourceBuffer.Dispose() }
    }
    finally { $resourceStream.Dispose() }
    $sourceXaml = [IO.File]::ReadAllBytes($xamlPath)
    if ([Convert]::ToHexString($embeddedXaml) -cne [Convert]::ToHexString($sourceXaml)) { throw 'Embedded Remote UI XAML differs from its source file.' }
    $xamlHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($sourceXaml)).ToLowerInvariant()
    $checks.Add([ordered]@{ assemblyResource = $resourceName; sha256 = $xamlHash; status = 'passed' })

    $contractSource = Get-Content -LiteralPath (Join-Path $root 'src/Codex.VisualStudio.Contracts/WorkerContracts.cs') -Raw
    $contractMatch = [regex]::Match($contractSource, 'public const int Current = (?<version>\d+);')
    if (-not $contractMatch.Success) { throw 'Worker contract version declaration was not found.' }
    $contractVersion = [int]$contractMatch.Groups['version'].Value
    if ($contractVersion -ne 21) { throw "Expected Worker contract v21, found v$contractVersion." }
    $checks.Add([ordered]@{ source = 'WorkerContracts.cs'; contractVersion = $contractVersion; status = 'passed' })
}
finally { $archive.Dispose() }

$result = [ordered]@{
    schemaVersion = 1
    configuration = $Configuration
    vsix = [IO.Path]::GetFileName($package.FullName)
    sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $package.FullName).Hash.ToLowerInvariant()
    publisher = [string]$identity.Publisher
    version = [string]$identity.Version
    excludedWorkerSatelliteFileCount = $allWorkerFiles.Count - $workerFiles.Count
    checks = @($checks)
    status = 'passed'
}
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $fullOutput = [IO.Path]::GetFullPath($OutputPath)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $fullOutput) | Out-Null
    [IO.File]::WriteAllText($fullOutput, (ConvertTo-Json $result -Depth 12) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}
$result | ConvertTo-Json -Depth 12
