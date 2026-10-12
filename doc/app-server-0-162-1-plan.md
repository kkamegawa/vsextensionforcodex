# Codex App Server 0.162.1 Support and MXC Backend Selection

[日本語](app-server-0-162-1-plan_ja.md)

## Summary

The extension targets the Codex CLI 0.162.1 App Server contract and keeps 0.159.1 as the regression fixture, because a remote App Server can still run it. Users can explicitly select the MXC Windows sandbox backend for the local App Server. Approved on 2026-10-12.

## Tracking

| Type | Issue |
|---|---|
| Parent | [Issue #198](https://github.com/kkamegawa/vsextensionforcodex/issues/198) |
| Contract baseline | [Issue #199](https://github.com/kkamegawa/vsextensionforcodex/issues/199) |
| MXC backend preference | [Issue #200](https://github.com/kkamegawa/vsextensionforcodex/issues/200) |
| Goal origin and MCP OAuth loginId | [Issue #201](https://github.com/kkamegawa/vsextensionforcodex/issues/201) |
| Receive-side payload changes | [Issue #202](https://github.com/kkamegawa/vsextensionforcodex/issues/202) |
| Integrated validation and documentation | [Issue #203](https://github.com/kkamegawa/vsextensionforcodex/issues/203) |

Related completed work: [Issue #149](https://github.com/kkamegawa/vsextensionforcodex/issues/149) and [Issue #156](https://github.com/kkamegawa/vsextensionforcodex/issues/156).

## Contract

- `app-server-contract.json` pins `targetVersion` 0.162.1 and `regressionVersion` 0.159.1, with the official Windows x64 asset SHA-256 for both releases.
- The recorded schema differences from 0.159.1 to 0.162.1 are 3 added / 0 removed / 31 changed stable files and 7 added / 0 removed / 35 changed experimental files. No used RPC or server request was removed.
- `config/read` and `configRequirements/read` are used client requests. Both exist in 0.159.1 and 0.162.1.
- New 0.162.1 surfaces that the extension does not use (`thread/attachmentOwner/list`, `thread/prediction/*`, `account/bedrock/checkGovCloudRequirements`, raw-response `additional_tools`, the removal of `namespaceTools`) are recorded as differences only.

## Design

### Goal origin

`thread/goal/set` and `thread/goal/clear` send `origin: "user"` for every mutation the extension sends: set, edit, pause, resume, clear, and Goal Stop. 0.162.1 treats a missing origin as no user authorization; 0.159.1 ignores the field. The extension never sends `automatic`. `thread/goal/updated` carries no origin, so it is not read.

### MCP OAuth completion

The Worker stores the `loginId` returned by `mcpServer/oauth/login`. `mcpServer/oauthLogin/completed` completes an attempt only when its `loginId` matches exactly. A completion that arrives before the login response is kept until the response's `loginId` is known, then applied or dropped. A completion with a different `loginId` is stale and changes nothing. An invalid `loginId` (not a bounded string without control characters) is ignored. When the server omits `loginId` (0.159.1), completion falls back to server-name matching.

### Message phase

`partial_answer`, `commentary`, and `final_answer` are separate `agentMessage` items with their own text. The extension shows every agent message item once, in order, live and in restored history, and ends a turn only on `turn/completed`. It does not derive completion or approval from `phase`, so no phase field is added to the Worker contract.

### Skill paths

0.162.1 types skill `path` as `LegacyAppPathString`, which need not be an absolute host path. Skill paths are opaque catalog identifiers: they are echoed back unchanged in skill turn input and compared ordinally, and the extension never opens them as files. Empty, overlong (over 1024 characters), and control-character values are still dropped. Skill load-error paths follow the same rule.

### Other receive-only changes

- Unknown `CodexErrorInfo` strings and objects, including `reviewTarget`, are read without failing and never become a retry or approval reason.
- `Turn.rootTurnId`, `turn/start.parentTurnId` and `rootTurnId`, and sub-agent `model`/`reasoningEffort` are read-compatible. The extension has no delegation path, so it never sends lineage.
- `supportsIndependentSpeedModes` is a `configRequirements/read` field about managed speed requirements. The extension does not apply managed speed requirements; `/fast` eligibility comes from the model's service tiers and the server enforces requirements.

### Windows sandbox backend (MXC)

| Setting | Launch override | Codex behavior |
|---|---|---|
| Follow Codex configuration (default) | none | Uses `windows.sandbox` and features from the Codex configuration and managed policy. |
| Prefer MXC | `-c features.prefer_mxc=true` | Uses MXC only when managed policy allows MXC and does not set `allowLocalBinding=false`; otherwise keeps the configured backend. |
| Require MXC | `-c windows.sandbox=mxc` | Always uses MXC. Codex refuses to load the configuration, and the App Server does not start, when managed policy does not allow MXC. |

- The setting is stored as a stable ID in the extension settings and applies the next time the local App Server starts. Remote profiles are never reconfigured; the Worker passes no override for them.
- Override values are a fixed allow-list. Bare values parse as TOML strings, so the direct and `cmd.exe` launch paths need no quoting.
- **Check sandbox status** reads `windowsSandbox/readiness`, `config/read` (effective `windows.sandbox` and `features.prefer_mxc`), and `configRequirements/read` (`allowedWindowsSandboxImplementations` and `network.allowLocalBinding`). A failed or unsupported read is reported as unknown, never guessed. The diagnosis says when the selection conflicts with policy and states that readiness does not prove which backend runs each command.
- When a Require MXC start fails before initialization, the connection error names the setting and tells the user to choose Follow Codex configuration and connect again. The sandbox panel stays visible after a failed local start for that reason.
- The extension never relaxes managed policy and never re-runs a failed command on another backend. `windowsSandbox/setupStart` remains the legacy elevated/unelevated setup; MXC needs no setup.
- The panel shows, in order: the backend picker and its description, a pending notice when the selection differs from the backend the current local App Server was started with, the status diagnosis with **Check sandbox status**, and the legacy setup controls.

The Worker contract version is 24.

## Implementation phases

1. Contract baseline (#199): verified CLI downloads, schema caches, manifest, CI and release workflows, build-time schema generation.
2. MXC (#200): contract and settings, launch overrides, diagnostics, sandbox panel, tests.
3. Goal origin and OAuth (#201): origin on all user paths, `loginId` matching, Fake App Server shapes.
4. Receive side (#202): opaque skill paths and receive-only coverage.
5. Validation and documentation (#203): builds, tests, smoke against both CLI versions, release verification, renders, real-machine acceptance, and documentation.

## Verification

- Schema comparison, contract surface, and schema cache scripts pass; smoke passes against 0.162.1 and 0.159.1; `verify-release.ps1` passes for Debug and Release.
- Core and UI tests cover every origin path, every `loginId` rule, partial answers live and in history, unknown `CodexErrorInfo`, relative and legacy skill paths, MXC launch arguments for direct and `cmd.exe` launches, readiness diagnostics with known and unknown policy, and the sandbox panel view model.
- Off-screen renders of the production template bound to the real view model cover Light, Dark, and High Contrast at 380 and 700 pixels. They check layout and information structure only.
- Real Windows MXC acceptance and Visual Studio Experimental Instance acceptance (themes, narrow width, keyboard, screen reader) are recorded separately. Schema checks alone do not complete this work.
