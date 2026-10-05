# Questions, Permissions, and MCP Interaction Design

[日本語](interaction-design_ja.md)

Status: Approved final specification (2026-10-05). Contract baseline: Worker v19; allocate the next available version at merge time (v20 if unchanged). CLI/SDK/runtime versions stay pinned.

## Goals and boundaries

Implement asynchronous questions, scoped permission choices, exact command approvals, MCP elicitation and authentication recovery, and local Gateway OAuth. Reject secret-bearing prompts before projecting them into UI. Defer native user-verification success until upstream supports Windows and this client. Issue #153 owns bounded attachment metadata recovery; Issue #155 owns attachment operations and presentation.

## Request lifecycle

Use a typed pending registry keyed by owner, connection generation, request kind, and original JSON-RPC ID. MCP elicitation may have no turn ID. Validate the proposed answer before atomically claiming completion. Answer, cancel, timeout, disconnect, server resolution, and connection retirement race to a single completion; resolved or stale requests receive no response. Once response dispatch begins, uncertain delivery is terminal and never retried.

### Worker and Remote UI interfaces

- Expose separate permission and MCP elicitation observers/resolvers so their payloads and response methods cannot be mixed. Both resolvers accept the captured owner/generation/request identity and reject a stale or already completed request.
- Question DTOs expose sanitized prompt/option text and opaque option IDs; free-text and “Other” answers are explicit answer variants. The Worker retains the original wire values and maps only a submitted opaque ID back to them.
- Command approval DTOs expose every server `Choice` with an opaque `ChoiceId`, label, and associated permission/rule-change description. The resolver accepts only a `ChoiceId` present in that request.
- Expose `InteractionAuthStatus` for Gateway OAuth state. Provide `ReadGatewayOAuth`, `LoginGatewayOAuth`, and `CancelGatewayOAuth`, plus `OpenAuthorizationUrl(ActionId)`. `ActionId` is opaque, owner/generation-bound, single-use, and maps in the Worker to a validated URL. Never accept a raw URL from a UI command.
- Provide `StartMcpOAuthLogin` to initiate the explicit MCP login flow and `DismissMcpOAuthLogin` to dismiss the UI wait/request only. Dismissal does not send a server-side OAuth cancellation because no MCP cancellation RPC exists.
- Resolvers validate the full response against the stored request before atomically claiming completion. Gateway OAuth calls are local-only and gated; remote calls can read status only.

## Questions and approvals

Show independent cards while a turn continues and leave the regular composer usable. Support blocking/non-blocking questions, free text, and “Other”. Defaults, focus, and selection are display state; require explicit Submit. Sanitize displayed strings, retain opaque choice IDs in the Worker, and map selected IDs back to original values only when responding. Reject secret-marked requests in the Worker before UI projection, with a visible reason and no sensitive value in UI, transcript, logs, settings, diagnostics, or exceptions.

Permission approval returns only a subset of the requested network/file permissions, with turn scope by default and explicit session persistence. Reject any permission outside the request. Show all server-provided command approval choices and associated permission changes as distinct choices; never relabel a rule change as ordinary acceptance. Destructive operations still pass through the existing approval policy.

## MCP elicitation and recovery

Dispatch exact method `mcpServer/elicitation/request`. Support form fields for string, number, integer, boolean, single choice, and multiple choice; validate required/type/length/range/format/choice-count in UI and Worker. Refuse unsupported extension schemas with a reason and never claim their capability. Validate URL data in the Worker, open only after explicit user action, and use OAuth completion/startup notifications to determine status. Opening a browser is not proof of success.

On expiry, revocation, failed OAuth completion, or `reauthenticationRequired`, show reauthentication guidance and end stale elicitation. MCP has no OAuth cancellation RPC, so UI cancellation describes only the UI request. After reauthentication, require a new explicit tool call; never automatically replay the failed call.

## Gateway OAuth

For local stdio, advertise `explicitGatewayOauth`. After initialize and before any authentication-required RPC, successfully read `account/gatewayOAuth/read` for every connection. Only then enable login/cancel and process changed notifications; bind every notification to the active owner and generation. Browser launch is explicit. Keep the login wait independent of unrelated RPC dispatch. If capability/read is unavailable, stop authenticated RPCs and show the reason; do not trigger automatic browser login.

For remote WebSocket profiles, expose status and sign-in guidance only. Do not advertise `explicitGatewayOauth` and do not issue login/cancel. The remote server remains responsible for its own authentication operations.

## Native user verification

CLI 0.159.1's native provider is macOS-only and does not include this extension client in its eligibility set. Until upstream supports Windows and this client, do not advertise/forward the capability. Reject a request before projecting challenge content, explain that the path is unsupported, and keep challenge/proof/credential data out of UI, transcript, logs, settings, diagnostics, and exceptions.

## Verification

Cover multiple concurrent cards, explicit-submit semantics, secret pre-projection refusal, exact permission subsets and scope, every command choice, supported/unsupported forms, browser outcomes, OAuth recovery, Gateway startup ordering and notification races, remote read-only behavior, and all response lifecycle races. Verify no automatic retry of uncertain responses or failed tool calls. Build and UI acceptance evidence are tracked in Issue #154 and the global plan.
