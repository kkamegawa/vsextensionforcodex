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
