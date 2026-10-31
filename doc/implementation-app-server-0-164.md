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
  - A compaction that does not complete reports "Compaction failed." Tracking starts before the request is sent, so a failure notified while the request is pending is still reported.
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

## Experimental Instance acceptance (2026-10-25 to 2026-10-26)

The owner ran this acceptance in a separate session, after the implementation session had been refused Visual Studio access twice and had taken no screenshots of its own. That session drove the Release extension in the Visual Studio Insiders 18.12 Experimental Instance through Computer Use, against the real CLI. For each step it kept a screenshot and a UI Automation text dump, which are stored with the acceptance report outside the repository (`<acceptance-evidence-folder>`). The results below are taken from that report.

**Passed (with screenshots):**
- Connections:
  - a local 0.161.0 connection
  - an authenticated WebSocket connection to 0.164.0
- Model and prompt:
  - selecting the GPT-6 luna model with high reasoning and standard speed
  - a real prompt that generated and built a WinForms solution
- Presets:
  - save, with a real CLI read-back
  - recall without sending
  - edit with the replace confirmation
- Display and accessibility:
  - Dark, Light, and High Contrast themes, including Japanese text and the presets panel
  - keyboard Tab navigation
  - Narrator output
- Diagnostics: a remote diagnostic ZIP saved without logs. It contained only `report.json` and `manifest.json`, and the server path was shown without being opened locally.

**Defects found, and fixed after the run:**
- **Preset delete failed with `missing field 'value'`.** The JSON-RPC connections drop null properties of ordinary objects, so the explicit null never reached `config/batchWrite`.
  - The edit is now built as a `JsonObject`, which keeps the null.
  - Against the real 0.164.0 CLI, with a temporary `CODEX_HOME`, an edit without `value` reproduced the error, and an edit with `"value": null` deleted the slot.
- **A narrow pane (about 480 logical pixels) squeezed the status to "Ready / Sign…".** The right-docked toolbar took the width first.
  - The header now gives the status its own full-width row. The toolbar sits below it, right-aligned, and wraps.
  - A standalone WPF render of the production header with the real view model reproduced the defect in the old layout at 300 and 360 pixels, and passed at 300, 360, 480, and 700 pixels after the change. The renders are in `artifacts/0164-header/before` and `after`.
- **An inherited `CODEX_INTERNAL_ORIGINATOR_OVERRIDE=Codex Desktop` made a supported 0.164.0 server look unsupported.** The originator contained a space, so the first space-separated `userAgent` token had no version.
  - The version is now read from the first `/` to the next whitespace. The design states this rule.
  - The real CLI returned `Codex Desktop/0.164.0 (Windows 10.0.26200; x86_64) ...` under the override.

## Review follow-up: compaction thread isolation (2026-10-26)

Tracking: #230; fixed in #236 and propagated into #237. Compaction start, completion, failure, and delayed RPC results are displayed only in the selected conversation that owns the operation. A background outcome still retires its tracking, and a completed compaction still refreshes usage. An early outcome cannot be overwritten by a later start acknowledgement.

Four regression tests cover background failure/completion, a delayed successful response, and an RPC exception after selecting another conversation. The original failure, completion, and RPC-exception cases failed before the fix. The eight compaction-focused tests now pass, with no skips.

Validation on the combined stack: Debug and Release UI suites each passed 569 tests, with 1 symbolic-link capability skip and no failures. The Release solution build completed with 0 warnings and 0 errors; `Test-VsixContents.ps1 -Configuration Release` passed. Core tests were not rerun for this UI-only fix.

The real view model's `Items` collection was also rendered in a standalone WPF transcript. Both background completion and failure left the selected conversation's text intact, without adding the other conversation's outcome; the PNGs were visually inspected. Local ignored evidence is under `artifacts/compaction-isolation/bin/Debug/net8.0-windows10.0.22621.0/`: `visual-result.json`, `screenshots/completion-thread-B.png`, and `screenshots/failure-thread-B.png`. This standard WPF rendering is not an Experimental Instance or Visual Studio theme check. The 2026-10-31 Experimental Instance follow-up is recorded below.

