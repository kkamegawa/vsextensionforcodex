# Codex App Server Update and Remote Connection Plan

[日本語](app-server-update-plan_ja.md)

> Approved plan recorded on 2026-09-20. Implementation is tracked by parent Issue [#149](https://github.com/kkamegawa/vsextensionforcodex/issues/149) and seven child Issues.

## Summary

Update the Visual Studio extension's existing C# integration with `codex app-server` for the CLI 0.155.1 contract and add secure, explicitly enabled connections to an already running remote App Server. Local stdio remains the default. The work includes exact request dispatch, event-order safety, remote transport, root mapping and authentication-principal isolation, reconnect and history recovery, stored thread attachments, asynchronous questions and scoped permissions, native user verification, MCP interaction and authentication recovery, daily-use events, shell execution, typed artifacts, Windows sandbox status, and integrated release validation.

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
| High | CLI 0.155.1 target schema, 0.154.0 regression comparison, initialization metadata, capability declaration and detection | [#150](https://github.com/kkamegawa/vsextensionforcodex/issues/150) |
| High | Secure remote connection, connection ownership, diagnostics, and read-only retry | [#151](https://github.com/kkamegawa/vsextensionforcodex/issues/151) |
| High | Local/server path mapping and connection/account/authentication-principal/root state partitioning | [#152](https://github.com/kkamegawa/vsextensionforcodex/issues/152) |
| High | Reconnect, draft retention, paged history and attachment recovery, and uncertain-mutation handling | [#153](https://github.com/kkamegawa/vsextensionforcodex/issues/153) |
| High | Asynchronous questions, partial permissions, native user verification, MCP forms/authentication revocation, and secret input | [#154](https://github.com/kkamegawa/vsextensionforcodex/issues/154) |
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

- Pin **CLI 0.155.1 stable** as the target contract. Keep **CLI 0.154.0 stable** as the regression comparison source; do not treat a local alpha schema as either stable contract.
- Generate standard and experimental schemas separately for both 0.154.0 and 0.155.1 and compare them structurally.
- Store CLI version and generation options in schema-cache metadata. Regenerate when either differs instead of accepting any existing file as a valid cache hit.
- Keep generated schema output out of Git.
- Add contract tests for every used method, required field, enum, nullable field, unknown item, additional field, and invalid payload, including every observed 0.154.0-to-0.155.1 difference.

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

- Keep the local Worker architecture and select stdio or WebSocket inside the Worker.
- Share JSON-RPC dispatch, message limits, cancellation, pending-request completion, and shutdown behavior across transports.
- Store profile display name, endpoint, token-file path, local root, server root, enabled state, and selected profile.
- Read token contents only inside the Worker at connection time.

### Endpoint and ownership policy

- Accept `wss` for remote endpoints; accept plain `ws` only for loopback.
- Attach bearer authentication at the WebSocket handshake without logging authentication data.
- Do not expose a certificate-validation bypass.
- Local profiles own child-process restart. Remote profiles close only the connection and present the operation as reconnect.
- Serialize connect, reconnect, and close so only one connection generation becomes active.

### Diagnostics and retry

- Use health endpoints for diagnosis only; a successful health response does not prove JSON-RPC or a feature is available.
- Distinguish local process status from remote connection status.
- Apply bounded exponential backoff with jitter only to explicitly idempotent/read-only RPC after overload (`-32001`).
- Never retry a mutation automatically.

## Phase 3 — Path mapping and state isolation

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

### Recovery state and draft retention

- Serialize recovery and expose distinct `Reconnecting` and `Synchronizing history` states.
- Attempt automatic recovery at most five times, then expose manual reconnect.
- While the Visual Studio surface remains alive, retain composer text, attachments, selected skill, and next-turn model/reasoning/speed/personality settings.
- Do not add new disk persistence for drafts in this phase.

### Reinitialize and rebuild history

- Reconnect transport, run initialization again, and resume the selected conversation.
- Restore transcript through explicit history reads. Use pagination where supported and do not materialize unbounded history at once.
- Buffer notifications received during history synchronization and merge history/notifications by thread, turn, and item ID.
- Treat completed item data as authoritative over earlier deltas while preserving stable display order.
- Page `thread/attachment/list` to rebuild stored thread attachments without resuming the thread. Merge pages and `thread/attachment/updated` notifications by attachment identity and retain MIME, payload, and mapping status as bounded untrusted data.

### Uncertain mutations and multi-client ownership

- Never automatically resend a message, approval answer, MCP response, shell command, or other mutation if delivery is uncertain.
- Treat `thread/attachment/add` and `thread/attachment/remove` as explicit mutations. Never replay them automatically after disconnect, and preserve an uncertain result for user review.
- Present uncertain input in a reviewable state so the user can explicitly retry it.
- If another client currently owns the conversation, offer history-only viewing, a reason, and explicit retry.
- Drop all events from an older generation after a new generation becomes active.

## Phase 5 — Questions, permission scopes, and MCP interaction

Tracking: [#154](https://github.com/kkamegawa/vsextensionforcodex/issues/154)

### Worker contract and one-response lifecycle

- Raise the Worker contract from v15 and use distinct types for questions, permission requests, MCP input, user verification, and stored attachment state.
- Use a common pending-request registry keyed by connection generation and request ID.
- Guarantee at most one response across answer, cancel, timeout, disconnect, and `serverRequest/resolved` races.

### Asynchronous questions

- Render questions as independent answer cards that remain actionable while work continues.
- Preserve selected/default choices for display only. Focus, defaults, elapsed time, and preselection are not answers.
- Support server option metadata such as free-form “other” and secret markers.

### Partial permissions and command approval

- Return only the selected subset of requested network/file permissions.
- Default permission grants to turn scope; require explicit selection for session persistence.
- Display every server-provided command approval choice and additional permission request.
- Do not collapse a rule-changing choice into a generic “Accept”.

### Native user verification

- Support the experimental `openai/userVerification` MCP elicitation only when the complete local verification path is implemented and `experimentalApi` is enabled.
- Route verification through typed `userVerification/status`, `userVerification/enroll`, `userVerification/verify`, `userVerification/cancel`, and `userVerification/delete` operations. Validate bounded challenge, title, and description fields before showing native platform UI.
- Limit native verification to supported local stdio/in-process hosts. Do not advertise or forward it over WebSocket or remote-control peers; return a visible reason when the platform or transport is unsupported.
- Treat cancellation, disconnect, authentication-principal change, timeout, and `serverRequest/resolved` as one-response races. Cancel the native operation explicitly and discard any proof that arrives after resolution.
- Keep verification proofs and credential material out of transcript, settings, logs, diagnostics, telemetry, and crash text.

### MCP form, URL, and secret flow

- Support documented MCP form field types and validate required/type/range/choice rules before responding.
- Reject unsupported schemas with a visible reason and do not advertise extended-form capability early.
- Open MCP URL/browser authentication only through explicit user action and refresh status afterward.
- Do not automatically retry a failed MCP tool call after authentication or elicitation.
- Use a protected secret-input path. Refuse a secret-bearing request when no safe path is available.
- Treat `mcpServer/startupStatus/updated` with `failureReason: "reauthenticationRequired"` and failed OAuth completion as terminal authentication states. Show re-login/reconnect guidance, reset pending elicitation state after reconnect, and require a new explicit tool invocation.

## Phase 6 — Daily-use App Server features

Tracking: [#155](https://github.com/kkamegawa/vsextensionforcodex/issues/155)

### Plans and status

- Render `item/plan/delta` incrementally and reconcile it with the completed plan item without duplicate steps or unbounded growth.
- Present thread state, configuration warnings, model changes/additional confirmation, and MCP execution state with distinct bounded UI treatments.
- Sanitize all displayed text and redact diagnostics.

### Model capability handling

- Treat the model catalog as the source of truth for model ID, reasoning levels (including `max`/`ultra`), speed/service tier, and input modalities.
- Preserve existing inherited, persistent, and next-turn override semantics.
- Explain or disable unsupported image/file modalities before starting a turn.

### Explicit shell execution

- Add `/shell [--timeout-ms N] -- <command>` backed by `thread/shellCommand`.
- Execute only from explicit user action.
- Show connection/profile, exact command, working directory, and server-reported sandbox behavior before execution.
- Keep command execution timeout separate from JSON-RPC response timeout.
- Omitted timeout uses the server default; `0` means immediate timeout; negative values are input errors.

### Typed artifacts and Windows sandbox status

- Render MCP results, images, and file artifacts as typed bounded content.
- Distinguish server-reported truncation from a local display limit.
- Use a bounded image preview and enable file operations only for mapped files.
- Show local Windows sandbox initial setup, progress, completion, and actionable failure reasons.
- Do not expose the local setup flow as a remote-server configuration editor.

### Stored thread attachments

- Use `thread/attachment/list` with cursor pagination and the server limits to show saved attachments without resuming the thread.
- Add and remove attachments only from explicit user actions through `thread/attachment/add` and `thread/attachment/remove`; merge `thread/attachment/updated` notifications without duplicate rows.
- Validate supported MIME types, payload/size bounds, and local/server path mapping before enabling preview, open, add, or remove actions. An unmappable or unsupported attachment remains non-openable with a visible reason.
- Preserve idempotent duplicate-add and absent-remove behavior, and rebuild attachment state after reconnect, history recovery, or a non-ephemeral fork.

## Phase 7 — Integrated validation and release readiness

Tracking: [#156](https://github.com/kkamegawa/vsextensionforcodex/issues/156)

### Contract and ordering

- Generate and structurally compare CLI 0.154.0 and 0.155.1 standard/experimental schemas, then compare the 0.155.1 target with representative live request, response, and notification traffic.
- Cover unknown methods/items/enums, extra fields, malformed payloads, missing required fields, nullability drift, and schema-cache invalidation.
- Reproduce completion-before-start-response, notification-during-history-read, duplicate item, resolved-response race, and older-generation events.

### Recovery, transport, and isolation

- Reproduce Worker exit, transport loss, authentication failure, token rotation, overload, endpoint/root/profile switch, and another-client ownership.
- Verify draft retention and zero automatic replay of uncertain mutations.
- Exercise remote `wss`, loopback `ws`, forbidden remote `ws`, certificate failure, health/RPC disagreement, retry exhaustion, and manual reconnect.
- Cover Windows/POSIX roots, mixed separators, case, sibling-prefix escape, traversal, symlink/junction escape, unmappable attachments, and mapped file actions.
- Verify multiple endpoints, accounts, roots, and Visual Studio instances cannot mix any cached/session state.
- Switch authentication principals on the same endpoint and verify the previous owner's remote session, pending requests, WebSocket state, model catalog, caches, and late events cannot be reused.
- Cover stored attachment page boundaries and limits, duplicate add, absent remove, MIME rejection, mapped/unmapped paths, notification merging, disconnect uncertainty, recovery reconstruction, and fork copy behavior.

### Interaction and secret protection

- Cover asynchronous answers, partial permission grants, turn/session scope, MCP supported/unsupported forms, URL flow, cancellation, disconnect, and resolved races.
- Cover supported and unsupported native user verification, enroll/verify/cancel/delete, disconnect and resolved races, principal changes, late-proof disposal, and secret/proof non-disclosure.
- Cover expired/revoked MCP OAuth, `reauthenticationRequired`, re-login success/cancel/failure, elicitation-state reset, and zero automatic replay of the failed tool call.
- Inspect transcript, Remote UI DTOs, logs, diagnostics, settings, and failure text to ensure secret values never appear.

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
- Secure remote connection, root mapping, authentication-principal cache/state partitioning, reconnect, paged history, and stored attachment recovery work without replaying uncertain mutations.
- Partial permission approval, asynchronous questions, resolved races, supported MCP forms, supported local native verification, browser flow, MCP reauthentication guidance, and safe secret handling work end to end.
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
