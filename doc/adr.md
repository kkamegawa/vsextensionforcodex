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

### Amendment — 2026-08-12: Refresh after turn completion

- Task: Refresh the Codex usage display after every conversation turn.
- Status: Accepted

The connection-generation, 60-second TTL, push-version ordering, and lifecycle invalidation
decisions above remain in force. `TurnCompleted` is additionally treated as an explicit
usage-consumption boundary, independent of the Busy-to-Ready state transition. After the existing
conversation-event projection completes, the Extension forces one `account/rateLimits/read` request
for every terminal turn outcome, including interruption or failure. If that request fails, the
last successful snapshot and its timestamp remain visible and the next supported refresh path may
retry it. The existing refresh gate and push-version check continue to reject stale responses.

This amendment changes only the trigger for a fresh read; it does not alter the Worker/RPC
contract, usage presentation model, or Remote UI surface.

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

## ADR-008: Skills catalog retrieval — capability probing, flattening, and contract scope

- Date: 2026-08-04
- Task: GitHub Issue #119 and sub-issue #120, Codex custom skills support
- Status: Accepted

### Decision

- Do not gate `skills/list` behind `WorkerOptions.ExperimentalApi`. Nothing in the vendored `schemas/` marks any `skills/*` method as experimental (verified against a freshly generated schema set from codex-cli 0.145.0, the current CLI at implementation time), and the existing `ExperimentalApi` hard gate (used for `permissionProfile/list`) is sticky for the life of the session and does not react to a settings change until reconnect. Rely purely on the `-32601` capability probe in `TrySendOperationAsync`.
- Flatten `SkillsListResponse.data` (an array of per-cwd `SkillsListEntry`) into a single `ListSkillsResult { Skills, Errors }` rather than mirroring the nested per-cwd shape. This is lossless in v1 because `skills/list` is always called with `cwds: []`, which the app-server documents as resolving to the single session working directory and therefore always returns exactly one entry. `SkillInfo.Cwd` and `SkillLoadError.Cwd` are carried per item so a future multi-root change does not require another contract bump. The per-cwd `errors[]` array is preserved as its own list rather than being dropped, since a skill's `SKILL.md` parse failure is the most common and most actionable failure a user can hit.
- Exclude `interface.brandColor`, `interface.iconSmall`/`iconLarge`, `interface.defaultPrompt`, and `dependencies.tools[]` from the `SkillInfo` contract entirely, rather than carrying and hiding them. `brandColor` is a free-form attacker-supplied string that would have to be interpreted as a WPF brush; the icon paths are `AbsolutePathBuf` values that would bind a WPF `Image.Source` to a file the app-server chose (a file-read/image-decoder attack surface); `defaultPrompt` is attacker-controlled text destined for the composer (a prompt-injection vector) with no v1 UI to review it before use; `dependencies.tools[]` describes external processes with no v1 consumer.

### Consequences

- A future skills UI that wants scope-aware disambiguation (e.g. resolving a name collision across `repo`/`user`/`system`/`admin`) must use `SkillInfo.Scope` plus `SkillInfo.Path`, since there is no server-assigned `id`.
- Re-adding any of the excluded `interface`/`dependencies` fields later requires its own security review (icon paths in particular need a decision on whether to fetch/display them at all) and is not merely a contract-widening change.
- `ReadSkills` in `CodexSessionService` must keep tolerating a missing or non-array `data` property, since the Fake app-server's catch-all response for an unhandled method is `{ }` and does not carry `data`.

## ADR-009: Unified slash menu and structured skill invocation

- Date: 2026-08-11
- Task: GitHub Issue #140
- Status: Accepted

### Decision

- Treat the existing Worker `skills/list` implementation as the only pre-existing skill capability. The Extension presents built-in commands and skills in one non-popup, virtualized list; the current main branch does not contain a skill-selection UI.
- Raise the Extension/Worker contract to v15. A selected skill is represented by one `SkillInvocationInfo { Name, Scope, Path }` chip. Scope and Path are used for exact Extension/Worker identity validation and never become Remote UI-bound raw path data. The app-server input is exactly `{ type: "skill", name, path }`; `skill_approval` is not sent or auto-granted.
- Keep one pending skill. Selecting another replaces it, clearing only the slash query through `SetComposerText("")` while the ordinary composer remains visible. Busy and approval-waiting states allow chip changes/removal, but a pending chip disables send/steer until the current turn finishes or the chip is removed.
- Cache the authoritative catalog for 60 seconds using `TimeProvider`, single-flight locking, immutable snapshots, generation invalidation, and sticky `-32601` probing. A turn start force-reloads and revalidates the complete identity against an enabled catalog item before app-server I/O.
- Accept `brandColor`, redacted bounded `defaultPrompt`, and bounded dependency metadata as display-only data. The default prompt is inserted only through an explicit button when the composer is empty and never auto-sends. Dependencies remain plain-text badges/tooltips; they do not execute or install anything.
- The icon spike is not part of this initial release until the Remote UI image/cache containment test is proven. Fixed glyph fallback remains mandatory; no raw icon path is exposed.
- Keep built-in candidates capped at eight and skill candidates capped at twenty. Headers and state rows are non-selectable. Same-name skills display a fixed scope label; path collisions use an opaque selection key and a safe ordinal suffix rather than exposing the path.

