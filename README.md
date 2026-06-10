# Codex for Visual Studio

A Visual Studio 2022+ extension project for running the local Codex app server from inside Visual Studio.

The planned extension starts `codex app-server` as a local subprocess and communicates with it through newline-delimited JSON-RPC over stdio. The product goal is a Copilot-like Visual Studio experience: a chat tool window, streaming responses, approval-aware command and file-change handling, slash commands, and later editor integrations such as inline completion.

This repository now contains the Phase 0 through Phase 2.5 implementation foundation. The architecture and implementation checklist live in:

- [doc/plan.md](doc/plan.md)
- [doc/task.md](doc/task.md)

## Design Priorities

- Security-first approval boundaries for shell commands, file changes, network access, OAuth, MCP tools, and workspace-outside writes.
- Stable Visual Studio performance through async JSON-RPC handling, streaming buffers, bounded command output, virtualized diff rendering, and fail-fast process recovery.
- Reproducible agent context through Microsoft APM, with lockfile-based restores and policy/audit checks.
- Runtime support for Codex, GitHub Copilot, and Claude agent files while keeping generated assets small.

## Current Status

Completed:

- Repository bootstrap documents.
- APM manifest, policy, lockfile, and generated agent files.
- Agent targets for Codex, GitHub Copilot, and Claude.
- Hybrid Visual Studio extension with a .NET Framework 4.7.2 WPF tool window and a .NET 8 worker.
- Named-pipe StreamJsonRpc contracts between Visual Studio and the worker.
- JSONL app-server process host, bidirectional JSON-RPC, thread/turn lifecycle, approvals, redaction, path policy, and bounded streaming.
- Chat MVVM, history, send/steer/interrupt, approvals, degraded/restart state, and safe text rendering.
- Account status display and ChatGPT browser sign-in initiation without handling credentials in the extension.
- A runnable app-server PoC plus build-time protocol schema generation from the local Codex CLI.
- Loopback/token-gated future WebSocket policy, overload retry policy, resolved-path boundaries, and scoped approval grants.
- Fake app-server plus Core and UI unit tests.
- VSIX packaging that includes the worker and its dependencies.

Validated locally:

- Visual Studio Enterprise 2026 18.7 loads the .NET 8 OOP tool window in an Experimental Instance.
- The C# PoC completed `initialize`/`initialized`/`thread/start`/`turn/start` against a live local app-server.
- Protocol schemas were validated against local `codex-cli 0.139.0`.

This repository can be prepared on macOS, but the Visual Studio extension itself is expected to be built and validated on Windows with Visual Studio 2022.

## Prerequisites

For agent asset setup:

- Git
- `uv`
- Microsoft APM CLI installed with `uv tool install apm-cli`

For extension development:

- Windows
- Visual Studio 2022 17.x or later
- .NET 8 SDK
- Visual Studio extensibility workload
- Local Codex CLI with `codex app-server`

## Build And Test

Restore and build the solution:

```powershell
dotnet restore CodexForVisualStudio.slnx
dotnet build CodexForVisualStudio.slnx --no-restore
```

`schemas/` contains generated output from the Apache-2.0-licensed Codex CLI and is intentionally
excluded from this MIT-licensed repository.
When `schemas/codex_app_server_protocol.schemas.json` is missing, building
`Codex.AppServer.Protocol` on Windows automatically runs:

```powershell
codex app-server generate-json-schema --out schemas
```

The build prefers `CODEX_PATH` when it is set, then an executable `codex` from `PATH`, and then
the Codex desktop app's local executable cache. Set `CODEX_PATH` when automatic discovery cannot
find an executable Codex CLI:

```powershell
$env:CODEX_PATH = "C:\path\to\codex.exe"
dotnet build CodexForVisualStudio.slnx --no-restore
```

Run the unit tests:

```powershell
dotnet test tests/Codex.VisualStudio.Core.Tests/Codex.VisualStudio.Core.Tests.csproj
dotnet test tests/Codex.VisualStudio.Ui.Tests/Codex.VisualStudio.Ui.Tests.csproj
```

Regenerate schemas manually and run the live app-server PoC:

```powershell
dotnet run --project src/Codex.AppServer.Poc/Codex.AppServer.Poc.csproj -- --schema-out schemas --cwd .
```

When Codex is installed through WindowsApps, its execution alias may be blocked for child processes. Pass `--codex <path-to-standalone-codex.exe>` in that environment.

The generated VSIX is:

```text
src/Codex.VisualStudio.Package/bin/Debug/net472/Codex.VisualStudio.Package.vsix
```

See [doc/implementation.md](doc/implementation.md) for the implemented boundaries and remaining validation work.

## Debug In Visual Studio

Debug builds do not deploy the extension by default. This avoids silently modifying any installed Visual Studio instance. To explicitly enable Experimental Instance deployment on one development machine, first create an ignored `Directory.Build.user.props` file:

```xml
<Project>
  <PropertyGroup>
    <DeployToExperimentalInstance>true</DeployToExperimentalInstance>
  </PropertyGroup>
</Project>
```

Remove that file, or set the property to `false`, to stop deployment. Command-line `dotnet build` always creates the VSIX without deploying it.

1. Open `CodexForVisualStudio.slnx` in Visual Studio.
2. Set `Codex.VisualStudio.Package` as the startup project.
3. Create `Directory.Build.user.props` as shown above.
4. Select the `Debug` configuration and press `F5`.
5. A Visual Studio Experimental Instance starts with `/RootSuffix Exp`.
6. In the Experimental Instance, open `View > Codex`.

The .NET 8 worker is a child process named `Codex.VisualStudio.Worker.exe`. Visual Studio does not automatically attach the .NET Framework package debugger to this .NET 8 child process. To debug worker code, use `Debug > Attach to Process`, select `Codex.VisualStudio.Worker.exe`, and choose the managed .NET Core code type.

To reset a broken Experimental Instance, close it and run:

```powershell
& "C:\Program Files\Microsoft Visual Studio\18\Enterprise\VSSDK\VisualStudioIntegration\Tools\Bin\CreateExpInstance.exe" /Reset /VSInstance=18.0 /RootSuffix=Exp
```

## Agent Setup

Install APM with `uv`:

```sh
uv tool install apm-cli
```

Register the marketplace:

```sh
apm marketplace add github/awesome-copilot
```

Restore the project agent assets:

```sh
apm install
```

Verify the lockfile, deployed files, allowlist, and drift checks:

```sh
apm audit --ci --policy apm-policy.yml
```

APM deploys only the currently needed agent assets:

- `.codex/agents/`
- `.github/agents/`
- `.claude/agents/`

The dependency cache in `apm_modules/` is intentionally ignored by Git. Re-run `apm install` to recreate it from `apm.yml` and `apm.lock.yaml`.

## Planned Architecture

The extension is planned as layered components:

- UI layer: chat tool window, composer, approval dialogs, diff display, and optional inline completion.
- Presentation layer: chat view models, streaming buffers, and slash-command suggestions.
- Application layer: session lifecycle, slash command routing, approval workflows, and workspace context collection.
- Security layer: approval policy, path access checks, secret redaction, and audit logging.
- Protocol layer: `codex app-server` process hosting, JSON-RPC dispatch, schema/version guards, and notification handling.

The default transport is stdio. WebSocket or Unix socket transports are future options only and must stay local/authenticated.

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE).
