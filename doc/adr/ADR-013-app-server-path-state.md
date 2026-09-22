# ADR-013: App Server path and state isolation

- Date: 2026-09-13
- Status: Accepted
- Tracking: Codex App Server update path and state child Issue

## Decision

- Represent local and server paths with separate types and map roots component by component.
- Apply one mapping service to working directories, IDE context, attachments, images, and change links; reject unmappable attachments before sending.
- Partition connection/WebSocket state, skills, model catalog, usage, approval, selected conversation, attachment, and history state by connection, account, authentication principal, and working root.
- Bind active connection generations and outbound results to the captured authentication principal. Account switch, logout, or authentication-owner change invalidates the previous owner's remote session, pending requests, WebSocket state, model catalog, caches, and late events before activating the new principal.
- Treat remote sandbox policy as server-owned and never use local protected-directory checks as a substitute.

## Consequences

Path conversion is auditable and remote sessions or authentication owners cannot contaminate another principal's caches or state.
