# Codex App Server 0.163.0 Task Record

Tracking: #217–#223. Detailed implementation evidence: [implementation record](implementation-app-server-0-163.md). Japanese record: [日本語](task-app-server-0-163_ja.md).

## Completed

- [x] Contract schema v2, release pins, per-version stable/experimental method tables, versioned schema sets, and both regression comparisons.
- [x] Bounded JSON-RPC error data for stdio and WebSocket; strict conflict discrimination and validated fallback reads.
- [x] Worker contract v25, experimental read-state model, explicit operations, serialized same-thread updates, notification race refresh, and stale owner/selection handling.
- [x] Safe history pagination and replacement semantics with failure preservation and complete-list cancellation.
- [x] MXC diagnosis correction, local opt-out, pre-ready version gate, and optional effective-config diagnostic.
- [x] Fake integration, Core and UI suites, Debug and Release solution builds, and Release VSIX inspection.
- [x] English/Japanese design, plan, implementation, task records, and updated user documentation under doc/.

## Review follow-up (concurrency and UI locking)

- [x] Read-state controls are republished when an update leaves flight, so the buttons no longer stay hidden after a successful, conflicting, or refreshed update.
- [x] The thread-list request gate is owner-scoped: a slow request of a retired owner no longer suppresses the new owner's first page, and a superseded recovery synchronization no longer marks the new owner's list stale. Failure messages are applied only for the current owner.
- [x] A notification carrying the displayed receipt (including the client's own committed update) is not treated as a concurrent change.
- [x] Selecting a conversation keeps its last known unread marker; a failed history read no longer erases it.
- [x] Row and read-state bookkeeping is guarded by one gate because OnUiAsync runs inline in the OOP host; in-flight update tokens are unique, so a retired owner's request cannot release a new one.
- [x] ThreadRows mirrors Threads position by position (incremental insert/remove, no extra row for an off-page selection); Threads is no longer replicated to Remote UI.
- [x] A timeout or disconnect of thread/readState/update is reported as OutcomeUnknown; the client rereads once and never resends.
- [x] A rejected pre-0.163 MXC opt-out retires the connection context and clears the detected version and metadata.

## PR #225 review follow-up

- [x] CI stops at the first failing schema comparison or method-surface check for any version and surface, instead of letting a later invocation overwrite the exit code.
- [x] The MXC opt-out version check strips semantic-version build metadata before parsing, so `0.163.0+build.5` is accepted while `0.163.0-alpha+build.5` is still rejected.
- [x] The Fake App Server guards read-state creation, revision allocation, conflict comparison, and mutation with one gate shared by the stdin handler and the turn-completion task, and returns detached snapshots before writing output.
- [x] A new or joined conversation on an experimental connection reads its read state once with thread/read, as the design requires, with the same owner and sequence checks as other reads. Forked conversations already read it through selection.
- [x] Debug and Release solution builds: 0 warnings, 0 errors. Per configuration: Core 468 passed / 5 skipped / 0 failed; UI 493 passed / 1 skipped / 0 failed.

## Omitted by agreement

- Capturing the Experimental Instance in supported themes and narrow widths and verifying keyboard and screen-reader behavior were omitted by agreement with the owner. #217, #221, and #223 are closed by PR #225 without these checks. The Experimental Instance was used for connection, history, read-state, mode, Restart, and automatic-recovery checks only.

After the review follow-up, Core tests in both configurations: 464 passed / 5 skipped / 0 failed. UI tests in both configurations: 489 passed / 1 skipped / 0 failed. Both solution builds completed with 0 warnings and 0 errors. The integrated release wrapper did not finish; component verification was performed directly. See the implementation record for details.
