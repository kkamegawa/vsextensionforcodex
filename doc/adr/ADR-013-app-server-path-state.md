# ADR-013: App Server path and state isolation

- Date: 2026-09-13
- Status: Accepted
- Tracking: Codex App Server update path and state child Issue

## Decision

- Represent local and server paths with separate types and map roots component by component.
- Apply one mapping service to working directories, IDE context, attachments, images, and change links; reject unmappable attachments before sending.
- Partition skills, model, usage, approval, and selected conversation state by connection, account, and working root.
- Treat remote sandbox policy as server-owned and never use local protected-directory checks as a substitute.

## Consequences

Path conversion is auditable and remote sessions cannot contaminate local caches or state.
