# ADR-015: Questions, approvals, and MCP interaction

- Date: 2026-09-13
- Status: Accepted
- Tracking: Codex App Server update interaction child Issue

## Decision

- Raise the Worker contract from v15 and model asynchronous questions, permission requests, and MCP input as distinct request types.
- Track one response per request, handle cancellation/disconnect/resolved races, and represent partial permission choices explicitly.
- Support bounded MCP form schemas and browser authentication flows. Do not declare unsupported form capabilities.
- Secret values use a dedicated safe input path; requests without one are rejected and secret text never enters ordinary transcript or logs.

## Consequences

Approval semantics remain visible and scoped, and MCP interaction cannot silently become an unrestricted text input.
