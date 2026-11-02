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
- [x] Add Alt+Shift+F in-window conversation search across the full paged history, with complete-result publication, cancellation, keyboard-accessible results, and search-state reset when the conversation changes.
- [x] Guard turn dispatch during connection recovery and bridge replacement. An unavailable Worker RPC is treated as a known not-dispatched request instead of a null reference.
- [x] Share account/model/permission/usage initialization between ordinary connection and recovery. Reproduce the missing catalog read before the fix (0 calls), verify reloading and preserving the selected model after recovery and a new conversation, and log the loaded extension/Worker build and assembly location.
- [x] Reproduce stderr-triggered owner invalidation during `thread/start` and `turn/start`: both regression cases failed with `StaleGeneration` before the fix. Keep optional MCP network errors and WebSocket fallback diagnostics from triggering connection recovery, and persist redacted stderr for diagnosis.
- [x] Stop forwarding raw stderr/HTTP HTML to the transcript; retain redacted diagnostics with HTML removed and a 2,048-character limit, plus one short network/MCP notice. Three send cases failed with duplicate raw transcript errors before the fix and pass afterward. Independent diff review found no remaining actionable defect in this scope.
- [x] Review Plan-mode mode serialization and completion against the running 0.166 turn: the turn completed and returned to Ready at 20:07:45; the license question was final model prose, not an extension lifecycle failure. Plan mode remains read-only.
- [x] Latest Release verification: Core tests (549 passed, 5 skipped), UI tests (596 passed, 1 skipped), solution build (0 warnings and errors), and VSIX content inspection.

### Real CLI confirmations

- [x] 0.161.0: `review/start` works in a new empty conversation, and the review runs on that same thread.
- [x] 0.161.0: the history list shows the conversation with an empty name and the review prompt as its preview.

- [x] 0.166.0: the generated schema matches the implementation for `threadUsage.nativeUsageUsdMicros` (nullable, where none means unknown), `account/usage/read.threadId`, `thread/start` (`permissions` and `activePermissionProfile` are experimental only), and `inline` as the only delivery value.
- [x] 0.166.0: two review turns in a new empty conversation completed on that same thread, and the history row matched 0.161.0. The test conversations were deleted.
- [x] 0.166.0: `codex doctor --network-only --no-color --ascii` accepted the flags, needed no TTY, wrote plain text, and exited with 0.
- [x] `verify-contract-surface.ps1` passed for 0.161.0 and 0.166.0 on both surfaces.
- [x] `verify-contract-surface.ps1` passed for 0.165.0, 0.164.0, 0.163.0, and 0.162.1 on both surfaces, using pinned packages with verified hashes. Schema generation completed despite temporary PATH alias access warnings.

### Not observable

- `review_model` precedence: setting it through `thread/start.config` changed nothing the protocol reports. No notification or `thread/read` field names the model a review turn used, so the precedence stays unknown, and the extension does not depend on it.

### Not yet verified

- [ ] Verify conversation search in Visual Studio: Alt+Shift+F focus, keyboard navigation, long histories, theme contrast, and the result panel at narrow widths. The automated tests and VSIX inspection do not replace this visual check.
- [ ] Run `/doctor` from the extension in Visual Studio.
- [ ] Capture and judge Experimental Instance screenshots for Light, Dark, High Contrast, Japanese, narrow width, keyboard, and Narrator. This includes selection, copy, Ctrl+F, and F3 in the conversation text tab, and its removal on close. This session had no current Experimental deployment and click-only Visual Studio access.
