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
