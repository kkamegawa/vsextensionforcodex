param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [string]$CodexPath
)

$ErrorActionPreference = 'Stop'
$output = [System.IO.Path]::GetFullPath($OutputDirectory)

function Resolve-CodexExecutable {
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        if (-not (Test-Path -LiteralPath $RequestedPath -PathType Leaf)) {
            throw "CODEX_PATH does not point to an existing file: $RequestedPath"
        }

        return (Resolve-Path -LiteralPath $RequestedPath).Path
    }

    $commands = @(Get-Command codex -CommandType Application -All -ErrorAction SilentlyContinue)
    foreach ($command in $commands) {
        if ($command.Source -notmatch '[\\/]WindowsApps[\\/]') {
            return $command.Source
        }
    }

    $localBin = Join-Path $env:LOCALAPPDATA 'OpenAI\Codex\bin'
    if (Test-Path -LiteralPath $localBin -PathType Container) {
        $candidate = Get-ChildItem -LiteralPath $localBin -Filter codex.exe -File -Recurse |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 1
        if ($null -ne $candidate) {
            return $candidate.FullName
        }
    }

    if ($commands.Count -gt 0) {
        return $commands[0].Source
    }

    throw 'Codex CLI was not found. Install Codex or set CODEX_PATH to an executable path.'
}

$codex = Resolve-CodexExecutable -RequestedPath $CodexPath
New-Item -ItemType Directory -Force -Path $output | Out-Null
Write-Host "Generating Codex app-server schemas at $output"
& $codex app-server generate-json-schema --out $output
if ($LASTEXITCODE -ne 0) {
    throw "Codex schema generation failed with exit code $LASTEXITCODE."
}
