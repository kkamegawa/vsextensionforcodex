# Codex App Server 0.163.0 Implementation Record

Tracking: #217–#223. Approved design and plan: [design](app-server-0-163-design.md), [plan](app-server-0-163-plan.md). Japanese record: [日本語](implementation-app-server-0-163_ja.md).

## Delivered

- Upgraded the contract manifest to schema version 2 with target 0.163.0, independent regression baselines 0.162.1 and 0.159.1, per-version release hashes, method surfaces, and known differences. Versioned stable and experimental schema sets remain separate.
- Updated the Fake App Server default to 0.163.0, added the 0.162.1 regression scenario, and advanced the Worker contract to v25.
- Added bounded JSON-RPC error.data retention for stdio and WebSocket responses. Only -32600 with reason readStateConflict enters conflict handling; the normal read-state validator is used, with thread/read fallback for missing or invalid embedded state.
- Added experimental-only read-state snapshots, five distinct states, generation-scoped notifications, explicit read/unread actions, opaque revision handling, per-thread update serialization, race refresh through thread/read, and stale-owner/selection guards. Merely opening or viewing a conversation never marks it read.
- Hardened full history refresh and pagination: owner and request validation, duplicate suppression, preserve-on-failure behavior, cursor retention, and cancellation of missing-thread work only after a successful complete same-owner list.
- Corrected MXC diagnostics so the legacy Windows sandbox allow-list does not decide MXC availability. Added the local opt-out, pre-initialized version gating, optional windows.allow_mxc diagnostics, and sanitized startup explanations.
- Updated setup and repository documentation in English and Japanese. The MCP elicitation check uses the existing serverRequest/resolved path. Live App Server notification delivery was not exercised in this environment; delivery of the new core-side discard event is not claimed. The existing five-minute pending-request timeout and disconnect cleanup remain the fallback.

## Verification

| Check | Result |
| --- | --- |
| Used-method contract surfaces | Passed for 0.159.1, 0.162.1, and 0.163.0 on stable and experimental surfaces. |
| Schema comparisons | Passed for both baselines against 0.163.0 on both surfaces (four comparisons). |
| Core tests | Debug and Release: 463 passed, 5 skipped, 0 failed in each configuration. |
| UI tests | Debug and Release: 483 passed, 1 skipped, 0 failed in each configuration. |
| Full solution builds | Debug and Release: 0 warnings, 0 errors. |
| Release VSIX inspection | Passed; payload hashes and embedded XAML matched, Worker contract v25 confirmed. |
| Whitespace validation | git diff --check passed. |

The five Core skips and one UI skip are reported as skips, not passes. The final integrated verify-release.ps1 -Configuration Both wrapper was started but did not produce observable progress in this session and was stopped; its constituent schema, build, test, and VSIX checks were run directly as listed above.

## Remaining acceptance

Experimental Instance visual acceptance is still open. No supported-theme, narrow-width, keyboard, or screen-reader screenshots were captured, so no host-level UI pass is claimed. The related visual acceptance criteria in #221 and #223, and the parent completion criterion, remain unchecked.
