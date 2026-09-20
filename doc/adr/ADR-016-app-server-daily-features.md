# ADR-016: Daily-use App Server features

- Date: 2026-09-13
- Status: Accepted
- Tracking: Codex App Server update daily features child Issue

## Decision

- Display plan deltas and confirmed plans, thread/config/model status, and typed MCP/artifact results with bounded output and redaction.
- Use the model catalog as the source of truth for modalities and reasoning levels, including newly supported levels.
- Add explicitly initiated `thread/shellCommand` with separate command execution and RPC deadlines; negative timeout is invalid and zero is immediate timeout.
- Keep daemon/worktree lifecycle, voice/Realtime, dynamic tools, ExternalMessage, plugin import, and attestation outside this plan.

## Consequences

Daily-use features are bounded and policy-controlled, while larger product surfaces remain separately designable.
