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

## ADR-004: Turn settings use a three-state wire contract and explicit restoration

- Date: 2026-07-20
- Task: GitHub Issues #85, #86, and #93 through #98
- Status: Accepted

### Decision

- Represent reasoning effort and service tier with a presence flag plus a nullable value. Omission inherits Codex configuration, explicit null clears a sticky thread override, and a non-null value sets a canonical override.
- Keep persistent preferences separate from model-compatible visual selections. Unsupported models temporarily display Default without modifying persistence.
- Preserve hidden default model metadata outside the visible model list so an injected default ID retains its capabilities.
- Treat `/reasoning` and `/fast` as thread-scoped one-turn overrides. Consume them only after a successful turn start, then explicitly restore the persistent or effective value captured before the override.
- Use the same resolved settings for normal and direct Plan turns. Sanitize all app-server-owned names and descriptions before display.

### Consequences

- The Extension/Worker RPC contract advances to version 13 and requires matching binaries.
- Thread summaries and Worker status expose effective reasoning effort and service tier.
- A restoration turn can carry explicit null even though a normal Default turn omits the property.

## ADR-005: Usage freshness is connection-generation and push-version scoped

- Date: 2026-07-20
- Task: GitHub Issue #87 and sub-issues #99, #100, and #101
- Status: Accepted

### Decision

- Treat a signed-in connection generation as the lifetime of one usage snapshot. Fetch once when that generation first reaches Ready, and do not interpret Busy-to-Ready turn transitions as a new connection.
- Refresh an open-on-demand snapshot only after a 60-second TTL. A push notification advances a monotonic version; a read started before that push cannot replace it.
- Invalidate the snapshot, generation, and pending read eligibility on disconnect, sign-out, and disposal.
- Present only the top-level limit, one canonical Codex map entry, or the sole map entry. Ambiguous maps and windows without `usedPercent` are unavailable rather than zero usage.
- Open only the two compile-time approved usage destinations after exact HTTPS host and path validation. Diagnostics record neither destination nor user-derived URL text.
- Keep the modeless WPF `Popup`, but do not claim that `FocusManager.FocusedElement` transfers keyboard focus. Raw Remote UI cannot run VS-side `Popup.Opened` focus code, so Escape is bound on both the still-focused host and popup content; deterministic opening focus would require an in-process host.

### Consequences

- Usage remains stable when a turn changes Ready to Busy and back, while opening the popup can refresh genuinely stale data.
- Late reads from an old connection or from before a push are harmless.
- The Remote UI popup shares one sanitized presentation model with `/status`, exposes automation metadata, and remains dismissible with Escape whether keyboard focus stays on the host or enters the popup.

## ADR-006: Empty workspaces receive only a root-level SLNX solution

- Date: 2026-07-20
- Task: GitHub Issue #88 and sub-issues #102, #103, and #104
- Status: Accepted

### Decision

- Replace the solution-and-project scaffold with an empty solution choice.
- Create only `ROOT/<Name>.slnx`; do not create `src`, a project file, or source code for this choice.
- Encode the exact document `<Solution>` + CRLF + `</Solution>` + CRLF as UTF-8 with BOM.
- Preserve the existing non-overwrite rule and the independent file-based app choice.

### Consequences

- Workspace setup no longer guesses an application type, target framework, or project layout.
- The generated file remains both valid XML and a solution accepted by the pinned `.NET` SDK.
- Adding a project becomes an explicit later action by the user or Codex.

## ADR-007: Release identity, tag-driven versioning, and CI

- Date: 2026-07-20
- Task: GitHub Issue #105 and sub-issues #106, #107, and #108
- Status: Accepted

### Decision

- Publish the extension under a Marketplace-style identity `relaycodexforvs.KazushiKamegawa.<GUID>` (the extension name is `relaycodexforvs`) instead of `Kkamegawa.CodexForVisualStudio`. Only the identity carries the new name; the display name stays `Codex for Visual Studio`. The `%APPDATA%\Kkamegawa.CodexForVisualStudio` settings folder keeps its name so existing user settings survive.
- Treat the SDK-generated `extension.vsixmanifest` as the only manifest for the out-of-process extension. `src/Codex.VisualStudio.Extension/source.extension.vsixmanifest` never contributed to the package and is removed; metadata lives in `ExtensionConfiguration`.
- Re-enable generated assembly info for the extension project so the release workflow can set the VSIX version with `-p:Version=X.Y.Z.W` derived from the git tag, instead of patching a manifest or source file.
- Bundle only English documents in the VSIX: `LICENSE.txt` and `icon.png` are staged explicitly by the `StageVsixAssets` target, never by wildcard, so `README_ja.md` cannot be packaged.
- Install the Codex CLI with winget on the CI runner so `Codex.AppServer.Protocol` can generate `schemas/` during the build, rather than committing Apache-2.0 generated output to this MIT repository.

### Consequences

- The VSIX version is always the git tag; a release cannot silently ship a stale hardcoded version.
- Extension metadata changes are C# changes covered by unit tests, and the packaging tests assert the bundled license and the absence of Japanese documents.
- CI depends on winget and the `OpenAI.Codex` package being installable on the runner; a winget failure fails the build loudly instead of producing an unverified VSIX.
