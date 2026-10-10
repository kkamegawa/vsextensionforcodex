# Goal Stop Design — Issue #174

Date: 2026-10-11  
Status: Approved design (2026-10-11).  
Tracking: [Issue #174](https://github.com/kkamegawa/vsextensionforcodex/issues/174)  
Plan: [Goal Stop plan](goal-stop-plan.md) and [Japanese translation](goal-stop-plan_ja.md)

## Context

Goal notifications currently update transcript history without keeping the composer action synchronized with the Goal lifecycle. The ordinary turn interrupt path is scoped to turns started by the Extension, so it cannot reliably stop Goal-driven automatic continuation. The Goal state is persisted per thread by App Server.

The design follows the approved Issue #174 plan: an explicit Goal Stop attempts to pause the Goal first, then interrupts the latest running turn known for the same thread. Goal state and operations are isolated by owner, connection generation, and thread.

## User-visible behavior

- When the selected thread has an Active Goal, the composer primary action is Stop even between turns. It is available during automatic continuation, approval waits, and input waits.
- A Stop request is single-flight. The composer reports that stopping is in progress and prevents Send, Steer, Resume, and duplicate Stop until the targeted turn completes or the operation reaches a definitive failure state.
- The Goal remains visibly stopping after pause while the target turn is still running. Pause alone is complete when there is no active turn.
- Paused, Complete, Blocked, usage-limited, budget-limited, or deleted Goals restore the normal composer action after the targeted turn is no longer running. Resume makes an Active Goal eligible for Stop again.
- The UI exposes one Stop control. Icon, tooltip, accessibility name/help, enabled state, and keyboard behavior describe the same action.
- Unsent text and attachments remain in the composer. Goal Stop cancels commands that have not been dispatched for that thread.
- Ctrl+Enter only sends. It never triggers Stop, so it is disabled while the primary action is a Goal action.
- Slash commands such as `/goal pause` and `/goal clear` stay available while a Goal is Active. They are rejected with a visible message only while a stop for that thread is unresolved.
- When the app-server does not support goals, the composer returns to ordinary Send and Interrupt and shows the reason.

## Goal state and synchronization

`ChatViewModel` owns the presentation snapshot for the currently selected thread. Its identity includes owner, connection generation, and thread ID. All sources use one update path:

1. The Goal result returned by `ExecuteGoalAsync`.
2. `thread/goal/updated` and Goal-cleared/deleted notifications.
3. `thread/goal/get` after an explicit thread selection or Join.

An asynchronous read captures the complete identity and the current Goal update revision. Apply its response only if identity and revision still match; otherwise keep the newer event. A selection or owner/generation change clears the old snapshot before loading the new one. Only a reconnect (a transition into Ready from a non-running state) refreshes the Goal read-only after the existing explicit selection/Join is re-established; turn boundaries rely on Goal notifications. No Stop or other mutation is replayed.

While Stop is in flight, local stopping state takes precedence over stale Active reads or notifications. A terminal/deleted Goal can update Goal presentation immediately, but does not clear stopping while the captured target turn remains active. A definitive turn completion or connection retirement resolves the target-turn wait.

## Composer stop states

Goal state is mutated from the Worker notification thread and from RPC continuations, so the stop state is updated under one lock and change notifications are raised after release. Each stop has an operation identity that advances on every Stop and every projection reset; results for an earlier identity are ignored.

| State | Primary action | Exit |
|---|---|---|
| None | Stop when the Goal is Active, otherwise Send/Steer | Stop starts Stopping |
| Stopping | Stopping (disabled) | Worker result: success → AwaitingTurn; failure → Retry; unknown or exception → Unknown; unsupported → None |
| AwaitingTurn | Stopping (disabled) | Target turn completes or the thread status reports no turn → None; a different same-thread turn is running → Retry |
| Unknown | Check Stop Status (read-only) | A definitive Goal read or notification: a same-thread turn running or an Active Goal → Retry; otherwise → None. A failed read stays Unknown and can be checked again |
| Retry | Retry Stop | Explicit Retry starts Stopping; no same-thread turn running and the Goal is not Active → None |

## Stop request contract and ordering

Add a dedicated Extension–Worker `StopThreadGoalAsync` operation. The request uses the existing owner-scoped request envelope and includes the selected `ThreadId`. The result returns the captured generation, observed/current Goal state, pause outcome, interrupt outcome, and target `TurnId`. Advance Worker contract v21 to the next unused version at implementation time.

The Extension captures owner, connection generation, thread, and the active turn identity when it accepts Stop. The Worker independently verifies that the generation is current and the thread is joined. Revalidate before and after each app-server call. A changed owner, connection, or thread makes the operation stale and prevents later calls from being redirected.

For the captured thread, Worker reads current Goal state. If Active, or if the read fails, it attempts `thread/goal/set` with `status: "paused"` only; omitting objective and budget preserves the existing Goal and its usage history. After the pause attempt, it identifies the latest running turn for that same thread and, under the user's explicit Stop authorization, calls `turn/interrupt` for that turn without a second confirmation. It makes this best-effort interrupt even if pausing failed or has an unknown outcome. If no turn is running, it reports the pause result without interrupting. If a `turn/start` for the thread is still in flight without a server turn ID, the interrupt stage is reported as unknown. The Goal Stop action applies to the current thread's running turn, including automatic continuation and a Goal that ended or was cleared after the click; it does not infer turn ownership from who started it.

Overlapping Stop requests are serialized; a later request observes the earlier outcome instead of reporting a failure. A request timeout is an unknown outcome; only an app-server error response is a definitive failure. An unsupported pause method is a failed pause stage and the interrupt is still attempted; the result is reported as unsupported only when nothing was attempted.

Pause and interrupt results are independent. A successful pause stays in effect if interruption fails. If pause fails but interrupt succeeds, report the partial outcome and do not present the Goal as Paused; offer another Stop only through a fresh explicit user action. An interrupt acknowledgement is not turn completion; wait for the matching completion notification. Resolve the target turn after the pause attempt from the latest running-turn state for the captured thread. Never redirect the interrupt to another thread.

## Queue, failure, and reconnect behavior

- Stop cancels pending, undispatched commands for its thread. Every dequeued command checks the thread's stop barrier immediately before send, so queued Resume or user commands cannot restart work after Stop begins. Keep composer text and attachments intact.
- Double-clicks share one operation. Commands for other threads are unaffected.
- Distinguish pause failure, interrupt failure, and unknown outcome. Attempt same-thread interruption after the pause attempt even when pause fails or is uncertain. If pause succeeds and interrupt fails, retain Paused; if pause fails and interrupt succeeds, report the partial result without claiming Paused. Do not automatically retry either mutation after timeout or disconnect.
- For an unknown result, issue read-only state checks when possible and display the unresolved stage. A new mutation requires a fresh explicit user action.
- Reconnection refreshes Goal and thread status only after the existing explicit selection/Join; it sends no Stop, Resume, turn, or queued mutation automatically.
- The dedicated Stop call and the read-only Goal read are not rejected solely due to an authentication-state change; owner and generation checks still apply. Logs use the existing secret redaction path.

## Interfaces and implementation boundaries

- Remote UI-bound Goal and stopping properties have `DataMember` and change notifications. Worker-only turn/owner details remain outside Remote UI unless reduced to bounded display state.
- The Worker's v22 contract result distinguishes generation, Goal state, pause result, interrupt result, and target turn. Each stage has explicit success, definitive failure, or unknown outcome semantics.
- The App Server Goal update payload omits objective and budget when pausing. The implementation remains pinned to CLI 0.159.1 and does not upgrade CLI, SDK, or packages.
- Keep ordinary no-Goal Send/Steer behavior and the existing local-start restriction for ordinary interrupt unchanged.

## Verification and acceptance

Automated tests cover Goal lifecycle and source reconciliation; pause-attempt-before-interrupt ordering; best-effort interrupt after pause failure or unknown outcome; no-turn pause; preserved objective, budget, and usage; latest same-thread turn targeting after the pause attempt without another confirmation; acknowledgement/completion distinction; terminal/deletion races; queue barriers; duplicate clicks; stale owner/thread/generation replies; no replay; partial, separate failure, and unknown results; and unchanged ordinary Send/Steer/interrupt behavior.

Run Core and UI suites, warning-free Debug and Release builds, the pinned 0.159.1 contract checks and 0.155.1 regression checks, plus VSIX DTO/XAML integrity checks. Experimental Instance acceptance covers Light, Dark, High Contrast, narrow width, keyboard/focus, and UI Automation. Record screenshots for Active Goal, stopping, stopped, and reconnected states. Tests or builds do not substitute for missing UI evidence.

## Evidence

- Codex App Server — “Manage a thread goal” (`<APP_SERVER_REFERENCE_URL>`; checked 2026-10-11): defines `thread/goal/set`, `thread/goal/get`, Goal update notifications, and that omitting the objective while changing status preserves usage history. This supports status-only pause and read/update synchronization; the explicit Stop ordering and UI behavior are this repository's approved design.
