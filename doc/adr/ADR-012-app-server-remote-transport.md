# ADR-012: Remote App Server transport

- Date: 2026-09-13
- Status: Accepted
- Tracking: Codex App Server update remote connection child Issue

## Decision

- Keep local stdio as the default and add an explicitly enabled WebSocket transport for an already running remote server.
- Accept `wss`; allow `ws` only for loopback. Use a token file for bearer authentication and never expose its contents. Certificate validation cannot be disabled.
- A local connection owns its child process. A remote connection owns only the socket and presents reconnect instead of restart.
- Serialize connection transitions, separate health checks from RPC availability, and retry read-only RPCs with bounded exponential backoff and jitter.

## Consequences

Remote lifecycle stays outside the extension while transport policy is shared by local and remote clients.

## Amendment (2026-09-23, PR #157 review)

- Task: Issue #151 (remote transport) as implemented in PR #157.
- Both transports share one server-request dispatcher, so request ownership, error shaping, and response suppression after close cannot diverge. Notifications are delivered one at a time in wire order on every transport. Streaming deltas and turn lifecycle events depend on that order.
- The Worker observes the remote transport's `Closed` event and reports `Degraded` with a reconnect action, because a remote connection has no child process `Exited` signal.
- CI builds with the pinned contract executable. The latest stable release runs only a non-blocking start-and-initialize smoke test, because a build that used the latest executable would fail once it differed from the pinned schema contract.
- Reason: these are corrections from the review, not new direction. They implement the existing decisions (shared transport behavior, distinct remote status, pinned contract).

## Amendment (2026-09-30, PR #157 second review)

- Task: Issue #151 (remote transport) as implemented in PR #157.
- The remote profile editor is part of PR #157 instead of a later Issue #151 change. A connection-target flyout in the tool window edits saved profiles and applies one on explicit request (design.md section 12). Reason: without a reachable editor, remote transport cannot be used from the normal UI, so the feature would ship incomplete.
- A remote connection requires both `localRoot` and `serverRoot`. The Worker refuses the connection as `Degraded` otherwise, so an unmapped local path never reaches the server. Reason: path mapping is the only way local paths become valid on the server; this tightens, and does not change, the existing mapping decision (ADR-013).
- A remote connection loss is published under the connection-transition gate and only when no newer connect, restart, or dispose superseded that connection. Reason: a delayed close must not overwrite a newer `Ready` state; this implements the existing serialized-transition decision.
