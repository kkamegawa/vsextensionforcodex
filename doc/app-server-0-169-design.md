# Codex App Server 0.169.0 Support Design

[日本語](app-server-0-169-design_ja.md)

Tracking: [Issue #268](https://github.com/kkamegawa/vsextensionforcodex/issues/268) and child Issues [#273](https://github.com/kkamegawa/vsextensionforcodex/issues/273), [#274](https://github.com/kkamegawa/vsextensionforcodex/issues/274), [#275](https://github.com/kkamegawa/vsextensionforcodex/issues/275), [#276](https://github.com/kkamegawa/vsextensionforcodex/issues/276). Implementation: [#269](https://github.com/kkamegawa/vsextensionforcodex/issues/269) (platform), [#270](https://github.com/kkamegawa/vsextensionforcodex/issues/270) (contract), [#271](https://github.com/kkamegawa/vsextensionforcodex/issues/271) (.NET 10 and C# 14), [#272](https://github.com/kkamegawa/vsextensionforcodex/issues/272) (features). Approved on 2026-11-16.

## Purpose

Support Codex App Server 0.167.0 through 0.169.0 on the Visual Studio 2026 and .NET 10 platform, and add four groups of features from those releases: model provider switching, approval UI improvements, thread management, and diagnostics and status display.

Decisions are recorded in [ADR-022](adr/ADR-022-visual-studio-2026-dotnet-10.md), [ADR-023](adr/ADR-023-codex-cli-0-167-minimum.md), and the ADR-011 amendment of 2026-11-16. Primary sources are the upstream releases `rust-v0.167.0` through `rust-v0.169.0` and the stable and experimental schemas generated from the pinned executables.

Every app-server method and field named in this document exists in the generated 0.169.0 schema. Names that the schema does not type, such as keys inside an untyped `_meta` object, configuration keys, and provider error codes, are named with the upstream change that defines them.

## 1. Version policy

| Role | Versions |
| --- | --- |
| Minimum (local and remote) | 0.167.0 |
| Target and recommended | 0.169.0 |
| Regression | 0.168.0, 0.167.2 |
| Unsupported | Anything below 0.167.0 |

Only the latest patch release of each minor series is pinned and verified. Every 0.167.x release may connect by policy. The stable and experimental schemas generated from 0.167.0, 0.167.1, and 0.167.2 are identical apart from the generator metadata, so the three releases expose the same protocol surface. Runtime behavior is verified only on 0.167.2; 0.167.0 and 0.167.1 are not pinned and their runtime behavior is unverified.

| Version | Release tag | `codex-x86_64-pc-windows-msvc.exe` SHA-256 | `codex-package-x86_64-pc-windows-msvc.tar.gz` SHA-256 |
| --- | --- | --- | --- |
| 0.167.2 | `rust-v0.167.2` | `6f65c42e3769209976cce097a400720b5d877e1d24ec8bb6272bf0f02d5e0fd7` | `20b0f94105f40b617bf209b1679b8674ab6d3d8f303e767ae43cf1860a8705da` |
| 0.168.0 | `rust-v0.168.0` | `ad8296f7d95eea56bc4bab33cef025eb000340a7252ddb83dd41041ebcc65d0b` | `f6757eb2a4daaad569b01b3ea519c9009d38d0bac5fc4082ff02517e00537797` |
| 0.169.0 | `rust-v0.169.0` | `ce1945eb440b5481ed1233ebaf32b7056ee7e4e452fa62ffe1cbfb26103cdee0` | `456db280d16f4393cd99a398f11b4a14a3ede68b6bd31610ce41fb10d6b65737` |

### Contract manifest

- `minimumVersion` is the lowest accepted version. `releases` lists only the pinned versions.
- `scripts/validate-contract-manifest.ps1` requires `minimumVersion` to be in the same minor series as the oldest pinned release and not above it. It no longer requires `minimumVersion` to be a regression version or a pinned release.
- `regressionVersions` is 0.167.2 and 0.168.0. `knownDifferences` records the file-level differences from each regression version to 0.169.0 for both surfaces, as produced by `scripts/compare-schemas.ps1`.
- `methodsByVersion` records the methods the extension uses with their introduction tier.

### Feature tiers

| Tier | Condition | Features |
| --- | --- | --- |
| `Unsupported` | Unknown, invalid, `0.0.0`, or below 0.167.0 | The connection is rejected after `initialize` and before `initialized` |
| `Baseline` | 0.167.0 or later | Every existing feature, thread management, section display, approval UI improvements, MCP diagnostics, terminal error messages |
| `Tier168` | 0.168.0 or later | Assistant token counts |
| `Tier169` | 0.169.0 or later | Model provider switching, account security reminder, `inferenceHookDenied` |

`GetFeatureTier` compares with each tier's introduction version and never with the target version. A version newer than the target uses `Tier169`. The comparison uses Semantic Versioning precedence: build metadata is ignored and a prerelease sorts below its release, so `0.167.0-alpha.1` is `Unsupported`. An experimental feature requires both its tier and the negotiated `experimentalApi` capability. The experimental API stays disabled by default.

The gates for 0.163, 0.164, 0.165, and 0.166 are removed. Every feature they guarded is part of `Baseline`, and the fallbacks for older servers are removed with them.

### Worker runtime

The Worker is framework-dependent and targets `net10.0`. `WorkerBridge` starts `Worker/Codex.VisualStudio.Worker.dll` with the `dotnet.exe` that hosts the Extension's own .NET runtime, so the Worker always runs on the runtime that Visual Studio provides. The Worker keeps the default `RollForward` policy, `Minor`: it runs on the newest installed .NET 10 patch and never on a different major version. A future Visual Studio that provides only a newer major version requires a retargeted Extension and Worker together. When that host is unavailable, the packaged apphost uses the machine-wide .NET 10 runtime under the same policy.

## 2. Protocol surface from 0.166.0 to 0.169.0

### Methods

| Introduced | Kind | Method | Surface |
| --- | --- | --- | --- |
| 0.167 | Server notification | `thread/section/changed` | Stable |
| 0.167 | Client request | `thread/guardianReview` | Experimental |
| 0.168 | Client request | `modelProvider/list` | Stable |
| 0.168 | Client request | `modelProvider/config/write` | Stable |
| 0.169 | Client request | `account/securitySetup/read` | Stable |
| 0.169 | Client request | `thread/prepareApprovedPlan` | Experimental |

No method is removed. No server request or client notification is added or removed.

### Shape changes

| Introduced | Type | Change | Surface |
| --- | --- | --- | --- |
| 0.167 | `CommandExecutionApprovalKind` | Adds `humanCommand` | Stable |
| 0.167 | `InitializeCapabilities` | Adds `requiredHumanApproval` | Stable |
| 0.167 | `OtelRequirements` | Adds `log_sse_deltas` | Stable |
| 0.167 | `ThreadSampleParams` | Adds `interruptOnCancel` | Experimental |
| 0.168 | `ItemCompletedNotification` | Adds `contentTokenCount` | Stable |
| 0.168 | `ThreadListParams`, `ThreadSearchParams` | Add `currentAccountOnly` | Stable, experimental |
| 0.168 | `ThreadItemsListParams` | Adds `deferOutput` | Stable |
| 0.168 | `ThreadItemEntry` | Adds `outputDeferred` | Experimental |
| 0.168 | `McpResourceReadParams` | Adds `_meta` | Stable |
| 0.168 | `Config` | Adds `client_search_provider` | Stable |
| 0.169 | `CodexErrorInfo` | Adds `inferenceHookDenied` | Stable |
| 0.169 | `ModelListParams` | Adds `modelProvider` | Stable |
| 0.169 | `GetAccountResponse` | Adds `userId` | Stable |
| 0.169 | `account/gatewayOAuth/read`, `account/gatewayOAuth/login` | Params change from `null` to optional `GatewayOAuthReadParams` and `GatewayOAuthLoginParams` with `modelProvider` | Stable |
| 0.169 | `GatewayOAuthLoginResponse`, `GatewayOAuthReadResponse`, `GatewayOAuthChangedNotification` | Add `prompt`, `supportsDeviceAuth`, `loginId` | Stable |
| 0.169 | `ThreadItem` `agentMessage` | Adds `moderation` | Stable |
| 0.169 | `Thread` | Adds `attachedToThreadId` | Experimental |
| 0.169 | `ThreadListParams`, `ThreadSearchParams` | Add `includeAttachedThreads` | Experimental |
| 0.169 | `ThreadSettings`, `ThreadSettingsUpdateParams`, `TurnSettingsUpdateParams` | Add `environments` | Experimental |
| 0.169 | `QueuedSubmission`, `ThreadQueueAddParams`, `ThreadQueueUpdateParams` | Add `responsesapiClientMetadata` | Experimental |
| 0.169 | `ThreadSampleParams` | Adds `allowInProgressTurn` | Experimental |
| 0.169 | `UserInput` `audio`, `ContentItem` and `FunctionCallOutputContentItem` `input_audio` | URL field changes | Stable |

The responses of `thread/start`, `thread/resume`, and `thread/fork` have the same shape on 0.166.0 and 0.169.0.

### Schema-to-parser mapping

The Worker reads payloads as JSON elements, so each changed shape that the Worker sends or reads has an explicit parser decision. Tests cover each row against the Fake app-server or a real CLI.

| Wire item | Worker code | Decision |
| --- | --- | --- |
| `initialize` `capabilities.requiredHumanApproval` | `CodexSessionService.CreateClientCapabilities` | Sent as `true` only when `experimentalApi` is sent |
| `item/commandExecution/requestApproval` `kind` | `CodexSessionService.CreateApprovalRequest`, `OfferedCommandChoiceSet` | Read. `command` keeps the existing card. `writeStdin` shows the card as input to a running terminal. `humanCommand` shows the organization checkpoint in section 4. An unknown value shows the ordinary card without session grants |
| `mcpServer/elicitation/request` `_meta` | `McpElicitationFormParser` | Read `tool_name` and `tool_annotations` as described in section 4 |
| `item/completed` `contentTokenCount` | `CodexSessionService.DispatchNotificationAsync` | Read on `Tier168` for `agentMessage` items |
| `codexErrorInfo` `inferenceHookDenied`, `sessionBudgetExceeded` | `CodexSessionService.ReadTurnErrorReason` | Recognized in addition to `flexUnavailable`, `tooManyDenials`, and `policyCapacityUnavailable` |
| `model/list` `modelProvider` | `CodexSessionService.ListModelsAsync` | Sent on `Tier169` when a provider other than the default is selected |
| `thread/start` `modelProvider` | `CodexSessionService.StartThreadAsync` | Sent on `Tier169` when a provider is selected for the new conversation |
| `account/read` `userId` | `CodexSessionService.ReadAccountStatus` | Read on `Tier169` to bind the security reminder |
| `account/gatewayOAuth/read`, `account/gatewayOAuth/login` `modelProvider` | Gateway OAuth operations | Sent on `Tier169` for the selected provider. `deviceAuth` is not sent, so `prompt` stays `null` and is ignored |
| `account/gatewayOAuth/changed` `loginId` | Gateway OAuth notification handler | Ignored; completion is matched by `providerId` |
| `thread/section/changed` | New notification handler | Updates the section of a listed thread |
| `ThreadItem` `agentMessage.moderation` | Typed item reader | Ignored; the extension does not apply `AgentMessageDisplay` actions |
| `turn/start` `personality` | `CodexSessionService.StartTurnAsync` | Never sent; see section 3 |
| Every other row in the shape change table | — | Not sent and not read |

## 3. Required protocol follow-ups

- **`personality`.** `Personality` values no longer select a style, and `Model.supportsPersonality` is always `false`. The `/personality` slash command, `StartTurnRequest.Personality`, the draft restore of the selection, and the read of `supportsPersonality` are removed. `turn/start` never sends `personality`.
- **`thread/rollback`.** The method is absent from every supported schema and the extension does not use it. The `CodexErrorInfo` value `threadRollbackFailed` still exists and displays as an ordinary error. Contract tests assert that the method is never sent.
- **Default thread permissions.** A thread created with default permissions stores its permissions as a permission profile (openai/codex#60472). The response shapes are unchanged: the Worker reads `activePermissionProfile` (an object with `id`, or a string) and the `sandbox` object as before. Real CLI tests on 0.167.2 and 0.169.0 resume a conversation created with default permissions and check the projected approval mode.
- **`features.multi_agent`.** The flag is deprecated in favour of `agents.enabled` (openai/codex#59479), and the CLI writes a warning to stderr when the flag is configured. The extension never writes either key. The warning is diagnostic output under the existing stderr rules and is not a connection failure.
- **Terminal errors.** A provider `budget_reached` error arrives as `sessionBudgetExceeded` (openai/codex#59228). Access program and workspace access denials arrive with existing `codexErrorInfo` values and a server message (openai/codex#60476, #60480). `inferenceHookDenied` is new in 0.169 (openai/codex#60511). None of them is retried by the server, so the extension never resends the turn automatically.

## 4. Features

Each group reuses the existing request helpers, notification dispatch, Worker contract records, and display classes. Dynamic strings pass through `SafeMarkdownService`, log text through `ISecretRedactor`, and destructive operations through `IApprovalPolicyEngine`. Each group adds tests showing that these paths cannot be bypassed.

### 4.1 Model provider switching

**Availability:** `Tier169`, stable surface.

- **Protocol:** `modelProvider/list` returns `data` (`ModelProviderSummary` with `id`, `name`, `baseUrl`, `canEdit`, `settings`), `defaultProviderId`, and `canChangeDefault`. `model/list` with `modelProvider` lists the models for that provider. `thread/start` with `modelProvider` starts a conversation on that provider.
- **Information structure:** the model picker gains a provider selector above it. The selector shows each provider's `name`, marks the default, and shows `baseUrl` only as a secondary line when it is not `null`. The provider type comes from `settings.type` and appears as a label (OpenAI, custom gateway, Amazon Bedrock, Google Cloud). Selecting a provider reloads the model list for it and keeps the composer usable. The provider of an existing conversation is shown read-only from `Thread.modelProvider`; switching applies to new conversations only.
- **Default provider:** when `canChangeDefault` is `true`, the selector offers Set as default for new conversations. It writes `config/value/write` with `keyPath: "model_provider"`, `value` set to the provider `id`, and `mergeStrategy: "replace"`, then reads `modelProvider/list` again. When `canChangeDefault` is `false`, the action is hidden and the selector explains that the server configuration fixes the default. A write failure keeps the previous default and shows the error. The write is not retried.
- **Gateway sign-in:** after a provider is selected, `account/gatewayOAuth/read` with `modelProvider` reports whether gateway OAuth is `required` and its `status`. When sign-in is needed, the existing gateway sign-in path runs `account/gatewayOAuth/login` with `modelProvider`. The authorization URL opens only through the safe browser path.
- **Errors:** JSON-RPC error `data.type` values `modelProviderNotConfigured`, `modelProviderPolicyConflict`, and `modelProviderMismatch` are read on `thread/start`, `thread/resume`, and `model/list`. Each shows guidance: configure the provider in the Codex configuration, choose a provider allowed by the organization policy, or open the conversation with its saved provider. An unknown value shows the redacted server message.
- **Not adopted:** `modelProvider/config/write`. Creating or editing providers, gateway, Bedrock, and Google Cloud settings stays in the Codex CLI and configuration files.

### 4.2 Approval UI improvements

**Availability:** `Baseline`. Organization checkpoints require `experimentalApi`.

- **MCP tool approvals:** an `mcpServer/elicitation/request` for an MCP tool call carries `_meta.tool_name` and `_meta.tool_annotations` (openai/codex#58161). `tool_annotations` may contain the booleans `readOnlyHint`, `destructiveHint`, `idempotentHint`, and `openWorldHint`; absent hints are unknown. The card shows the server name and the tool name as its title, then badges: Read-only, Destructive, Idempotent, and Accesses external systems. A badge appears only when its hint is present; `destructiveHint: true` uses the warning style. Hints are declared by the MCP server and are untrusted: they never change the available decisions, never enable automatic approval, and never bypass `IApprovalPolicyEngine`. A missing, non-string, or oversized `tool_name` falls back to the existing card.
- **Organization checkpoints:** with `experimentalApi` negotiated, `initialize` declares `requiredHumanApproval`. A command approval with `kind: "humanCommand"` is a checkpoint required by the organization's managed policy. The card is titled Approval required by your organization, shows the command and reason, and offers only Approve, Decline, and Cancel, which send `accept`, `decline`, and `cancel`. The checkpoint never uses session grants, `acceptForSession`, execution policy amendments, local policy automation, or automatic approval, and the timeout sends `cancel`. Without `experimentalApi`, the capability is not declared and the server applies its own behavior.

### 4.3 Thread management

**Availability:** `Baseline`. `thread/search` requires `experimentalApi`.

- **Archive:** the history list offers Archive and, in the Archived view, Unarchive, through `thread/archive` and `thread/unarchive`. The Archived view uses `thread/list` with `archived: true`. `thread/archived` and `thread/unarchived` notifications update the list.
- **Delete:** Delete permanently removes a conversation with `thread/delete`. A confirmation dialog names the conversation and states that deletion cannot be undone. The operation passes through `IApprovalPolicyEngine` and is blocked for the conversation with a running turn. A parent with linked forks cannot be deleted while a child remains. `thread/deleted` removes the conversation from the list.
- **Revert:** Revert to before this turn is offered on a turn in a joined conversation with paginated history. A confirmation dialog states that the turn and every later turn are removed from the conversation history, and that local file changes are not reverted. The operation passes through `IApprovalPolicyEngine` and sends `thread/revert` with `threadId` and `beforeTurnId`. The transcript then reloads through `thread/turns/list` and `thread/items/list` with the returned cursors. It is unavailable during a running turn.
- **Search:** with `experimentalApi`, the history list search box calls `thread/search` with `searchTerm` and shows each result's thread with its `snippet`. Without it, the existing title filter through `thread/list.searchTerm` stays. Snippets pass through `SafeMarkdownService.ToSafeLiteralText`.
- **Sections:** the history list shows each conversation's section from `Thread.section` and filters by section with `thread/list.sectionId`. The filter lists every section from `threadSection/list`, showing each section's `name`. `thread/section/changed` updates the listed conversation. The extension does not create, rename, delete, or move sections.
- **Not adopted:** `currentAccountOnly`, `includeAttachedThreads`, and section management.

### 4.4 Diagnostics and status display

- **MCP startup** (`Baseline`): `mcpServer/startupStatus/updated` carries `name`, `status` (`starting`, `ready`, `failed`, `cancelled`), `error`, and `failureReason`. A `failed` server shows its redacted `error`, limited to 2,048 characters, in the MCP details, and `reauthenticationRequired` keeps the existing sign-in path. A server returned by `mcpServerStatus/list` without a startup status shows as Not started, because a server with deferred startup starts on first use. The composer never waits for a server that has not reported a status.
- **Terminal errors** (`Baseline`, `inferenceHookDenied` on `Tier169`): `sessionBudgetExceeded` shows that the session budget is reached and that a new conversation or a larger budget is needed. `tooManyDenials` keeps its existing message. `inferenceHookDenied` shows that the organization's inference hook denied the request. Each message includes the redacted server message and offers no automatic retry.
- **Assistant token counts** (`Tier168`): the item details of an assistant message show `contentTokenCount` when it is present. A missing or `null` value is not shown and never appears as zero.
- **Account security reminder** (`Tier169`): after sign-in and after an account change, the Worker calls `account/securitySetup/read`. A `notice` is shown only when its `accountId` matches the current account and its `userId` matches `account/read.userId`. The account area shows `title` and `description` as plain text through `SafeMarkdownService` and offers `action.label` as a button. The button opens `action.url` only through the safe browser path, which accepts `https` only. The reminder can be dismissed for the session. A change of account discards a pending or shown reminder. A `null` notice shows nothing.

### Not adopted

- Cloud connection.
- Audio attachments and realtime audio.
- Terminal-only features: workspace side chats, the agent command center, report editor, and terminal notifications.
- Vertex AI and Bedrock setup wizards, `account/bedrock/*`, `account/gcp/setup`, and `modelProvider/config/write`.
- Message-board APIs.
- `userVerification/*`.
- Animated image generation.
- `thread/guardianReview` and `thread/prepareApprovedPlan`.
- Deferred output in history (`deferOutput`, `outputDeferred`). History requests never send `deferOutput`, so every output stays inline.
- `agentMessage.moderation` display actions.

## 5. Worker contract

The Worker contract takes the next unused version and adds:

- Provider list, provider selection for model lists and new conversations, and the default provider write.
- The command approval kind and the MCP tool name and annotation hints.
- Archive, unarchive, delete, revert, search, section list, and section change records.
- MCP startup error text, the token count, the new error reasons, and the security notice.

`StartTurnRequest.Personality` is removed.

### Retry policy

| Method | Surface | Retry policy |
| --- | --- | --- |
| `modelProvider/list` | Stable | Added to `ReadOnlyRequestAllowlist` |
| `account/securitySetup/read` | Stable | Added to `ReadOnlyRequestAllowlist` |
| `thread/search` | Experimental | Added to `ReadOnlyRequestAllowlist` |
| `threadSection/list` | Stable | Added to `ReadOnlyRequestAllowlist` |
| `config/value/write` | Stable | Not retried |
| `thread/archive`, `thread/unarchive` | Stable | Not retried |
| `thread/delete` | Stable | Not retried |
| `thread/revert` | Stable | Not retried |

A disconnect during a mutation is an unknown outcome. The extension reloads the affected list and never resends the mutation.

## 6. Verification

The checks follow [ADR-021](adr/ADR-021-validation-policy.md).

- **Contract:** `scripts/validate-contract-manifest.ps1`, `scripts/verify-contract-surface.ps1`, and `scripts/smoke-app-server.ps1` against real CLI 0.167.2 and 0.169.0. A real CLI 0.166.0 is rejected. Version boundary tests cover 0.166.x, 0.167.0, 0.167.2, 0.168.0, 0.169.0, prerelease and build-metadata versions, and a version newer than the target.
- **Parsers:** each row of the schema-to-parser mapping has a Fake app-server or real CLI test.
- **Security:** tests show that MCP hints never enable automatic approval, that `humanCommand` never uses session grants, that delete and revert pass through `IApprovalPolicyEngine`, that snippets, notices, and errors pass through `SafeMarkdownService` and `ISecretRedactor`, and that the security reminder URL opens only through the safe browser path.
- **UI:** each feature group is exercised in Visual Studio 2026 with real CLI 0.169.0, with Light and Dark theme screenshots and a recorded verdict.
- **Quality:** Release build with zero warnings and all Core and UI tests passing.
