[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$CodexPath,
    [int]$TimeoutSeconds = 60
)

# Starts `codex app-server` over stdio and completes the initialize handshake. This is the
# executable smoke check for a Codex release that is not the pinned schema contract: it proves
# the binary starts and answers JSON-RPC, without signing in or starting a thread.
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $CodexPath -PathType Leaf)) {
    throw "Codex executable was not found: $CodexPath"
}

$startInfo = [Diagnostics.ProcessStartInfo]::new((Resolve-Path -LiteralPath $CodexPath).Path)
$startInfo.ArgumentList.Add('app-server')
$startInfo.UseShellExecute = $false
$startInfo.RedirectStandardInput = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)

$process = [Diagnostics.Process]::Start($startInfo)
try {
    $request = [ordered]@{
        id = 1
        method = 'initialize'
        params = [ordered]@{
            clientInfo = [ordered]@{ name = 'codex_visual_studio_ci'; title = 'Codex for Visual Studio CI'; version = '0.0.0' }
            capabilities = [ordered]@{ experimentalApi = $false }
        }
    } | ConvertTo-Json -Compress -Depth 5
    $process.StandardInput.WriteLine($request)
    $process.StandardInput.Flush()

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ($true) {
        $remaining = $deadline - [DateTimeOffset]::UtcNow
        if ($remaining -le [TimeSpan]::Zero) {
            throw "codex app-server did not answer initialize within $TimeoutSeconds seconds."
        }

        $read = $process.StandardOutput.ReadLineAsync()
        if (-not $read.Wait($remaining)) {
            throw "codex app-server did not answer initialize within $TimeoutSeconds seconds."
        }

        $line = $read.Result
        if ($null -eq $line) {
            throw "codex app-server exited before answering initialize (exit code $($process.ExitCode))."
        }

        $message = $line | ConvertFrom-Json
        # Notifications may precede the response; only the response to id 1 decides the result.
        if ($message.PSObject.Properties.Name -notcontains 'id' -or $message.id -ne 1) {
            continue
        }

        if ($message.PSObject.Properties.Name -contains 'error') {
            throw "codex app-server rejected initialize: $($message.error.code) $($message.error.message)"
        }

        Write-Host "codex app-server initialize succeeded (userAgent: $($message.result.userAgent))."
        break
    }
}
finally {
    if (-not $process.HasExited) {
        $process.Kill($true)
        $process.WaitForExit()
    }

    $process.Dispose()
}
