# Codex App Server 0.164.0 Implementation Plan

[日本語](app-server-0-164-plan_ja.md)

Tracking: [Issue #226](https://github.com/kkamegawa/vsextensionforcodex/issues/226). Approved on 2026-10-25.

## Outcome

This plan delivers [app-server-0-164-design.md](app-server-0-164-design.md) under [ADR-019](adr/ADR-019-app-server-0-164.md) and the ADR-011 amendment of 2026-10-25:

- Target 0.164.0, with 0.161.0 as the minimum supported version and 0.163.0, 0.162.1, and 0.161.0 as regression baselines.
- Reject 0.159.1 and undeterminable versions.
- Gate the new features by version tier.
- Keep the experimental API opt-in.

## Work sequence

| Phase | Work | Tracking |
| --- | --- | --- |
| 1. Contract and versions | Raise the manifest to schema v3 with `minimumVersion`. Pin 0.164.0 and 0.161.0 and remove 0.159.1. Generate and compare the stable and experimental schemas for the four versions. Make the scripts, the install helper, CI, the Fake App Server, and the setup documents read from the manifest. Add the Worker version constants and the manifest consistency test. | [#227](https://github.com/kkamegawa/vsextensionforcodex/issues/227) |
| 2. Version gate and Worker | Implement Semantic Versioning determination from `userAgent`, non-retryable rejection, and the feature tier. Remove the 0.159.1-only fallbacks. Raise the Worker contract to v26. Add the Worker paths for presets (`config/read` layers, `config/batchWrite` with an allow-listed key and `expectedVersion`), `feedback/save`, MCP runtime state and `toolsError`, generated-image references, and tier-gated resume settings. | [#228](https://github.com/kkamegawa/vsextensionforcodex/issues/228) |
| 3. Input and presets UI | Add the `/model` parser with CLI parity and atomic resolution, and the speed fallback. Hold queued sends while a `/permissions` confirmation is pending. Add the `/presets` command and the composer button, the presets panel with edit and delete rules, conflict handling, recall into the composer with replace confirmation, and the tier explanation. | [#229](https://github.com/kkamegawa/vsextensionforcodex/issues/229) |
| 4. State, images, MCP, diagnostics, and compaction UI | Apply resume settings with a null tier meaning normal speed. Keep or reset the selection after an account change, with a notice. Add the image reference and failure display, the three-part MCP status, the diagnostic ZIP dialog for local and remote connections, and the compaction failure and older-tier resume messages. | [#230](https://github.com/kkamegawa/vsextensionforcodex/issues/230) |
| 5. Integrated verification and records | Exercise the four versions with Fake and real App Servers, the rejection cases, every acceptance check, builds, and the VSIX inspection, and take Experimental Instance screenshots. Update the implementation and task records with evidence. | [#231](https://github.com/kkamegawa/vsextensionforcodex/issues/231) |

Representative files:

- `app-server-contract.json` and the contract and schema scripts, `.github/workflows/ci.yml`, and `doc/codex-cli-setup*.md`
- `src/Codex.VisualStudio.Contracts/WorkerContracts.cs` and `DailyUseContracts.cs`
- `src/Codex.VisualStudio.Worker/CodexSessionService*.cs` and `WorkerRpcService.cs`
- `src/Codex.VisualStudio.Extension/ChatViewModel.cs`, `SlashCommands.cs`, `WorkerBridge.cs`, and the Remote UI XAML
- `src/Codex.AppServer.Fake/Program.cs`

Existing code to reuse:

- Version detection: `ReadCodexVersion` and `IsValidCodexVersion`. The version comparison is generalized from `IsCodexVersionAtLeast163`.
- The `/permissions` resolution and the Full Access confirmation.
- The image paths `AddServerPathPart` and `AddImageGenerationFallback`.
- The existing `feedback/upload` path and classification validation.
- The MCP `McpAuthStatuses` handling and `loginId` matching.
- The owner-snapshot checks: `CaptureOwnerSnapshot` and `IsCurrentOwner`.

## Acceptance checks

- **Contract:**
  - The scripts validate 0.164.0, 0.163.0, 0.162.1, and 0.161.0 on both surfaces, and report each regression difference with its version.
  - `minimumVersion` validation rejects missing, mismatched, or lower pinned versions.
  - The Worker constants match the manifest.
  - No 0.159.1 entry remains in the manifest, CI, the Fake App Server, or the setup documents.
- **Version gate:**
  - 0.159.1, 0.160.x, a 0.161.0 prerelease, `0.0.0`, and a missing or invalid `userAgent` are rejected locally and remotely, with no recovery loop and with the settings still available.
  - 0.161.0 through 0.164.0 and newer versions connect.
  - `0.164.0-alpha` gets the 0.163 tier.
- **Input:**
  - Every `/model` parser parity case passes.
  - An invalid command changes nothing.
  - The speed fallback produces a notice.
  - Queued sends wait for a pending `/permissions` confirmation.
- **Presets:**
  - Validation boundaries are enforced, including 8,000 and 8,001 bytes.
  - The edit and delete rules hold with project, system, and session layers, and with a missing user layer.
  - A conflict keeps the editor contents and is not retried.
  - Recall never sends.
  - Slots written by the CLI round-trip.
  - Remote presets never touch local configuration.
  - Connections older than 0.164.0 show the version explanation.
- **State, images, and MCP:**
  - Resume applies the settings only in the 0.164 tier, and a null tier means normal speed.
  - Stale owner responses are discarded.
  - Every image format and failure case is projected correctly in live notifications and in replay.
  - `toolListMeta` never reaches the contract, UI, or logs.
  - MCP status combinations display as designed.
- **Diagnostics:**
  - Local and remote saves succeed.
  - An existing path is rejected.
  - Logs are off by default.
  - A disconnect or timeout reports an unconfirmed outcome with no resend.
  - The action is unavailable below 0.164.0.
- **Compaction:**
  - Fake App Server failure and older-tier resume-error scenarios pass.
  - On a real 0.164.0 CLI, compaction followed by a resume after restart succeeds.
- **Real environment:** MXC startup on Windows with 0.164.0, and a 0.161.0 regression connection.
- **Quality:**
  - Core, UI, and Fake test suites and the Debug and Release builds complete with zero warnings.
  - The VSIX inspection passes.
  - Experimental Instance screenshots validate each theme, a narrow width, Japanese text, keyboard operation, and screen-reader output.
- **Records:** the English and Japanese design, plan, ADR, implementation, and task records are UTF-8 with BOM and CRLF. Issue checklists reflect only verified scenarios.

## Verification evidence

After each phase, record the exact commands, configurations, totals, skips, generated release versions, asset hashes, and screenshot paths in `doc/task.md` and `doc/implementation.md`. A phase is not complete while a required check is blocked or skipped.
