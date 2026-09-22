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
