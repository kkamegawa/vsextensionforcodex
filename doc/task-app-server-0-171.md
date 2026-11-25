# Codex CLI 0.169 through 0.171 compatibility validation

[日本語](task-app-server-0-171_ja.md) · [Tracking issue #331](https://github.com/kkamegawa/vsextensionforcodex/issues/331)

## Real CLI initialization

On 2026-11-23, the Release Worker from PR #335 at `004f733` initialized the public Windows x64 CLI binaries over stdio and authenticated loopback WebSocket. Each executable matched the SHA-256 digest of the published GitHub release asset. Each run used an isolated Codex home and workspace. The test recorded the JSON-RPC method names and whether the Worker sent `initialized`.

| CLI | Local stdio | Authenticated WebSocket |
| --- | --- | --- |
| 0.167.2 | Rejected before `initialized` | Rejected before `initialized` |
| 0.168.0 | Rejected before `initialized` | Rejected before `initialized` |
| 0.169.0 | Accepted | Accepted |
| 0.169.1 | Accepted | Accepted |
| 0.170.0 | Accepted | Accepted |
| 0.171.0 | Accepted | Accepted |

Each accepted connection sent `initialized` once. Each rejected connection sent only `initialize`. The 35 focused Core tests passed, including local and remote version boundaries, simulated prerelease precedence, build metadata, the notice for versions newer than the target, and owner isolation.

A fresh signed-out 0.169.0 app-server returned provider inventory, provider-scoped model lists and thread sections. A disposable thread could be created, named, archived and restored through the public requests. The archived list included it. Title search and the restored active list could not be confirmed with a pre-turn fixture.

## Schema contracts

Both stable and experimental schemas generated successfully for 0.169.0, 0.169.1, 0.170.0, and 0.171.0. The 0.169.0 and 0.169.1 schemas were structurally equivalent. Both regression releases matched the expected structural changes to 0.171.0, and all eight generated version and surface combinations passed used-method verification.

The checks used [schema generation](../scripts/generate-schemas.ps1), [schema comparison](../scripts/compare-schemas.ps1), and [method verification](../scripts/verify-contract-surface.ps1).

## Review-fix baseline

The integrated Release build at `004f733` passed with zero warnings and errors. VSIX validation passed for Worker contract v29. The local UI suite had 648 passes and 3 filesystem-link skips. The first local Core run had 707 passes, 7 filesystem-link skips, and 2 owner-boundary failures. Both failing tests passed on a focused rerun. The Ready-notification assertion also failed at the pre-review head `9ff68d5`; the test now waits for the replacement owner's Ready notification before asserting its count. [CI run 395](https://github.com/kkamegawa/vsextensionforcodex/actions/runs/45040239905) passed for the same integrated review revision.

The logout failure was reproduced by holding the old connection response until the replacement sent Ready. Logout completion now reads the retired connection's logout flag. The eight focused owner-boundary tests present at that revision passed with the corrected ordering. At the final PR #337 head, all 14 `WorkerRpcOwnerBoundaryTests` passed.

The full local Core rerun after both owner-boundary fixes finished with 709 passes, seven filesystem-link skips, and zero failures. CI run 398 passed for the same code.

## Visual Studio acceptance

The Light and Dark theme screenshots in the Visual Studio 2026 Experimental Instance remain pending. Enterprise environment acceptance is waived.
