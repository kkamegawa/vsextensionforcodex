# ADR-011: App Server contract and request routing

- Date: 2026-09-13
- Status: Accepted
- Tracking: Codex App Server update parent Issue

## Decision

- Validate the target client contract against CLI 0.159.1. Generate standard and experimental schemas for both 0.155.1 and 0.159.1, keep 0.155.1 as the regression source, and contract-test their structural differences. Cache the target schemas separately with version and generation-option metadata.
- Determine feature support from declared capabilities, known read-only contracts, and `-32601`; never probe a mutating operation.
- Route server requests by exact method name and request shape. Unknown requests receive a protocol-appropriate rejection and cannot enter a generic approval path.
- Track connection generation, thread, turn, and pending server request identity so late responses cannot revive completed state. Disconnect completes or cancels pending requests according to their ownership.

## Consequences

Contract drift from the previous stable baseline becomes visible in tests and stale connection events are safely ignored. Generated schema output remains uncommitted.

## Amendment (2026-09-23, PR #157 review)

- Task: Issue #150 and the remote options carried by PR #157 (Issues #151/#152).
- Worker contract v16 is the version that adds the remote endpoint, token-file path, and local/server roots to `WorkerOptions`. The Phase 5 interaction contract (questions, permission scopes, MCP input, user verification, stored attachments) therefore raises the contract from v16, not from v15.
- Reason: the remote options already cross the Extension-to-Worker boundary in this change. Reusing v16 for different Phase 5 shapes would let a v16 Extension and Worker disagree about the wire contract. This does not contradict the approved decisions: token contents still never cross the boundary, and only file paths do.
- `turn/completed` is keyed by `params.turn.id` (wire shape `{ threadId, turn }`), and `serverRequest/resolved` accepts the `string | int64` RequestId. Contract tests use the wire shapes.

## Amendment (2026-09-30, contract update to CLI 0.159.1)

- Task: Issue #150, released with PR #157.
- The target contract moves from 0.155.1 to 0.159.1, and the regression source moves from 0.154.0 to 0.155.1. Release assets and SHA-256 hashes stay pinned in `app-server-contract.json`; scripts read the pinned versions from that manifest instead of hard-coding them.
- The observed 0.155.1-to-0.159.1 differences are additive for the methods this extension uses: new enum values (`PlanType.promax`, `CodexErrorInfo.flexUnavailable` and `tooManyDenials`), new optional fields (`Model.availableAccessPrograms`, `disabledPluginIds`, `McpServerStatus.httpOrigin`), gateway OAuth methods, and the removal of the unused `thread/rollback`. No Worker parsing change is required; contract tests cover the new values.
- Reason: Codex delivers the model catalog per client version, so newer models (for example `gpt-6.1-sol`) appear only for a newer CLI. Keeping the older target would verify a contract that users no longer run. This keeps the existing decision (one pinned stable target plus one regression baseline) and changes only the versions.
