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

- [x] The owner's Experimental Instance acceptance against the real CLI:
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

## Not completed

- [ ] Recheck the preset delete and the narrow-width header in the Experimental Instance after the fixes.
- [ ] A local diagnostic ZIP save on 0.164.0.

The automated test for the account-change notices is out of scope by agreement.
