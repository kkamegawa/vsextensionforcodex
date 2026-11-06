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

## Amendment (2026-10-25, minimum supported version and CLI 0.164.0)

- Status: Accepted
- Task: [Issue #226](https://github.com/kkamegawa/vsextensionforcodex/issues/226), Codex App Server 0.164.0 support ([design](../app-server-0-164-design.md), [ADR-019](ADR-019-app-server-0-164.md)).
- The manifest moves to schema version 3 and records a required `minimumVersion`. Validation requires `minimumVersion` to be the lowest pinned version and one of `regressionVersions`, and no pinned version may be below it. The Worker's compiled minimum and target constants are tested against the manifest.
- The target becomes 0.164.0 and the regression set becomes 0.163.0, 0.162.1, and 0.161.0. The minimum supported version for local and remote servers is 0.161.0. 0.159.1 is no longer supported and is removed from the manifest, CI, the Fake App Server regression surfaces, and the setup documents.
- The version comes only from the first product token of the `initialize` `userAgent` and is compared with Semantic Versioning precedence: build metadata is ignored and a prerelease sorts below its release. An unknown, invalid, or `0.0.0` version, and anything below the minimum, are rejected after the `initialize` response and before `initialized`. Rejection is non-retryable, keeps the settings available, and never updates a server.
- Features above the baseline are enabled by a version tier computed per connection (0.163 and 0.164), in addition to capability detection.
- Reason: supporting 0.159.1 required fallbacks (`serverInfo.version`, OAuth completion without `loginId`) that no longer have a supported user, and an unbounded version range made the regression matrix and acceptance checks grow without limit. A recorded minimum version keeps the supported range, tests, and rejection behavior consistent. This supersedes the single-regression-baseline wording of the earlier amendments; the other decisions stay unchanged.

## Amendment (2026-10-31, CLI 0.166.0 and the complete package)

- Status: Accepted
- Task: [Issue #240](https://github.com/kkamegawa/vsextensionforcodex/issues/240), Codex App Server 0.166.0 support ([design](../app-server-0-166-design.md), [ADR-020](ADR-020-app-server-0-166.md)).
- The manifest moves to schema version 4 and records the `codex-package-x86_64-pc-windows-msvc.tar.gz` asset name and SHA-256 alongside the existing executable information. The install helper and real verification use the complete package.
- Helper executables are not listed in the manifest, because `codex-package.json` in the package is authoritative. `codex-exec-server` is not required at runtime, because the npm distribution does not contain it.
- The target becomes 0.166.0 and the regression set becomes 0.165.0, 0.164.0, 0.163.0, 0.162.1, and 0.161.0. The minimum supported version stays 0.161.0.
- Feature tiers 0.165 and 0.166 are added, and each tier compares with its own introduction version, not with the target.
- Reason: helper executables changed repeatedly during 0.166 development, so a fixed list would break on the next release, and comparing tiers with the target would lower existing connections whenever the target moves. The other decisions stay unchanged.
