# Path mapping and connection state isolation

[日本語](path-state-isolation-design_ja.md)

## Scope and authority

This design implements the accepted Phase 3 of the Codex App Server update plan and ADR-013, tracked by Issue #152. It preserves the secure connection lifecycle from Issue #151. The detailed remote profile and transport design remains authoritative for endpoint admission, token reads, TLS, diagnostics, retries, and socket ownership.

The local Worker owns all App Server interaction. Local stdio remains the default. Remote profiles map an existing local working tree to an existing server working tree; synchronization is external. A remote server enforces its own sandbox. Local filesystem checks protect only the Visual Studio host.

## Path domains

`LocalPath` and `ServerPath` are separate values. Only the shared `RemotePathMapper` converts between them. Worker JSON serialization unwraps a server value at the protocol boundary; a local file action unwraps a local value only after successful mapping and local containment validation.

| Property | Rule |
|---|---|
| Absolute roots | Accept rooted Windows drive, UNC share, and POSIX paths with an explicit path family. Reject relative paths, drive-relative paths, device namespaces that cannot be represented safely, control characters, and invalid roots. |
| Components | Compare complete normalized components, never substrings. A root named `C:\repo` does not contain `C:\repo2`. |
| Windows | Compare ordinally without case sensitivity. Normalize separators and supported extended drive/share prefixes without erasing the UNC share identity. Reject alternate data streams and unsafe local filename aliases. |
| POSIX | Compare ordinally with case sensitivity. Preserve case and Unicode code points. Do not normalize Unicode forms into an identity that the server did not supply. |
| Dot components | Normalize `.` and `..` with an explicit root boundary. Reject an attempted escape instead of mapping it into another root. |
| Root equality | The root itself maps to the other root. A separator suffix does not change a root's identity. |
| Returned server path | Use the configured server path family, independently of the Visual Studio host operating system. |
| Local filesystem | Resolve symlinks and junctions at the local trust boundary. A path must remain inside the resolved local root; unreadable, cyclic, or escaping links fail closed. |

The same mapper governs working directory, active document, selection source, referenced files, file attachments, `localImage`, changed-file paths, file artifacts, and local open/reveal actions. Existing file actions consume a mapped local path. Future typed artifact presentation consumes the same boundary rather than opening an App Server string directly.

Issue #152 delivers the mapper, the physical boundary, and owner stamping, and integrates every consumer that exists today. Consumers that do not exist yet are delivered by later phases and must use the same services:

| Consumer | Delivered by | Required boundary |
|---|---|---|
| Working directory, IDE context, attachments, `localImage`, approval paths, thread list working directory | Issue #152 | `RemotePathMapper` + `LocalPathBoundary` |
| Changed-file links, typed file artifacts, image preview, local open/reveal | Issue #155 | Server-to-local mapping and physical containment before the action is enabled |
| Stored attachments, recovered history, retained drafts | Issue #153 (rendering and operations: Issue #155) | Owner partition and generation, plus server-to-local mapping before any file action |

The thread list shows a server working directory only as its mapped local path. When the path cannot be mapped, it shows a fixed remote-directory label instead of the server string.

Before `turn/start`, validate every explicit attachment and its physical containment. An unmappable, missing, unreadable, or protected attachment rejects the whole start request with a bounded actionable reason that names only the file. It never sends a partial attachment list or exposes a sensitive full path in the error. Optional IDE context is included only when safely mappable. Validate physical containment again when opening or revealing a mapped local file.

Skill paths remain bounded server-provided identifiers. They are compared as part of the exact `(Name, Scope, Path)` identity and sent unchanged. Remote skills never depend on filesystem existence on the Visual Studio host.

## Owner identity and lifecycle

The Worker assigns an opaque state partition to the captured connection owner. Its inputs distinguish connection kind, profile and endpoint metadata, local/server working roots, authentication credential owner, and account identity. Partition fields are unambiguous, bounded, and hashed; token contents and account details never cross Remote UI or appear in diagnostics.

A bearer-token digest distinguishes explicit handshakes, including token rotation. It remains a Worker-only discriminator, not a claim to know the upstream principal. The CLI 0.159.1 account contract does not provide a universally authoritative account ID: API-key accounts contain only a type, and ChatGPT accounts contain nullable email and plan metadata. Email and plan alone cannot authorize sharing between accounts. When stable owner identity is unavailable, use a volatile per-Worker/attempt partition and disable both disk reads and disk writes of skill snapshots. The pinned contract therefore does not enable persistent cross-Worker reuse for local or remote owners.

Account change and logout retire the old owner's pending work and state. An unsolicited account notification is verified by reading the account again; only a changed account identity is a boundary. Remote owner changes invalidate the previous socket before accepting new-owner activity. Retiring a socket never stops the external server and never replays a mutation.

### Account change lifecycle

