# Codex for Visual Studio

A Visual Studio extension that runs the local Codex CLI app server from inside the IDE and exposes it
through a chat tool window: streaming responses, approval-aware command and file-change handling,
slash commands, and workspace context.

The extension is an out-of-process `Microsoft.VisualStudio.Extensibility` extension (`net8.0`) that
starts a `net8.0` worker process. The worker owns the `codex app-server` subprocess and talks to it
with newline-delimited JSON-RPC over stdio. No credentials are handled inside Visual Studio; sign-in
happens in the Codex CLI.

This extension is not published on the Visual Studio Marketplace yet. Install the VSIX from the
GitHub releases of this repository. A Marketplace link will be added here once it is available.

## Requirements

- Windows (x64 or Arm64)
- Visual Studio 2022 17.14 or later, or Visual Studio 2026 (Community, Professional, or Enterprise)
- Codex CLI 0.145.0 or later, installed with winget (see [Limitations](#limitations))
- A ChatGPT account that can sign in with `codex login`

## Setup

1. Install the Codex CLI:

   ```powershell
   winget install --id OpenAI.Codex --source winget
   ```

2. Confirm the version. The extension is verified against 0.145.0; older builds are not supported:

   ```powershell
   codex --version
   ```

   If the reported version is older than 0.145.0, update it:

   ```powershell
   winget upgrade --id OpenAI.Codex --source winget
   ```

3. Sign in once from a terminal. Visual Studio never sees the credentials:

   ```powershell
   codex login
   ```

4. Download `Codex.VisualStudio.Extension.vsix` from the latest GitHub release of this repository and
   double-click it to install, or install it from **Extensions > Manage Extensions**.

5. Restart Visual Studio and open **View > Codex**.

6. Open a solution or folder, type a prompt, and send it. The first turn starts the worker and the
   `codex app-server` subprocess. If nothing happens, see the [FAQ](#faq).

## Limitations

- **Old Codex CLI versions are not supported.** The verified version is 0.145.0. Earlier builds
  expose different app-server protocol shapes, so `initialize` or `turn/start` can fail or silently
  drop events. Issues reproduced only on older versions are out of scope.
- **Multiple Codex installations can select the wrong version.** Version managers (mise), winget, npm,
  and the Codex desktop app each place a `codex` executable in a different location, and the one that
  wins on `PATH` is not necessarily the newest. The worker resolves the executable in this order:
  1. the `CODEX_PATH` environment variable
  2. the explicit path in the worker options
  3. `codex.exe` on `PATH`, skipping the `WindowsApps` execution aliases
  4. `%LOCALAPPDATA%\OpenAI\Codex\bin`

  Run `where.exe codex` to see every match. When more than one is listed, set `CODEX_PATH` to the
  executable you want and restart Visual Studio.
- **npm installs are not recommended.** The `@openai/codex` npm package is known to break in this
  setup: the shim can stop resolving after a Node.js update, and the app server then exits
  immediately after start. Use the winget package instead.
- The winget manifest can lag a few days behind a Codex CLI release. Always confirm with
  `codex --version` rather than assuming the installed build is current.
- The extension is Windows-only and targets Visual Studio; there is no Visual Studio Code or
  cross-platform host.

## FAQ

**The Codex tool window does not appear under View.**
Check that Visual Studio is 17.14 or later, that the extension is listed and enabled in
**Extensions > Manage Extensions**, and restart Visual Studio once after installing the VSIX.

**Chat never responds, or the worker exits right away.**
This is almost always the Codex CLI, not the extension. Run `codex --version` (0.145.0 or later) and
`codex login` in a terminal. If both succeed there but not in Visual Studio, a different `codex` is
being launched; pin it with `CODEX_PATH` as described in [Limitations](#limitations).

**How do I pin one specific Codex CLI?**
Set the environment variable and restart Visual Studio so it inherits the change:

```powershell
[Environment]::SetEnvironmentVariable('CODEX_PATH', 'C:\path\to\codex.exe', 'User')
```

**Where are the logs?**
`%TEMP%\Kkamegawa.CodexForVisualStudio\diagnostics.log`. Extension and worker entries share the file
and are tagged `[EXTENSION]` and `[WORKER]`. URLs and credential-shaped values are redacted before
they are written.

**Can I stop being asked for approval on every command?**
Use `/permissions` (alias `/approve`) in the chat input, or the approval-mode picker in the tool
window. `ask`, `auto`, `full`, and `custom` are the built-in modes. `full` disables the Codex sandbox
and normal approval prompts, so it requires an explicit confirmation. See
[doc/slash-commands.md](doc/slash-commands.md) for the full command catalog, including `/model`,
`/reasoning`, and `/review`.

**Where are my settings stored?**
`%APPDATA%\Kkamegawa.CodexForVisualStudio\settings.json`. It holds the approval mode, reasoning
effort, service tier, and the experimental-API switch. Deleting the file resets everything to the
defaults; a corrupt file is ignored rather than blocking the tool window.

**Do I need a proxy or firewall exception?**
The extension itself only talks to a local child process over stdio and a local named pipe. All
outbound network traffic is made by the Codex CLI, so proxy and firewall configuration belongs to
the CLI and its own configuration file.

## Build and test

Prerequisites for development:

- Visual Studio 2022 17.14 or later with the Visual Studio extension development workload
- .NET 8 SDK
- A local Codex CLI (used to generate protocol schemas during the build)

Restore and build:

```powershell
dotnet restore CodexForVisualStudio.slnx
dotnet build CodexForVisualStudio.slnx -c Release --no-restore
```

`schemas/` contains generated output from the Apache-2.0-licensed Codex CLI and is intentionally
excluded from this MIT-licensed repository. When `schemas/codex_app_server_protocol.schemas.json` is
missing, building `Codex.AppServer.Protocol` on Windows automatically runs:

```powershell
codex app-server generate-json-schema --out schemas
```

The build prefers `CODEX_PATH` when it is set, then an executable `codex` from `PATH`, and then the
Codex desktop app's local executable cache. Set `CODEX_PATH` when automatic discovery cannot find an
executable Codex CLI:

```powershell
$env:CODEX_PATH = "C:\path\to\codex.exe"
dotnet build CodexForVisualStudio.slnx --no-restore
```

Run the unit tests:

```powershell
dotnet test tests/Codex.VisualStudio.Core.Tests/Codex.VisualStudio.Core.Tests.csproj
dotnet test tests/Codex.VisualStudio.Ui.Tests/Codex.VisualStudio.Ui.Tests.csproj
```

Run the live app-server proof of concept and regenerate schemas manually:

```powershell
dotnet run --project src/Codex.AppServer.Poc/Codex.AppServer.Poc.csproj -- --schema-out schemas --cwd .
```

When Codex is installed through WindowsApps, its execution alias may be blocked for child processes.
Pass `--codex <path-to-standalone-codex.exe>` in that environment.

The VSIX is produced by the out-of-process extension project:

```text
src/Codex.VisualStudio.Extension/bin/Release/net8.0-windows10.0.22621.0/Codex.VisualStudio.Extension.vsix
```

`src/Codex.VisualStudio.Package` is a `net472` placeholder for future in-process features and does
not produce a VSIX.

See [doc/implementation.md](doc/implementation.md) for the implemented boundaries and remaining
validation work.

## Debug in Visual Studio

Debug builds do not deploy the extension by default, so a development build never modifies an
installed Visual Studio instance silently.

1. Open `CodexForVisualStudio.slnx` in Visual Studio.
2. Set `Codex.VisualStudio.Extension` as the startup project.
3. Select the `Debug` configuration and press `F5`. Visual Studio builds, deploys, and starts an
   experimental instance.
4. In the experimental instance, open **View > Codex**.

The worker is a child process named `Codex.VisualStudio.Worker.exe`. To debug worker code, use
**Debug > Attach to Process**, select `Codex.VisualStudio.Worker.exe`, and choose the managed
.NET Core code type.

## Release

Releases are tag-driven:

1. Merge the release commit into `main`.
2. Push a `vX.Y.Z` tag (`vX.Y.Z.W` is also accepted for hotfix re-publishes).
3. The release workflow verifies that the tag is on `main`, writes the tag into the VSIX
   `Identity Version` (`vX.Y.Z` becomes `X.Y.Z.0`), builds and tests, and creates the GitHub release
   with the VSIX attached.

Pull requests are validated by the CI workflow, which builds the solution, runs both test projects,
and uploads the VSIX as a build artifact.

## Agent setup

Agent assets are managed with Microsoft APM. Install the CLI with `uv`:

```powershell
uv tool install apm-cli
apm marketplace add github/awesome-copilot
apm install
apm audit --ci --policy apm-policy.yml
```

APM deploys `.codex/agents/`, `.github/agents/`, and `.claude/agents/`. The dependency cache in
`apm_modules/` is ignored by Git; re-run `apm install` to recreate it from `apm.yml` and
`apm.lock.yaml`.

## Architecture

- UI layer: chat tool window, composer, approval prompts, and diff display.
- Presentation layer: chat view models, streaming buffers, and slash-command suggestions.
- Application layer: session lifecycle, slash command routing, approval workflows, and workspace
  context collection.
- Security layer: approval policy, path access checks, secret redaction, and audit logging.
- Protocol layer: `codex app-server` process hosting, JSON-RPC dispatch, schema and version guards,
  and notification handling.

The transport is stdio. WebSocket or Unix socket transports remain future options and must stay
local and authenticated.

Design and planning documents: [doc/design.md](doc/design.md), [doc/plan.md](doc/plan.md),
[doc/task.md](doc/task.md), [doc/implementation.md](doc/implementation.md),
[doc/adr.md](doc/adr.md).

## Security

Report vulnerabilities as described in [SECURITY.md](SECURITY.md). All `codex app-server` output is
treated as untrusted input, rendered through a safe Markdown pipeline, and redacted before logging.

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE).

Japanese translation: [README_ja.md](README_ja.md).
