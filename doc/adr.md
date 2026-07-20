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

## ADR-003: Command output uses a bounded Remote UI projection

- Date: 2026-07-20
- Task: GitHub Issue #80 and sub-issues #81, #82, and #83
- Status: Accepted

### Decision

- Keep sanitized command output in an incremental `StringBuilder` that is not a Remote UI `DataMember`. Bound the extension-side buffer to 2 MiB of characters independently of the Worker streaming limit.
- Publish the complete text only while it is short or while the user has explicitly expanded it. Once output exceeds three logical lines or 4,096 characters, publish only the first three lines capped at 4,096 characters while collapsed.
- Count CRLF as one logical line break, including when `\r` and `\n` arrive in separate streaming deltas. Do not create a hidden empty line for a trailing line break.
- Use the standard WPF `Expander` with TwoWay expanded state. Keep all command text non-wrapping and horizontally scrollable, and use Visual Studio dynamic theme resources for the control surface and text.
- When output is truncated, describe it as buffered output and do not report an exact total or hidden line count. Preserve overflow-file details outside the serialized Remote UI contract.

### Consequences

- Collapsed streaming updates change the serialized `Text` property only while the bounded preview itself changes; later hidden deltas no longer resend the accumulated command output across Remote UI.
- The standard Expander supplies keyboard focus and the UI Automation ExpandCollapse pattern without a custom control or third-party package.
- Expanding a large command deliberately republishes the bounded full buffer on subsequent deltas. This cost is user-selected and remains capped.
