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

## Account change reconnect and consumer scope

- Date: 2026-10-04
- Task: Issue #152 follow-up
- A logout requested by the owner, or an `account/login/completed` whose `loginId` matches the sign-in the owner started, retires the old owner and then automatically connects a new volatile owner with the same bound options. The connection runs only the existing connect sequence and replays no mutation. An unsolicited `account/updated` or an unmatched login completion still ends in Degraded and requires an explicit reconnect. Reason: the accepted plan activates the new principal after invalidating the old one; leaving every sign-in in Degraded made sign-in unusable after logout.
- Any unmappable, missing, unreadable, or protected explicit attachment rejects the whole `turn/start`; no partial attachment list is sent.
- Changed-file links, typed file artifacts, image preview, and open/reveal are delivered by Issue #155; stored attachments, recovered history, and retained drafts by Issue #153. Both must use the mapper, physical boundary, and owner partition delivered here.
- Remote mode stays Preview until Issue #153 is complete and the upstream WebSocket transport is no longer experimental.
- Amended the same day: an unsolicited `account/updated` or an unmatched login completion rereads the account for the same owner. Only a changed Worker-only account fingerprint (SHA-256 of type, email, and plan) retires the owner and ends in Degraded; an unchanged one refreshes the account status. Reason: CLI 0.159.1 sends `account/updated` shortly after startup without any account change, which retired every new connection in the Experimental Instance.

## Transient recovery draft quarantine

- Date: 2026-10-04
- Task: Issue #153
- Approval reference: user-approved plan in this conversation
- On owner replacement, clear active conversation, transcript, and live owner state, but preserve the unsent draft only as an immutable, in-memory quarantine entry tagged with its old opaque owner and target snapshot. The quarantine is not part of the new owner partition and is never auto-restored.
- A replacement Worker cannot prove continuity with the former Worker-only account fingerprint. Refresh the new target's thread list and require the user to review the active target, select a conversation in its history, and explicitly restore or discard the quarantined draft. Sending is a separate action and revalidates attachments and current skill/model choices.
- Do not carry approvals, grants, pending requests, caches, prior thread identifiers, or credentials into the replacement owner. This clarifies the earlier instruction to clear drafts: clear active draft state while retaining only the isolated review copy.
