# ADR-018: Codex App Server 0.162.1 contract and MXC backend selection

[日本語](ADR-018-app-server-0-162-1_ja.md)

- Date: 2026-10-12
- Status: Accepted
- Tracking: [Issue #198](https://github.com/kkamegawa/vsextensionforcodex/issues/198) and child issues [#199](https://github.com/kkamegawa/vsextensionforcodex/issues/199)–[#203](https://github.com/kkamegawa/vsextensionforcodex/issues/203)

## Context

Codex CLI 0.162.1 adds goal mutation provenance, MCP OAuth attempt identifiers, a partial-answer message phase, a non-absolute skill path type, open-ended `CodexErrorInfo`, turn lineage, and stricter alignment of MXC selection with managed policy. MXC already existed in 0.159.1 and needs no extension-side installer or helper: Codex runs its own executable as the MXC helper. A remote App Server may still run 0.159.1.

## Decision

- Target 0.162.1 and keep 0.159.1 as the regression fixture. New request fields must be safe for 0.159.1 to ignore, and new response fields must be optional when read.
- Send `origin: "user"` on every goal mutation, because all of them come from explicit user actions. Never send `automatic`.
- Complete MCP OAuth only for a matching `loginId`; hold a completion that overtakes the response; fall back to server-name matching only when `loginId` is absent.
- Treat skill paths as opaque catalog identifiers instead of absolute host paths.
- Do not add a message phase or turn lineage to the Worker contract: the extension shows every agent message item once and ends a turn only on `turn/completed`, and it has no delegation path.
- Do not apply managed speed requirements in the extension; `supportsIndependentSpeedModes` is recorded but not used.
- Offer Follow Codex configuration, Prefer MXC (`features.prefer_mxc=true`), and Require MXC (`windows.sandbox=mxc`) for local stdio launches only, passed as allow-listed `-c` overrides at the next local start. Read `config/read` and `configRequirements/read` for diagnosis and report unknown values as unknown. Never relax managed policy, reconfigure a remote server, or re-run a failed command on another backend. Keep `windowsSandbox/setupStart` for the legacy elevated/unelevated backends only.

## Consequences

- The Worker contract version becomes 24 because `WorkerOptions` and the readiness result gain backend fields.
- A Require MXC selection that managed policy forbids stops the local App Server before initialization. The connection error names the setting, and the sandbox panel stays available after a failed local start so the user can change it.
- Readiness and configuration reads cannot prove which backend ran a given command; the UI states this rather than inferring it.
- The approved design and plan are in [app-server-0-162-1-plan.md](../app-server-0-162-1-plan.md).
