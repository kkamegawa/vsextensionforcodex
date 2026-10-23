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

## Still open

- [ ] Capture and inspect the actual Experimental Instance in supported themes and narrow widths; verify keyboard and screen-reader behavior. The session had no Visual Studio target window available, so #221, #223, and the parent visual acceptance remain open.

After the review follow-up, Core tests in both configurations: 464 passed / 5 skipped / 0 failed. UI tests in both configurations: 489 passed / 1 skipped / 0 failed. Both solution builds completed with 0 warnings and 0 errors. The integrated release wrapper did not finish; component verification was performed directly. See the implementation record for details.
