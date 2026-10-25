# Codex App Server 0.164.0 Implementation Record

Tracking: [#226](https://github.com/kkamegawa/vsextensionforcodex/issues/226) and its sub-issues [#227](https://github.com/kkamegawa/vsextensionforcodex/issues/227)–[#231](https://github.com/kkamegawa/vsextensionforcodex/issues/231). Approved design and plan: [design](app-server-0-164-design.md), [plan](app-server-0-164-plan.md), [ADR-019](adr/ADR-019-app-server-0-164.md). Japanese record: [日本語](implementation-app-server-0-164_ja.md).

## Delivered

The work is delivered as stacked pull requests, merged bottom up: design #232, contract #233, version gate and Worker #234, input and presets #235, state and diagnostics #236, and this verification record.

- **Contract (#227).**
  - The contract manifest uses schema version 3, with `minimumVersion` 0.161.0, `targetVersion` 0.164.0, and the regression versions 0.163.0, 0.162.1, and 0.161.0.
  - It records per-version release hashes, method tables, and known differences. 0.159.1 was removed from the manifest, CI, the Fake App Server, and the setup documents.
  - `scripts/validate-contract-manifest.ps1` validates the manifest and generates the CI version matrix.
- **Version gate and Worker (#228).**
  - The Worker reads the server version from the first `initialize` `userAgent` token and compares versions by Semantic Versioning 2.0 precedence.
  - Unknown versions and versions below 0.161.0 are rejected before `initialized`, with the non-retryable `UnsupportedServerVersion` failure.
  - The connection status carries the feature tier: Baseline, Tier163, or Tier164.
  - Worker contract v26 adds presets (`config/read` layers and `config/batchWrite`), `feedback/save`, MCP runtime details, generated-image references, and authoritative resume settings.
  - The 0.159.1 fallbacks were removed.
- **Input and presets (#229).**
  - `/model [model] [--reasoning <level>] [--speed <tier>]` parses with Codex CLI 0.164.0 parity, using a port of shlex 1.3.0.
  - The command is resolved as a whole before anything is applied, and `standard` is sent as `default`.
  - A pending `/permissions` confirmation holds the slash-command queue.
  - `/presets` and a composer button open the presets panel. It applies the CLI edit and delete rules, asks for confirmation, keeps the editor on conflicts, and never sends on recall.
- **State, MCP, diagnostics, and compaction (#230).**
  - A 0.164 resume applies the saved model, effort, and tier together. A null effort means the model default, and a null tier means normal speed.
  - After an owner change, the model selection is kept when the new catalog offers it.
  - `/mcp` shows authentication, runtime state, and tool discovery separately.
  - `/diagnostics` saves a diagnostic ZIP through `feedback/save`.
  - A compaction that does not complete reports "Compaction failed; the conversation history is unchanged."
  - On older tiers, a resume failure adds a note that CLI 0.164.0 resumes compressed histories.

## Verification

Verified on 2026-10-25 on Windows 11 (10.0.26200) x64 with .NET SDK 10.0.402 and PowerShell 7.6.7. Official release executables, each matching the manifest hash:

| Version | SHA-256 |
| --- | --- |
| 0.164.0 | `6f267830b029ee25d9339864aa867cd45de9f8fd4c5ea007d2e63c13a0c66d36` |
| 0.163.0 | `2692c8c7bb2cf8b0aefe638741fc90236f4b824c59578ce84994f9de58200993` |
| 0.162.1 | `dd13bdb162a184fa04039488b234a8258ef9b76f84e926c505d5742b19249a6a` |
| 0.161.0 | `a0f89acca07a511734cac7fddee26b2106fab68918717bc90df16104a0760a30` |

| Check | Result |
| --- | --- |
| Manifest validation | `validate-contract-manifest.ps1` passed: minimum 0.161.0, target 0.164.0, regression versions 0.163.0, 0.162.1, and 0.161.0. |
| Schema generation and used-method surfaces | `generate-schemas.ps1` and `verify-contract-surface.ps1` passed for all four versions on the stable and experimental surfaces. |
| Schema comparisons | `compare-schemas.ps1` passed for each regression version against 0.164.0 on both surfaces (six comparisons). |
| Real CLI startup | `smoke-app-server.ps1` completed `initialize` with 0.164.0, 0.163.0, 0.162.1, and 0.161.0. |
| MXC on 0.164.0 | Startup with `-c windows.allow_mxc=false` completed `initialize`. The test machine's user configuration sets `windows.sandbox = "mxc"`, so `config/read` returned the CLI's conflict error: `windows.sandbox = "mxc"` is not allowed when `windows.allow_mxc = false`. This is the explicit conflict case of the 0.163 design. Normal startup with the MXC configuration ran the real turns below. |
| Real compaction and resume on 0.164.0 | Signed in with ChatGPT, a real turn completed. `thread/compact/start` ended with a completed `contextCompaction` item and a completed turn. After the server process was stopped and started again, `thread/resume` of the compacted conversation succeeded and returned the saved `reasoningEffort` with a null `serviceTier`. A following turn completed. |
| Core tests | Debug and Release: 508 passed, 5 skipped, 0 failed. |
| UI tests | Debug and Release: 558 passed, 1 skipped, 0 failed. |
| Full solution builds | Debug and Release: 0 warnings, 0 errors. |
| Release VSIX inspection | `Test-VsixContents.ps1` passed. Payload hashes and the embedded XAML matched, and Worker contract v26 was confirmed. |

The test-suite evidence script (`Invoke-TestSuitesWithEvidence.ps1`) reported `blocked` in both configurations, with no failures. The six skips are tests that need symbolic links, which this machine cannot create without Developer Mode or elevation. They are reported as skips, not passes. During one Release run before the final run, the Core test `FakeAppServerExercisesConcurrentInteractionsAndAuthenticationRecoveryOverStdio` timed out under load. Its standalone and full reruns passed.

## Remaining acceptance

- **Experimental Instance.** No screenshots were captured, because access to Visual Studio for UI automation was not granted in this session. No host-level UI pass is claimed for:
  - the presets panel, `/model`, and `/diagnostics`
  - the supported themes, narrow widths, Japanese text, keyboard operation, and screen-reader output
  - the extension's own connection to a real CLI
- **Account change.** The account-change notices have no automated test, because the fake bridge cannot replace the owner mid-session.
- **Real CLI coverage.** The real CLI runs used direct JSON-RPC from a validation script, not the extension. Remote presets, remote diagnostic saves, and the real CLI preset round-trip were not exercised.