## 2026-10-31 Experimental Instance follow-up

The owner reran the local acceptance on Windows 11 (`10.0.29695.0`, PowerShell 7.6.5, .NET SDK 10.0.403) with the official Codex CLI 0.164.0 (`F:/temp/issue226-20261031/cli/codex.exe`, SHA-256 `6f267830b029ee25d9339864aa867cd45de9f8fd4c5ea007d2e63c13a0c66d36`). Visual Studio Enterprise 18.11.12328.437, Experimental Instance `18.0_3b4a7eadExp`, loaded extension 0.2.1.0 from the Experimental Instance deployment; the loaded DLL SHA-256 was `DD4BD666EF5C0979E2445591C04AC20AA2CB6AAA9909121BD70098BEEA82C934`. The disposable WinForms solution at `F:/temp/issue226-20261031/WinFormsAcceptance/Issue226Acceptance.slnx` built in Release with 0 warnings and 0 errors.

**Passed:**
- Local `/diagnostics`: the save picker and confirmation showed the local path and contents; logs were off by default. Canceling the picker created no file. Saving produced a ZIP with only `report.json` and `manifest.json` (SHA-256 `586d237af6935df4f1c210db6b39295bc5cbc2a602ee7d1badec9a56a323b4da`); no rollout or log files were present. An existing destination was rejected and the ZIP stayed unchanged.
- Presets: saved a test preset, confirmed its label and input by reading the CLI config, then deleted it with confirmation and confirmed the slot was absent. The temporary CLI configuration was restored to the original backup SHA-256 `6ea2e8c2610200d3b3963391c7ef6cf1cf7cab2eeb42ab584eba8fb5f31fd420`.
- Dark-theme header screenshots at 694 and 479 logical pixels showed the status on its own row and the wrapping toolbar readable without overlap. Screenshots and UI Automation dumps are under `F:/temp/issue226-20261031/screenshots/`.

**Maintainer acceptance (2026-10-31):**
- The 300-pixel clipping defect was fixed by reducing the outer grid minimum width from 360 to 300 logical pixels. The existing narrow-width regression assertion was updated. The post-fix Release solution build completed with zero warnings and errors; Core passed 516 tests with 5 symbolic-link capability skips, and UI passed 569 with 1 dangling-solution symbolic-link skip. Release VSIX inspection passed.
- In the post-fix Experimental Instance, the approximately 300-pixel header was captured in Light with CLI 0.164.0 and Dark with CLI 0.161.0. The maintainer manually checked keyboard operation, accepted the other widths based on the narrow-width check, and accepted High Contrast. No additional post-fix screenshots were requested for those accepted conditions.
- The 2026-10-25/26 Experimental Instance run had already captured Light, Dark, High Contrast, Japanese, keyboard, and Narrator output. The owner marked the remaining visual acceptance complete on 2026-10-31.

The 2026-10-31 screenshots and machine-readable evidence are under `F:/temp/issue226-20261031/`. The temporary WinForms solution is under `F:/temp/issue226-20261031/WinFormsAcceptance/`.

## Acceptance completion (2026-10-31)

All tracked Issue #226 acceptance checks are complete.

- The owner manually verified keyboard operation in the Experimental Instance.
- The owner accepted the remaining pane widths based on the narrow-width checks and accepted High Contrast. No additional post-fix screenshots were requested. Post-fix screenshots record Light at approximately 300 logical pixels with CLI 0.164.0 and Dark at approximately 300 logical pixels with CLI 0.161.0; earlier screenshots cover Light, Dark, High Contrast, Japanese text, keyboard navigation, and Narrator output.
- The post-fix Debug solution build completed with 0 warnings and 0 errors. Debug Core passed 516 tests with 5 symbolic-link capability skips and 0 failures; Debug UI passed 569 tests with 1 dangling-solution symbolic-link skip and 0 failures. The evidence runner reports `blocked` solely because those six capability-dependent checks could not run. Release results are recorded above.
- The account-change notification automated test remains excluded by agreement; owner-change and stale-result coverage is recorded under #230.
