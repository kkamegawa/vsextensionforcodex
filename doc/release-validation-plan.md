# Integrated Release Validation Plan

Status: Approved on 2026-10-09 for follow-up implementation. Baseline: main commit b44e856; Worker contract v21; CLI 0.159.1; regression fixtures 0.155.1. This plan implements the approved design in [release-validation-design.md](release-validation-design.md). No runtime/API change is planned in this documentation phase.

## Work packages

| Package | Work | Gate |
|---|---|---|
| P0 — Evidence map | Map every Issue #156 criterion to existing test, script, package inspection, manual scenario, or remaining test; preserve PR #170 as historical evidence. | Every criterion has an owner, class, and evidence source. |
| P1 — Windows orchestrator | Add a PowerShell 7 entry point for pinned CLI acquisition/hash, four schema surfaces, comparisons/cache/method checks, Debug/Release builds, Core/UI runs, VSIX checks, and result manifest. | A clean Windows run produces all expected evidence and returns nonzero for any required failure. |
| P2 — CI/release result handling | Update both workflows for per-attempt TRX, failed-test-only one retry, sticky flaky failure, and `if: always()` sanitized artifacts. Probe required link capabilities before tests. | Initial and retry outcomes are both preserved; no suite retry hides a failure. |
| P3 — Gap tests | Add only missing race, protocol, path, isolation, or refusal cases found in P0. Run symlink/junction cases under a verified-capable Windows identity and classify every skip. | Every Local-required automated criterion passes; skip reasons are explicit. |
| P4 — External acceptance | Run fixed CLI traffic, live MCP OAuth lifecycle, and real remote TLS/certificate/token-rotation scenarios manually outside public CI. | Pass or explicitly accepted, evidence-backed External limitation. |
| P5 — Experimental Instance | Verify themes, narrow layout, keyboard/focus, UI Automation/Narrator, workflow states, and two same-machine instances; save the required evidence. | Every required visual/accessibility/isolation scenario passes. |
| P6 — Final readiness record | Run the complete matrix at the release candidate commit, review evidence and approved limitations, update `doc/implementation.md` and `doc/task.md`, and record the decision in Issue #156. | No `failed`, `flaky`, or `not-run`; all Local-required passed; External passed or explicitly accepted blocked. |

## Result format

The manifest records commit, host/OS, PowerShell/.NET/Visual Studio versions, CLI version/hash, scenario ID/class/status, command, exit code, pass/fail/skip counts, skip reasons, retry outcomes, artifact paths/hashes, and approved External limitations. Upload sanitized TRX and reports; never upload raw authenticated traffic.

## Commands and platform

Full solution build, UI suite, pinned CLI schema generation, VSIX inspection, link-capability tests, and Experimental Instance acceptance run on Windows with PowerShell 7. Core-only runs on other systems are optional diagnostics; platform skips are not Windows acceptance.

Example Windows PowerShell 7 entry point after implementation:

```powershell
pwsh -NoProfile -File scripts/verify-release.ps1 -Configuration Both -OutputDirectory <EVIDENCE_DIRECTORY>
```

Portable Bash examples may invoke Core-only checks or compare already generated schemas. A Bash example for the full gate is valid only in a Windows-capable Bash environment and must call the same PowerShell entry point; it does not make the Windows CLI, WPF UI suite, or Visual Studio portable to Linux.

## Delivery sequence

1. Build and review the evidence map against `app-server-contract.json`, existing tests, CI, and PR #170 evidence.
2. Implement the orchestrator and manifest, then exercise failure, skip, and retry paths on Windows.
3. Apply the same result rules in CI and Release workflows and verify artifact upload on success and failure.
4. Close Local-required test gaps; run External scenarios with runtime-supplied values and sanitized outputs.
5. Complete Experimental Instance acceptance and record each state.
6. Run the full matrix on the release candidate; update the final readiness record and Issue #156.

Do not treat a design, test definition, off-screen render, or earlier PR result as evidence that a final-candidate scenario passed.