### Consequences

- Structured skills can start a text-free turn without conflating them with `SlashCommands.ActiveCommand` or text-only steering.
- Stale, disabled, unsupported, and transient catalog states fail closed without leaving Worker status Busy.
- ADR-008 is superseded only for the metadata fields explicitly admitted above. Its capability probe, flattened catalog, identity tuple, and missing-data tolerance remain in force.

## ADR-010: Complete skill catalog presentation and durable stale-while-revalidate cache

- Date: 2026-08-11
- Task: GitHub Issue #140
- Status: Accepted

### Decision

- Treat the live app-server `skills/list` response as the skill catalog system of record. The Worker remains the sole cache owner inside the client and must not infer skills by scanning the filesystem or treat a persisted snapshot as authoritative.
- Present every distinct `(Name, Scope, Path)` identity that the Worker safely accepts from the response. Remove the skill presentation cap of twenty while retaining ADR-008's untrusted-input safety cap of 200 skills. When the Worker reaches that cap, preserve `IsTruncated` and render a passive catalog-truncated row instead of claiming that every app-server skill is visible. The built-in-command cap of eight remains unchanged.
- Keep the existing 60-second in-memory snapshot as the hot cache and add a versioned, per-workspace persistent snapshot under `%LOCALAPPDATA%\Kkamegawa.CodexForVisualStudio\skill-catalog\v1`. On a cache hit, return the bounded snapshot immediately as stale, start one generation-guarded live refresh, and publish the fresh result through the existing invalidation path. Cached rows remain visibly stale and non-selectable until they are reconciled with a live catalog identity.
- A persistent snapshot is a display and startup optimization only. `turn/start` always bypasses stale and TTL caches, force-reloads `skills/list`, and validates an enabled exact `Name + Scope + Path` identity before serializing `{ type: "skill", name, path }`. A refresh or validation failure retains the pending chip and prevents offline or stale invocation.
- Key snapshots by the SHA-256 hash of the normalized working directory and store a format version, Codex version, saved UTC timestamp, truncation state, and at most 200 already bounded skill records. Do not persist `defaultPrompt`, dependency values, icon source paths, raw app-server JSON, or opaque Remote UI selection IDs. Raw skill paths remain Worker/Extension validation data and never become Remote UI data members.
- Treat cache files as untrusted input. Apply the same field/count limits used for live responses, reject unknown versions, control characters, malformed JSON, files larger than 4 MiB, and snapshots at least 24 hours old. Limit the cache root to 64 MiB total with least-recently-used eviction.
- Use same-directory temporary files, atomic replacement, and a bounded cross-process lock. A lock timeout, corrupt file, cleanup failure, or unsupported cache format falls back to live discovery without blocking the composer. Only a successful supported live response, including an empty or Worker-truncated response, may replace the persisted snapshot. `skills/changed` invalidates both memory and persisted snapshots; generation checks prevent an older request from republishing or persisting after invalidation.

### Consequences

- ADR-009 remains authoritative for the unified menu, pending-skill lifecycle, exact identity, metadata boundary, and built-in cap. ADR-010 supersedes only its twenty-skill presentation cap and its volatile-cache-only assumption.
- Empty-query and filtered skill results may contain up to 200 rows, so virtualization, keyboard navigation, UI Automation, deterministic ordering, and collision suffixes must work beyond the former twentieth row.
- Persistent data can improve first-menu latency and preserve a read-only preview through transient failures, but it never enables offline invocation and never changes sticky `-32601` capability behavior.
- Tests must cover 0, 1, 20, 21, 200, and 201 server entries; stale-to-fresh replacement; workspace isolation; empty, unsupported, failed, and truncated states; corruption, expiry, oversize input, atomic multi-instance writes, LRU cleanup, generation races, and force-reload validation before turn start.
