# Codex App Server 0.164.0 Support Design

[日本語](app-server-0-164-design_ja.md)

Tracking: [Issue #226](https://github.com/kkamegawa/vsextensionforcodex/issues/226) and child Issues [#227](https://github.com/kkamegawa/vsextensionforcodex/issues/227), [#228](https://github.com/kkamegawa/vsextensionforcodex/issues/228), [#229](https://github.com/kkamegawa/vsextensionforcodex/issues/229), [#230](https://github.com/kkamegawa/vsextensionforcodex/issues/230), [#231](https://github.com/kkamegawa/vsextensionforcodex/issues/231). Approved on 2026-10-25.

## Purpose

Update the Visual Studio extension to the Codex App Server 0.164.0 contract. Add inline `/model` arguments, presets shared with the CLI, resumed thread settings, generated-image references, separated MCP status, diagnostic ZIP saving, and defined compaction-failure and compressed-history behavior. Raise the minimum supported CLI to 0.161.0 and reject older servers.

Decisions are recorded in [ADR-019](adr/ADR-019-app-server-0-164.md) and the ADR-011 amendment of the same date.

## 1. Supported versions and contract

### Version policy

| Role | Versions |
| --- | --- |
| Minimum (local and remote) | 0.161.0 |
| Target and recommended | 0.164.0 |
| Regression | 0.163.0, 0.162.1, 0.161.0 |
| Unsupported | Anything below 0.161.0, including 0.159.1 |

0.159.1 is removed from the manifest, the install helper, the Fake App Server regression surfaces, CI, and the setup documents. Code paths that exist only for 0.159.1 are removed: the `serverInfo.version` fallback in version detection, the server-name fallback when an MCP OAuth response omits `loginId`, and comments that rely on 0.159.1 ignoring fields. Every supported version provides `userAgent` in `initialize` and `loginId` in `mcpServer/oauth/login`.

The experimental API stays opt-in and disabled by default.

### Contract manifest

`app-server-contract.json` moves to schema version 3 and adds a required `minimumVersion`:

- `minimumVersion` is `0.161.0`, `targetVersion` is `0.164.0`, and `regressionVersions` is `0.163.0`, `0.162.1`, `0.161.0`.
- Each target and regression version pins its release tag, Windows x64 asset, and SHA-256, and lists the used stable and experimental methods.
- Validation fails when `minimumVersion` is missing, is not the lowest pinned version, is not one of the regression versions, or when any pinned version is below it. `knownDifferences` has one entry per regression version.
- The 0.164.0 used methods add `config/batchWrite` and `feedback/save`, both on the stable surface.

Schemas are generated from the hash-verified official executables for all four versions and both surfaces, and the target is compared with each regression version separately. Scripts, the install helper, the CI matrix, and the Fake App Server read versions from the manifest. The Worker's compiled minimum and target versions are constants, and a unit test checks them against the manifest.

### Version determination

1. Source: the first product token of the `initialize` response `userAgent`, in the form `<originator>/<version>`. No other field and no `--version` process is used.
2. Syntax: Semantic Versioning 2.0. `MAJOR.MINOR.PATCH` has exactly three numeric components without leading zeros, followed by an optional `-prerelease` and an optional `+build`. At most 64 characters, with the existing character validation.
3. Ordering: Semantic Versioning precedence. Build metadata is ignored, and a prerelease sorts below its release, so `0.161.0-alpha.1` is below the minimum and `0.164.0-alpha.1` is treated as below 0.164.0.
4. Versions that cannot be determined are unsupported: a missing or invalid `userAgent`, an invalid token, or `0.0.0` from a source build. No override setting exists.
5. Evaluation happens once per connection, after the `initialize` response and before the `initialized` notification. Every reconnect evaluates it again, because a remote server can be updated.
6. Rejection stops the local process or closes the WebSocket. It is reported as a non-retryable failure, so automatic recovery does not loop. The message shows the sanitized detected version (or "unknown"), the minimum 0.161.0, and the recommended 0.164.0. For local connections it points to the CLI setup instructions and the install helper. For remote connections it asks the user to update the server. The extension never updates a server itself. The settings remain available so the user can change the CLI path or the endpoint.
7. A version newer than the target is accepted and uses the 0.164.0 tier. `/status` notes that the version is newer than the verified one.

### Feature tiers

| Tier | Condition | Features |
| --- | --- | --- |
| Unsupported | Unknown, or below 0.161.0 | The connection is rejected |
| Baseline | 0.161.0 or later | Existing features, `/model` arguments, `/permissions` |
| 0.163 | 0.163.0 or later | Local MXC opt-out, and read state when experimental is negotiated |
| 0.164 | 0.164.0 or later | Presets, every new behavior in section 3, and diagnostic ZIP saving |

The Worker computes the tier and sends it to the Extension with the connection status. The UI hides or explains a feature that needs a higher tier, instead of failing. Tolerance of missing fields and unknown enum values applies to every tier. One example is the plan types `sci` and `sci_plus`, which must not break the account or usage display.

## 2. Input commands and shared presets

### `/model`

The syntax is `/model [model] [--reasoning <level>] [--speed <tier>]`. It is available in every supported tier, because it only changes the `turn/start` parameters. Argument parsing matches the CLI 0.164.0 slash-command parser, so presets written by either client behave the same:

- Tokenization is shell-style. Whitespace separates tokens, and single or double quotes group them. An unbalanced quote is an error.
- A value flag accepts `--flag=value` or `--flag value`. The space form takes the next token only when it does not start with `--`. An empty `--flag=` takes the next token. A missing value is an error.
- A repeated flag is an error, even with the same value. An unknown `--name` is an error. A bare `--` ends option parsing.
- At most one positional argument, the model, is allowed.

Resolution:

- Model: when omitted, the current model is used. A named model must be in the current catalog and visible in the picker.
- Reasoning: when omitted and the model is also omitted, the current effort is kept, with the model default as a fallback. When omitted and a model is named, the new model's default is used. A specified level must be one of the model's supported efforts. A model without a list accepts only its default.
- Speed:
  - `standard` is an explicit normal speed. It is sent as `default`, the CLI's "no service tier" request value, which every supported version accepts.
  - `flex` maps to `flex`.
  - Any other value matches a catalog tier by ID, or by name ignoring case. An unavailable value is an error that lists the available values.
  - When speed is omitted, the current effective tier is kept if the resulting model supports it. Otherwise the speed falls back to normal, with a notice. This deliberately differs from the CLI, which sends no tier.
- The whole command is validated before anything is applied, so an error changes nothing. The change takes effect from the next `turn/start`, and an active turn is not changed. `/fast` and `/reasoning` remain.

### `/permissions`

The existing resolution is reused, with built-in mode IDs and display names and the `permissionProfile/list` profile IDs. The existing Full Access confirmation is reused as well. While a permission confirmation is pending, queued sends do not proceed. A preset that invokes `/permissions` goes through the same confirmation.

### Presets

Presets are available only in the 0.164 tier. On older connections, the command and the button explain that 0.164.0 is required.

- UI: `/presets` and a composer button open a panel with ten slots in the order 1–9 and 0. Each slot shows its label, or the first line of its input when there is no label, together with its defining layer and whether it can be edited or deleted. Preset text comes from the server, so it passes through `SafeMarkdownService` before display.
- Storage: the connected server's `tui.presets.<digit>`, as `{input, label}`. A remote connection uses the remote server's user configuration and never the local one.
- Validation matches the CLI's `QuickDial::validate`. These inputs are rejected:
  - an input that is empty after trimming,
  - an input whose trimmed start is `!`,
  - an input larger than 8,000 UTF-8 bytes.

  Newlines and whitespace in the input are preserved. The label is trimmed, and a missing label is written as an explicit empty string, which masks a lower-layer label.
- Reading: `config/read` is called with `includeLayers=true` and `cwd` set to the workspace root as a server path. Slot values come from `layers[].config.tui.presets` read from the highest layer down, not from the effective `config`, whose schema has no `tui` key. The effective slot comes from the highest layer that defines it. The user-layer `version` is kept for writes.
- Editing rules: a slot can be edited only when no layer defines it or when its highest defining layer is the user layer. A slot can be deleted only when no non-user layer defines it, so a delete never reveals a shadowed value. The server's write-time override check ignores `cwd`, so the client enforces these rules for the current workspace. Project-layer shadowing can therefore differ between workspaces.
- Writing: `config/batchWrite` sends exactly one edit with key path `tui.presets.<digit>`, `mergeStrategy` set to `replace`, and the value `{input, label}`, or `null` to delete.
  - `expectedVersion` is the user-layer version, and is omitted when no user layer exists. The Worker allows only this key path.
  - On a version conflict, the editor keeps its contents, the slots are re-read, and the conflict is shown. Nothing is retried automatically.
  - After a successful write, the slots are re-read. Other errors are shown after sanitization.
  - Replacing or deleting a slot requires a confirmation.
- Limitations: the CLI TUI writes its configuration file directly, without a version check, and keeps presets in memory. A concurrent CLI edit is therefore detected only at the next read. A running CLI may not see an extension edit until it reloads its configuration.
- Recall: selecting a slot copies its input into the composer and never sends it. When a draft exists, the user confirms the replacement, and cancelling keeps the draft. This deliberately differs from the CLI, which queues the input and runs it. The user sends it with the normal send action.
- Unsupported commands: CLI-only commands such as `/title` and `/statusline` can be kept and edited. When the user sends one, the existing unsupported-command handling explains it, and it is never converted into a plain prompt.
- Refresh: when the panel opens and on explicit refresh, with no polling. Results are checked against the owner and the connection generation.

## 3. State, images, MCP, diagnostics, and compaction

Every new behavior in this section requires the 0.164 tier. Older connections keep the current behavior, except where an item says it applies to all tiers.

### Resumed thread settings

Each new value replaces the previous one, and owner, connection, and selection generations are checked before results are applied.

- In the 0.164 tier, the `model`, `reasoningEffort`, and `serviceTier` values of a `thread/resume` response are applied together to the conversation and the composer settings.
  - A null `serviceTier` means normal speed and clears any previous tier.
  - A null `reasoningEffort` means the model default.
- Older connections keep applying only the non-empty values, because those servers return global defaults instead of the saved thread settings.

### Account change

An account change still follows ADR-013: the owner is replaced, and the new owner's connect sequence reads `model/list`. The persisted model, reasoning, and speed selections are kept when the new catalog offers them. Otherwise each falls back to the new catalog default, with a notice. A late response from the previous owner is discarded.

Limitations:

- A server older than 0.164.0 may keep the previous account's catalog after an in-process account change.
- A 0.164.0 server keeps its previous provider catalog when a refresh fails.

The client cannot detect either case, so it does not claim that the catalog belongs to the new account.

### Generated images

- The 0.164 tier recognizes `fileId`, `result`, the optional `savedPath`, `inputImages`, and `failure`. When both `fileId` and `result` are present, `fileId` is the reference. Upstream does the same for histories written during the migration.
- A `savedPath` that passes the existing path validation uses the existing preview path. An inline `result` is standard Base64, or a data URI. It is size-limited before decoding and checked to be a real PNG or JPEG.
- `fileId` is an internal attachment-store identifier, and no public RPC resolves it. It is not treated as a `thread/attachment/list` ID. When only a reference exists, the item shows a reference with the reason that this connection cannot preview it. The extension never builds a URL or calls a nonexistent API.
- `inputImages` is shown as provenance. Its paths are never read or reattached automatically.
- The item state comes from `status` and `failure`. In-progress and failed items are never shown as complete. Live notifications and history replay use the same projection.

### MCP

- The existing `mcpServer/oauth/login` flow, authorization URL validation, and `loginId` completion matching remain. The CLI owns enterprise credential selection, storage, and exchange.
- The 0.164 tier shows three separate values:
  - Authentication state, from `authStatus`.
  - Runtime state, from `runtimeStatus`. A missing or unknown value is shown as unknown.
  - Tool discovery, as the tool count or `toolsError`.

  The rules for combining them:
  - When `runtimeStatus` is `authenticationRequired`, re-authentication is shown even if `authStatus` reports a credential type.
  - A tool count of zero never implies a failed or unauthenticated server.
- `toolsError` is length-limited and passed through `ISecretRedactor` and `SafeMarkdownService`. The opaque `toolListMeta` never reaches the Worker contract, the UI, or logs.
- After an OAuth completion or an account change, the status is read again. Results for a previous owner are discarded.

### Diagnostic ZIP

- "Save diagnostic ZIP" is a separate action from sending feedback, and needs the 0.164 tier.
- Worker contract v26 adds `SaveFeedbackRequest` and `SaveFeedbackResult` and a Worker method that calls `feedback/save`. The request uses the existing `visual-studio` classification and the selected conversation's `threadId`, and passes no extra log files.
- Including logs is off by default. With logs off, upstream writes only the report and the diagnostics text, without the rollout or log files. The confirmation shows what will be saved and on which host.
- Local connections use a save picker. A path that already exists is rejected before the RPC, and the user picks another name, because the server never overwrites a file.
- Remote connections ask for an absolute path on the server. The result is labeled as a server path and is never opened as a local file. A server error, such as an existing path, is shown after sanitization.
- A disconnect or timeout is reported as an unconfirmed outcome, and the save is never resent automatically.

### Compaction failure and compressed histories

- All tiers: compaction is complete only on `thread/compacted` or a completed `contextCompaction` item.
  - An RPC error, an error notification, or a failed turn during compaction is shown as "Compaction failed; the conversation history is unchanged." No compaction marker is added, and nothing is retried.
  - Displayed history always comes from the server. The extension never synthesizes or hides summary items.
- 0.164 tier: upstream no longer keeps partial summaries after a failed compaction and can resume compressed histories. No additional client handling is needed.
- Older tiers:
  - A failed compaction can leave a partial summary in server history, and it is shown as returned.
  - Resuming a compressed history can fail. In that case the sanitized error is shown, together with a note that CLI 0.164.0 resumes compressed histories. The history row is kept, and nothing is retried automatically.

## 4. Worker contract

Worker contract v25 becomes v26. The typed DTOs are:

- Connection status: the detected version and the feature tier. The recovery failure kind gains an unsupported-server-version value.
- Presets: list, save, and delete, with each slot's input, label, defining layer, edit and delete availability, the user-layer version, and a conflict outcome.
- Diagnostic ZIP: the save request and the save result.
- MCP: runtime state, tool count, and a bounded `toolsError`.
- Generated images: a reference-only state with its reason.

Every new asynchronous result checks the owner, the connection generation, and the selection generation.

## 5. Validation

- Contract: all four versions on both surfaces, `minimumVersion` validation, and code constants that match the manifest.
- Version determination:
  - Rejection for 0.159.1, 0.160.x, the `0.161.0` prerelease, `0.0.0`, and a missing or invalid `userAgent`.
  - Acceptance for 0.161.0, 0.162.1, 0.163.0, 0.164.0, and newer versions.
  - No recovery loop after a rejection.
  - Tier gating for `0.164.0-alpha`.
- Input: `/model` parser parity cases (quotes, the `=` form, the space form, an empty value, duplicates, unknown flags, `--`, extra arguments) and atomic application. Speed fallback, `/permissions` confirmation, and holding the queue while a confirmation is pending.
- Presets:
  - Each validation boundary, including 8,000 and 8,001 bytes.
  - Reading the layers and the edit and delete rules with project, system, and session layers, and a missing user layer.
  - Conflicts, keeping the draft, slots written by the CLI, remote isolation, and connection switching.
- State, images, and MCP:
  - Resume on 0.164 and on older versions, including a null tier and a null effort.
  - Account change with stale responses.
  - Image projection: `fileId` only, `result` only, both, a `savedPath`, a data URI, corrupt or oversized data, failed or in-progress items, and replay.
  - MCP status combinations and re-authentication.
- Diagnostics: local and remote saves, rejecting an existing path, the logs choice, an unconfirmed outcome, and the tier gate.
- Compaction:
  - Fake App Server scenarios for compaction failure and a resume error on an older version.
  - On a real 0.164.0 CLI, compaction followed by a resume after restart. A compaction failure cannot be induced reliably on a real CLI, so the Fake scenarios cover it and the attempt is recorded.
- Real environment: Windows with MXC startup on 0.164.0, and a 0.161.0 regression connection.
- Quality:
  - Core, UI, and Fake tests, and Debug and Release builds with zero warnings, plus a VSIX inspection.
  - Experimental Instance screenshots in each theme, at a narrow width, and in Japanese, with keyboard-only operation and screen-reader output.

## 6. Out of scope

- No .NET or NuGet changes.
- Not ported: Kitty or Sixel previews, the Bedrock TUI wizard, the experimental `thread/sample`, and WebSocket compression.
- The CLI's internal Windows proxy-authentication fix is not evidence for the extension's own .NET WebSocket proxy support.
