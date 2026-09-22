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
