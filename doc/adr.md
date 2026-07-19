# Architecture Decision Records

## ADR-001: File attachment interaction and trust boundaries

- Date: 2026-07-19
- Task: GitHub Issue #67, file attachment support
- Status: Accepted

### Decision

- Defer drag-and-drop support. Visual Studio Remote UI cannot forward dropped file paths from event handlers to the out-of-process extension, so attachments use the SDK file picker and `#` workspace-file suggestions.
- Treat attachment chips as the source of truth. The `#filename` composer text is only a visible echo; editing that text does not remove an attachment. Users remove attachments with the chip command.
- Permit explicitly selected files outside the workspace, while validating existence and protected-directory policy in both the extension and Worker. IDE-context mentions remain restricted to the workspace.
- Keep `turn/steer` text-only. Attachments added during an active turn remain pending for the next `turn/start`.
- Clear pending attachments only after `turn/start` succeeds. Preserve them when connection or turn startup fails.

### Consequences

- `#` suggestions operate only on the final whitespace-delimited token because Remote UI does not expose the composer caret position.
- The feature provides an accessible, keyboard-operable removal path without code-behind.
- Drag-and-drop can be reconsidered when Visual Studio Extensibility exposes a supported Remote UI file-drop contract.

## ADR-002: Approval modes use app-server-owned policy and profile semantics

- Date: 2026-07-19
- Task: GitHub Issue #75, approval mode picker plan review
- Status: Accepted

### Decision

- Model manual approval as `approvalPolicy=on-request`, `approvalsReviewer=user`, and a workspace-write sandbox. Model approval on the user's behalf with the same approval and sandbox policy plus `approvalsReviewer=auto_review`; do not use the deprecated `on-failure` policy for auto-review.
- Keep display text separate from the stable persisted mode ID and resolve the complete approval/reviewer/sandbox tuple through one catalog entry.
- Do not parse `[profiles.*]` with an extension-owned partial TOML reader. General Codex profiles are process-start configuration and can contain settings that cannot be projected safely onto a turn. Discover `[permissions.<id>]` through `permissionProfile/list` and select one only when the runtime advertises the experimental turn `permissions` override.
- Treat Full access as bypassing the Codex sandbox and normal approval prompts. The Worker policy engine is a handler for app-server approval requests, not an independent execution boundary, so documentation and accessibility text must not claim that it protects operations for which no request is emitted.

### Consequences

- `StartTurnRequest` and the Worker bridge must carry `approvalsReviewer`; the Fake app-server and contract tests must verify it together with approval and sandbox values.
- Permission-profile entries are capability-gated. Unsupported app-server versions retain the built-in modes and Custom behavior without reading `config.toml` directly.
- Persisted dynamic selections require a placeholder/loading state so Remote UI null write-back cannot erase them before profile discovery completes.
- Full access requires an explicit warning and confirmation. Users must not be told that extension-side approval checks remain universally active.
