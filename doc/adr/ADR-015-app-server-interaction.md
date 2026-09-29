# ADR-015: Questions, approvals, and MCP interaction

- Date: 2026-09-13
- Status: Accepted
- Tracking: Codex App Server update interaction child Issue

## Decision

- Raise the Worker contract from v15 and model asynchronous questions, permission requests, MCP input, native user verification, and stored attachment state as distinct request types.
- Track one response per request, handle cancellation/disconnect/resolved races, and represent partial permission choices explicitly.
- Support bounded MCP form schemas and browser authentication flows. Do not declare unsupported form capabilities.
- Support experimental `openai/userVerification` only through the complete local stdio/in-process native path. Use typed `userVerification/*` operations, reject unsupported platforms/transports with a reason, cancel native work on disconnect or resolution, and discard late proofs.
- Treat MCP OAuth expiration or revocation, including `reauthenticationRequired`, as a terminal state that offers re-login/reconnect, resets pending elicitation state, and never automatically replays the failed tool call.
- Secret values use a dedicated safe input path; requests without one are rejected and secret text never enters ordinary transcript or logs.

## Consequences

Approval and verification semantics remain visible and scoped, and MCP interaction cannot silently become an unrestricted text input or leak native proof material.
