# Daily-use App Server Features — Issue #155

Date: 2026-10-08
Tracking: [Issue #155](https://github.com/kkamegawa/vsextensionforcodex/issues/155), under [Issue #149](https://github.com/kkamegawa/vsextensionforcodex/issues/149). Umbrella plan: [Codex App Server Update and Remote Connection](app-server-update-plan.md), Phase 6.
Status: Approved design.

## Summary

Complete the remaining daily-use App Server features against CLI 0.159.1 stable, with 0.155.1 regression fixtures, by extending the merged #152 (mapping/ownership), #153 (history and saved-attachment metadata recovery), and #154 (questions/permissions/MCP interaction) foundations. The baseline is commit `69deda1` (PR #169 merged), Worker contract v20; allocate the next available contract version at merge time. Do not upgrade the CLI, SDK, runtime, or packages.

Reuse the existing model catalog/default/hidden-default/reasoning/service-tier handling and inherited/persistent/next-turn settings; structured `turn/plan/updated` steps; composer attachments and `localImage`; #153 bounded attachment paging, tombstones, and single-flight recovery; and #154 MCP authentication and elicitation.

## Plans and status

- Keep `turn/plan/updated` as the structured step snapshot. Add bounded provisional text from `item/plan/delta` per item. The completed plan item replaces provisional text; its final text can differ from concatenated deltas. Never parse text into duplicate steps.
- The pinned stable schema describes `item/plan/delta` as EXPERIMENTAL. Treat it as optional, correctly render completed plans without deltas, and record it as experimental-described in the contract file.
- Completed state takes precedence over duplicate or late deltas and history/live overlap. Bound plan text to 64 KiB UTF-8 and structured plans to 200 steps; reuse existing history/notification limits and batch updates within 50–100 ms.
- Present `thread/status/changed`, `configWarning`, `model/rerouted`, `model/verification`, and `item/mcpToolCall/progress` in a bounded notice area: at most 50 notices per thread and 4 KiB per notice, coalesced by kind and identity. Show connection readiness separately from thread status. `model/verification` is informational and never answers or launches native verification.

## Catalog and input admission

- The live catalog is authoritative. Accept `max`/`ultra` only when advertised; never hard-code a default model.
- Add bounded `inputModalities` and `availableAccessPrograms` to the model projection. An omitted `inputModalities` uses the schema default (text, image); an explicit empty list or unknown value grants no support.
- Validate the effective model in the composer and in the Worker immediately before `turn/start`. Unsupported input remains in the composer with a reason; never silently change the model. Non-image files remain `mention` inputs (mapped server path references) and require text support only. Images require image support. There is no generic file modality; audio and unknown modalities are display-only.

## Explicit shell

- The only entry point is `/shell [--timeout-ms N] -- <command>`. Suggestion selection never executes. `/shell` is the ninth built-in; show up to nine built-ins without dropping skills.
- Preserve the command after `--` exactly. Reject an empty command, duplicate or unknown options, negative, malformed, or over-`int64` timeouts. Omission uses the server default (one hour); zero means immediate timeout; no value means unlimited.
- Only a joined, current-owner idle thread is eligible. Confirmation shows connection/profile, thread, exact command as inert text (never raw in logs), server cwd, timeout, and that `thread/shellCommand` always runs unsandboxed with full access in 0.159.1.
- After Execute, revalidate target, generation, and cwd, then evaluate `IApprovalPolicyEngine` locally. Policy denial cannot be overridden; Full access or old grants never skip confirmation.
- The empty RPC response is an acknowledgement, not completion; its deadline is independent of execution timeout. Show events using their real thread/turn/item IDs without asserting correlation to the request.
- Allow one pending shell submission per thread. Release it on definitive acknowledgement or error. After acknowledgement timeout or disconnect, keep it locked until the generation retires; a new generation allows submission only after `thread/status` reports idle.
- Cancel before dispatch sends nothing. Do not send a request-specific Stop after dispatch or infer interruption from timing/text. Never replay, fall back to `command/exec`, or start a local process.

## Typed results and local file actions

- Project project text, MCP content, `imageView`/`imageGeneration`, and `fileChange` into typed bounded parts; use a reasoned fallback for generic JSON. Track server truncation separately from local display limits.
- Keep raw payloads and server paths in the Worker. Remote UI receives sanitized metadata and opaque owner/generation-bound action IDs.
- Changed-file links, artifacts, Open, and Reveal go through `RemotePathMapper` and `LocalPathBoundary`, then are revalidated immediately before action for existence, type, owner, symlink, and junction boundaries. Never open raw server paths.
- Preview PNG/JPEG only. Require MIME/signature agreement; cap input at 10 MiB and dimensions at 4,096 × 4,096 and 16 million pixels, checked before full decode. Clean up by owner/generation. Do not allow SVG/HTML, remote fetch, automatic browser launch, or raw URI binding. Treat `fileId` as opaque; `mcpAppUi` gets text fallback rather than embedded web runtime.

## Saved attachments

- Saved attachments are metadata records, not uploads or composer chips, and never auto-insert model input. The wire contract has only `attachmentType`, `identityKey`, and arbitrary `payload`.
- Use client-owned type `relaycodex.file.v1` with payload `{ version: 1, serverPath, mimeType, displayName }`; `serverPath` is an absolute normalized server-domain path. `identityKey` is a version-prefixed SHA-256 of the server path normalized by server OS rules from initialization, independent of local profile mapping.
- Recognize `text/plain`, `application/pdf`, `image/png`, and `image/jpeg`; preview PNG/JPEG only. Unknown types/versions and malformed payloads remain visible and read-only with a reason.
- Add revalidates source, protected-directory policy, profile/root mapping, and physical containment; respects created/existing without overwriting. Remove sends type plus identity key, requires confirmation and policy evaluation, validates owner/profile/root and payload provenance but not target existence; absent removal succeeds.
- Retain #153 bounds and reconciliation: limit 50, 100/page, 100 active/thread, 64 KiB payload, and 256-byte type/key. Record NotSent, definitive, or OutcomeUnknown. Idempotency never authorizes retry; uncertain outcomes are checked with read-only list without claiming causality.

## Local Windows sandbox setup

- Offer only for owned local stdio after initialization confirms Windows; never for remote profiles.
- Require explicit elevated/unelevated mode and Start. Optional `cwd` is the active solution root validated by `LocalPathBoundary` and shown before Start, or null when no solution is open.
- States: Not observed, Starting, Running, Succeeded, Failed, Unsupported, OutcomeUnknown. `started=true` is not success; `started=false` is not proof of success; completion may precede acknowledgement. Progress is indeterminate.
- Allow one attempt per connection generation, including after failure or unknown outcome. Ignore stale or unsolicited completions. Bound and redact errors; do not retry automatically.

## Additive 0.159.1 fields and exclusions

Handle additive fields as follows: display `availableAccessPrograms` read-only; display `disabledPluginIds` without a plugin editor; show MCP `serverName`, `httpOrigin`, and `serverCapabilities` as filtered status metadata; provide a text fallback for `mcpAppUi`; keep `image.fileId` opaque; distinguish `flexUnavailable` and `tooManyDenials` without retry; display `promax` without quotas. Exclude `rollout/compress`, daemon/worktree, Realtime, dynamic tools, ExternalMessage, plugin import/editor, native verification success, and attestation.

## Work packages

| Package | Content | Depends on |
|---|---|---|
| P0 | Contract version, used-method registration (stable vs experimental), pinned-schema fixtures, typed DTOs, payload registry | — |
| P1 | Plan delta/final, thread/config/model/MCP notices, modality admission on both sides of the Bridge, additive fields | P0 |
| P2 | Typed parts, PNG/JPEG preview lifecycle, changed-file Open/Reveal with action-time validation | P0, P1 envelope |
| P3 | `relaycodex.file.v1` reader/writer, explicit add/remove, outcome tracking on existing store | P2 |
| P4 | `/shell` parser, confirmation, policy gate, dispatch, pending lock | P0, P1 |
| P5 | Local Windows setup state machine | P0 |
| P6 | Integration, sub-agent review, full validation, screenshots | P1–P5 |

## Affected components

Worker: `CodexSessionService`, `WorkerRpcService`, notification parsers. Contracts: `WorkerContracts`, `InteractionContracts`. Extension: `WorkerBridge`, `ChatViewModel`, tool window XAML, `FilePickerService`. Shared: `RemotePathMapper`, `LocalPathBoundary`, `IApprovalPolicyEngine`, `SafeMarkdownService`, `ISecretRedactor`. Contract manifest: `app-server-contract.json`.

## Verification criteria

Use Fake App Server and pinned 0.159.1/0.155.1 fixtures. Cover completed plan without delta, final-before-late-delta, status bursts, owner switch during catalog load; unsupported modality retained in composer; shell parsing, timeouts, policy denial, pre-dispatch cancel, no Stop, other-client turns, pending-lock release, zero replay; typed mixed results, invalid/oversized media, junction escape between render and action; attachment created/existing, absent remove, stale pages, unknown types, missing target, reconnect/history/fork, uncertain delivery; sandbox completion-before-response, `started=false`, unsupported method, cwd validation, retired generation, and remote refusal.

Run focused then full Core/UI tests, warning-free Debug/Release builds, pinned-schema and used-method checks, and VSIX DTO/XAML integrity checks. Verify Light, Dark, High Contrast, narrow width, keyboard/focus, and accessibility in Experimental Instance screenshots. Missing screenshots remain unmet criteria. Issue #155 stays open until all evidence is recorded; Issue #156 retains the release gate.
