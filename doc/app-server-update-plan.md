# Codex App Server Update and Remote Connection Plan

[日本語](app-server-update-plan_ja.md)

> Approved plan recorded on 2026-09-20. Implementation is tracked by parent Issue [#149](https://github.com/kkamegawa/vsextensionforcodex/issues/149) and seven child Issues.

## Summary

Update the Visual Studio extension's existing C# integration with `codex app-server` for the CLI 0.159.1 contract and add secure, explicitly enabled connections to an already running remote App Server. Local stdio remains the default. The work includes exact request dispatch, event-order safety, remote transport, root mapping and authentication-principal isolation, reconnect and history recovery, stored thread attachments, asynchronous questions and scoped permissions, explicit refusal of unsupported native verification and secret input, MCP interaction and authentication recovery, and Gateway OAuth, daily-use events, shell execution, typed artifacts, Windows sandbox status, and integrated release validation.

## Background and decisions

The extension already uses `codex app-server`, so migration from the deprecated MCP Server is not required. Python SDK changes such as `.params` and `HookMetadata.root` do not directly affect this C# repository. Event order, history reads, request shapes, capability behavior, and error handling do affect it and must be validated against the App Server contract.

The approved operating model is:

- Keep **Extension → local Worker**. The Worker selects local stdio or WebSocket and shares JSON-RPC dispatch, limits, pending-request cleanup, and shutdown behavior across transports.
- Local stdio remains the default and owns its child process.
- A remote profile connects to an already running server. Server startup, update, SSH/tunnel management, and file synchronization remain external responsibilities.
- Remote transport is explicit because upstream WebSocket support is experimental.
- The Visual Studio host and remote server already expose the same working tree. A configured local root maps to a configured server root; the extension does not synchronize files.
- `wss` is used for remote hosts. Plain `ws` is restricted to loopback. Authentication reads a bearer token from a file, and certificate validation cannot be disabled.

## Priority and tracking

| Priority | Area | Tracking |
|---|---|---|
| Critical | Exact request dispatch; unknown requests must not fall through to generic approval | [#150](https://github.com/kkamegawa/vsextensionforcodex/issues/150) |
| Critical | Connection-generation and turn-order races | [#150](https://github.com/kkamegawa/vsextensionforcodex/issues/150) |
| High | CLI 0.159.1 target schema, 0.155.1 regression comparison, initialization metadata, capability declaration and detection | [#150](https://github.com/kkamegawa/vsextensionforcodex/issues/150) |
| High | Secure remote connection, connection ownership, diagnostics, and read-only retry | [#151](https://github.com/kkamegawa/vsextensionforcodex/issues/151) |
| High | Local/server path mapping and connection/account/authentication-principal/root state partitioning | [#152](https://github.com/kkamegawa/vsextensionforcodex/issues/152) |
| High | Reconnect, draft retention, paged history and attachment recovery, and uncertain-mutation handling | [#153](https://github.com/kkamegawa/vsextensionforcodex/issues/153) |
| High | Asynchronous questions, partial permissions, explicit secret/native-verification refusal, MCP forms/authentication recovery, and local Gateway OAuth | [#154](https://github.com/kkamegawa/vsextensionforcodex/issues/154) |
| High | Plan/thread/config/model/MCP state, stored attachment operations, and typed artifact presentation | [#155](https://github.com/kkamegawa/vsextensionforcodex/issues/155) |
| Medium | Model modalities, reasoning levels, explicit shell execution, and independent timeouts | [#155](https://github.com/kkamegawa/vsextensionforcodex/issues/155) |
| Medium | Local Windows sandbox setup status and mapped image/file actions | [#155](https://github.com/kkamegawa/vsextensionforcodex/issues/155) |
| Release gate | Contract, race, recovery, remote, interaction, UI, build, package, and Experimental Instance evidence | [#156](https://github.com/kkamegawa/vsextensionforcodex/issues/156) |

## Architecture and security invariants

- Treat every App Server message as untrusted input. Sanitize displayed text, redact logs, and bound text, arrays, history pages, command output, and image previews.
- Route destructive operations through the existing approval policy.
- Determine feature support from implemented client capabilities, the known contract, read-only API results, and `-32601`. Do not call a mutating API merely to probe support.
- Never automatically resend user input, approval answers, MCP submissions, shell commands, or other mutations when delivery is uncertain.
- Partition connection/WebSocket state, skills, models, usage, approval records, selected conversation, drafts, attachments, and recovered history by connection profile, account, authentication principal, and working root.
- When the account or authentication principal changes, invalidate the previous owner's remote session, pending requests, WebSocket state, model catalog, caches, and late events before making the new principal active.
- Remote sandbox policy is enforced by the remote server. Local protected-directory checks do not claim to secure the remote host.
- Preserve existing ADR decisions for cache boundaries, approval semantics, and inherited/persistent/next-turn settings. Record any required change before implementation.
- Never persist bearer-token contents in settings, Remote UI, transcripts, logs, diagnostics, telemetry, or crash text.

## Phase 1 — Protocol contract and transport core

Tracking: [#150](https://github.com/kkamegawa/vsextensionforcodex/issues/150)

### Contract and schema baseline

- Pin **CLI 0.159.1 stable** as the target contract. Keep **CLI 0.155.1 stable** as the regression comparison source; do not treat a local alpha schema as either stable contract.
- Generate standard and experimental schemas separately for both 0.155.1 and 0.159.1 and compare them structurally.
- Store CLI version and generation options in schema-cache metadata. Regenerate when either differs instead of accepting any existing file as a valid cache hit.
- Keep generated schema output out of Git.
- Add contract tests for every used method, required field, enum, nullable field, unknown item, additional field, and invalid payload, including every observed 0.155.1-to-0.159.1 difference.

### Initialization and capability detection

- Preserve initialization metadata such as the server platform family/OS.
- Do not assume `initialize` returns a universal server-capability list.
- Advertise only client capabilities implemented end to end.
- Combine declared capability, known contract, read-only results, and `-32601` to determine support.
- Do not invoke a change-producing method to test availability.

### Request dispatch and lifecycle ordering

- Dispatch server requests by exact method name and expected payload shape.
- Return a protocol-appropriate error or defined refusal for unsupported requests. Never route an unknown request through generic approval or an existing grant.
- Track state by connection generation, thread ID, turn ID, item ID, and server-request ID.
- Absorb `turn/start` response versus `turn/completed`/`turn/started` notification reordering.
- On connection close, deterministically cancel or complete outstanding client and server requests.
- Ignore every late response, notification, or resolved event from an older connection generation.

## Phase 2 — Secure remote connection

Tracking: [#151](https://github.com/kkamegawa/vsextensionforcodex/issues/151)

### Transport and profile model

- Keep Extension → local Worker; select owned local stdio or an explicitly enabled remote WebSocket connection inside the Worker.
- Preserve shared RPC dispatch, message bounds, notification order, cancellation, and generation retirement. Store only profile metadata, never token contents.
- The detailed final contract is [secure-remote-connection-design.md](secure-remote-connection-design.md), with a [Japanese translation](secure-remote-connection-design_ja.md). The UI contract is `doc/design.md` section 12; the decision record is `doc/adr/ADR-012-app-server-remote-transport.md`.
- Existing retry helpers and endpoint/token checks are partial implementations. Extend the helpers with method/parameter eligibility and one timeout budget; replace unbounded token reads and duplicate UI endpoint validation. Preserve the already implemented profile editor and transport ownership.

### Endpoint, authentication, and ownership

- Put endpoint-only `RemoteEndpointPolicy` in Contracts (`netstandard2.0`) for Extension, Worker, and Protocol. Accept remote `wss` and strictly defined loopback `ws`; reject URI credentials, query strings, fragments, and unspecified bind addresses.
- Save validates metadata and the token-file path requirement; only the Worker reads and validates file existence, readability, encoding, bounded content, and bearer format immediately before handshake.
- Read a local token file afresh on explicit connect/reconnect and on every #153 coordinator-triggered remote attempt. Token rotation alone does not trigger automatic recovery. Use lease-based exact-token redaction, platform TLS verification, no redirects, and the same captured proxy-resolution policy for WebSocket and HTTP diagnosis.
- Local restart owns a child process. Remote reconnect owns only its socket. Reject remote `worker/restart` with the typed connection-operation rejection before any stop.
- Reconnect reloads the latest saved profile by the applied name and requires enabled, unchanged metadata and the expected generation. Changed/disabled/deleted profiles require an explicit apply/target choice; token-file content rotation alone does not change profile metadata.
- Serialize same-instance configuration mutations and reconnect snapshot validation/dispatch, then revalidate under the Worker transition gate. Persistent principal/cache partitioning and cross-instance configuration transactions remain Issue #152.

### Diagnosis, liveness, and retry

- Health GETs to `/healthz` and `/readyz` use a five-second shared budget, no authentication/Origin, no redirects/body display, and independent typed results. Labels identify authority-root scope; a path-routed App Server remains unverified by a root probe. Health never determines RPC readiness or feature support.
- Keep .NET 8. A 30-second keepalive interval does not prove peer responsiveness; a generation-bound idle RPC watchdog uses two silent ten-second probes to retire a half-open connection. The watchdog only detects and closes; it does not own retries. The separate #153 Extension coordinator may retry eligible transient-loss signals. Neither path replays mutations. Any valid inbound request, response, or notification during a probe returns the watchdog to the thirty-second silence period.
- Remote startup has a 45-second overall deadline: token read at most five seconds and handshake, initialize, and startup account read each at most fifteen seconds, all capped by remaining time.
- The exact retry inventory contains eight current methods. `account/read` requires explicit `refreshToken=false`; `skills/list` requires explicit `forceReload=false`. Forced skill refresh remains a single request because cache eviction and rescanning add work. Future history-read methods require separate reviewed allowlist additions.
- Retry only completed `-32001` responses, at most three retries/four sends, with 250/500/1000 ms base delays and plus/minus 20 percent jitter. One monotonic timeout budget includes sends and waits; connection retirement cancels pending retry work.

### Implementation sequence and gates

1. Confirm the revised Issue #151 design and proposed ADR-012 amendment. Keep this Phase 2 section, `doc/design.md` section 12, the English/Japanese detailed design, and Wiki plan/index pairs synchronized. Implementation begins after design confirmation.
2. Extend shared policy/retry components, replace unbounded token I/O, add secret leases and redacted diagnostic production, and make failed-start cleanup deterministic. Keep the existing runtime/SDK/package versions.
3. Add typed diagnostics, reconnect refusal reasons, target/generation snapshots, and the .NET 8 liveness watchdog. Allocate the next Worker contract version from the actual merge base; update and package all producers/consumers atomically.
4. Wire profile freshness validation, independent health/RPC rows, and local Restart/remote Reconnect labels. Add policy/token/TLS/proxy/read-only retry/lifecycle/serialization/command-state tests, including all excluded argument variants.
5. Use the pinned CLI 0.159.1 for a zero-warning Release build, Core/UI tests, schema/contract gates, VSIX manifest/assembly/XAML inspection, and Experimental Instance screenshots. Record observed evidence in `doc/implementation.md` and `doc/task.md` with Issue #151 tracking; leave completion unchecked until every acceptance criterion has evidence.

## Phase 3 — Path mapping and state isolation

Detailed design: [Path mapping and connection state isolation](path-state-isolation-design.md) / [日本語](path-state-isolation-design_ja.md). The implementation uses the accepted ADR-013 ownership boundary, including volatile isolation when the pinned account contract cannot prove a stable owner.

Tracking: [#152](https://github.com/kkamegawa/vsextensionforcodex/issues/152)

### Path domains and mapping

- Represent local paths and server paths as different types.
- Map normalized roots component by component. A root such as `C:\repo` must not match `C:\repo2`.
- Define Windows/POSIX separators, case rules, root equality, `.`/`..`, long paths, Unicode, drives/shares, and symlink/junction behavior.
- Use one mapping service for working directory, IDE context, attachments, `localImage`, changed-file links, file artifacts, and local open/reveal actions.
- Reject an unmappable attachment before send and show an actionable reason.
- Never open a server path directly as a local file.

### Server-owned identifiers and state keys

- Keep skill paths as server-provided identity. Do not require them to exist on the Visual Studio host.
- Partition skill cache, model catalog, usage, approval grants/audit, selected conversation, drafts, attachments, history, and WebSocket/session state by connection, account, authentication principal, and working root.
- Switch or clear selected state deterministically when endpoint, account, authentication principal, or root changes.
- Bind each active connection generation and outbound result to the captured authentication principal. On account switch, logout, or owner change, close or invalidate the old remote session and discard its pending responses, cached WebSocket state, model catalog, and notifications.
- Treat remote sandbox enforcement as a remote-server responsibility.

## Phase 4 — Reconnect and history recovery

Tracking: [#153](https://github.com/kkamegawa/vsextensionforcodex/issues/153)

Detailed design: [English](connection-history-recovery-design.md) / [日本語](connection-history-recovery-design_ja.md). Implemented with Worker contract v19; validation evidence is recorded in [implementation.md](implementation.md).

### Connection recovery and quarantined drafts

- One Extension-owned coordinator survives Worker death and schedules a single recovery episode. Retire a dead or disconnected Bridge, including a stale non-null RPC proxy, before recreating it. Each Worker connection attempt uses the existing transition gate; callbacks enqueue loss signals and return without awaiting recovery.
- Recover only transient Worker/child-process exit, transport loss, silent peer, or unexpected server close. Authentication, TLS/certificate, profile/settings/root, known owner changes, and cancellation stop automatic attempts. Preserve #152's separate owner-initiated sign-in/sign-out connection lifecycle. Remote recovery reconnects the socket; the external server remains externally managed.
- Make at most five attempts, with waits before successive attempts of 0, 1, 2, 4, and 8 seconds and ±20% jitter on nonzero waits. Each attempt is limited to 45 seconds and the whole episode to five minutes. Serialize manual operations and show a stable manual-reconnect action after exhaustion. Re-read the token file for each eligible remote attempt; rotation alone never starts recovery.
- Distinguish Reconnecting, confirmation required, Synchronizing history, history-only viewing, and manual reconnect required. Before clearing active owner state, freeze the old draft's text, attachment references, skill, and next-turn model/reasoning/speed/personality settings in isolated Extension memory. Keep it unchanged across retries and only while the VS surface lives.
- Initialize the new connection, refresh the current owner's thread list, and require target review plus current-owner conversation selection before explicit Restore or Discard. Restore copies into the composer only; Send is separate. Revalidate attachment mapping/physical boundaries and model/skill/settings catalogs. Never carry approvals, caches, credentials, pending server requests, or secret proofs into the new owner. Add no disk persistence.

### Read-only history and attachment reconciliation

- Read thread/read with includeTurns=false, the latest 50 turn summaries using thread/turns/list with sortDirection=desc and itemsView=summary, and the latest 100 thread items using thread/items/list with sortDirection=desc. Load older pages and per-turn detail explicitly with returned string cursors. The 0.159.1 structured exclusive item anchor requires turnId and a known item boundary; the 0.155.1 regression path uses strings.
- Merge pages and buffered notifications by owner, connection generation, thread, turn, and item IDs. Completed item data takes precedence over deltas; duplicates and late deltas cannot roll display back. Keep a moving window of at most 1,000 items/16 MiB displayed text. Cap notifications at 1,024 events/8 MiB; overflow visibly fails synchronization and offers explicit read-only resynchronization.
- Keep viewing separate from Join/Resume. Only an explicit action calls thread/resume with excludeTurns=true. An active conversation is possibly in use; the contract does not prove another client's ownership. A resume failure preserves fetched history and the isolated draft; show a sanitized reason and explicit action.
- Page thread/attachment/list with limit=50 until nextCursor is null. Validate a maximum of 100 active records/thread, 100/page, 64 KiB serialized payload/record, and 256 UTF-8 bytes each for attachmentType and identityKey. #153 validates payloads and produces bounded basic metadata; #155 owns rich interpretation, preview, add/remove, and file actions. Unknown or invalid formats show an unavailable reason without enabling an action. There is no universal protocol MIME field.
- Merge attachment membership by (threadId, attachmentType, identityKey) and attachment ID. Created notifications contain no payload and trigger a bounded list refresh; deleted-ID tombstones prevent stale-page resurrection, while a later new ID can recreate the identity. Refresh after an explicit non-ephemeral fork. Ignore retired owners and generations.
- Added the four read methods and attachment notification to the contract manifest and read-only overload allowlist. The existing three-retry/four-send policy remains separate from connection recovery; resume and mutations are not overload-retryable.

### Uncertain operations and verification

- Record mutations locally at the dispatch boundary. NotSent requires proof that dispatch never began; a lost response after possible dispatch remains OutcomeUnknown. History absence or matching text/time proves nothing about the outcome. A definitive response can resolve the recorded outcome; a pre-correlated server item ID confirms acceptance only, not all side effects.
- Automatically replay zero messages, approvals, MCP submissions, shell commands, file mutations, or attachment changes. Keep uncertain content reviewable and require Copy/Edit/revalidation followed by a separate new Send action. Local operation IDs are not wire idempotency fields; expired request IDs and secret proofs are never reused.
- Recovery exclusions, five-attempt exhaustion, stale generations, draft Restore/Discard, history bounds, attachment pagination, and mutation non-replay are covered by Core/UI tests. Both solution configurations build with zero warnings; pinned 0.159.1/0.155.1 contracts and the Release VSIX passed inspection. Experimental Instance screenshots remain the outstanding visual acceptance evidence because Visual Studio is unavailable in the implementation environment; see [implementation.md](implementation.md).

## Phase 5 — Questions, permission scopes, and MCP interaction

Tracking: [#154](https://github.com/kkamegawa/vsextensionforcodex/issues/154)

The implementation starts from Worker contract v19 and allocates the next available contract version at merge time (v20 if no intervening change). CLI/SDK/runtime versions remain pinned to the existing baseline: CLI 0.159.1 is the target and 0.155.1 is the regression comparison.

### Contract and request lifecycle

- Use distinct request and response types for asynchronous questions, permission requests, command approvals, MCP elicitation, Gateway OAuth, and unsupported user-verification requests.
- Keep one pending-request registry keyed by connection generation and original JSON-RPC request ID, including MCP requests without a turn ID. Apply existing owner validation to every response.
- Validate an answer before atomically claiming completion. Answer, cancel, timeout, disconnect, and `serverRequest/resolved` races produce at most one response. Never answer resolved requests or requests from retired generations; never retry when delivery is uncertain.

### Questions and permissions

- Show independent question cards that remain actionable while the turn continues and do not block the normal composer. Handle blocking/non-blocking questions, free text, and “Other” options. A selection, focus, or default value is display state; only explicit Submit answers.
- Detect secret-marked questions in the Worker before creating a UI projection. Refuse them with a reason because no safe dedicated input route exists; do not expose secret content to ordinary controls, transcript, logs, settings, diagnostics, or exceptions.
- Return only the selected subset of requested network/file permissions. Default to turn scope; session scope requires an explicit action. Reject permissions outside the server request.
- Present every command-approval choice and additional permission, preserving rule-changing choices as their own server options. Route destructive operations through the existing approval policy.

### MCP elicitation and recovery

- Handle the exact `mcpServer/elicitation/request` method. Validate supported form fields (string, number, integer, boolean, single choice, and multiple choice) for required, type, length, range, format, and selection-count constraints in both UI and Worker.
- Refuse unsupported extension schemas such as `openai/form` with a reason and do not advertise an extended-form capability.
- Validate and retain authentication URLs in the Worker. Open a browser only after an explicit user action; opening it does not prove authentication succeeded.
- Connect `mcpServer/oauth/login`, `mcpServer/oauthLogin/completed`, and startup-status notifications. On expiration, revocation, or `reauthenticationRequired`, explain reauthentication and retire stale elicitation state. UI cancellation does not claim to cancel server-side OAuth because MCP defines no cancellation RPC.
- After reauthentication, require a new explicit tool invocation. Never automatically replay the failed tool call.

### Gateway OAuth and native user verification

- On local stdio only, declare `explicitGatewayOauth`; after initialization, successfully call `account/gatewayOAuth/read` before any RPC requiring authentication. Gate each connection this way, then support login/cancel/change notifications and an explicit browser action.
- Bind Gateway OAuth notifications to the active connection and owner. Do not block unrelated RPC dispatch while login is pending. If unsupported or the initial read fails, stop authenticated RPCs and do not fall back to automatic browser login.
- For remote connections, expose status and sign-in guidance only: do not declare the capability or send Gateway OAuth login/cancel mutations.
- Fixed CLI 0.159.1 user-verification support is macOS-only, and this extension client is not in the upstream eligibility set. Defer the successful native path until upstream supports Windows and the extension client. For this implementation, do not declare or forward the capability; reject a request with a visible reason and keep challenges, proofs, and credentials out of all UI, transcript, logs, settings, diagnostics, and exceptions.

### Attachment ownership

- Phase 5 adds no stored-attachment contract or UI behavior. Phase 4 / Issue #153 owns bounded metadata recovery; Phase 6 / Issue #155 owns attachment actions and presentation.

## Phase 6 — Daily-use App Server features

Tracking: [#155](https://github.com/kkamegawa/vsextensionforcodex/issues/155). The approved final design and package plan are [Daily-use App Server Features](daily-use-app-server-design.md) and [its implementation plan](daily-use-app-server-plan.md).

The phase targets CLI 0.159.1 stable with 0.155.1 regression fixtures. Baseline: commit `69deda1` (PR #169 merged), Worker contract v20; allocate the next available contract version at merge time. CLI, SDK, runtime, and packages stay pinned. Reuse the #152 mapping/ownership, #153 bounded history and saved-attachment metadata recovery, and #154 question/permission/MCP foundations.

Phase 6 includes optional experimental plan deltas and bounded notices; live-catalog input admission and additive 0.159.1 fields; `/shell [--timeout-ms N] -- <command>` with exact command preservation, confirmation, local policy evaluation, independent RPC/execution deadlines, and a per-thread pending lock; typed bounded result parts, PNG/JPEG preview, and mapped Open/Reveal with action-time physical validation; explicit saved-attachment add/remove using the client-owned `relaycodex.file.v1` payload; and local-Windows-only sandbox setup with indeterminate progress and truthful outcome states. The shell method always executes unsandboxed with full access in CLI 0.159.1. Saved attachments remain metadata and never become composer input automatically.

Work packages P0–P6 and their dependencies are defined in the detailed plan. Verification includes pinned 0.159.1/0.155.1 fixtures, targeted then full Core/UI tests, warning-free Debug/Release builds, contract/schema and VSIX integrity checks, plus Experimental Instance screenshots for themes, narrow width, keyboard/focus, and accessibility. Missing screenshots remain unmet criteria. Issue #155 stays open until evidence is recorded; Issue #156 retains the release gate.

## Phase 7 — Integrated validation and release readiness

Tracking: [#156](https://github.com/kkamegawa/vsextensionforcodex/issues/156)

### Contract and ordering

- Generate and structurally compare CLI 0.155.1 and 0.159.1 standard/experimental schemas, then compare the 0.159.1 target with representative live request, response, and notification traffic.
- Cover unknown methods/items/enums, extra fields, malformed payloads, missing required fields, nullability drift, and schema-cache invalidation.
- Reproduce completion-before-start-response, notification-during-history-read, duplicate item, resolved-response race, and older-generation events.

### Recovery, transport, and isolation

- Reproduce Worker exit, transport loss, authentication failure, token rotation, overload, endpoint/root/profile switch, and another-client ownership.
- Verify draft retention and zero automatic replay of uncertain mutations.
- Exercise remote `wss`, loopback `ws`, forbidden remote `ws`, certificate failure, health/RPC disagreement, retry exhaustion, and manual reconnect.
- Cover Windows/POSIX roots, mixed separators, case, sibling-prefix escape, traversal, symlink/junction escape, unmappable attachments, and mapped file actions.
- Verify multiple endpoints, accounts, roots, and Visual Studio instances cannot mix any cached/session state.
- Switch authentication principals on the same endpoint and verify the previous owner's remote session, pending requests, WebSocket state, model catalog, caches, and late events cannot be reused.
- Cover basic attachment metadata page/record limits, notification identity merging, disconnect uncertainty, and recovery reconstruction. Issue #155 separately covers duplicate add, absent remove, MIME/payload rejection, mapped/unmapped paths, typed actions, and fork copy behavior.

### Interaction and secret protection

- Cover multiple question cards, composer independence, free text/Other, explicit Submit, display-only defaults, and secret rejection before UI projection.
- Exercise answer/cancel, duplicate submission, timeout, disconnect, `serverRequest/resolved`, and retired-generation races; each request produces at most one response.
- Verify exact partial network/file permission responses, turn/session scope, out-of-request permission rejection, and faithful command-approval choices.
- Cover supported MCP form fields and validation, unsupported schema refusal, explicit browser launch, cancellation/failure/success, OAuth expiration/revocation, `reauthenticationRequired`, and reset of stale elicitation. Confirm zero automatic tool replay and that UI cancellation does not imply server-side cancellation.
- Verify local Gateway OAuth startup gating, notifications arriving before responses, cancellation, reconnect, and remote read-only behavior.
- Verify native user-verification requests are rejected with a reason and capability remains undeclared. Inspect UI, transcript, logs, settings, diagnostics, and exception text for challenge/proof/credential disclosure.

### UI, build, and package evidence

- Run focused tests for each phase, then full Core and UI test suites.
- Run Debug and Release solution builds with zero warnings.
- Inspect VSIX contents, Worker payload, manifests, generated schema/cache metadata, embedded XAML, and relevant hashes.
- Run the installed extension in a Visual Studio Experimental Instance.
- Verify actual Light, Dark, and High Contrast rendering; narrow widths; keyboard navigation; accessible names/live regions; question cards and authentication states. Capture screenshots and record pass/fail; unavailable screenshots remain incomplete visual acceptance evidence.
- Record evidence and accepted limitations in `doc/implementation.md` and `doc/task.md` with links to this issue hierarchy.

### UI, build, and package evidence

- Run focused tests for each phase, then full Core and UI test suites.
- Run Debug and Release solution builds with zero warnings.
- Inspect VSIX contents, Worker payload, manifests, generated schema/cache metadata, embedded XAML, and relevant hashes.
- Run the installed extension in a Visual Studio Experimental Instance.
- Verify actual Light, Dark, and High Contrast rendering; keyboard navigation; focus order; accessible names/live regions; virtualization; reconnect/history states; interaction cards; artifacts; and shell confirmation.
- Capture screenshots for required themes/states and record pass/fail evidence rather than relying only on source inspection.
- Record evidence and accepted limitations in `doc/implementation.md` and `doc/task.md` with links to this issue hierarchy.

## Completion criteria

- Supported messages use exact method/type contracts; unsupported requests receive a protocol-appropriate rejection.
- A response or notification from a stale connection cannot revive a completed turn or mutate current state.
- Secure remote connection, root mapping, authentication-principal cache/state partitioning, transient reconnect, paged history, and bounded basic attachment metadata recovery work without replaying uncertain mutations.
- Partial permission approval, asynchronous questions, resolved races, supported MCP forms, browser flow, MCP reauthentication guidance, safe refusal of secret input, and local/remote Gateway OAuth behavior work end to end. Native user-verification success remains deferred until upstream supports Windows and this extension client.
- Shell execution is explicit, bounded, connection-labelled, and governed by the existing approval policy.
- Core/UI tests, zero-warning Debug and Release builds, VSIX checks, and Experimental Instance visual/accessibility checks pass; screenshots provide evidence for the displayed states.
- Shell execution is explicit, bounded, connection-labelled, and governed by the existing approval policy.
- Core/UI tests, zero-warning Debug and Release builds, VSIX checks, and Experimental Instance visual/accessibility checks pass.

## Deferred follow-up capabilities

The following are useful but are separate plans because they require additional product, lifecycle, or trust-boundary design:

| Capability | Treatment |
|---|---|
| Windows shared daemon and automatic worktree creation/management | Follow-up after external-server connection is stable; server lifecycle and Git operations stay separate |
| Conversation rename, pin, and archive | Add after history recovery is complete |
| Voice/Realtime and dynamic tools | Separate UI, transport, and permission design |
| `ExternalMessage` | Define the authority difference from direct user input first |
| Plugin management and importing other-agent configuration | Separate source-of-truth and change-confirmation plan |
| Attestation | Remain opt-in until an attestation provider and trust model exist |

## References

- [Parent Issue #149](https://github.com/kkamegawa/vsextensionforcodex/issues/149)
- [Official App Server documentation](https://learn.chatgpt.com/docs/app-server)
- [Official changelog](https://learn.chatgpt.com/docs/changelog)
