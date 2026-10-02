# ADR-013: App Server path and state isolation

- Date: 2026-09-13
- Status: Accepted
- Tracking: Issue #152

## Decision

- Represent local and server paths with separate types and map roots component by component.
- Apply one mapping service to working directories, IDE context, attachments, images, and change links; reject unmappable attachments before sending.
- Partition connection/WebSocket state, skills, model catalog, usage, approval, selected conversation, attachment, and history state by connection, account, authentication principal, and working root.
- Bind active connection generations and outbound results to the captured authentication principal. Account switch, logout, or authentication-owner change invalidates the previous owner's remote session, pending requests, WebSocket state, model catalog, caches, and late events before activating the new principal.
- Treat remote sandbox policy as server-owned and never use local protected-directory checks as a substitute.

## Consequences

Path conversion is auditable and remote sessions or authentication owners cannot contaminate another principal's caches or state.

## Owner identity and cache partition contract

- Date: 2026-10-03
- Task: Issue #152
- Authority: the accepted Phase 3 partition requirement supersedes ADR-010's workspace-only cache key.
- Use distinct local/server path domains with the shared component mapper and physical local-root containment checks. Windows drive/UNC and POSIX roots determine their own comparison rules independently of host OS; preserve Unicode code points.
- Bind all state and result publication to an opaque owner partition and connection generation. Retire a remote owner's socket and pending work on account/authentication changes before accepting new-owner state.
- CLI 0.159.1 provides no universally authoritative account identifier. Nullable email, plan, and a shared bearer-token digest cannot establish account continuity across instances or offline account changes. Use volatile per-Worker/attempt partitions whenever stable owner identity cannot be established; do not read or write shared persistent skill snapshots in that case.
- Keep the existing persistent cache bounds and atomic/cross-process write discipline for future contract-backed stable partitions. Old workspace-only snapshots cannot authorize owner sharing.
- Clear selected conversation, draft, attachments, models, usage, skills, approval grants/audit, and history on owner replacement. Drop asynchronous results captured for a previous owner.
- Keep remote skill paths as opaque server identifiers and remote sandbox enforcement server-owned.

Detailed contract: [Path mapping and connection state isolation](../path-state-isolation-design.md).
