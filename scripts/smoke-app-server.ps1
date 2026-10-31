[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$CodexPath,
    [int]$TimeoutSeconds = 60,
    [switch]$VerifyIdleUnload,
    [int]$IdleUnloadTimeoutSeconds = 100
)

# Starts `codex app-server` over stdio and completes the initialize handshake. Optionally it
# verifies the one-minute idle unload notification and resumes the same thread.
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $CodexPath -PathType Leaf)) {
    throw "Codex executable was not found: $CodexPath"
}

$startInfo = [Diagnostics.ProcessStartInfo]::new((Resolve-Path -LiteralPath $CodexPath).Path)
$startInfo.ArgumentList.Add('app-server')
$startInfo.UseShellExecute = $false
$startInfo.RedirectStandardInput = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)

$process = [Diagnostics.Process]::Start($startInfo)
$stderrRead = $process.StandardError.ReadToEndAsync()
$pendingMessages = [System.Collections.Generic.List[object]]::new()
$threadId = $null

function Send-Request([int]$Id, [string]$Method, [object]$Params) {
    $request = [ordered]@{ id = $Id; method = $Method; params = $Params } | ConvertTo-Json -Compress -Depth 20
    $process.StandardInput.WriteLine($request)
    $process.StandardInput.Flush()
}

function Send-Notification([string]$Method, [object]$Params) {
    $notification = [ordered]@{ method = $Method; params = $Params } | ConvertTo-Json -Compress -Depth 20
    $process.StandardInput.WriteLine($notification)
    $process.StandardInput.Flush()
}

function Read-Message([TimeSpan]$Timeout) {
    $read = $process.StandardOutput.ReadLineAsync()
    if (-not $read.Wait($Timeout)) {
        throw "codex app-server did not answer within $([int]$Timeout.TotalSeconds) seconds."
    }

    $line = $read.Result
    if ($null -eq $line) {
        throw "codex app-server exited before answering (exit code $($process.ExitCode))."
    }

    return $line | ConvertFrom-Json
}

function Read-Response([int]$Id, [int]$TimeoutSeconds) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ($true) {
        $remaining = $deadline - [DateTimeOffset]::UtcNow
        if ($remaining -le [TimeSpan]::Zero) {
            throw "codex app-server did not answer request $Id within $TimeoutSeconds seconds."
        }

        $message = Read-Message $remaining
        if ($message.PSObject.Properties.Name -contains 'id' -and $message.id -eq $Id) {
            if ($message.PSObject.Properties.Name -contains 'error') {
                throw "codex app-server rejected request $Id`: $($message.error.code) $($message.error.message)"
            }

            return $message
        }

        $pendingMessages.Add($message)
    }
}

try {
    Send-Request 1 'initialize' ([ordered]@{
            clientInfo = [ordered]@{ name = 'codex_visual_studio_ci'; title = 'Codex for Visual Studio CI'; version = '0.0.0' }
            capabilities = [ordered]@{ experimentalApi = $false }
        })
    $initialize = Read-Response 1 $TimeoutSeconds
    Write-Host "codex app-server initialize succeeded (userAgent: $($initialize.result.userAgent))."
    Send-Notification 'initialized' ([ordered]@{})

    if ($VerifyIdleUnload) {
        Send-Request 2 'thread/start' ([ordered]@{ cwd = (Get-Location).Path; ephemeral = $false; threadSource = 'codex_visual_studio_release_validation' })
        $threadStart = Read-Response 2 $TimeoutSeconds
        $threadId = [string]$threadStart.result.thread.id
        if ([string]::IsNullOrWhiteSpace($threadId)) {
            throw 'codex app-server thread/start returned an empty thread ID.'
        }

        Send-Request 3 'thread/name/set' ([ordered]@{ threadId = $threadId; name = 'Codex Visual Studio idle unload verification' })
        [void](Read-Response 3 $TimeoutSeconds)

        $messageId = 'msg-' + [guid]::NewGuid().ToString('N')
        Send-Request 4 'thread/inject_items' ([ordered]@{
                threadId = $threadId
                items = @([ordered]@{
                        id = $messageId
                        type = 'message'
                        role = 'user'
                        content = @([ordered]@{ type = 'input_text'; text = 'Local app-server idle-unload verification.' })
                    })
            })
        [void](Read-Response 4 $TimeoutSeconds)

        Send-Request 5 'thread/unsubscribe' ([ordered]@{ threadId = $threadId })
        $unsubscribe = Read-Response 5 $TimeoutSeconds
        if ([string]$unsubscribe.result.status -notin @('unsubscribed', 'notSubscribed')) {
            throw "Unexpected thread/unsubscribe status: $($unsubscribe.result.status)"
        }

        $idleStart = [DateTimeOffset]::UtcNow
        $idleDeadline = $idleStart.AddSeconds($IdleUnloadTimeoutSeconds)
        $threadClosed = $false
        while ([DateTimeOffset]::UtcNow -lt $idleDeadline) {
            $closedMessage = $pendingMessages | Where-Object {
                $_.method -eq 'thread/closed' -and [string]$_.params.threadId -eq $threadId
            } | Select-Object -First 1
            if ($null -ne $closedMessage) {
                $threadClosed = $true
                break
            }

            $remaining = $idleDeadline - [DateTimeOffset]::UtcNow
            $message = Read-Message $remaining
            if ($message.method -eq 'thread/closed' -and [string]$message.params.threadId -eq $threadId) {
                $threadClosed = $true
                break
            }

            if ($message.PSObject.Properties.Name -contains 'id') {
                $pendingMessages.Add($message)
            }
        }

        $idleSeconds = ([DateTimeOffset]::UtcNow - $idleStart).TotalSeconds
        if (-not $threadClosed) {
            throw "codex app-server did not unload thread $threadId within $IdleUnloadTimeoutSeconds seconds."
        }
        if ($idleSeconds -lt 50) {
            throw "codex app-server unloaded the thread too early ($([Math]::Round($idleSeconds, 1)) seconds)."
        }

        Send-Request 6 'thread/resume' ([ordered]@{ threadId = $threadId })
        $resumed = Read-Response 6 $TimeoutSeconds
        if ([string]$resumed.result.thread.id -ne $threadId) {
            throw 'codex app-server resumed a different thread after idle unload.'
        }

        Write-Host "codex app-server unloaded thread after $([Math]::Round($idleSeconds, 1)) seconds and resumed it successfully."

        Send-Request 7 'thread/unsubscribe' ([ordered]@{ threadId = $threadId })
        [void](Read-Response 7 $TimeoutSeconds)
        Send-Request 8 'thread/delete' ([ordered]@{ threadId = $threadId })
        [void](Read-Response 8 $TimeoutSeconds)
        Write-Host 'Removed the local app-server idle-unload verification thread.'
    }
}
finally {
    if (-not $process.HasExited) {
        $process.Kill($true)
        $process.WaitForExit()
    }

    if ($null -ne $stderrRead) {
        [void]$stderrRead.Result
    }

    $process.Dispose()
}
