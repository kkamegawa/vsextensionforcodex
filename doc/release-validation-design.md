# Integrated Release Validation Design

Status: Approved on 2026-10-09 for follow-up implementation. Baseline: `b44e856`; Worker contract v21; Codex CLI 0.159.1, with 0.155.1 regression fixtures.

## Purpose

Define reproducible evidence for the integrated App Server update. This design reuses the existing contract, recovery, ownership, interaction, path, and packaging tests. It distinguishes local acceptance from external services that cannot run in public CI.

## Scenario classes and decision

| Class | Scope | Completion |
|---|---|---|
| Local-required | Pinned schemas, Fake App Server, all local tests, local transport/TLS/token-rotation simulations, paths, packages, Experimental Instance, and two instances on one machine | Every required scenario passes. A required skip or blocked result is incomplete. |
| External | Authenticated pinned CLI traffic, live MCP OAuth expiry and reauthentication, and a real remote endpoint/certificate/token rotation | Pass, or a specifically accepted blocked limitation with impact and evidence recorded. |

Record each scenario as `passed`, `failed`, `blocked`, `not-run`, or `flaky`. A release-readiness decision rejects any `failed`, `flaky`, or `not-run` result. External `blocked` is acceptable only with the scenario, reason, unverified risk, substitute evidence, approver, date, and approval reference recorded. Local-required `blocked` is never waived as External.

PR #170 results are historical baseline evidence only. The final candidate requires fresh evidence at its exact revision.

## Automated validation

Use a Windows PowerShell 7 orchestrator to run the pinned CLI hash check, schema generation for 0.155.1/0.159.1 stable/experimental, schema comparisons, used-method/cache checks, Debug and Release builds, Core and UI suites, and VSIX inspection. Keep `CODEX_PATH` limited to build or schema steps that need it; test processes must run without it.

Compare packaged manifest, Worker payload, contract version, DLLs, and embedded XAML with their corresponding build outputs. Save a result manifest containing commit, environment/tool versions, CLI version/hash, scenario class/status, command, exit code, counts, skip reasons, retry outcomes, artifact hashes, and any approved limitation.

CI and release workflows preserve the initial test result and may retry only failed test cases once. A retry pass is `flaky` and fails the gate. Do not rerun the entire suite when an exact data-driven row cannot be isolated or the test host exits abnormally; record the failure and retain both diagnostics. Upload sanitized TRX, schema reports, hashes, and the manifest with `if: always()`. Never publish raw authentication or protocol logs.

Symlink/junction setup probes creation in a dedicated temporary directory under the actual test identity. If a required link cannot be created, setup fails with the capability reason. Skip results are recorded by test and reason. The six skips recorded for PR #170 are not assigned a cause without their TRX evidence.

## External scenarios

Run live authenticated CLI, real MCP OAuth expiry/reauthentication, and real remote TLS/certificate/token-rotation scenarios manually outside public CI. Use a dedicated workspace and endpoints supplied at run time. Store only a redacted summary and result manifest; never store raw credentials, tokens, or unredacted traffic. Local Fake, loopback, and TLS simulations remain Local-required and do not replace these scenarios.

## UI and instance acceptance

Use the SDK-owned F5/Experimental Instance deployment workflow and the existing duplicate-identity guard. On Windows, inspect Light, Dark, High Contrast, narrow layouts, connection/recovery/history/draft states, question/permission/MCP cards, artifacts, shell confirmation, and Windows setup. Verify keyboard flow, focus order, accessible names, Narrator/Accessibility Insights announcements, and long-history behavior. Run two Experimental Instances on the same machine to verify conversation, draft, catalog, authentication, and approval isolation.

Capture screenshots tied to scenario ID, environment, expected result, and pass/fail. Keyboard and screen-reader checks also require recorded steps and observations. Off-screen WPF renders are supplementary evidence only. If the Experimental Instance is unavailable, Local-required visual acceptance stays incomplete.

## Platform boundary

Full release validation runs on Windows with PowerShell 7: the pinned Codex asset, schema generation, WPF UI tests, VSIX packaging, and Experimental Instance are Windows-bound. Core/Worker target `net8.0` and may run elsewhere, but platform-specific cases can be inconclusive there; such a run does not replace Windows acceptance. Bash examples may cover portable commands or syntax in a Windows-capable Bash environment and must not imply Linux can run the pinned Windows CLI or WPF UI suite.

## Out of scope

This phase adds no public RPC or DTO. It defines evidence collection and release criteria; implementation of the orchestrator, workflow changes, and any uncovered test cases is tracked as follow-up work.
