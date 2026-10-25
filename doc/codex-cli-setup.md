# Configure and use Codex CLI from Visual Studio

[日本語](codex-cli-setup_ja.md) · [README](../README.md)

This guide covers the extension's current implementation and its pinned Codex CLI **0.164.0** contract. Start with local mode. The **Connection target** flyout selects where App Server runs; it does not install Codex or choose a CLI executable. When it says **Connected to: Local** and **No remote profiles**, a remote profile is unnecessary for normal use.

## Contents

- [1. Choose the connection mode](#1-choose-the-connection-mode)
- [2. Install the complete Windows CLI package](#2-install-the-complete-windows-cli-package)
- [3. Authenticate the same CLI](#3-authenticate-the-same-cli)
- [4. Understand the three kinds of settings](#4-understand-the-three-kinds-of-settings)
- [5. Start a local conversation](#5-start-a-local-conversation)
- [6. Read the Connection target flyout](#6-read-the-connection-target-flyout)
- [7. Prepare an authenticated remote App Server](#7-prepare-an-authenticated-remote-app-server)
- [8. Reconnect, rotate a token, or return to local](#8-reconnect-rotate-a-token-or-return-to-local)
- [9. Diagnose configuration problems](#9-diagnose-configuration-problems)
- [10. References and validation boundary](#10-references-and-validation-boundary)

## 1. Choose the connection mode

| | Local (default) | Remote (Preview) |
|---|---|---|
| Visual Studio | Windows PC | Windows PC |
| Codex execution | CLI child process on that PC | App Server already running on another host, or a separate listener |
| CLI selection | `CODEX_PATH` / executable resolution | Server operator selects the server's CLI |
| CLI login and configuration | Windows user's Codex home | Server process user's Codex home |
| Transport | Worker-managed stdio | Worker-managed WebSocket |
| Workspace | Open local solution/folder | Same working tree accessible through configured local/server roots |
| Server lifetime | Extension starts and can restart its child | Operator starts/manages the server; extension connects/reconnects |
| Required profile | None | Saved and enabled remote profile |

The extension provides no remote installation, server lifecycle management or file synchronization. Mapping two unrelated checkouts does not synchronize their contents.

## 2. Install the complete Windows CLI package

Requirements: Windows x64 or Arm64; Visual Studio 2022 **17.14+** or Visual Studio 2026; an account/provider usable by Codex; and the extension VSIX.

1. Follow the official `rust-v0.164.0` release link in [README Requirements](../README.md#requirements).
2. Download `codex-package-x86_64-pc-windows-msvc.tar.gz` for x64 or `codex-package-aarch64-pc-windows-msvc.tar.gz` for Arm64.
3. Use the complete package. Keep its directory layout, including `bin\codex.exe`, `bin\codex-code-mode-host.exe` and `codex-resources\`. A standalone executable can initialize successfully while tool calls fail with `failed to spawn code-mode host`.
4. Compare the package SHA-256 with the digest for that exact asset on the release page. The standalone executable hash in `app-server-contract.json` is for a different asset.

Run the following in **PowerShell 7**. Enter your downloaded archive and its expected digest. The script stops before extraction if they disagree; it installs in a dedicated version directory.

~~~powershell
$archive = (Resolve-Path -LiteralPath (Read-Host 'Downloaded .tar.gz path')).Path
$expectedHash = (Read-Host 'SHA-256 for this package from the release page').Trim()
$actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
if ($expectedHash -notmatch '^[0-9a-fA-F]{64}$' -or $actualHash -ine $expectedHash) {
    throw 'Package SHA-256 mismatch.'
}
$codexInstall = Join-Path $env:LOCALAPPDATA 'OpenAI\Codex\0.164.0'
New-Item -ItemType Directory -Force -Path $codexInstall | Out-Null
tar -xzf $archive -C $codexInstall
if ($LASTEXITCODE -ne 0) { throw 'Package extraction failed.' }
$codexExe = Join-Path $codexInstall 'bin\codex.exe'
if (-not (Test-Path -LiteralPath $codexExe -PathType Leaf)) {
    throw 'Locate bin\codex.exe in the extracted package and correct codexInstall.'
}
if (-not (Test-Path -LiteralPath (Join-Path $codexInstall 'bin\codex-code-mode-host.exe'))) {
    throw 'The complete CLI package is required.'
}
$version = & $codexExe --version
if ($LASTEXITCODE -ne 0 -or $version.Trim() -ne 'codex-cli 0.164.0') {
    throw 'Expected codex-cli 0.164.0.'
}
$env:CODEX_PATH = $codexExe
[Environment]::SetEnvironmentVariable('CODEX_PATH', $codexExe, 'User')
$version
~~~

If the archive has an extra enclosing directory, locate its actual `bin\codex.exe` and use that full path when setting `CODEX_PATH`. Keep the sibling resources together. Installing a newer CLI is not equivalent to validating this extension against it.

### Select the intended executable

`CODEX_PATH` is a path to the executable file, not its containing directory, a command with arguments, or the App Server endpoint. Do not include quote characters in the stored value.

The Worker resolves the local executable in this order:

1. An existing file named by `CODEX_PATH`.
2. An explicit Worker option, when supplied internally. The current UI uses `codex` and has no executable-path picker.
3. `codex.exe`, then `codex.cmd` / `codex.bat` on `PATH`, excluding WindowsApps execution aliases.
4. A `codex.exe` under `%LOCALAPPDATA%\OpenAI\Codex\bin`.
5. The requested command as a final fallback.

An invalid `CODEX_PATH` can fall through to another installation. Check the exact path before troubleshooting the account or model:

~~~powershell
$codexExe = [Environment]::GetEnvironmentVariable('CODEX_PATH', 'User')
if (-not $codexExe -or -not (Test-Path -LiteralPath $codexExe -PathType Leaf)) {
    throw 'Set CODEX_PATH to the intended codex.exe first.'
}
& $codexExe --version
where.exe codex
~~~

`where.exe codex` checks `PATH`; it does not show which `CODEX_PATH` file the Worker will prefer. Setting `CODEX_PATH` does not add that directory to `PATH`. Use `& $codexExe` in the examples, even if the bare `codex` command is unavailable or selects a different version.

Close and reopen **all Visual Studio instances** after changing persistent environment variables. An already-running IDE retains its old environment. A terminal-only `$env:CODEX_PATH` assignment affects that terminal and children launched from it.

## 3. Authenticate the same CLI

Use the same Windows account and Codex home as Visual Studio:

~~~powershell
$codexExe = [Environment]::GetEnvironmentVariable('CODEX_PATH', 'User')
& $codexExe login
& $codexExe login status
~~~

Complete the browser sign-in, then confirm the status command succeeds. If browser callback networking is unavailable, and device login is enabled for your account/workspace:

~~~powershell
& $codexExe login --device-auth
& $codexExe login status
~~~

Codex CLI manages the credentials. Do not put credentials in the extension's settings or paste them into chat. An App Server connection can be healthy while its provider account is signed out.

The default Codex home on Windows is `%USERPROFILE%\.codex`. If you deliberately use `CODEX_HOME`, make the same value visible to both the login process and Visual Studio, then restart the IDE. Credentials and configuration in WSL's Linux home are separate from native Windows CLI state.

For API-key authentication, use the CLI's supported `login --with-api-key` flow with the key supplied on stdin from your existing secret-management mechanism. Never put a key in a command argument or in the remote profile. The local child environment allows `OPENAI_API_KEY`, but arbitrary provider-specific environment variables are not all forwarded. A provider that works in your terminal may require a different supported credential configuration for this child process.

In remote mode, authenticate the **server process account on the server host**. Local Windows CLI login does not authenticate the remote server's provider account.

## 4. Understand the three kinds of settings

| Setting | Location / owner | How to change it |
|---|---|---|
| CLI executable | Windows `CODEX_PATH`, then resolver fallbacks | Environment variable; restart Visual Studio |
| CLI/provider defaults | `%USERPROFILE%\.codex\config.toml` or `CODEX_HOME\config.toml`; project layers when trusted | Edit the appropriate TOML; restart local App Server to reload startup configuration |
| Extension preferences / remote profiles | `%APPDATA%\Kkamegawa.CodexForVisualStudio\settings.json` | Extension controls and Connection target flyout |
| Remote provider/configuration | Server user's Codex home and server startup environment | Server operator; reinitialize/restart the externally managed server when required |

The extension does not provide a general `config.toml` editor, arbitrary App Server startup arguments, or a CLI `--profile` selector. A **remote connection profile** stores an endpoint, token-file path and roots; it is not a Codex CLI configuration profile.

For ordinary local use, no manual TOML edit is required. To set explicit defaults, merge only the keys you need into your existing configuration; do not replace the whole file:

~~~toml
approval_policy = "on-request"
sandbox_mode = "workspace-write"
model_reasoning_effort = "medium"
~~~

User configuration, trusted project `.codex\config.toml` layers, runtime overrides and organization-enforced requirements can affect the result. Project configuration is ignored when the project is untrusted. Mark a project trusted only after reviewing its configuration and instructions. Organization policy can restrict choices.

The extension's model/reasoning/speed controls and permission mode can override defaults for a turn. Use `/status` to inspect desired and effective state rather than assuming the file alone determines it. General CLI configuration profiles and permission profiles are different features.

The settings JSON persists `ExperimentalApiEnabled`, `ApprovalModeId`, `ReasoningEffortId`, `ServiceTierId`, `RemoteProfiles` and `SelectedRemoteProfileName`. Tokens are never stored there. Do not hand-edit it while the extension is running. To reset, close the IDE and back it up before removing it; this also removes saved profiles and preferences.

## 5. Start a local conversation

1. Install the VSIX through **Extensions > Manage Extensions**, or download it using the repository release link in [README](../README.md). Restart Visual Studio.
2. Open the solution or folder you want Codex to work in.
3. Open **View > Codex**.
4. Keep **Connection target** on **Local**. An empty remote-profile list is normal.
5. Choose **Chat** for read-only work or **Agent** for work that may run commands or edit files.
6. Keep the default model or choose one offered by the server. Enter a small request such as “Summarize this solution's projects without changing files” and send it.
7. Check the connection state, reported CLI version and streamed response. Inspect `/status` if needed.

The local Worker starts and owns `codex app-server` over stdio. You do not need to run a listener, create a token file, or start the terminal TUI first.

Useful controls:

| Control | Meaning |
|---|---|
| `/model` | Show/select server-provided models. Availability still depends on the account. |
| `/reasoning` | Select supported reasoning effort; picker defaults can persist. |
| `/fast` | Change the supported speed/service tier. |
| `/permissions` | Show/select permission modes; `/approve` is an alias. |
| `/status` | Inspect current session and next-turn/effective settings. |
| Leading `/` menu | Built-in commands and available skills. |
| `#` file suggestion / attachment control | Add bounded workspace context. |
| Stop | Stop active work; inspect unresolved status before sending more work. |

Agent permission choices are **Ask for approval**, **Approve on my behalf**, **Full access**, and **Custom (config.toml)**, plus supported permission profiles. Chat forces read-only operation. Full access disables Codex's sandbox and ordinary approval prompts and requires confirmation. Custom does not clear overrides already applied to a thread; switching such a thread to Custom requires a new thread.

**Experimental API** is off by default and applies on the next initialization/reconnect. Enable it only for supported features that need experimental APIs; it is not a CLI installation or authentication setting. With CLI 0.163.0 and an experimental API connection, conversation history shows unread state and offers explicit Mark as read / Mark as unread actions. Opening or interacting with a conversation does not mark it read automatically. The explicit `/shell` command runs unsandboxed with full access in every supported CLI version; read its confirmation and the [command guide](slash-commands.md) before using it.

The **Windows sandbox** panel selects the local app-server backend: **Follow Codex configuration**, **Prefer MXC**, **Require MXC**, or **Do not use MXC**. The choice applies the next time the local app-server starts. **Check sandbox status** reports windows.allow_mxc when available and shows managed requirements; missing values are unknown, and the App Server does not expose whether MXC is required. **Start setup** runs the legacy elevated/unelevated setup; MXC needs no setup. **Do not use MXC** requires CLI 0.163.0 or later and passes windows.allow_mxc=false to local startup. These are local-only operations; they do not configure a remote host. See the [README FAQ](../README.md#faq) for what each backend setting does.

## 6. Read the Connection target flyout

| Display / action | Meaning |
|---|---|
| **Connected to: Local** / profile name | Current target presentation. Confirm operational status separately. |
| **Remote profiles (Preview)** | Saved endpoint metadata for optional WebSocket connections. |
| **No remote profiles. Codex runs on the local app-server.** | No profile has been saved. Local use is available. |
| **Add** | Open a new profile editor; it does not connect. |
| **Remove** | Remove selected saved metadata; it does not stop the external server. |
| **Save profile** | Validate and persist metadata; it does not read the token or test a connection. |
| **Check health** | Probe listener health for the selected saved enabled profile. |
| **Connect with this profile** | Apply the selected saved enabled profile and establish its RPC connection. |
| **Use local app-server** | Switch the connection back to the local child process. |
| **Close** | Close the flyout. |

“Save the profile before connecting with it” means the form must be saved, enabled and unchanged before Connect is eligible. A disabled button is expected with no profile, incomplete/invalid metadata or unsaved edits. Selecting a row and saving edits do not themselves switch the active connection. Selecting a saved row persists that profile as the target for a subsequent Worker connection/start; an unsaved row is not a saved remote target. Check the intended target again after restarting the IDE.

The Preview notice in the supplied screen includes a warning about unavailable per-account/per-endpoint isolation. The current code also has owner-boundary clearing and recovery mechanisms; the notice is not a guarantee that every isolation scenario has passed release acceptance. Treat remote mode as Preview and use it only with the intended server and workspace.

## 7. Prepare an authenticated remote App Server

The server operator must prepare these items **before** filling in the Visual Studio profile:

1. CLI 0.164.0 with its complete platform package and working tool helpers.
2. Provider authentication for the server process user; verify `login status` on that host.
3. The same working tree, accessible through the server root. Keep contents synchronized through your existing workspace/storage process.
4. A high-entropy bearer token and an authenticated WebSocket listener.
5. For a non-loopback connection, a TLS endpoint that supports WebSocket upgrades and forwards Authorization to App Server. The extension requires trusted TLS and does not disable certificate validation.
6. A private local copy of the **same client bearer token** on the Windows PC.

There are two independent authentications: the bearer token authorizes access to App Server; the server's Codex login authorizes model/provider use. The token file is not `auth.json`, an OpenAI API key or the signing secret for JWTs.

### Start the listener

The examples prompt for deployment-specific values; nothing is started by this documentation task. Choose a loopback listener for a local test, SSH forwarding, or a TLS reverse proxy running on the same host. Direct remote profile endpoints use `wss://<SERVER_HOST>:<TLS_PORT>/<OPTIONAL_ROUTE>`. Plain `ws://<LOOPBACK_HOST>:<PORT>` is accepted only for loopback.

On a **Windows server, PowerShell 7**, create a private parent directory first and use a new token-file path. This generates 32 random bytes as a 64-character hexadecimal token without printing it:

~~~powershell
$serverCodex = (Resolve-Path -LiteralPath (Read-Host 'Server codex.exe path')).Path
$serverVersion = & $serverCodex --version
if ($LASTEXITCODE -ne 0 -or $serverVersion.Trim() -ne 'codex-cli 0.164.0') {
    throw 'Expected codex-cli 0.164.0 on the server.'
}
& $serverCodex login status
if ($LASTEXITCODE -ne 0) { throw 'Sign in as the server process user first.' }
$serverTokenFile = Read-Host 'New absolute local token-file path'
if (-not [IO.Path]::IsPathFullyQualified($serverTokenFile)) { throw 'Use an absolute path.' }
if (-not (Test-Path -LiteralPath (Split-Path -Parent $serverTokenFile) -PathType Container)) {
    throw 'Create the private parent directory first.'
}
if (Test-Path -LiteralPath $serverTokenFile) { throw 'Choose a new file; do not overwrite a token.' }
$serverToken = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$tokenStream = [IO.File]::Open($serverTokenFile, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
try {
    $tokenBytes = [Text.Encoding]::UTF8.GetBytes($serverToken)
    $tokenStream.Write($tokenBytes)
} finally {
    $tokenStream.Dispose()
    $serverToken = $null
}
$listenUri = Read-Host 'App Server ws listener URI'
& $serverCodex app-server --listen $listenUri --ws-auth capability-token --ws-token-file $serverTokenFile
~~~

Keep the token file private using your normal access-control process. This foreground command runs until you stop it.

On a **macOS/Linux server, bash**, first install the complete 0.164.0 package for that host using its official release assets. OpenSSL is needed for this token-generation example:

~~~bash
read -r -p 'Server codex executable path: ' server_codex
server_version=$("$server_codex" --version) || exit 1
[ "$server_version" = "codex-cli 0.164.0" ] || { printf 'Expected codex-cli 0.164.0.\n' >&2; exit 1; }
"$server_codex" login status || exit 1
read -r -p 'New absolute local token-file path: ' server_token_file
case "$server_token_file" in /*) ;; *) printf 'Use an absolute path.\n' >&2; exit 1 ;; esac
[ -d "$(dirname "$server_token_file")" ] || { printf 'Create the private parent directory first.\n' >&2; exit 1; }
(umask 077; set -o noclobber; openssl rand -hex 32 > "$server_token_file") || exit 1
read -r -p 'App Server ws listener URI: ' listen_uri
"$server_codex" app-server --listen "$listen_uri" \
  --ws-auth capability-token --ws-token-file "$server_token_file"
~~~

Use an existing approved secure-transfer mechanism to copy this token into a private file on a local Windows drive. Do not generate a different token on the client. For signed-bearer-token deployments, the client file contains a valid issued bearer token, not the server's shared signing secret.

The listener's `--listen` option is separate from `--code-mode-host`, which chooses App Server's execution backend. This extension does not configure that backend. Do not assume the CLI accepts `wss` as its own listener URI: the pinned help lists `ws` for TCP listening. TLS termination and routing are deployment responsibilities.

### Fill in the profile

Open **Connection target > Add**, then enter:

| Field | Value |
|---|---|
| **Name** | A nonempty unique name, for example `Development server`. Names are unique ignoring case. |
| **Endpoint (wss://, or ws:// for loopback)** | Client-facing absolute WebSocket endpoint. A routing path is allowed; embedded credentials, query strings and fragments are rejected. |
| **Token file path** | Absolute path to the client token file on a local Windows drive, for example `C:\CodexSecrets\development.token`. UNC/network drives are rejected. |
| **Local root (this PC)** | Absolute root containing the local solution/folder, for example `C:\src\sample`. |
| **Server root (remote app-server)** | Absolute path to the same workspace on the server, for example `/srv/work/sample` or `D:\work\sample`. |
| **Enabled** | New profiles start disabled. Check this to permit connection. |

The token file must be UTF-8 (optional BOM), at most **16 KiB**, and contain one ASCII bearer token of at least **32 characters**. Outer whitespace is trimmed; embedded whitespace/control characters and invalid encoding are rejected. A symbolic link or junction must still resolve to a local file. Save checks the path's syntax; the Worker checks file access and contents at connection time.

Then select **Save profile**, optionally **Check health**, and **Connect with this profile** with a solution/folder open. Confirm the toolbar's profile name, connection status and `/status`. Send a read-only request first and verify the server working directory is the one you intended.

### Understand root mapping and health

With local root `C:\src\sample` and server root `/srv/work/sample`:

| Local path | Server path |
|---|---|
| `C:\src\sample` | `/srv/work/sample` |
| `C:\src\sample\src\app.cs` | `/srv/work/sample/src/app.cs` |
| `C:\src\sample-other\app.cs` | Rejected: outside local root |
| A linked path escaping the workspace boundary | Rejected by applicable physical containment checks |

Roots are directories, not solution files or endpoint URLs. Mapping preserves relative components and follows the server's path rules; it does not create directories, copy files or turn a local drive path into a server share. Server permissions and sandboxing are enforced on the server.

Health checks request `/healthz` and `/readyz` at the endpoint's authority root without a bearer token. With endpoint `wss://<HOST>:<PORT>/codex`, probes still target the authority's root routes. A proxy may need distinct health routing. HTTP success does not prove WebSocket upgrade, token validity, initialization or the configured route; health failure does not necessarily mean RPC cannot connect.

## 8. Reconnect, rotate a token, or return to local

Use **Restart local app-server** for the owned local process and **Reconnect remote app-server** for the active remote connection. Remote reconnect never restarts the server.

The current implementation can attempt bounded automatic recovery for transient connection/Worker loss: at most five attempts, 45 seconds per attempt and five minutes overall. Authentication, certificate, profile/root and owner changes stop automatic recovery. Read-only overload retry is a separate bounded mechanism. Neither authorizes replay of a user message, approval, shell command or attachment mutation with uncertain delivery.

If recovery restores a draft, inspect it and the recovered history before explicitly sending. A profile/account/root change can clear previous-owner conversation and composer state; do not depend on drafts surviving a target switch.

To rotate a token, coordinate the server's credential update, replace the client file with the new matching token, then explicitly reconnect. The Worker rereads it at each handshake; changing file contents alone triggers no reconnect. If you edit profile metadata, **Save profile > Connect with this profile** again; a stale or unsaved applied profile blocks Reconnect.

Choose **Use local app-server** to return to local execution. Remove deletes saved metadata and selects/persists an adjacent remaining profile, if any, for a future connection. It does not stop or switch the active connection. If you remove the currently applied profile, reconnect/recovery can no longer validate it; explicitly apply another saved profile or choose local.

## 9. Diagnose configuration problems

| Symptom | Check / action |
|---|---|
| View > Codex is missing | Supported Visual Studio version; extension enabled; IDE restarted after install. |
| CLI not found / wrong version | Check the exact `CODEX_PATH` file and `& $codexExe --version`; restart every IDE instance. |
| No response after Send | Open a solution/folder; verify CLI login with the same user/home; inspect connection status and diagnostics. |
| Every tool call fails to spawn code-mode host | Install the complete package and preserve helpers/resources. |
| Login works in WSL or another terminal only | Native user/home, `CODEX_HOME`, selected executable and inherited environment must match. |
| A model is missing or rejected | Use the server's catalog and account access; verify server CLI version for remote mode. |
| Config change seems ignored | Check project trust, configuration layers and extension overrides; restart the relevant server and inspect `/status`. |
| Connect / Check health disabled | Select a complete saved enabled profile with no pending edits. Open a workspace before Connect. |
| Token file missing/unreadable/invalid | Absolute local file, readable by current user, valid UTF-8/token format and correct size. |
| Authentication rejected | Client bearer token must match the endpoint's verifier; then separately check server provider login. |
| Certificate rejected | Fix trust, hostname and certificate chain on the endpoint. Validation cannot be bypassed. |
| Healthy but not connected | Check WebSocket upgrade/routing, token and RPC initialization separately. |
| Attachment or working directory rejected | Both roots must contain the intended workspace; check relative mapping and escaping links. |
| Reconnect refused after an edit | Save and explicitly apply the updated profile; do not retry stale metadata. |
| Corporate network failure | Check inherited proxy and CA environment and current Windows proxy policy; do not disable TLS. |

For diagnostics, open **View > Output** and select **Codex Diagnostics**; also inspect `%TEMP%\Kkamegawa.CodexForVisualStudio\diagnostics.log`. Extension/Worker entries are tagged and redacted. Review the file before sharing it and provide the CLI/extension versions, local/remote mode, failing step and categorical error. Do not share token files, `auth.json` or raw authenticated traffic.

The local CLI child forwards a fixed environment allowlist including `PATH`, `PATHEXT`, `CODEX_PATH`, `CODEX_HOME`, `OPENAI_API_KEY`, proxy variables (`HTTP_PROXY`, `HTTPS_PROXY`, `ALL_PROXY`, `NO_PROXY`) and supported CA variables. This is not a promise that every shell variable reaches the CLI. Restart Visual Studio after changing persistent environment settings.

## 10. References and validation boundary

- [README](../README.md): official release links, current requirements and abbreviated setup.
- [Slash commands](slash-commands.md): command syntax and permission semantics.
- [Remote connection design](secure-remote-connection-design.md), [path/owner isolation](path-state-isolation-design.md) and [connection/history recovery](connection-history-recovery-design.md): implementation boundaries.
- [Pinned contract](../app-server-contract.json): `targetVersion` and regression baseline.
- Official OpenAI documentation topics: **Codex CLI**, **Authentication**, **Config basics**, **Codex App Server**. Deployment-specific endpoints in this guide are placeholders; replace them with operator-provided values. Scripts prompt for actual values.

This guide was checked against source, CLI 0.159.1 `app-server --help` and official guidance. The 2026-10-12 same-PC loopback test initially found a product bug: credential loading changed the owner partition without advancing its generation, leaving the UI at **Connecting / Checking account**. The repaired source advances that owner generation and publishes the finalized Connecting before initialization events. Retesting the SDK-deployed extension 0.2.1.0 with the complete 0.159.1 Windows package and an existing ChatGPT login passed UI health checks, Local-to-Remote connection (**Ready / Signed in**), a real model response, a read of the test workspace marker, and manual reconnect after correcting a rejected bearer token. The externally managed server stayed running throughout. A separate fresh IDE launch with the remote profile already selected rendered **Ready / Signed in**, CLI 0.159.1 and **Connected to: Loopback test**, with the Worker connected to the external listener and no local CLI child. This test did not cover a different computer, TLS, cross-OS mapping, attachments, server-side token rotation, or a fresh provider login. For the 0.162.1 contract, `codex app-server --help` was confirmed identical to 0.159.1, and the official 0.162.1 Windows x64 executable completed `initialize`. The loopback, TLS and remote scenarios above were not repeated with 0.162.1. 0.159.1 is no longer supported; the extension rejects servers older than 0.161.0.
