[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path ([IO.Path]::GetTempPath()) 'codex-link-capabilities.json')
)

$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ("codex-link-probe-" + [guid]::NewGuid().ToString('N'))
$result = [ordered]@{
    identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    symlink = [ordered]@{
        file = [ordered]@{ available = $false; detail = '' }
        directory = [ordered]@{ available = $false; detail = '' }
        available = $false
    }
    junction = [ordered]@{ available = $false; detail = '' }
    required = @('symlink', 'junction')
    status = 'failed'
}

try {
    $target = Join-Path $root 'target'
    $links = Join-Path $root 'links'
    New-Item -ItemType Directory -Force -Path $target, $links | Out-Null
    $targetFile = Join-Path $target 'probe.txt'
    [IO.File]::WriteAllText($targetFile, 'probe')

    try {
        $fileLink = Join-Path $links 'probe-file-link.txt'
        [IO.File]::CreateSymbolicLink($fileLink, $targetFile) | Out-Null
        if ([IO.File]::ReadAllText($fileLink) -ne 'probe') { throw 'File symbolic link target verification failed.' }
        $result.symlink.file.available = $true
        $result.symlink.file.detail = 'File symbolic link creation and read succeeded under the current identity.'
    }
    catch {
        $result.symlink.file.detail = $_.Exception.Message
    }

    try {
        $directoryLink = Join-Path $links 'probe-directory-link'
        [IO.Directory]::CreateSymbolicLink($directoryLink, $target) | Out-Null
        if (-not (Test-Path -LiteralPath (Join-Path $directoryLink 'probe.txt') -PathType Leaf)) { throw 'Directory symbolic link target verification failed.' }
        $result.symlink.directory.available = $true
        $result.symlink.directory.detail = 'Directory symbolic link creation and target verification succeeded under the current identity.'
    }
    catch {
        $result.symlink.directory.detail = $_.Exception.Message
    }
    $result.symlink.available = $result.symlink.file.available -and $result.symlink.directory.available

    try {
        $junction = Join-Path $links 'probe-junction'
        & (Join-Path $env:SystemRoot 'System32/cmd.exe') /d /c "mklink /J `"$junction`" `"$target`"" 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "mklink /J failed with exit code $LASTEXITCODE." }
        if (-not (Test-Path -LiteralPath (Join-Path $junction 'probe.txt') -PathType Leaf)) { throw 'Junction target verification failed.' }
        $result.junction.available = $true
        $result.junction.detail = 'Directory junction creation and target verification succeeded under the current identity.'
    }
    catch {
        $result.junction.detail = $_.Exception.Message
    }

    if ($result.symlink.available -and $result.junction.available) {
        $result.status = 'passed'
    }
}
finally {
    $resolvedRoot = [IO.Path]::GetFullPath($root)
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $resolvedRoot.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove probe directory outside the temporary root: $resolvedRoot"
    }
    if (Test-Path -LiteralPath $resolvedRoot) { Remove-Item -LiteralPath $resolvedRoot -Recurse -Force }
    foreach ($kind in @('file', 'directory')) {
        $result.symlink[$kind].detail = $result.symlink[$kind].detail.Replace($root, '[PROBE]')
    }
    $output = [IO.Path]::GetFullPath($OutputPath)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $output) | Out-Null
    [IO.File]::WriteAllText($output, (ConvertTo-Json $result -Depth 8) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

if ($result.status -ne 'passed') {
    throw "Required symbolic-link and junction capabilities are not both available under '$($result.identity)'. See $OutputPath"
}
Write-Host "Link capability passed for '$($result.identity)'. Evidence: $OutputPath"
