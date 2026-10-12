# Goal Stop Plan — Issue #174

Date: 2026-10-11  
Status: Approved implementation plan (2026-10-11).  
Tracking: [Issue #174](https://github.com/kkamegawa/vsextensionforcodex/issues/174)  
Design: [Goal Stop design](goal-stop-design.md) and [Japanese translation](goal-stop-design_ja.md)

## Summary

Keep the composer Stop action available whenever the selected thread has an Active Goal, including between turns and during automatic continuation. Stop attempts to pause the Goal first, then interrupts the latest running turn for that same thread even if pause failed or has an unknown outcome. The operation is scoped to the current owner and connection generation, and it never replays after reconnect.

## Implementation phases

| Phase | Work | Deliverables |
|---|---|---|
| P0 — Contract and design | Reconcile the accepted behavior with the design/plan records; advance Worker contract v21 to the next unused version | English canonical design/plan and corresponding Japanese translations; typed Worker request/result |
| P1 — Worker Stop lifecycle | Add Goal read, status-only pause, latest same-thread turn interrupt, owner/generation/thread guards, queue cancellation barrier, and independent stage outcomes | Worker implementation and Fake App Server ordering/race/failure scenarios |
| P2 — Goal state and composer | Unify Goal response/notification/read reconciliation; bind Active/stopping state into one composer Stop action and block keyboard send during stopping | Extension ViewModel and Remote UI behavior |
| P3 — Integration review | Review Worker races and WPF behavior; fix findings; run the defined verification | Reviewed implementation, automated results, package checks, and Experimental Instance evidence |

## Acceptance criteria

- Stop appears for Active Goals during a turn, between turns, automatic continuation, approval waits, and input waits. Paused/terminal/limited/deleted Goals restore the normal composer state once the targeted turn ends; Resume restores Stop while Active.
- Stop attempts a status-only pause before interrupting, preserving objective, budget, and usage. After the pause attempt it best-effort interrupts the latest running turn for the same thread even if pause failed or is uncertain; no extra confirmation is required. If no turn runs, it reports the pause outcome without interrupting.
- Goal read responses cannot overwrite newer notifications or state from another owner, thread, or connection generation. Reconnect and late results cause zero mutation replay.
- Stop cancels undispatched work for its thread and blocks already-dequeued Resume/commands before send, while preserving unsent composer text and attachments. Double-clicks cause one operation.
- Pause and interrupt failures/outcomes remain distinct. A successful pause is retained if interrupt fails. If pause fails but interrupt succeeds, report a partial result without claiming the Goal is Paused. Acknowledgement is not completion; result-unknown states are read-only until the user explicitly retries.
- Goal Stop can interrupt the current same-thread automatic turn independent of its origin. Ordinary no-Goal interrupt keeps its existing local-start restriction. Other threads and ordinary Send/Steer behavior remain unchanged.
- Remote UI members are serialized and notify changes. Stop icon, tooltip, automation name/help, and keyboard behavior agree and do not expose a duplicate Stop control.

## Verification

- Fake App Server and unit coverage: Goal creation/update/delete/Resume; initial read versus event races; pause-attempt-before-interrupt; no-turn pause; preserved fields; latest same-thread turn targeting after the pause attempt; interrupt attempted after pause failure/unknown; turn completion after ACK; mid-stop turn changes; terminal/deletion races; double-click; queue barriers; stale owner/thread/generation; reconnect with zero replay; pause and interrupt failure independently; partial outcomes; unknown outcomes; unsupported methods; authentication-state changes.
- Regression coverage: normal Send/Steer and ordinary interrupt, selected-thread isolation, no unsent input/attachment loss, and no Stop control duplication.
- Run all Core and UI tests, Debug and Release solution builds with zero warnings, pinned CLI 0.159.1 contract checks and CLI 0.155.1 regression checks, and VSIX DTO/XAML integrity checks.
- Inspect the Extension in an Experimental Instance under Light, Dark, High Contrast, narrow width, keyboard/focus, and UI Automation. Capture Active Goal, stopping, stopped, and reconnected states. Mark any unavailable visual check incomplete.

## Scope and completion record

Issue #173, CLI/SDK/NuGet upgrades, backward-compatibility work, and data migration are excluded. Do not record implementation or test outcomes until observed. At completion, update `doc/task.md` and implementation evidence, link those records from Issue #174, and retain missing screenshot criteria as incomplete.