| Trigger | Owner handling | Resulting connection |
|---|---|---|
| Logout requested by this owner | Retire the old owner after `account/logout` succeeds. | The Worker immediately connects a new volatile owner with the same bound options (local: new child process; remote: reread token file, new socket). The account shows Signed out and Sign in is available. |
| `account/login/completed` whose `loginId` matches the sign-in this owner started | Retire the old owner, whether `success` is true or false. | Same automatic new-owner connection. The account is read by the new owner. |
| `account/updated`, or `account/login/completed` without a matching `loginId` | Read the account again for the same owner and compare its account fingerprint. A notification that arrives before the owner's first account read completes is covered by that read. | Same fingerprint: the account status is refreshed and the owner stays. Different fingerprint: retire the old owner; Degraded, and the user reconnects or restarts explicitly. |

The account fingerprint is a Worker-only SHA-256 digest of the account type, email, and plan from `account/read`. It never crosses Remote UI, diagnostics, or disk. The first account read of an owner records it; any later read that returns a different fingerprint retires the owner. CLI 0.159.1 sends `account/updated` shortly after startup without any account change, so treating every notification as a boundary would retire every new connection. An account notification or identity change processed while the owner's own `account/logout` is in flight counts as that logout, regardless of whether the response or the notification is handled first.

The automatic connection runs only `initialize`/`initialized` and the existing connect sequence under the transition gate. It replays no message, approval, or other mutation, and it clears selected state like any owner replacement. A failure publishes the ordinary Degraded state. The connection status reports the account change while the new session starts; the transcript shows no error.

Every operation captures the current owner and generation. Check both before committing results, emitting callbacks, or applying presentation state. An old response, notification, close callback, approval answer, model read, or catalog refresh cannot update a replacement owner. Retirement cancels pending requests and responses, clears approvals and audit presentation, and prevents stale refreshes from persisting under a new partition. Every Worker request that reads or mutates owner state, including model, thread, skill, MCP, permission-profile, and rate-limit reads, carries the captured owner. The Worker validates it under the transition gate and releases the gate before awaiting the app-server, so approval and user-input answers, owner retirement, and the watchdog never queue behind a pending call.

## State boundaries

| State | Owner and reset behavior |
|---|---|
| Socket, pending RPC, server requests | Worker connection generation and captured owner; retire before replacement. |
| Skills | Worker memory snapshot and persistent store use the owner partition plus working roots. Force-reload identity validation remains mandatory before a turn. |
| Model catalog and unsupported methods | Worker/UI results are owner-bound; replacement invalidates old catalogs and capability state. |
| Usage | Clear the owner snapshot and reject late reads and pushes after replacement, in both the Worker emission filter and the Extension. |
| Approval grants and audit | Grants cannot cross owner or connection lifetime. Audit presentation is bounded and owner-bound. |
| Selected conversation and transcript/history | Clear deterministically on endpoint, profile, root, account, or principal replacement. |
| Composer, skill selection, attachments, next-turn state | Belong to the selected owner. Clear on owner replacement; ordinary same-owner failure does not authorize silently sending them elsewhere. |
| Local file actions | Require mapped local values and physical containment for the current configured roots. |

No new disk persistence is added for drafts, history, or attachments. Phase 4 draft recovery is conditional on proving owner continuity: a retained draft cannot become the active composer of an unverified replacement owner. With the pinned identity contract, an explicit or account-change reconnect starts a new volatile owner and clears selected state. An account notification establishes a new boundary only when the reread account fingerprint differs. The existing skill cache retains its bounds, hard expiry, atomic replacement, and cross-process locking, with a revised partition format. Old workspace-only snapshots cannot establish ownership and are not reused. Contract v18 carries only the bounded, non-secret owner discriminator needed to invalidate Extension state; bundled producers and consumers change together.

## Implementation and verification

1. Implement typed normalization and mapping plus local physical-boundary checks. Cover Windows-to-POSIX, Windows-to-Windows, and POSIX-server-to-Windows-local mappings, extended drive/UNC roots, device aliases, alternate data streams, trailing dot/space aliases, reserved names, root-only paths, mixed separators, Unicode, case, drive/share mismatch, sibling prefixes, traversal, and symlink/junction escapes.
2. Integrate all existing outbound and inbound path consumers. Verify rejected attachments produce no `turn/start`, remote skill identities require no host filesystem lookup, and unmappable server paths never become local actions.
3. Partition Worker caches and grants and bind operations to owner/generation. Cover two endpoints, profiles, accounts, credential owners, roots, and concurrent Worker instances, including token rotation and delayed events from a retired owner.
4. Reset Extension selected state on owner replacement and guard asynchronous models, history, usage, skills, approvals, sends, and file actions. Preserve explicit connect/reconnect behavior and display bounded mapping errors.
5. Run focused regressions and the full Core/UI suites, zero-warning Debug/Release builds, pinned CLI 0.159.1 contract/schema checks, VSIX payload and embedded-XAML inspection, and `git diff --check`. Inspect actual Experimental Instance rendering and record observed evidence separately from source and unit-test evidence.

Acceptance requires the mapped paths and every listed state boundary to fail closed across owner changes. UI evidence records the rendered connection switch, empty selected state, attachment rejection, and usable remote skill selection. Any unavailable verification remains explicitly incomplete in the implementation/task record.
