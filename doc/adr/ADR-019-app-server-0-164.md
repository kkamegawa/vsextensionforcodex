# ADR-019: Codex App Server 0.164.0 support, version tiers, and shared presets

[日本語](ADR-019-app-server-0-164_ja.md)

- Date: 2026-10-25
- Status: Accepted
- Tracking: [Issue #226](https://github.com/kkamegawa/vsextensionforcodex/issues/226) and child Issues [#227](https://github.com/kkamegawa/vsextensionforcodex/issues/227), [#228](https://github.com/kkamegawa/vsextensionforcodex/issues/228), [#229](https://github.com/kkamegawa/vsextensionforcodex/issues/229), [#230](https://github.com/kkamegawa/vsextensionforcodex/issues/230), [#231](https://github.com/kkamegawa/vsextensionforcodex/issues/231)

## Context

Codex CLI 0.164.0 makes several changes that affect the extension:

- Inline `/model` and `/permissions` arguments.
- Ten preset slots under `tui.presets`.
- Generated images stored as attachment-store references (`fileId`).
- MCP tool-list metadata.
- A `feedback/save` method that writes a diagnostic ZIP on the server host.
- Two server-side fixes: resume now restores the saved thread settings instead of global defaults, and a failed compaction no longer leaves partial summaries.

Remote servers may still run 0.161.0 to 0.163.0. Those versions return global defaults on resume, and they lack `feedback/save` and `fileId`.

## Decision

- **Supported versions:** the minimum supported version is 0.161.0, the target is 0.164.0, and the regression set is 0.163.0, 0.162.1, and 0.161.0. Older or undeterminable servers are rejected, as recorded in the ADR-011 amendment of the same date.
- **Version tiers:** each connection gets a tier, computed by the Worker and sent to the Extension:
  - Baseline: 0.161.0 or later.
  - 0.163: 0.163.0 or later.
  - 0.164: 0.164.0 or later.

  These features are enabled only in the 0.164 tier: presets, resumed thread settings, the new generated-image projection, the separated MCP status, and diagnostic ZIP saving. Older connections keep the existing behavior. Tolerance of missing fields and unknown enum values applies in every tier.
- **`/model` arguments:** parsed with the same rules as the CLI 0.164.0 slash-command parser, and validated before anything is applied. Omitting the speed keeps the current tier when the model supports it. This deliberately differs from the CLI, which sends no tier. `standard` is sent as the `default` request value.
- **Presets:** shared with the connected server's `tui.presets`.
  - Slots are read from `config/read` layers with the workspace `cwd`.
  - Each write is a single `config/batchWrite` edit to `tui.presets.<digit>`, guarded by `expectedVersion`.
  - A slot defined by a non-user layer cannot be edited. A slot that a non-user layer also defines cannot be deleted.
  - Recalling a preset fills the composer and never sends it. This deliberately differs from the CLI, which queues and runs it.
- **Resume:** in the 0.164 tier, a null `serviceTier` on resume means normal speed and clears the previous tier.
- **`fileId`:** treated as an unresolvable reference, because no public RPC resolves the attachment store.
- **Diagnostic ZIP:** an explicit action. Logs are off by default, an existing path is rejected, and a disconnect or timeout is reported as unconfirmed, with no automatic resend.
- **Compaction:** in every tier, displayed history comes from the server, and a compaction is complete only on `thread/compacted` or a completed `contextCompaction` item. Older tiers document the remaining upstream limitations instead of working around them on the client.
- **Worker contract:** becomes v26.

## Consequences

- Users on 0.159.1 or a source build (`0.0.0`) must update before they can connect.
- Users on 0.161.0 to 0.163.0 can connect, but do not get the 0.164 features. The UI explains which version a hidden feature requires.
- A preset edited in a running CLI is detected only at the next read, because the CLI writes without a version check.
- The approved design and plan are in [app-server-0-164-design.md](../app-server-0-164-design.md) and [app-server-0-164-plan.md](../app-server-0-164-plan.md).
- [ADR-020](ADR-020-app-server-0-166.md) raises the target to 0.166.0 and adds tiers 0.165 and 0.166. Each tier now compares with its own introduction version, so the 0.164 tier stays defined by 0.164.0 and not by the target.
