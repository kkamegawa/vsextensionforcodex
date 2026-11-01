# Codex App Server 0.166.0 Implementation Record

Tracking: [#241](https://github.com/kkamegawa/vsextensionforcodex/issues/241). Task checklist: [English](task-app-server-0-166.md) · [日本語](task-app-server-0-166_ja.md). Japanese implementation record: [日本語](implementation-app-server-0-166_ja.md).

## Contract and CLI packages

`app-server-contract.json` is schema v4. It targets 0.166.0 and lists 0.165.0, 0.164.0, 0.163.0, 0.162.1, and 0.161.0 as regressions. Each release entry pins the complete Windows package asset and package/executable SHA-256. The installer extracts that package into a staging directory, rejects unsafe paths and link entries, reads its `codex-package.json` entrypoint, validates the pinned hashes, and only then publishes the files, instead of relying on a fixed helper-file list. The manifest validator passed.

The official complete packages for all six versions were downloaded and validated. The real CLI initialization smoke test passed on all six. Stable and experimental schemas were generated for each version; all ten regression-to-0.166.0 comparisons and all twelve method-surface checks passed. Per-version schema paths not adopted by the host remain recorded under `knownDifferences`.

## Compatibility and session behavior

Feature tiers 0.165 and 0.166 are selected by comparing the installed CLI version with each feature's introduction version. Worker contract v27 replaces `ReviewDelivery` with `ReviewPlacement`; review requests no longer send `detached`.

Item `startedAtMs` and `completedAtMs` values are projected with entry timestamps as fallback. Provider JSON-RPC errors retain structured codes, including `policyCapacityUnavailable` from notifications and history. Unknown codes display as ordinary errors after bounded formatting and secret redaction. When a provider rejects `thread/resume`, the extension can still open read-only history with `thread/read`; active-thread recovery keeps interrupt and goal pause/clear available.

The pinned 0.166.0 CLI was left idle after its thread subscription was released. It sent `thread/closed` after approximately 60.3 seconds, and the smoke scenario resumed that same thread ID. The test-created local thread was deleted afterwards.

## Verification evidence

- Debug and Release solution builds completed with zero warnings and zero errors.
- Debug and Release Core/UI evidence runs passed with no retry required.
- Release VSIX inspection passed, including the embedded Worker contract v27 check.
- The contract manifest validator passed. Stable and experimental schema generation, regression comparison, and method-surface checks passed for all six pinned versions.
- Complete-package install and real CLI `initialize` checks passed for all six versions. The 0.166.0 idle-unload/resume scenario passed.
- `git diff --check` passed.

Evidence files are under `D:\temp\issue241-verify-2\`, including the six pinned CLI install logs, initialization logs, schema output, Debug/Release test manifests, and VSIX inspection report. The first integrated release-validation attempt ran before the final manifest and protocol-project version fixes and is not treated as final evidence; the relevant scenarios were rerun after those fixes as listed above.

## Visual acceptance limit

The changed behavior includes a Visual Studio UI state. This session attempted to inspect the Visual Studio surface with Computer Use, but `sky.list_apps()` timed out twice. The skill's recovery instructions required stopping after that retry, so no screenshot-based display judgment was made. Automated UI tests passed; maintainer visual acceptance remains pending.

## Stage 2: stable features

Tracking: [#242](https://github.com/kkamegawa/vsextensionforcodex/issues/242). The Worker contract stays at v27, as the design defines. Open items are listed in the [task record](task-app-server-0-166.md).

- **Review:** `ReviewPlacement.NewThread` runs in `CodexSessionService.Review.cs`. Settings come from the active conversation's effective state, and an unmappable value stops before `thread/start`. The named profile field exists only on the experimental surface, so the stable surface sends `sandbox`. The response is compared with the source, and a different report keeps the conversation without starting the review. `ActiveThreadId` moves only after `review/start` succeeds. The result separates Started, NotCreated, CreatedNotStarted, and Unknown, and nothing is resent.
- **History scope:** `GitWorktreeScope` runs read-only Git commands with a timeout, and any failure hides the scope. The folder set is fixed when a scope is chosen, a scope change discards the cursor, and a late response is dropped by generation. A filtered list never cancels queued commands as missing, and the open conversation keeps its row.
- **Questions:** question text and option descriptions use `ToSafeTextAndBlocks`. `AppendUserInputResultItem` shows a submitted answer only as a fixed confirmation, and a synthetic reply is sent as a user message through the existing `ToSafeText` path, so no answer needs its own rendering.
- **Conversation text:** the whole history is paged into memory first, so a failure or cancellation opens nothing. `ConversationTextStore` writes a generated file name under an owner-separated temporary folder. Closing the tab deletes the file, and startup removes unused files older than 24 hours.
- **Usage:** values stay `long?` micro-units. Limits may be cached for 60 seconds, but a different conversation always rereads its amounts.
- **Diagnostics:** `BoundedProcessRunner` starts `/doctor` with no shell, bounded output, and a 60-second timeout that kills the process tree. `--no-color --ascii` keeps terminal codes out, and output passes through `ISecretRedactor`.

Real CLI probes ran on 0.161.0 and 0.166.0 in a throwaway repository, and their test conversations were deleted. The 0.161.0 turn was stopped after 150 seconds, so that result covers the start and the history row only. Both 0.166.0 turns completed. The 0.166.0 CLI was installed with `scripts/install-codex.ps1`, which verified the pinned hashes. The protocol does not report which model a review turn used, so `review_model` precedence could not be observed. The Release VSIX inspection result is recorded in `doc/task.md`.
