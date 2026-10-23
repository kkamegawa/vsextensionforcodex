# Codex App Server 0.163.0 Implementation Plan

Tracking: Issue #217; children #218–#223. Approved on 2026-10-23.

## Outcome

Deliver the approved design in app-server-0-163-design.md, maintain 0.162.1 and 0.159.1 as independent regression baselines, and keep the experimental API opt-in.

## Work sequence

| Phase | Work | Tracking |
| --- | --- | --- |
| 1. Contract and Protocol | Upgrade the manifest to schema v2; pin all three releases; generate versioned stable and experimental schemas; implement baseline-by-baseline comparison and validation; preserve bounded JSON-RPC error.data for stdio and WebSocket. | #218, #219 |
| 2. Worker and history loading | Advance Worker contract to v25; implement typed read-state parsing, updates, conflict recovery, notifications, local MXC startup gating, and owner/request-generation-safe list loading. | #220, #221 |
| 3. History UI | Add observable thread rows, unread markers, explicit read/unread actions, stable-server explanation, selection preservation, stale-result protection, and theme/accessibility behavior. | #221 |
| 4. MXC settings and diagnosis | Correct legacy-backend diagnosis, add local MXC opt-out and windows.allow_mxc diagnostics, and avoid unsupported policy or per-command backend claims. | #222 |
| 5. Integrated verification and records | Exercise both regression versions and the target, Fake and real App Server paths, race/recovery cases, all test suites and builds, package/schema integrity, and Experimental Instance UI acceptance. Update implementation and task records with evidence. | #223 |

## Acceptance checks

- Contract scripts validate 0.163.0, 0.162.1, and 0.159.1 independently for stable and experimental surfaces. Each regression diff is reported with its version. Schema cache and evidence format versions change only when their formats change.
- Protocol retains valid bounded error.data for both transports. Oversized or malformed conflict data and unrelated -32600 errors never become read-state conflicts.
- Read-state handling distinguishes read, unread-at-thread-start, unread-at-turn, not-applicable, unavailable, and invalid values. Experimental-disabled and old-server connections hide read-state controls.
- State changes, selection transitions including A→B→A, owner changes, notifications, conflicts, disconnects, and stale requests cannot apply state to the wrong row or owner. A visible conversation remains unread after a new result until explicit user action.
- List failures preserve current rows, selection, and cursor. Owner-mismatched results are discarded; partial lists never cancel missing conversations.
- MXC tests cover a missing legacy mxc entry, explicit MXC selection conflicting with opt-out or managed policy, and unknown/old CLI versions. The opt-out is local only.
- MCP tests assert the existing serverRequest/resolved path. They do not require the core-side question-discard event to reach the client.
- Steer/turn completion and cold-resume permission restoration are covered.
- Core, UI, and Fake test suites and Debug/Release builds complete with zero warnings.
- Experimental Instance screenshots validate each theme, narrow width, keyboard interaction, and screen-reader output.
- English and Japanese design, plan, implementation, and task records are UTF-8 with BOM and CRLF. Issue checklists reflect only verified scenarios.

## Verification evidence

Record exact commands, configurations, totals, skips, generated release versions, asset hashes, and screenshot paths in doc/task.md and doc/implementation.md after each phase. Do not mark a phase complete when a required check is blocked or skipped.