# ADR-014: Reconnect and history recovery

- Date: 2026-09-13
- Status: Accepted
- Tracking: Codex App Server update recovery child Issue

## Decision

- Retain composer text, attachments, selected skill, and next-turn settings in memory while the VS surface lives.
- Reinitialize after reconnect and recover history through explicit, paged reads where supported. Merge incoming notifications by thread, turn, and item identity.
- Rebuild stored attachment state through paged `thread/attachment/list` reads and merge `thread/attachment/updated` notifications by attachment identity without resuming the thread.
- Never automatically resend an input, approval, or mutating command whose delivery is uncertain. Expose history-only and user-confirmed retry behavior.
- Treat `thread/attachment/add` and `thread/attachment/remove` as explicit mutations whose uncertain results require user review rather than automatic replay.
- Limit automatic recovery to five attempts, then provide manual reconnect.

## Consequences

Recovery avoids duplicate side effects while keeping the user's draft and safely reconstructed attachment state available for deliberate review or retry.

## Current recovery contract

- Date: 2026-10-04
- Task: Issue #153
- Approval reference: user-approved plan in this conversation
- The accepted bullets above record the earlier decision; this dated amendment is the current target.
- An Extension-owned single-flight episode survives Worker replacement. It retries transient Worker/transport loss only: five attempts after waits of 0, 1, 2, 4, and 8 seconds (±20% jitter on non-zero waits), 45 seconds per attempt, and five minutes total. Authentication, TLS/certificate, profile/settings/root, owner changes, and cancellation are terminal. The idle watchdog detects/closes only; the #153 coordinator handles eligible transient loss. The owner-initiated #152 login/logout lifecycle remains separate.
- Refresh the current target and thread list after connection; load the selected thread's latest history page only after explicit conversation selection. Never auto-resume the prior thread. Call thread/resume with excludeTurns=true only after explicit user action. A replacement Worker cannot prove continuity with its former Worker-only owner fingerprint; quarantine the old draft under its opaque owner until target review, current-history conversation selection, and explicit restore or discard. Do not migrate prior owner partitions, approvals, caches, requests, credentials, thread IDs, or proofs.
- Load newest bounded history pages first and older pages only on request. Merge by server IDs and generation; cap retained history at 1,000 items/16 MiB and notifications at 1,024 events/8 MiB.
- #153 reads basic attachment metadata only: 50 per page, cursor until null, 100 per thread, serialized payload 64 KiB, type and identityKey each 256 UTF-8 bytes. Validate lists, cursors, fields, and payloads; show unknown/unavailable reasons. Notifications contain no MIME or payload. Scope IDs to the owning thread and refresh after delete/recreate or fork. Rich interpretation and typed actions remain #155.
- Never replay an operation after transport write may have begun. Operation IDs stay local. Text/time similarity and absence from loaded history prove nothing. A definitive response can resolve the recorded outcome; a server item identity correlated before disconnect confirms acceptance only, not all side effects. NotSent requires proof that dispatch never began. Otherwise retain uncertainty and require explicit Copy/Edit/Send review. Never reuse approval IDs/proofs.
- Review the four additional read-only history methods separately before adding the overload allowlist; they are separate from the existing eight-method policy and five connection attempts.

Detailed target: [Connection and thread-history recovery](../connection-history-recovery-design.md). This is a plan; implementation evidence remains pending.
