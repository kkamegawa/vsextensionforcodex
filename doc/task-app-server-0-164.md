# Codex App Server 0.164.0 Task Record

Tracking: [#226](https://github.com/kkamegawa/vsextensionforcodex/issues/226), [#227](https://github.com/kkamegawa/vsextensionforcodex/issues/227)–[#231](https://github.com/kkamegawa/vsextensionforcodex/issues/231). Detailed implementation evidence: [implementation record](implementation-app-server-0-164.md). Japanese record: [日本語](task-app-server-0-164_ja.md).

## Completed

- [x] Design, plan, ADR-019, and amendments to ADR-011 and ADR-018, in English and Japanese (#232).
- [x] Contract manifest v3 with the minimum version, the 0.164.0 target, and the 0.163.0, 0.162.1, and 0.161.0 regression versions; manifest validation and the CI matrix; 0.159.1 removed (#233).
- [x] Semantic Versioning determination from `userAgent`, non-retryable rejection below 0.161.0, the feature tier, and Worker contract v26 paths (#234). The VSIX inspection now expects contract v26.
- [x] `/model` arguments with CLI parity, the `/permissions` queue hold, and the presets panel (#235).
- [x] Resume settings, account-change selection notices, the MCP three-part status, `/diagnostics`, and compaction outcomes (#236).
- [x] Contract scripts for all four versions on both surfaces, six schema comparisons, and real CLI `initialize` for all four versions.
- [x] MXC startup with 0.164.0, and a real 0.164.0 compaction followed by a resume after a restart.
- [x] Core and UI suites and the Debug and Release builds, with 0 warnings; Release VSIX inspection.

- [x] The owner's Experimental Instance acceptance against the real CLI, run in a separate Computer Use session with a screenshot for each step (stored outside the repository):
  - connections, model selection, and a real prompt
  - preset save with a CLI read-back, recall, and edit
  - Dark, Light, and High Contrast themes
  - Japanese text, keyboard operation, and Narrator output
  - a remote diagnostic ZIP save
- [x] Acceptance defects fixed:
  - Preset delete now sends an explicit null value (#234).
  - A `userAgent` whose originator contains spaces is read correctly (#234).
  - The header keeps the status on its own row above a wrapping toolbar (#237).
  - The delete fix was verified against the real CLI. The header fix was verified by a standalone WPF render at four widths.

- [x] Review follow-up for #230: isolate compaction status and transcript updates to the selected operation thread, while retaining background tracking cleanup and usage refresh. Added four regressions; eight compaction-focused tests pass. Debug/Release UI: 569 passed, 1 symbolic-link skip each. Release solution build: 0 warnings/errors; VSIX inspection passed. Standalone WPF transcript renders were inspected; see the implementation record for local evidence and its limits.

## 2026-10-31 acceptance follow-up

- [x] Local diagnostic ZIP on CLI 0.164.0: default without logs, cancel produced no file, new save contained only `report.json` and `manifest.json`, existing-path rejection left the ZIP unchanged. Artifact: `F:/temp/issue226-20261031/local-diagnostics.zip` (SHA-256 `586d237af6935df4f1c210db6b39295bc5cbc2a602ee7d1badec9a56a323b4da`).
- [x] Preset save and delete with confirmation and CLI config read-back. After deletion, the temporary configuration matched the saved original SHA-256 `6ea2e8c2610200d3b3963391c7ef6cf1cf7cab2eeb42ab584eba8fb5f31fd420`.
- [x] The Enterprise Experimental Instance loaded extension 0.2.1.0 with SHA-256 `DD4BD666EF5C0979E2445591C04AC20AA2CB6AAA9909121BD70098BEEA82C934`. Dark header screenshots at 694 and 479 logical pixels passed.

## Maintainer acceptance

- [x] The 300-pixel clipping was fixed by reducing the outer tool-window minimum width to 300 and updating the existing narrow-width regression assertion. Debug and Release solution builds completed with 0 warnings and 0 errors; both configurations' Core suites had 516 passed, 5 symbolic-link capability skips, and 0 failed, and UI suites had 569 passed, 1 dangling-solution symbolic-link skip, and 0 failed. The evidence runner reports `blocked` solely because the six capability-dependent checks cannot run in this environment. Release VSIX inspection passed.
- [x] Post-fix Experimental Instance screenshots show the wrapped header at approximately 300 pixels in Light with CLI 0.164.0 and Dark with CLI 0.161.0. The maintainer manually verified keyboard operation, accepted the other requested widths based on the narrow-width check, and accepted High Contrast without requiring separate post-fix screenshots.
- [x] The owner accepted all remaining #231 visual checks on 2026-10-31. The additional acceptance judgment is recorded here so it is distinct from the captured screenshots.

Evidence and screenshots are under `F:/temp/issue226-20261031/`. The CLI configuration was restored after preset deletion. The temporary test solution is under `F:/temp/issue226-20261031/WinFormsAcceptance/`.

The automated test for account-change notices remains out of scope by agreement; owner-change and stale-result behavior has separate #230 coverage.
