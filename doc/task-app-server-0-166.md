# Codex App Server 0.166.0 Task Record

Tracking: [#241](https://github.com/kkamegawa/vsextensionforcodex/issues/241). Detailed implementation evidence: [implementation record](implementation-app-server-0-166.md). Japanese record: [日本語](task-app-server-0-166_ja.md).

## Completed

- [x] Raise the contract manifest to schema v4 with target 0.166.0, regression versions 0.165.0, 0.164.0, 0.163.0, 0.162.1, and 0.161.0, and the complete Windows package asset names and hashes.
- [x] Verify complete packages for all six versions. Installation follows `codex-package.json` entrypoint metadata and checks package and executable hashes.
- [x] Generate stable and experimental schemas for all six versions, compare each regression version with 0.166.0, verify method surfaces, and retain unadopted differences in `knownDifferences`.
- [x] Add feature tiers 0.165 and 0.166, compare version boundaries against each feature introduction, and raise the Worker contract to v27 with `ReviewPlacement` and no `detached` field.
- [x] Project item timestamps with entry-level fallback; project structured provider errors, capacity policy notifications and history, bounded secret-redacted unknown errors, and read-only history when resume is rejected.
- [x] Verify the 0.166.0 CLI idle unload: `thread/closed` arrived after about 60 seconds and the same thread resumed.
- [x] Pass contract and compatibility tests, Debug and Release Core/UI test suites, Debug and Release solution builds with zero warnings and errors, and Release VSIX inspection.
- [x] Pass real CLI `initialize` checks for 0.166.0, 0.165.0, and the older regression versions.

## Pending maintainer acceptance

- [ ] Capture and inspect the changed behavior in the Visual Studio Experimental Instance. The Computer Use app-list request timed out on its permitted retries, so this session could not obtain a screenshot. Automated UI tests passed; this does not replace the requested visual check.

Evidence is under `D:\temp\issue241-verify-2\`.

## Stage 2: stable features

Tracking: [#242](https://github.com/kkamegawa/vsextensionforcodex/issues/242). This stage stays open until the items under Not yet verified are done.

### Implemented and passing automated tests

- [x] Add `/review --new-thread` as one Worker operation over `thread/start` and `review/start` with `inline` delivery.
- [x] Add the history scopes All, Current working folder, and Same repository worktrees over `thread/list.cwd`.
- [x] Render question text and option descriptions through the safe Markdown path, keeping `OptionId` as the submitted value.
- [x] Add Open conversation text as a fixed snapshot in a read-only editor tab.
- [x] Show item times on transcript items.
- [x] Read `account/usage/read` with `threadId` on 0.166 and later, keeping actual USD, estimated USD, and estimated credits distinct.
- [x] Add `/doctor`, which runs `codex doctor --network-only` on local 0.166 connections and shows the procedure on remote connections.
- [x] Add button-operated in-window conversation search across the full paged history, with complete-result publication, cancellation, keyboard-accessible results, and search-state reset when the conversation changes.
- [x] Render conversation, Markdown blocks, generated Plan text, and command output as read-only selectable text. Selection works within each text block; Open conversation text provides whole-conversation selection in the editor.
- [x] Restore Reasoning summaries and Command command/output from history. Preserve secret redaction and the 64 KiB limit, including separator bytes in truncation detection.
- [x] Read user-message `text` content, rebuild Markdown blocks on every history merge, and explain when a saved Reasoning item has no summary. Normalize snapshot line endings to CRLF so the editor does not ask to rewrite a read-only snapshot.
- [x] Guard turn dispatch during connection recovery and bridge replacement. An unavailable Worker RPC is treated as a known not-dispatched request instead of a null reference.
- [x] Share account/model/permission/usage initialization between ordinary connection and recovery. Reproduce the missing catalog read before the fix (0 calls), verify reloading and preserving the selected model after recovery and a new conversation, and log the loaded extension/Worker build and assembly location.
- [x] Reproduce stderr-triggered owner invalidation during `thread/start` and `turn/start`: both regression cases failed with `StaleGeneration` before the fix. Keep optional MCP network errors and WebSocket fallback diagnostics from triggering connection recovery, and persist redacted stderr for diagnosis.
- [x] Stop forwarding raw stderr/HTTP HTML to the transcript; retain redacted diagnostics with HTML removed and a 2,048-character limit, plus one short network/MCP notice. Three send cases failed with duplicate raw transcript errors before the fix and pass afterward. Independent diff review found no remaining actionable defect in this scope.
- [x] Review Plan-mode mode serialization and completion against the running 0.166 turn: the turn completed and returned to Ready at 20:07:45; the license question was final model prose, not an extension lifecycle failure. Plan mode remains read-only.
- [x] Stage 2 review fixes, commit `b3b23e4`: Debug and Release builds had zero warnings and errors. Each configuration reported Core 553 passed and 5 skipped, UI 602 passed and 1 skipped, and no failures. Release VSIX inspection passed. The release gate remains blocked by six required symbolic-link cases because this Windows account lacks link creation privilege. Evidence: `app-server-0166-acceptance-evidence/release-validation.json`.

### Real CLI confirmations

- [x] 0.161.0: `review/start` works in a new empty conversation, and the review runs on that same thread.
- [x] 0.161.0: the history list shows the conversation with an empty name and the review prompt as its preview.

- [x] 0.166.0: the generated schema matches the implementation for `threadUsage.nativeUsageUsdMicros` (nullable, where none means unknown), `account/usage/read.threadId`, `thread/start` (`permissions` and `activePermissionProfile` are experimental only), and `inline` as the only delivery value.
- [x] 0.166.0: two review turns in a new empty conversation completed on that same thread, and the history row matched 0.161.0. The test conversations were deleted.
- [x] 0.166.0: `codex doctor --network-only --no-color --ascii` accepted the flags, needed no TTY, wrote plain text, and exited with 0.
- [x] `verify-contract-surface.ps1` passed for 0.161.0 and 0.166.0 on both surfaces.
- [x] `verify-contract-surface.ps1` passed for 0.165.0, 0.164.0, 0.163.0, and 0.162.1 on both surfaces, using pinned packages with verified hashes. Schema generation completed despite temporary PATH alias access warnings.

- [x] 0.166.0: an isolated CLI with a loopback Responses fixture sent the configured `review_model` for an inline review. With the setting omitted, it sent the conversation model. The fixture recorded the request model; this probe did not measure review completion.

### Maintainer-confirmed Visual Studio acceptance

- [x] Conversation search finds matching content. The maintainer confirmed focus, keyboard operation, Light/Dark, and narrow-width accessibility on 2026-11-02 and supplied search screenshots.
- [x] `/doctor` appears and works in the extension; accepted by the maintainer on 2026-11-02.

- [x] PR #248 records maintainer acceptance of long-history search, generated Plan selection and copy, High Contrast, Narrator, Japanese conversation selection and copy, and restored Command content in the tool window.

- [x] Restore a conversation with a saved non-empty Reasoning summary in Visual Studio; accepted by the maintainer on 2026-11-04 (see #242).
- [x] Verify selection, copy, Ctrl+F, F3, Japanese text, long histories, and file removal on close in the conversation text editor tab; accepted by the maintainer in PR #248.

### Stage 2 review follow-up

- [x] Compare the new conversation's effective approval policy, reviewer, profile, and full sandbox settings before starting the review. Keep the source selected when the settings differ or the source changes during creation.
- [x] Clear conversation-specific usage when the selection changes and refresh it while the usage popup is open.
- [x] Keep the history popup open during scope selection. Escape and Close history dismiss it.
- [x] Skip redirected conversation-text roots and owner directories during automatic cleanup. Cover junctions with a stale external file in the regression test.
- [x] Verify the scope ComboBox in the Visual Studio Experimental Instance; the maintainer confirmed it opens and selects without closing the history flyout in PR #248.

## Stage 3: experimental features

Tracking: [#243](https://github.com/kkamegawa/vsextensionforcodex/issues/243). This stage stays open until the items under Not yet verified are done.

### Implemented and passing automated tests

- [x] Create linked forks on 0.166 and later with `thread/fork.attachToSource` and `excludeTurns`, and list children with `thread/list.attachedToThreadId` without a `sourceKinds` filter. Children are opened with paginated history and stay out of the ordinary history list.
- [x] Navigate from a parent to its related conversations and back. A refused linked fork shows the redacted server reason and leaves the parent selected.
- [x] Show the memory of a joined conversation through `thread/memory/read` in a separate pane on explicit request. A `null` summary shows that no memory is available. Long summaries are truncated without splitting a surrogate pair, and the read is retried only for an explicit thread.
- [x] Switch workspaces with `accountSession/list` and `accountSession/switch` after a confirmation dialog. Local SSO opens only secure URLs through the existing browser path and can be cancelled with `account/login/cancel`. An unknown outcome is confirmed through `accountSession/list` and never retried.
- [x] Add the workspace ID to the account fingerprint. A switch, or an account read that shows another workspace with the same email and plan, retires the owner. Pending operations are cancelled, late results are dropped, and recovery reloads the account, model, permission, and usage catalogs. MCP status has no cache and is read again on the next request.
- [x] Experimental off, or a version below the introduction version, sends none of the linked fork, memory, or workspace requests.
- [x] Workspace names are sanitized once before display.
- [x] Release build with zero warnings and errors; Core 576 passed and 5 skipped, UI 602 passed and 1 skipped, no failures; Release VSIX inspection passed. The skipped cases need symbolic-link creation privilege on this Windows account.

### Not yet verified

- [ ] Check the `attachedToThreadId` query, with no `sourceKinds` filter, against a real 0.166.0 CLI.
- [ ] Check linked forks, the memory pane, and the workspace picker in the Visual Studio Experimental Instance.
- [ ] Real environments for an SSO-required workspace and for multiple workspaces with the same email and plan are not available. Fake coverage passes; these remain unverified in the release record.
