# ADR-015: Questions, approvals, and MCP interaction

[日本語](ADR-015-app-server-interaction_ja.md)

- Date: 2026-10-05
- Status: Accepted
- Tracking: [Issue #154](https://github.com/kkamegawa/vsextensionforcodex/issues/154)

## Context

The accepted 2026-09-13 decision was written against Worker contract v15 and assumed a protected secret-entry path and a complete local native user-verification route could be delivered by the extension. Review against the implemented v19 contract and pinned Codex CLI 0.159.1 exposed constraints that invalidate those success-path assumptions: the fixed upstream user-verification provider is macOS-only, and this extension client is not in the upstream eligibility set. The current extension also has no isolated secret-input channel. Gateway OAuth has a local stdio startup gate that must run before authentication-requiring RPCs; remote peers do not own that OAuth mutation flow.

## Decision

- Start from Worker contract v19 and allocate the next available contract version at merge time (v20 if unchanged). Keep the pinned CLI/SDK/runtime baseline unchanged.
- Represent questions, permission requests, command approval, MCP elicitation, Gateway OAuth, and unsupported user-verification requests as distinct typed interactions. Use one generation- and request-ID-bound pending registry, validate before atomic completion, and send at most one response across cancellation, timeout, disconnect, resolved, and stale-generation races.
- Render independent question cards. Defaults and selections are display state until explicit Submit. Detect secret-marked input before UI projection and refuse it with a reason; no secret value enters ordinary UI, transcript, logs, settings, diagnostics, or exceptions.
- Return only the requested permission subset. Turn scope is the default; session scope requires an explicit choice. Preserve every command-approval option, including rule-changing options, and route destructive actions through the existing approval policy.
- Support the documented MCP form field set with UI and Worker validation. Refuse unsupported extension schemas without declaring their capability. Open validated authentication URLs only after explicit user action. Treat OAuth expiry/revocation and `reauthenticationRequired` as reauthentication states, reset stale elicitation, and require a new explicit tool invocation. UI cancellation does not imply server-side cancellation; never replay the failed tool call.
- On local stdio only, declare `explicitGatewayOauth` and require a successful `account/gatewayOAuth/read` after initialization and before authenticated RPCs on each connection. Support login/cancel/change notifications and explicit browser launch without blocking other RPCs. A failed/unsupported gate stops authenticated RPCs and never triggers automatic browser login. Remote mode exposes status/guidance only and performs no login/cancel mutation.
- Defer native user-verification success until upstream supports Windows and the extension client. For this change, do not declare or forward verification capability; reject requests with a visible reason and never expose challenge, proof, or credential material.
- Attachment recovery remains owned by Issue #153 and attachment actions/presentation by Issue #155; Phase 5 adds no attachment contract or UI behavior.

## Consequences

The interaction contract has explicit ownership, response, and cancellation semantics. Unsupported secret and user-verification paths fail visibly before exposing sensitive content. Local Gateway OAuth cannot begin before its read gate succeeds, while remote connections remain read-only for this capability. Reauthentication always requires a fresh tool invocation, and uncertain mutations are never replayed.

## Decision history

- 2026-09-13: The prior ADR was accepted with v15-based assumptions for native verification success and protected secret entry. This accepted decision supersedes those assumptions; the prior acceptance is historical and does not approve this revision.
- 2026-10-05: Approved by the maintainer on 2026-10-05 after review of the complete Issue #154 specification. This accepted decision supersedes the 2026-09-13 assumptions described above.
