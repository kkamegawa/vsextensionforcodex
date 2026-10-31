# Codex App Server 0.166.0 Implementation Plan

[日本語](app-server-0-166-plan_ja.md)

Tracking: [Issue #240](https://github.com/kkamegawa/vsextensionforcodex/issues/240). Approved on 2026-10-31.

## Outcome

This plan delivers [app-server-0-166-design.md](app-server-0-166-design.md) under [ADR-020](adr/ADR-020-app-server-0-166.md) and the ADR-011 amendment of 2026-10-31:

- Target 0.166.0, with 0.161.0 as the minimum supported version and 0.165.0, 0.164.0, 0.163.0, 0.162.1, and 0.161.0 as regression baselines.
- Compare each feature tier with its own introduction version.
- Keep the experimental API opt-in.
- Make every stage releasable on its own.

## Work sequence

| Stage | Work | Tracking |
| --- | --- | --- |
| 1. Compatibility | Raise the manifest to schema v4 with the complete package asset and hash. Confirm the package for 0.162.1, 0.163.0, and 0.165.0. Move the install helper and real verification to the complete package. Compare stable and experimental schemas for the six versions. Add tiers 0.165 and 0.166 and fix `GetFeatureTier`. Raise the Worker contract to v27, replace `ReviewDelivery` with `ReviewPlacement`, and remove `Detached`. Project item times, structured provider errors, and `policyCapacityUnavailable`. Open read-only history on provider rejection. Verify the 60-second unload. | [#241](https://github.com/kkamegawa/vsextensionforcodex/issues/241) |
| 2. Stable features | Add `/review --new-thread`, the history scopes, question Markdown, read-only conversation text, USD usage, and network diagnostics. | [#242](https://github.com/kkamegawa/vsextensionforcodex/issues/242) |
| 3. Experimental features | Add linked forks with parent and child navigation, the memory view, and workspace switching with SSO, the confirmation dialog, and the fingerprint change. | [#243](https://github.com/kkamegawa/vsextensionforcodex/issues/243) |
| 4. Integration and records | Exercise all versions with Fake and real App Servers, take Experimental Instance screenshots, inspect the VSIX, and update the implementation and task records with evidence. | [#244](https://github.com/kkamegawa/vsextensionforcodex/issues/244) |

Representative files:

- `app-server-contract.json`, `scripts/install-codex.ps1`, `scripts/validate-contract-manifest.ps1`, `scripts/verify-release.ps1`, and `doc/codex-cli-setup*.md`
- `src/Codex.VisualStudio.Contracts/CodexVersionContracts.cs` and `WorkerContracts.cs`
- `src/Codex.AppServer.Protocol/TransportPolicies.cs`
- `src/Codex.VisualStudio.Worker/CodexSessionService*.cs` and `WorkerRpcService.cs`
- `src/Codex.VisualStudio.Extension/ChatViewModel.cs`, `SlashCommands.cs`, `WorkerBridge.cs`, and the Remote UI XAML
- `src/Codex.AppServer.Fake/Program.cs`

Existing code to reuse:

- Version parsing and ordering in `CodexCliVersion`.
- `ErrorData` in `JsonRpcMessages.cs` and its 16 KiB limit.
- The owner-snapshot checks: `CaptureOwnerSnapshot` and `IsCurrentOwner`.
- The fork flow in `ExecuteForkAsync`, where selection loads read-only history and joining stays explicit.
- `ToSafeTextAndBlocks` for question Markdown.
- `ArtifactPreviewCache` owner separation and the `OpenDocumentAsync` path in `ArtifactFileActions.cs`.
- The existing safe browser path for login URLs.

`StartThreadAsync` is not reused for separate-conversation review, because it changes `ActiveThreadId` at once and sends only the working directory.

## Real CLI confirmations

These behaviors are confirmed on a real CLI in the named stage. If one differs from the design, the design is updated before implementation continues.

| Stage | Confirmation |
| --- | --- |
| 1 | Whether the complete package exists for 0.162.1, 0.163.0, and 0.165.0 |
| 2 | How a separate-conversation review appears in the history list, including its title |
| 2 | Whether `review_model` takes precedence over the inherited model |
| 2 | Whether `review/start` works in a new empty conversation on 0.161.0 |
| 3 | Whether `thread/list.attachedToThreadId` works together with `sourceKinds` |

## Acceptance checks

- **Contract:**
  - The scripts validate all six versions on both surfaces and report each regression difference with its version.
  - Manifest validation fails when the package asset or hash is missing.
  - The Worker constants match the manifest.
- **Version tiers:**
  - 0.164.0 stays in the 0.164 tier, and 0.165.0 gets the 0.165 tier.
  - `0.166.0-alpha` gets the 0.165 tier.
  - A version newer than the target gets the 0.166 tier.
  - Experimental off sends no experimental request.
- **Compatibility:**
  - No code path can send `detached`.
  - Item times, provider errors, and `policyCapacityUnavailable` are projected in live notifications and in replay.
  - Provider rejection opens read-only history and leaves interrupt and goal pause or clear available.
  - `thread/closed` after the 60-second unload leaves the conversation resumable.
- **Review:**
  - Inherited settings never widen permissions.
  - A setting that cannot be inherited stops before a conversation exists.
  - Failure, disconnect, and unknown outcome behave as designed, with no automatic resend.
  - The original conversation stays selected until the review has started.
- **History:**
  - Worktree scope handles multiple worktrees, subfolders, no Git, path normalization, late responses, and paging.
  - Remote connections do not show it.
- **Questions and text:**
  - Markdown renders safely, and submitted values stay `OptionId`.
  - Conversation text supports selection, copy, search, Japanese text, and long histories.
  - Refresh creates a separate file, closing deletes it, and startup cleanup removes old files.
- **Usage and diagnostics:**
  - Actual USD, estimated USD, estimated credits, zero, and missing values display correctly.
  - Diagnostics apply `ISecretRedactor` and the timeout.
- **Linked forks and memory:**
  - Unsupported storage is rejected with a reason.
  - Children stay out of the normal list, and subagents stay distinct from linked forks.
  - A `null` summary shows no memory available.
- **Workspace switching:**
  - A switch between workspaces with the same email and plan updates the owner boundary.
  - The confirmation dialog, SSO and cancellation, failure, and unknown outcome behave as designed.
  - Late responses and old queues are dropped.
- **Quality:**
  - Core, UI, and Fake test suites and the Debug and Release builds complete with zero warnings.
  - The VSIX inspection passes.
  - Experimental Instance screenshots validate each theme, a narrow width, Japanese text, keyboard operation, and screen-reader output.
- **Records:** the English and Japanese design, plan, ADR, implementation, and task records use UTF-8 with BOM. Issue checklists reflect only verified scenarios.

## Verification evidence

After each stage, record the exact commands, configurations, totals, skips, versions, asset hashes, and screenshot paths in `doc/task.md` and `doc/implementation.md`. A stage 1 or stage 2 check that is blocked or skipped keeps that stage open. Stage 3 environments named in the design's real environment boundary may close on Fake coverage, with the unverified state in the release record.
