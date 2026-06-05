# Codex for Visual Studio

A Visual Studio 2022+ extension project for running the local Codex app server from inside Visual Studio.

The planned extension starts `codex app-server` as a local subprocess and communicates with it through newline-delimited JSON-RPC over stdio. The product goal is a Copilot-like Visual Studio experience: a chat tool window, streaming responses, approval-aware command and file-change handling, slash commands, and later editor integrations such as inline completion.

This repository is currently in planning and bootstrap stage. The architecture and implementation checklist live in:

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

Not started:

- Visual Studio extension project scaffold.
- `codex app-server` C# protocol proof of concept.
- Windows-only Visual Studio build and VSIX validation.

This repository can be prepared on macOS, but the Visual Studio extension itself is expected to be built and validated on Windows with Visual Studio 2022.

## Prerequisites

For agent asset setup:

- Git
- `uv`
- Microsoft APM CLI installed with `uv tool install apm-cli`

For extension development later:

- Windows
- Visual Studio 2022 17.x or later
- .NET 8 SDK
- Visual Studio extensibility workload
- Local Codex CLI with `codex app-server`

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
