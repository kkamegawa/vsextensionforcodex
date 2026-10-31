# Codex App Server 0.166.0 Support Design

[日本語](app-server-0-166-design_ja.md)

Tracking: [Issue #240](https://github.com/kkamegawa/vsextensionforcodex/issues/240) and child Issues [#241](https://github.com/kkamegawa/vsextensionforcodex/issues/241), [#242](https://github.com/kkamegawa/vsextensionforcodex/issues/242), [#243](https://github.com/kkamegawa/vsextensionforcodex/issues/243), [#244](https://github.com/kkamegawa/vsextensionforcodex/issues/244). Approved on 2026-10-31.

## Purpose

Verify the Visual Studio extension against Codex App Server 0.166.0 and add the features from 0.165 and 0.166 that are useful in Visual Studio. The minimum supported version stays 0.161.0. Work ships in stages, so compatibility support never waits for feature work.

Decisions are recorded in [ADR-020](adr/ADR-020-app-server-0-166.md) and the ADR-011 amendment of the same date. Primary sources are the upstream releases `rust-v0.164.0`, `rust-v0.165.0`, and `rust-v0.166.0`.

## 1. Scope and supported versions

### Adopted changes

| Introduced | Change | Treatment | Stage |
| --- | --- | --- | --- |
| 0.164 | Existing support | Kept and covered by regression | 1 |
| 0.161 | `thread/list.cwd` accepts an array | Used for the worktree scope, with no version gate | 2 |
| 0.165 | Reasoning effort changes during a conversation, Vertex AI, Terra | Served by the existing send path and the dynamic model list | 1 |
| 0.165 | Memory read | Available when experimental is enabled | 3 |
| 0.166 | `detached` review delivery removed (openai/codex#57667) | `Detached` removed, and separate-conversation review added as a new feature | 1, 2 |
| 0.166 | Item times, structured provider errors, `policyCapacityUnavailable` | Projected, with recovery paths | 1 |
| 0.166 | Default thread unload delay of 60 seconds (openai/codex#57242) | Regression coverage | 1 |
| 0.166 | Markdown in questions, conversation selection and search | Implemented in the extension for every supported version, with no version gate, because neither needs a new server method or wire format | 2 |
| 0.166 | USD usage, network diagnostics | Implemented | 2 |
| 0.166 | Linked forks (openai/codex#57256) | Available when experimental is enabled, with 0.166 as the introduction version | 3 |
| 0.166 | Workspace switching | Available when experimental is enabled | 3 |

### Not adopted

- Vertex AI and Bedrock authentication wizards, and `account/gcp/setup`. They are recorded in the contract differences only.
- Rewrite suggestions from `turn/moderationMetadata`. The metadata is untyped and unstable, so it is recorded only, and the extension shows just the reason and recovery guidance.
- Memory v2 default changes and import.
- TUI-only operations and internal runtime fixes.

### Version policy

| Role | Versions |
| --- | --- |
| Minimum (local and remote) | 0.161.0 |
| Target and recommended | 0.166.0 |
| Regression | 0.165.0, 0.164.0, 0.163.0, 0.162.1, 0.161.0 |
| Unsupported | Anything below 0.161.0 |

### Feature tiers

| Tier | Condition | Features |
| --- | --- | --- |
| Unsupported | Unknown, or below 0.161.0 | The connection is rejected |
| Baseline | 0.161.0 or later | Existing features, separate-conversation review, worktree scope, question Markdown, conversation text |
| 0.163 | 0.163.0 or later | Existing 0.163 features |
| 0.164 | 0.164.0 or later | Existing 0.164 features |
| 0.165 | 0.165.0 or later | Memory view when experimental is negotiated |
| 0.166 | 0.166.0 or later | USD usage, network diagnostics, and, when experimental is negotiated, linked forks and workspace switching |

`GetFeatureTier` compares with each tier's introduction version and never with the target version. A version newer than the target uses the 0.166 tier. The experimental API stays disabled by default, and an experimental feature requires both its introduction version and the negotiated capability.

## 2. Separate-conversation review

`/review --new-thread [uncommitted|base <branch>|commit <sha>|custom <instructions>]` runs a review in a new conversation. `/review` without that option keeps reviewing in the current conversation. The option appears in the input candidates and hints.

- **Protocol:** on every supported version, the extension sends `thread/start` and then `review/start` with `inline` delivery to the new thread ID. It never sends `detached`.
- **Inherited settings:** working directory, model, service tier, approval policy, approvals reviewer, and sandbox mode or permission profile. Permissions are never widened. If a setting cannot be inherited, no conversation is created and the reason is displayed.
- **History:** the new conversation does not inherit the source history. A user who needs it runs `/fork` and gives the instruction there.
- **Selection:** the UI selection and `ActiveThreadId` stay on the original conversation until the review has started. On success, the extension switches to the new conversation, joins it, and shows progress.
- **Failure:** a failure after the conversation is created keeps the conversation and explains what happened. Nothing is deleted or resent automatically.
- **Unknown outcome:** a disconnect or missing response is not treated as started. The user is guided to the history.
- **Duplicates:** a second start is blocked while one is running.

## 3. Linked forks

With experimental enabled on 0.166 or later, the extension offers Create linked fork.

- The parent shows a Related conversations list that navigates to children, and a child navigates back to its parent.
- Children are hidden from the normal history list.
- Only persistent paginated history in local storage qualifies. A rejection displays its reason.
- A child's history does not contain parent turns.
- A parent cannot be deleted while a child remains.
- Children are obtained through `thread/list.attachedToThreadId`. `parentThreadId` and `forkedFromId` are never used as a substitute.

## 4. History scope and memory

### History scope

The history list offers the scopes All, Current working folder, and Same repository worktrees. All is the default.

- The worktree scope appears only for local connections with two or more distinct worktrees after normalization. It is hidden for remote connections and outside Git, matching the upstream `repository_cwd_filter`.
- The relative folder is resolved to a path in each worktree with read-only Git commands, and the paths are passed to `thread/list.cwd` as an array.
- Matching is exact, so separators, casing, and the `MapLocalPathForServer` canonical form are unified before sending.
- The target set is fixed when paging starts. Changing the scope discards the cursor and reloads.
- A conversation outside the list is never treated as deleted.

### Memory view

A joined conversation offers This conversation's memory. An explicit action calls `thread/memory/read` and shows the result in a separate pane.

- Viewing history never resumes a conversation.
- The view has no editing and no automatic sending, and the content is never logged.
- A `null` `summary` shows that no memory is available, without guessing a cause.

## 5. Workspace and provider

### Workspace switching

The account area shows the current workspace name and a Switch action. The list comes from `accountSession/list` with `refreshWorkspaceMetadata: true`. The extension does not manage multiple accounts.

- **Availability:** disabled during a running turn, an unresolved approval, or an active login. Nothing is interrupted automatically.
- **Confirmation:** switching stops all loaded conversations, so a dialog confirms it. On remote connections, the dialog also says other clients may be affected. Selecting the current workspace does nothing.
- **Login:** local connections send `allowBrowserLogin: true`. When `loginId` is returned, SSO proceeds: the existing safe browser path opens `authUrl`, and the extension waits for `account/login/completed`. Cancellation uses `account/login/cancel`. Remote connections do not send `allowBrowserLogin`.
- **Unknown outcome:** confirmed through `accountSession/list`. A switch is never retried automatically.
- **Account fingerprint:** adds the workspace ID from `account/read.workspaceRouting.chatgptAccountId`. External switches are not detected while experimental is disabled.
- **After success:** the owner boundary is updated, pending operations are cancelled, and the model list, usage, and MCP authentication state are read again.
- **Closed conversations:** a conversation closed by `thread/closed` shows as resumable and never resumes automatically.

### Providers

Vertex AI, Bedrock, and Terra come from the server's model list. A mismatch never overwrites settings or switches automatically.

When `thread/resume` is rejected with a `ModelProviderError`, the conversation opens as read-only through `thread/read` with recovery guidance. Interrupt and goal pause or clear stay available.

## 6. Questions, text, usage, and diagnostics

### Questions

Question text, option descriptions, and answer display use the safe Markdown path `ToSafeTextAndBlocks`. The submitted value stays the existing `OptionId`, and the final Submit step stays.

### Conversation text

Open conversation text shows the conversation in a read-only Visual Studio editor tab, which provides selection, Ctrl+C, Ctrl+F, F3, and word and line movement.

- **Snapshot:** paging reads the whole history into a fixed snapshot, with progress and cancellation. A failure opens nothing incomplete, and the content does not update live.
- **Refresh:** each refresh is a separate snapshot file.
- **Linked forks:** a child contains only the child's history.
- **Content:** generated from sanitized display text with secrets removed. Images and similar items appear as descriptions.
- **Storage:** reuses the `ArtifactPreviewCache` owner separation, in a location only the current user can read. The file name never contains the conversation title, and `CustomDocumentWindowCaption` provides the tab name.
- **Read-only:** uses `IsUserReadonly`.
- **Cleanup:** `SubscribeAsync` and `ClosedAsync` remove a closed file. Startup removes unused files older than 24 hours.
- **Dependencies:** no new NuGet package is needed.

### Item times

Item details show times, using `item.startedAtMs` and `completedAtMs` with fallback to the entry values. Older formats stay valid, and every field may be missing or `null`.

### Usage

On 0.166 or later, `account/usage/read` receives `threadId`.

- An actual USD amount appears only when `threadUsage.nativeUsageUsdMicros` is returned.
- `estimatedUsageUsdMicros` and `estimatedUsageCreditsMicros` are labeled as estimates.
- Values are held as integer microdollars.
- A missing or `null` value never appears as zero.

### Network diagnostics

Local connections on 0.166 or later run `codex doctor --network-only` with the resolved CLI path, a timeout, and `ISecretRedactor`. Remote connections show the procedure to run on the server.

## 7. Contract and implementation structure

### Worker contract v27

The contract adds history filter conditions, fork conditions, memory read, workspace list and switch, usage amounts, structured errors, item times, and review placement. The question wire format is unchanged.

`ReviewDelivery` is replaced by `ReviewPlacement` with `CurrentThread` and `NewThread`. `NewThread` runs `thread/start` and `review/start` as one Worker operation:

- Every step checks the owner generation.
- `ActiveThreadId` does not change until success.
- The result says whether a conversation was created.

### New RPC calls

| Method | Surface | Retry policy |
| --- | --- | --- |
| `thread/memory/read` | Experimental | Added to `ReadOnlyRequestAllowlist` |
| `account/usage/read` with `threadId` | Stable | Added to `ReadOnlyRequestAllowlist` |
| `accountSession/list` with `refreshWorkspaceMetadata: true` | Experimental | Not retried |
| `accountSession/switch` | Experimental | Not retried |
| `account/login/cancel` | Stable | Not retried |

All of them are recorded in `methodsByVersion`. Linked forks use `thread/fork.attachToSource` and `thread/list.attachedToThreadId`.

### Error projection

| Path | Source | Values |
| --- | --- | --- |
| Notifications and history | `error`, `turn/completed`, history items | `policyCapacityUnavailable`, distinct from existing safety policy reasons |
| JSON-RPC error `data` | Rejected requests | `modelProviderMismatch`, `modelProviderNotConfigured`, `modelProviderPolicyConflict`; unknown values display as ordinary errors |

The size limit and secret removal stay in force.

### Generation checks

Every asynchronous operation checks the owner, connection, request, and selection generations. Marking a conversation read happens only on explicit action.

### Contract manifest v4

`app-server-contract.json` keeps the existing executable information and adds the `codex-package-x86_64-pc-windows-msvc.tar.gz` asset name and SHA-256. The install helper and real verification use the complete package.

- Helper executables are not listed in the manifest. `codex-package.json` is authoritative.
- `codex-exec-server` is not required at runtime, because the npm distribution does not contain it.
- Stable and experimental schemas are generated and compared for all six versions. Unadopted features are recorded in the differences.

## 8. Delivery stages

Each stage can be released on its own.

| Stage | Content | Tracking |
| --- | --- | --- |
| 1. Compatibility | Version tiers, complete package, schema differences, Worker v27, `Detached` removal, time and error projection, read-only display on provider rejection, unload regression | [#241](https://github.com/kkamegawa/vsextensionforcodex/issues/241) |
| 2. Stable features | Separate-conversation review, worktree scope, question Markdown, conversation text, USD usage, network diagnostics | [#242](https://github.com/kkamegawa/vsextensionforcodex/issues/242) |
| 3. Experimental features | Linked forks, memory view, workspace switching | [#243](https://github.com/kkamegawa/vsextensionforcodex/issues/243) |
| 4. Integration and records | Per-stage real CLI and UI acceptance, VSIX inspection, design, implementation, and task records | [#244](https://github.com/kkamegawa/vsextensionforcodex/issues/244) |

## 9. Verification and completion

- **Contract:** all six versions on both surfaces, version boundaries and prereleases, no experimental request while disabled, and fork rejection for unsupported storage.
- **Review:** separate-conversation review on every supported version, inherited settings without wider permissions, stopping when a setting cannot be inherited, failure after creation, disconnect and unknown outcome, duplicate prevention, the original conversation staying selected on failure, and the history list appearance.
- **History and forks:** multiple worktrees, subfolders, no Git, hidden remote scope, path normalization, responses during a scope change, paging, linked forks versus subagents, and parent and child navigation.
- **Account:** switching between workspaces with the same email and plan, the confirmation dialog, SSO and cancellation, failure, unknown outcome, external change, late responses, invalidated queues, and `thread/closed` after a switch.
- **Display:** question Markdown and submitted values, selection, copy, and search across the whole history, Japanese text, long histories, a separate file on refresh, deletion on close, cleanup, missing times, and USD actual, estimated, zero, and unknown values.
- **Runtime state:** reasoning effort changes during a conversation, approval settings after resume and fork, pending operations after Stop and Plan changes, safety blocks, read-only display on provider rejection, and `thread/closed` and resume after the 60-second unload. Internal CLI fixes are not reimplemented.
- **Quality:** Core, UI, and Fake tests, Debug and Release builds with zero warnings, the VSIX content inspection, and real CLI connections to 0.165, 0.166, and the existing versions.
- **UI acceptance:** a real Visual Studio Experimental Instance in Light, Dark, and High Contrast, with Japanese text, a narrow width, keyboard operation, and Narrator, with screenshots and a verdict recorded.

### Real environment boundary

An unperformed check in stage 1 or stage 2 blocks completion. The following environments complete with passing Fake tests and an explicit unverified entry in the release record:

- A workspace that requires SSO.
- Multiple workspaces with the same email and plan.
- A plan that returns native USD.
- Vertex AI or Bedrock only.
