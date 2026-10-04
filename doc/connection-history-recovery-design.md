# Connection and thread-history recovery design

- Date: 2026-10-04
- Tracking: [#149](https://github.com/kkamegawa/vsextensionforcodex/issues/149) (parent plan), [#153](https://github.com/kkamegawa/vsextensionforcodex/issues/153) (recovery), [#155](https://github.com/kkamegawa/vsextensionforcodex/issues/155) (attachment presentation and file actions)
- Contract baseline: Codex CLI 0.159.1; regression baseline 0.155.1
- Repository evidence baseline: 517d991250889c19b1b2c86b38ae20dc08cebe9c

## Summary

Recover a lost app-server connection only for temporary transport or Worker failures. Keep recovery single-flight for the lifetime of the Visual Studio chat surface. Reconnect and initialize without restarting a remote server, then refresh the conversation list and wait for the user to select a conversation. Do not infer that the CLI account is the same: the pinned account contract cannot prove a stable owner identity.

Freeze the previous owner's unsent draft in a volatile, immutable snapshot. Do not place it in the new composer automatically. The user must confirm the active connection, select a conversation, and explicitly restore or discard the snapshot. Restoring only copies it into the composer; sending remains a separate user action and revalidates the content against the current owner, root, skill catalog, and model catalog.

Read transcript history without resuming a thread. Load the newest summary and item pages first, provide an explicit older-page action, and merge history with buffered notifications by stable identities. Rebuild all stored attachment metadata through bounded cursor pages, but do not retain arbitrary payloads or infer a MIME contract that the protocol does not define. Never replay an operation whose delivery is uncertain.

## Protocol and owner boundary

The Extension cannot establish that a reconnected CLI session has the same account owner as the retired session. Therefore every successful connection refreshes the current thread list and requires a new conversation selection before history, resume, or draft restoration. The former selected thread is not resumed automatically. Its draft remains isolated in memory until explicit connection/profile confirmation and conversation selection; no draft field, approval, credential, or grant crosses into the active owner automatically.

The pinned schemas and implementation refine those statements:

- Both 0.155.1 and 0.159.1 support metadata-only thread/read, paged thread/turns/list, paged thread/items/list, and thread/resume with excludeTurns. Full-history hydration is deprecated for paginated threads.
- In 0.159.1, thread/items/list accepts an opaque string cursor or a structured exclusive item anchor. The anchor is target-version-only, requires turnId, and does not replace the returned opaque cursor for continuation. The regression schema accepts only string cursors.
- Resume with a running thread ID rejoins the running thread. excludeTurns suppresses embedded history; it does not make resume read-only.
- Thread status exposes idle/active and a small set of waiting flags, but neither the schema nor server contract identifies the owning client or defines a canonical “owned by another client” error. Treat active as possibly in use; do not claim another client is proven.
- Stored attachments are client-defined metadata identified by threadId, attachmentType, and identityKey. The 0.159.1 server caps a page at 100 entries, a thread at 100 active entries, serialized payload at 65,536 bytes, and attachmentType/identityKey at 256 UTF-8 bytes. There is no protocol MIME field or MIME allowlist.

## Evidence baseline

Design sources: doc/app-server-update-plan.md, doc/adr/ADR-013-app-server-path-state.md, doc/adr/ADR-014-app-server-recovery.md, and doc/path-state-isolation-design.md. Contract evidence: app-server-contract.json and schemas/0.155.1/stable plus schemas/0.159.1/stable. Attachment limits are defined in the pinned 0.159.1 state and app-server attachment sources.

## Recovery coordinator and lifecycle

One Extension-owned RecoveryCoordinator is scoped to the live chat surface and its current Worker bridge. It permits one recovery episode at a time. Each episode captures a unique episode ID, the current bridge identity, connection generation, owner-generation/partition token, applied profile revision, and local/server roots.

The coordinator uses a short-held state gate only to claim an episode, publish a state transition, or commit a result. It never holds that gate, a callback lock, or a UI dispatcher lock while awaiting bridge shutdown, process launch, socket connection, RPC, retry delay, notification drain, or UI dispatch. Every continuation rechecks the captured episode, bridge, connection generation, owner generation, profile revision, roots, and cancellation token before publishing state or applying data. A late completion from an earlier episode is discarded.

An unexpected Worker exit, bridge closure, transport reset, or timeout is eligible for recovery only while the applied connection/profile/root snapshot is unchanged and no authentication or owner-change signal has occurred. A local recovery creates a new Worker and local app-server process using the existing local lifecycle. A remote recovery creates a new Worker/socket connection to the configured endpoint; it never starts, stops, or restarts the remote server.

Before recovery RPCs, call EnsureWorkerStarted outside the state gate. If the proxy object is still non-null but its process has exited, or a call reports processExited/RpcDisconnected, retire that bridge and recreate the Worker process and proxy; never keep using a stale non-null RPC proxy. Subscribe to process-exit and bridge-disconnect signals for the current bridge and make repeated signals idempotent. A previous bridge's late signal cannot retire the replacement bridge.

Authentication failure, logout/login or account-owner change, token-file/profile/endpoint/root change, disabled/deleted profile, unsaved profile edits, user cancellation, or any stale snapshot stops automatic attempts. Show the appropriate explicit sign-in, profile-apply, reconnect, or conversation-selection action. Do not reinterpret these conditions as transient network failures.

### Episode states

Expose these distinct user-visible states:

| State | Meaning and allowed action |
|---|---|
| Ready | Current bridge is usable. |
| Reconnecting | One bounded automatic recovery episode is attempting to re-establish the same applied connection target. |
| Synchronizing history | Transport initialized; conversation list or selected-thread history is being read. No user mutation is sent. |
| Awaiting conversation selection | Connection is ready, but owner continuity is unproven. Refresh thread/list and require the user to select a conversation. |
| Manual reconnect required | The five attempts or episode deadline expired. Offer one explicit reconnect action. |
| Sign-in required | Credentials/authentication were rejected. Wait for explicit authentication and connection action. |
| Connection/profile confirmation required | Owner, endpoint, root, or profile changed or cannot be validated. Keep the prior draft isolated pending explicit confirmation. |
| History refresh required | A read failed or a bounded notification buffer overflowed. Keep current history marked stale and offer explicit resynchronization. |

### Retry schedule and bounds

Make at most five connection attempts in one episode. The delay before attempts is 0, 1, 2, 4, and 8 seconds, with ±20% jitter on non-zero delays. Each attempt has a 45-second deadline covering Worker bridge establishment, transport handshake, initialize/initialized, and the initial account/status read. The complete episode has a five-minute monotonic deadline, including waits. Cancellation, authentication failure, owner/profile/root change, or stale episode ends the loop immediately. Exhaustion ends in Manual reconnect required and never starts a background retry storm.

Only transport/Worker unavailability and explicitly classified temporary connection failures are retryable. A read-only history request failure may restart the current recovery attempt only if its underlying failure is classified as transient and all snapshots still match; it does not authorize replaying a mutation. Authentication, permission, invalid-profile, server identity, or generic application errors are terminal for automatic retry.

Add only thread/read, thread/turns/list, thread/items/list, and thread/attachment/list to the existing bounded overload-retry allowlist, using its current retry count, delay, and deadline rules. These are read-only calls. Never retry thread/resume after a timeout or overload response; the server may already have rejoined the live thread.

## Draft isolation and uncertain operations

At the first eligible connection loss, freeze the old owner's in-memory draft as an immutable RecoveryDraft. It contains composer text, attachment descriptors needed to reconstruct the draft, selected skill identity, and next-turn model/reasoning/speed/personality choices. Keep it only while the Visual Studio surface lives. Do not store it on disk.

The snapshot excludes credentials, token contents, cookies, approval grants/audit, pending approval or MCP answers, user-verification proofs, and other secrets. It is never copied into a replacement owner's composer automatically. Because the CLI cannot prove account continuity and reconnect requires a fresh thread list, restoration requires all of these user actions: confirm the active connection/profile, select a conversation from the refreshed list, then choose Restore draft or Discard draft.

Restore draft consumes the isolated snapshot and copies its safe draft fields into the active composer as an unsent draft. It does not select or resume a thread, execute a skill, or send content. Sending is a separate action. Immediately before send, revalidate owner/generation, physical attachment containment and readability, selected skill against the live catalog, and model/reasoning/service-tier values against the live catalog. If validation fails, keep the composer content visible and explain the failed field; do not redirect it to another owner.

After the user selects a conversation, show a separate Join action. Join calls thread/resume with excludeTurns=true only after explicit user action; resume is never part of automatic recovery and never part of the overload-retry allowlist. If Join fails or times out, preserve the read-only history, page cursors, and isolated RecoveryDraft, explain that the session could not be joined, and allow explicit retry. Do not clear history or the draft on failure.

After the user selects a conversation, show a separate Join action. Join calls thread/resume with excludeTurns=true only after explicit user action; resume is never part of automatic recovery and never part of the overload-retry allowlist. If Join fails or times out, preserve the read-only history, page cursors, and isolated RecoveryDraft, explain that the session could not be joined, and allow explicit retry. Do not clear history or the draft on failure.

Track each mutation in a local, volatile operation ledger; add no opaque recovery fields to app-server requests. Record local operation ID, owner/generation, operation kind, target conversation, and state: NotSent, InFlight, Confirmed, Rejected, or OutcomeUnknown. Mark InFlight at the dispatch boundary. A definitive response resolves it; loss after dispatch without a definitive response becomes OutcomeUnknown. Do not replay messages, turn starts/steers, approval answers, MCP submissions, shell commands, or attachment add/remove operations.

An absent message in recovered history is not evidence that it was never accepted: persisted history can be delayed or lossy. A matching text string, approximate timestamp, or similar content is also not proof that the uncertain operation produced a particular item. A definitive operation response can resolve the recorded outcome. An exact server item identifier correlated to the operation before disconnect can confirm acceptance only; it does not prove that every side effect completed. Classify NotSent only when dispatch is proven not to have begun. Keep other uncertain text reviewable and state that retry may duplicate the operation. Any retry requires explicit restore/revalidation followed by a separate Send action. An uncertain attachment add/remove stays visible for review and is reconciled by a fresh list; it is never automatically repeated.

## Read-only history recovery

After initialize/initialized succeeds, refresh the owner-scoped thread list and show Awaiting conversation selection. Do not automatically resume the previously selected thread. Once the user selects a thread:

1. Call thread/read with includeTurns=false for metadata and status.
2. Request the newest 50 turns using thread/turns/list with sortDirection=desc and itemsView=summary.
3. Request the newest 100 items across the thread using thread/items/list with sortDirection=desc and no turnId. Group ThreadItemEntry results by turnId and merge by item ID.
4. Show an explicit Load older action that continues with the returned opaque nextCursor. Keep a separate opaque cursor for each page/filter. A per-turn expansion requests that turn's items in pages of at most 100 and merges by item ID.
5. Use the target-only structured item anchor only to begin at a known exclusive item position in a visible turn; require turnId. Use server-returned string cursors for all continuation and for the 0.155.1-compatible path.

Keep one in-memory history window to at most 1,000 items and 16 MiB of rendered text. Virtualize presentation. When older pages would exceed either bound, retain the viewport-anchored window, evict items furthest from the viewport, and provide Load older/Load newer navigation. Enforce byte bounds before rendering; mark local truncation distinctly from server truncation. Never hydrate an entire paginated history during reconnect.

Subscribe and begin buffering notifications before the first history request. Key all rows and buffered events by owner generation, connection generation, thread ID, turn ID, and item ID. Apply item/completion data over earlier deltas while preserving stable order. Ignore older generations. Bound the notification buffer at 1,024 events and 8 MiB serialized size. If either limit is exceeded, mark history out of sync, stop applying the incomplete buffer, show History refresh required, and offer a fresh read; never silently drop events while presenting the transcript as complete.

Treat server-provided text and structured content as untrusted. Sanitize dynamic display content through SafeMarkdownService and redact log text through ISecretRedactor. History reading is read-only and never answers pending requests.

## Stored attachment reconstruction

After a thread is selected, call thread/attachment/list with limit=50 and continue page by page with the same threadId/cursor until nextCursor is null. Do not stop after a first page. The server retains at most 100 active attachments per thread, so two 50-entry pages are the normal maximum; still honor the cursor contract and a hard local unique-record ceiling of 100. Validate type and identity as non-empty UTF-8 values of at most 256 bytes each, and validate serialized payload size at most 65,536 bytes. Treat server data as untrusted even when it came from the pinned CLI.

Validate the serialized payload against the 65,536-byte limit before parsing. Convert recognized attachment types into narrow, bounded type-specific metadata and discard the raw JSON after parsing. Keep only basic bounded identity/type/time metadata for unknown types; do not retain or render their arbitrary payload JSON. This phase adds no generic MIME interpretation: the protocol exposes attachmentType and client-defined payload, not a MIME property or MIME enum. Unknown attachment types, unknown MIME values inside a recognized type, malformed payloads, oversized records, and unmappable paths remain metadata-only with a visible unavailable reason. Typed previews, open/reveal actions, and per-type MIME policy belong to #155 and must use the #152 path mapper and local physical boundary.

Start the attachment notification subscription before paging. Merge by (threadId, attachmentType, identityKey), while tracking attachmentId for each observed generation of that identity. A deleted notification tombstones that exact attachmentId so an older in-flight page cannot resurrect it; a later created notification with a new attachmentId for the same identity is a new row and must not be hidden by the old tombstone. A created notification contains identity but no payload; record an invalidation and refresh the paged list rather than fabricating attachment data. Coalesce a refresh after the current scan; if membership keeps changing during the bounded reconciliation pass, expose the list as stale with an explicit refresh action. Notifications from a prior owner or connection generation are discarded.

A non-ephemeral fork copies attachment membership but emits no per-attachment update notifications. On thread/started with forkedFromId, explicitly refresh the new thread's complete attachment list. Do not assume that source-thread attachment rows or IDs are valid for the fork.

## Worker contract and manifest

Use Worker contract v19 for the #153 recovery API because v18 is reserved by the accepted #152 owner-partition change. Update all bundled contract producers and consumers atomically. Add typed owner/generation-stamped read requests/results for thread metadata, turn pages, item pages, and attachment pages; add a generation-stamped attachment-update event and bounded recovery status/draft/operation-ledger callbacks. Keep app-server protocol payloads exactly as defined by the pinned schemas; recovery state and ledger IDs stay local and are not sent as unknown wire fields.

Update app-server-contract.json in the same change to list the methods actually used: thread/read, thread/turns/list, thread/items/list, thread/attachment/list, and the thread/attachment/updated notification. Do not add thread/attachment/add or remove to the allowlist unless a separately approved user action implements them. Add contract tests for both pinned schema versions, including excludeTurns, cursor union versus string-only regression, paging response cursors, attachment limits/shape, missing MIME semantics, notification identity, and every new Worker contract payload.

## Implementation sequence

1. Add v19 typed contracts and local recovery/draft/operation records; update the protocol method/notification allowlist and schema contract tests.
2. Implement the Extension-owned single-flight coordinator, short gate boundaries, bridge-death signal handling, transient-failure classifier, episode cancellation, retry jitter, attempt/episode deadlines, and terminal status presentation.
3. Implement isolated draft capture/restore/discard and the local uncertain-operation ledger. Require connection/profile confirmation and conversation selection before restore; keep restore separate from Send.
4. Implement metadata-only thread read, summary turn pages, global item pages, per-turn expansion, notification buffering/merge, history window bounds, overflow/manual-resync behavior, and the separate selected-conversation workflow.
5. Implement full bounded attachment pagination, type/identity/payload validation, notification tombstones/invalidation/reconciliation, and metadata-only unsupported states. Leave typed preview and file actions to #155.
6. Verify with focused race, transport, owner-switch, recovery, paging, payload, and UI tests; zero-warning Debug/Release builds; pinned 0.159.1 contract checks and 0.155.1 regression checks; VSIX/XAML inspection; and screenshots of retry, recovered selection, isolated draft restore/discard, history paging/overflow, and attachment states. Record only evidence actually observed in [implementation.md](implementation.md) and [task.md](task.md).

## Acceptance criteria

- Concurrent bridge-exit, close, timeout, and callback signals produce exactly one recovery episode; callbacks never wait on network/RPC work while holding shared locks.
- A stale non-null proxy after processExited/RpcDisconnected is retired and replaced.
- Only eligible temporary transport/Worker failures retry, using the defined five attempts and deadlines. Authentication, profile/settings/root change, account-owner change, cancellation, and stale generation stop retries.
- Remote recovery never restarts the external server. Recovered connections refresh thread/list and require explicit conversation selection.
- The old draft remains unchanged and isolated until explicit restore/discard; restore never sends. Secrets, approvals, and grants do not cross owner generations.
- Uncertain operations are never replayed. History absence, matching text, or approximate time never resolves OutcomeUnknown. Only a definitive operation response or a server item ID correlated to the operation before disconnect can confirm acceptance; classify an operation as NotSent only when dispatch is proven not to have begun.
- Initial history loads newest pages first; older pages and per-turn detail are incremental. Stable identities, completion precedence, generation fencing, 1,000-item/16-MiB window, and 1,024-event/8-MiB overflow behavior are covered.
- Attachment paging continues through null nextCursor, stays within server and local ceilings, and merges attachment-ID-aware deletion tombstones/creation invalidations without inventing payloads. Fork events trigger a new-thread refresh. Arbitrary payload and MIME values cannot enable rendering or local file actions.
- All supported errors are visible and actionable. No disk persistence is added.
- UI acceptance covers light, dark, and high-contrast themes at narrow tool-window widths, keyboard-only navigation/focus order, and the explicit Join, draft restore/discard, Send, older-history, refresh, and retry actions.

| Theme | Viewport and input mode | Scenarios |
|---|---|---|
| Light | Narrow tool-window width; keyboard only | Reconnecting, manual recovery, Join, draft restore/discard, separate Send, Load older, history refresh, attachment unavailable states |
| Dark | Narrow tool-window width; keyboard only | Reconnecting, manual recovery, Join, draft restore/discard, separate Send, Load older, history refresh, attachment unavailable states |
| High contrast | Narrow tool-window width; keyboard only | Reconnecting, manual recovery, Join, draft restore/discard, separate Send, Load older, history refresh, attachment unavailable states |
