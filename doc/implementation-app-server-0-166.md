# Codex App Server 0.166.0 Implementation Record

Tracking: [#241](https://github.com/kkamegawa/vsextensionforcodex/issues/241). Task checklist: [English](task-app-server-0-166.md) · [日本語](task-app-server-0-166_ja.md). Japanese implementation record: [日本語](implementation-app-server-0-166_ja.md).

## Contract and CLI packages

`app-server-contract.json` is schema v4. It targets 0.166.0 and lists 0.165.0, 0.164.0, 0.163.0, 0.162.1, and 0.161.0 as regressions. Each release entry pins the complete Windows package asset and package/executable SHA-256. The installer extracts that package, reads its `codex-package.json` entrypoint, and validates the pinned hashes instead of relying on a fixed helper-file list. The manifest validator passed.

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
