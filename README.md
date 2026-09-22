# Codex for Visual Studio

A Visual Studio extension that runs the local Codex CLI app server from inside the IDE and exposes it through a chat tool window: streaming responses, approval-aware command and file-change handling, slash commands, custom skill invocation, and workspace context.

The extension is an out-of-process `Microsoft.VisualStudio.Extensibility` extension (`net8.0`) that starts a `net8.0` worker process. The worker owns the `codex app-server` subprocess and talks to it with newline-delimited JSON-RPC over stdio. No credentials are handled inside Visual Studio; sign-in happens in the Codex CLI.

## How to use

[![How to use Codex for Visual Studio](https://img.youtube.com/vi/J5mvALbV8Mk/0.jpg)](https://youtu.be/J5mvALbV8Mk)

## Requirements

- Windows (x64 or Arm64)
- Visual Studio 2022 17.14 or later, or Visual Studio 2026 (Community, Professional, or Enterprise)
- The official Windows x64 Codex CLI executable from the [latest stable release](https://github.com/openai/codex/releases/latest)
- A ChatGPT account that can sign in with `codex login`

## Setup

1. Download the Windows x64 MSVC executable from the [latest stable Codex release](https://github.com/openai/codex/releases/latest) into a local directory. The extension starts this standalone executable directly; winget is not required.

   ```powershell
   $codexDirectory = Join-Path $env:LOCALAPPDATA 'OpenAI\Codex\bin'
   New-Item -ItemType Directory -Force -Path $codexDirectory | Out-Null
   Invoke-WebRequest `
     -Uri 'https://github.com/openai/codex/releases/latest/download/codex-x86_64-pc-windows-msvc.exe' `
     -OutFile (Join-Path $codexDirectory 'codex.exe')
   [Environment]::SetEnvironmentVariable('CODEX_PATH', (Join-Path $codexDirectory 'codex.exe'), 'User')
   ```

2. Restart Visual Studio and confirm the executable. The current latest release is 0.155.1, which is the app-server contract used by this extension:

   ```powershell
   codex --version
   ```

3. Sign in once from a terminal. Visual Studio never sees the credentials:

   ```powershell
   codex login
   ```

4. Download `Codex.VisualStudio.Extension.vsix` from the latest GitHub release of this repository and double-click it to install, or install it from **Extensions > Manage Extensions**.

5. Restart Visual Studio and open **View > Codex**.

6. Open a solution or folder, type a prompt, and send it. The first turn starts the worker and the `codex app-server` subprocess. If nothing happens, see the [FAQ](#faq).

## Limitations

- **Other Codex CLI versions are not the target contract.** The verified version is 0.155.1. Version 0.154.0 is retained only as the schema regression baseline. Other builds
  expose different app-server protocol shapes, so `initialize` or `turn/start` can fail or silently drop events. Issues reproduced only on older versions are out of scope.
- **Multiple Codex installations can select the wrong version.** Version managers (mise), winget, npm, and the Codex desktop app each place a `codex` executable in a different location, and the one that wins on `PATH` is not necessarily the newest. The worker resolves the executable in this order:
  1. the `CODEX_PATH` environment variable
  2. the explicit path in the worker options
  3. `codex.exe` on `PATH`, skipping the `WindowsApps` execution aliases
  4. `%LOCALAPPDATA%\OpenAI\Codex\bin`

  Run `where.exe codex` to see every match. When more than one is listed, set `CODEX_PATH` to the executable you want and restart Visual Studio.
- **npm installs are not recommended.** The `@openai/codex` npm package is known to break in this
  setup: the shim can stop resolving after a Node.js update, and the app server then exits immediately after start. Use the official standalone release executable instead.
- **Skill support depends on the Codex CLI.** Skills come from the app server's `skills/list`. A CLI
  that does not implement it makes the `Skills` group report that the catalog is unavailable, and the
  slash menu keeps working with built-in commands only for the rest of the session. Skill icons
  declared by `interface.iconSmall` are not rendered; every row uses a fixed glyph. Skill-specific
  approval requests are declined rather than granted.
- The app-server contract is verified against 0.155.1. A newer latest release is downloaded by CI for local executable smoke coverage, while schema generation remains pinned to the manifest versions so contract comparisons stay reproducible. Confirm the local executable with `codex --version`.
- The extension is Windows-only and targets Visual Studio; there is no Visual Studio Code or cross-platform host.

## FAQ

**The Codex tool window does not appear under View.**
Check that Visual Studio is 17.14 or later, that the extension is listed and enabled in **Extensions > Manage Extensions**, and restart Visual Studio once after installing the VSIX.

**Chat never responds, or the worker exits right away.**
This is almost always the Codex CLI, not the extension. Run `codex --version` (0.155.1) and `codex login` in a terminal. If both succeed there but not in Visual Studio, a different `codex` is being launched; pin it with `CODEX_PATH` as described in [Limitations](#limitations).

**How do I pin one specific Codex CLI?**
Set the environment variable and restart Visual Studio so it inherits the change:

```powershell
[Environment]::SetEnvironmentVariable('CODEX_PATH', 'C:\path\to\codex.exe', 'User')
```

**Where are the logs?**
`%TEMP%\Kkamegawa.CodexForVisualStudio\diagnostics.log`. Extension and worker entries share the file and are tagged `[EXTENSION]` and `[WORKER]`. URLs and credential-shaped values are redacted before
they are written.

**Can I stop being asked for approval on every command?**
Use `/permissions` (alias `/approve`) in the chat input, or the approval-mode picker in the tool window. `ask`, `auto`, `full`, and `custom` are the built-in modes. `full` disables the Codex sandbox and normal approval prompts, so it requires an explicit confirmation. See [doc/slash-commands.md](doc/slash-commands.md) for the full command catalog, including `/model`, `/reasoning`, and `/review`.

**How do I run one of my Codex skills?**
Type `/` in the chat input. Built-in commands come first, then a `Skills` group listing the skills the
Codex CLI reports for the current workspace. Selecting a skill adds a removable chip above the
composer and leaves the composer editable, so you can add a prompt or send the skill on its own. One
skill is attached per turn, and selecting another replaces it. Disabled skills stay visible but
cannot be selected. Because a skill is delivered as structured `turn/start` input rather than
steering text, sending is disabled while a turn is still running; wait for it to finish or remove the
chip.

**Where are my settings stored?**
`%APPDATA%\Kkamegawa.CodexForVisualStudio\settings.json`. It holds the approval mode, reasoning effort, service tier, and the experimental-API switch. Deleting the file resets everything to the defaults; a corrupt file is ignored rather than blocking the tool window.

The skill catalog is cached separately under `%LOCALAPPDATA%\Kkamegawa.CodexForVisualStudio\skill-catalog\v1`, keyed per workspace, so the menu opens without waiting for the CLI. Cached rows are shown as stale and cannot be selected until a live refresh lands, and a turn always revalidates the skill against the live catalog. Deleting the folder only costs one refresh.

**Do I need a proxy or firewall exception?**
The extension itself only talks to a local child process over stdio and a local named pipe. All outbound network traffic is made by the Codex CLI, so proxy and firewall configuration belongs to the CLI and its own configuration file.

## Build and test

Prerequisites for development:

- Visual Studio 2022 17.14 or later with the Visual Studio extension development workload
- .NET 8 SDK
- The official Codex CLI 0.155.1 executable (used to generate the target protocol schema during the build)

Restore and build:

```powershell
dotnet restore CodexForVisualStudio.slnx
dotnet build CodexForVisualStudio.slnx -c Release --no-restore
```

`schemas/` contains generated output from the Apache-2.0-licensed Codex CLI and is intentionally excluded from this MIT-licensed repository. Caches are separated as `schemas/<version>/<stable|experimental>/` and are reused only when the CLI version, surface, generator arguments, metadata, and schema sentinel all match. When `schemas/0.155.1/stable/codex_app_server_protocol.schemas.json` is missing or stale, building `Codex.AppServer.Protocol` on Windows automatically runs the equivalent of:

```powershell
pwsh -NoProfile -File scripts/generate-schemas.ps1 -OutputDirectory schemas -Version 0.155.1 -Surface stable -CodexPath $env:CODEX_PATH
```

The generator prefers `CODEX_PATH` when it is set and otherwise resolves `codex` from `PATH`. It rejects a version other than the selected stable manifest entry. CI downloads the pinned 0.154.0 and 0.155.1 Windows x64 release assets, verifies their SHA-256 hashes from `app-server-contract.json`, generates both stable and experimental surfaces, and checks the normalized structural differences. The build and release jobs also download the latest stable Windows x64 executable into the runner's local temporary directory and pass that path as `CODEX_PATH`; this does not replace the pinned schema generator. Set `CODEX_PATH` to the official 0.155.1 executable for local schema builds:

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

`src/Codex.VisualStudio.Package` is a `net472` placeholder for future in-process features and does not produce a VSIX.

See [doc/implementation.md](doc/implementation.md) for the implemented boundaries and remaining validation work.

## Debug in Visual Studio

Debug builds do not deploy the extension by default, so a development build never modifies an installed Visual Studio instance silently.

1. Open `CodexForVisualStudio.slnx` in Visual Studio.
2. Set `Codex.VisualStudio.Extension` as the startup project.
3. Select the `Debug` configuration and press `F5`. Visual Studio builds, deploys, and starts an
   experimental instance.
4. In the experimental instance, open **View > Codex**.

The worker is a child process named `Codex.VisualStudio.Worker.exe`. To debug worker code, use **Debug > Attach to Process**, select `Codex.VisualStudio.Worker.exe`, and choose the managed .NET Core code type.

## Release

Releases are tag-driven:

1. Merge the release commit into `main`.
2. Push a `vX.Y.Z` tag (`vX.Y.Z.W` is also accepted for hotfix re-publishes).
3. The release workflow verifies that the tag is on `main`, writes the tag into the VSIX
   `Identity Version` (`vX.Y.Z` becomes `X.Y.Z.0`), builds and tests, and creates a **draft**
   GitHub release with the VSIX attached.
4. Review the draft and publish it manually from the GitHub Releases page when it is ready.
   Publishing is never an automatic side effect of pushing a tag.

Pull requests are validated by the CI workflow, which builds the solution, runs both test projects, and uploads the VSIX as a build artifact.

## Agent setup

Agent assets are managed with Microsoft APM. Install the CLI with `winget`:

```powershell
winget install microsoft.apm
apm marketplace add github/awesome-copilot
apm install
apm audit --ci --policy apm-policy.yml
```

APM deploys `.codex/agents/`, `.github/agents/`, and `.claude/agents/`. The dependency cache in `apm_modules/` is ignored by Git; re-run `apm install` to recreate it from `apm.yml` and `apm.lock.yaml`.

## Architecture

- UI layer: chat tool window, composer, approval prompts, and diff display.
- Presentation layer: chat view models, streaming buffers, and the unified slash command and skill suggestion list.
- Application layer: session lifecycle, slash command routing, skill catalog caching and identity validation, approval workflows, and workspace context collection.
- Security layer: approval policy, path access checks, secret redaction, and audit logging.
- Protocol layer: `codex app-server` process hosting, JSON-RPC dispatch, schema and version guards, and notification handling.

The transport is stdio. WebSocket or Unix socket transports remain future options and must stay local and authenticated.

Design and planning documents: [doc/design.md](doc/design.md), [doc/plan.md](doc/plan.md),
[doc/task.md](doc/task.md), [doc/implementation.md](doc/implementation.md),
[doc/adr.md](doc/adr.md).

## Security

Report vulnerabilities as described in [SECURITY.md](SECURITY.md). All `codex app-server` output is treated as untrusted input, rendered through a safe Markdown pipeline, and redacted before logging.

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE).

Japanese translation: [README_ja.md](README_ja.md).
