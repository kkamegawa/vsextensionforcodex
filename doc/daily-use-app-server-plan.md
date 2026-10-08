# Daily-use App Server Features — Implementation Plan

Date: 2026-10-08
Tracking: [Issue #155](https://github.com/kkamegawa/vsextensionforcodex/issues/155), under [Issue #149](https://github.com/kkamegawa/vsextensionforcodex/issues/149).
Design: [approved design](daily-use-app-server-design.md) and [Japanese translation](daily-use-app-server-design_ja.md).
Status: Approved plan; implementation and verification evidence are tracked in `task.md` and `implementation.md`.

## Baseline and execution rules

Target CLI 0.159.1 stable and retain 0.155.1 regression fixtures. Start from baseline commit `69deda1` (PR #169 merged), Worker contract v20; allocate the next available contract version at merge time. Do not upgrade CLI, SDK, runtime, or packages. Reuse the foundations from #152, #153, and #154.

Implement packages in dependency order. At each checkpoint, preserve the approved design's bounds and trust boundaries. Integrate with concurrent source changes without reverting them. Keep Issue #155 open until implementation evidence is recorded. Issue #156 remains the release gate.

## Package sequence

| Package | Implementation scope | Dependencies | Checkpoint |
|---|---|---|---|
| P0 | Register used methods as stable or experimental-described; update contract version; pin 0.159.1/0.155.1 schema fixtures; add typed DTOs and payload registry. | — | Contract/schema tests establish the wire boundary; experimental plan delta is optional. |
| P1 | Plan delta/final reconciliation; bounded thread/config/model/MCP notices; catalog projection and modality validation in composer and Worker; additive 0.159.1 fields. | P0 | Fixture tests prove completed state authority, notice bounds, owner races, and no unsupported turn input. |
| P2 | Typed bounded content parts; distinct truncation metadata; PNG/JPEG preview lifecycle; mapped changed-file Open/Reveal with action-time validation. | P0, P1 envelope | Tests cover mixed results, malformed/oversized images, cleanup, and path replacement/escape between display and action. |
| P3 | `relaycodex.file.v1` payload reader/writer; explicit attachment add/remove over the existing bounded store; delivery outcome tracking and read-only reconciliation. | P2 | Tests cover created/existing, absent removal, malformed/unknown payloads, stale pages, missing targets, and uncertain delivery without retry. |
| P4 | Exact `/shell` parsing; target/command/cwd confirmation; local approval policy; `thread/shellCommand` dispatch; per-thread pending lock and independent deadlines. | P0, P1 envelope | Tests cover grammar, timeout ranges, denial, pre-dispatch cancel, other-client turns, acknowledgement uncertainty, no Stop, and zero replay. |
| P5 | Local Windows sandbox setup state machine, mode/cwd confirmation, generation-scoped completion and bounded redacted errors. | P0 | Tests cover response/completion order, `started` semantics, unsupported method, cwd containment, stale generations, and remote refusal. |
| P6 | Cross-package integration, sub-agent review, full validation, VSIX inspection, and Experimental Instance visual/accessibility review with screenshots. | P1–P5 | All required automated, build, package, and visual evidence is recorded; absent screenshots remain incomplete. |

The implementation may parallelize independent packages after their dependencies are ready. P1 is the shared envelope used by P2 and P4; do not duplicate or fork its owner/generation, catalog, or bounded-notice rules.

## Verification sequence

1. Run package-focused Fake App Server tests against pinned 0.159.1 and 0.155.1 fixtures, including the edge cases listed in the approved design.
2. Run the full Core and UI test suites after focused tests pass.
3. Build Debug and Release with `TreatWarningsAsErrors=true`; both must report zero warnings and zero errors.
4. Verify pinned schema comparisons, contract used-method registration, DTO/XAML integrity, and VSIX contents.
5. Run the installed extension in a Visual Studio Experimental Instance. Capture and inspect Light, Dark, High Contrast, narrow width, keyboard/focus, and accessibility states. Record pass/fail and screenshot evidence; if the instance or screenshot is unavailable, leave visual acceptance pending.
6. Record actual counts, commands, results, accepted limitations, and evidence references in `implementation.md` and `task.md`. Do not infer a pass from source inspection or from another package's result.

## Completion criteria

Complete every P0–P6 checkpoint; satisfy the approved design's bounded-input, ownership, generation, policy, path, and no-replay invariants; pass automated, build, contract, and package checks; and record the required visual evidence. Unavailable visual evidence remains an open criterion. Issue #155 does not close the Issue #156 release gate.
