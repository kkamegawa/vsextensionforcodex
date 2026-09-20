# Codex App Server Update and Remote Connection Plan

This plan updates the extension's Codex App Server integration while preserving local stdio as the default. It adds an explicitly enabled secure WebSocket connection to an already running remote server, reconnect and history recovery, precise request routing, richer approval and question handling, and the app-server features needed for daily use.

## Scope and invariants

- Local stdio remains the default and owns its child process. A remote connection only owns the client connection and exposes reconnect, not server restart.
- Remote transport accepts `wss`; plain `ws` is limited to loopback. Authentication uses a token file without persisting token contents in settings, logs, or Remote UI. Certificate validation cannot be disabled.
- Local and server roots are represented as separate path types. Component-wise root mapping is used for working directories, IDE context, attachments, images, and change links; file synchronization is out of scope.
- State and caches are partitioned by connection, account, and working root. Remote sandbox policy is enforced by the remote server.
- Unrecognized server requests are never routed through an approval fallback. Secret-bearing requests are rejected unless a dedicated safe input path exists.

## Implementation phases

1. **Protocol contract and transport core**: pin CLI 0.154.0 for contract validation; separate standard and experimental schema generation; version schema cache metadata; preserve initialize OS data; use capability, read-only probes, and `-32601` to determine support; add exact request routing and generation-aware thread/turn state.
2. **Remote connection**: add stdio/WebSocket transport selection in the Worker; validate endpoint and authentication; serialize connect/reconnect; apply read-only exponential backoff with jitter; distinguish health from RPC availability.
3. **Path and state isolation**: add component-wise local/server root mapping and apply it consistently to all path-bearing inputs and links; reject unmappable attachments before send; partition skills, models, usage, approvals, and selected conversation state.
4. **Reconnect and history recovery**: retain in-memory draft state; initialize again after reconnect; page history where supported; merge notifications by thread, turn, and item identity; never automatically resend operations whose delivery is uncertain; expose retry and history-only behavior when another client owns the conversation.
5. **Questions, approvals, and MCP interaction**: raise the Worker contract from v15; model questions, permission requests, and MCP input independently; prevent duplicate responses and handle cancellation, disconnect, and resolved notifications; support scoped permission choices, MCP forms, and browser-based authentication without exposing secrets.
6. **Daily-use app-server features**: render plan deltas and confirmed plans, thread/config/model status, typed MCP and artifact results; use the model catalog for modalities and reasoning levels; add explicitly initiated `thread/shellCommand` with separate command and RPC timeouts; add local Windows sandbox setup status.
7. **Integrated verification and release readiness**: exercise contract drift, notification races, transport failures, path boundaries, permission flows, and UI accessibility in Light/Dark/High Contrast themes; run focused and full tests, Debug/Release builds, VSIX inspection, and Experimental Instance screenshots.

## Acceptance criteria

- Supported messages are handled by exact method/type contracts, and unknown requests receive a protocol-appropriate rejection.
- A stale response or notification from an earlier connection cannot revive a completed turn or mutate the current connection's state.
- Secure remote connection, root mapping, cache partitioning, reconnect, and paged history recovery work without resending uncertain mutations.
- Permission and MCP UI supports partial approval, asynchronous questions, resolved races, supported form schemas, and safe secret handling.
- Shell execution is explicit, bounded, connection-labelled, and governed by the existing approval policy.
- Core/UI tests, zero-warning Release build, VSIX checks, and Experimental Instance visual/accessibility checks pass.

## Deliberately deferred

Daemon lifecycle and worktree management, conversation pin/archive/rename, voice or Realtime, dynamic tools, ExternalMessage, plugin/agent configuration import, and attestation remain separate follow-up plans until their product and trust boundaries are defined.

## Tracking

The parent Issue and seven child Issues are created in the repository. The Wiki contains this plan in English and Japanese, with both Home indexes updated.
