# Phase 0-2.5 Implementation Notes

> **アーキテクチャ変更（Session 2）**: out-of-proc Worker（WorkerBridge 経由のサイドカー）から
> `Microsoft.VisualStudio.Extensibility` SDK ネイティブの OOP 拡張に移行済み。
> 詳細は `design.md` を参照。

## Runtime Boundaries

- `Codex.VisualStudio.Extension` は OOP プロセスとして .NET 8 で動作する（`Microsoft.VisualStudio.Extensibility` SDK）。
  コマンド・ツールウィンドウ・ビジネスロジックはすべてここに置く。
- `Codex.VisualStudio.Package` は net472 in-proc パッケージ（将来の差分ビュー等 VSSDK 依存機能用プレースホルダ）。
  現時点では実装なし。
- `Codex.VisualStudio.Worker` は net8.0 コンソールアプリ。Extension の `WorkerBridge` が Worker を spawn し、
  Worker が `codex app-server` を spawn・仲介する。
- Extension と `codex app-server` は stdio（JSONL）で通信する。

The publisher is `kkamegawa` (see CLAUDE.md for the current VSIX identity string).

## Issue #152: Path mapping and connection state isolation

The accepted Wiki Phase 3 and ADR-013 are implemented according to
[the path/state design](path-state-isolation-design.md). `LocalPath` and `ServerPath`
retain their own Windows/POSIX grammar; mapping compares complete components and rejects
root escapes and unsafe Windows aliases. The Worker resolves local links at attachment
admission and keeps remote skill identifiers opaque. Unmappable explicit attachments
reject the entire turn before `turn/start`.

Contract v18 binds mutation requests and notification envelopes to the captured owner.
The Worker uses a volatile partition for each Worker/connection attempt, retires old
pending work and buffers, and scopes approval grants to the connection context. The
Extension clears selected conversation, draft, attachments, skills, models, usage, and
interaction state at an owner boundary and rejects late asynchronous completions.

The pinned account schema cannot establish authoritative account continuity for every
provider. Both skill-cache disk reads and writes are disabled for these sessions;
the future-ready file store uses v2 composite workspace/owner keys and rejects old v1
snapshots. Reconnect is explicit and starts a new owner; mutations are never replayed.

Actual Experimental Instance rendering remains unverified: Visual Studio discovery
returned no installed instance, and the selected `orca` executable was not recognized.
The computer-use skill requires stopping when the selected executable cannot run.
Source review and UI unit tests do not substitute for this visual acceptance gate.

Validation on 2026-10-03:

- Debug and Release solution/VSIX builds: zero warnings and errors.
- Full Release Core: 311 passed, 5 skipped; UI: 317 passed, 1 skipped. All skips require unavailable symlink creation capability.
- Actual Windows junction smoke: in-root existing/future files accepted, escaping existing/future files rejected.
- Pinned CLI 0.159.1: stable/experimental contract-surface verification, schema-cache contract checks, and live initialize smoke passed.
- Release VSIX: contract v18, Worker/Extension and both Contracts payload hashes match Release outputs; raw embedded XAML matches source; publisher `kkamegawa`, existing VSIX identity, and Preview flag verified.
- Release VSIX SHA-256: `9F35C339BC4A1566553D9BFDBD5AAC28299F8BAD8DD08B068214FB04F867F73A`.
- Changed files use UTF-8 BOM/CRLF; `git diff --check` passed. Experimental Instance screenshots remain pending.

## Issue #153: Connection and history recovery

Issue [#153](https://github.com/kkamegawa/vsextensionforcodex/issues/153) is implemented against
the accepted [recovery design](connection-history-recovery-design.md). Worker contract v19 adds
owner- and generation-stamped thread, turn, item, and attachment metadata reads, plus attachment
membership notifications. The Extension retries only transient Worker/transport/server loss in
one bounded five-attempt episode. Authentication, certificate, profile, configuration, root, and
owner changes stop automatic retries. Remote recovery reconnects only to the configured external
server and never launches or restarts that server.

After recovery, the Extension refreshes the current owner's thread list. Selecting a thread reads
bounded history without joining it; a separate Join action calls `thread/resume(excludeTurns=true)`.
The previous draft remains isolated in memory until explicit restore or discard after a current
conversation is selected. Restore revalidates settings and attachment paths and only copies values
into the composer. The user sends separately. Approval state, caches, secrets, and pending requests
do not cross owner generations.

History pages and live notifications merge by owner, connection, thread, turn, and item identity.
Completed items take precedence over late deltas. The Extension caps history at 1,000 items and
16 MiB of displayed text and caps buffered notifications at 1,024 entries and 8 MiB. Attachment
recovery retains bounded metadata only and reports the 100-record ceiling or unavailable payloads.
Messages and other mutations with uncertain delivery are marked for review and are never retried.

Validation on 2026-10-04:

- Release Core tests: 343 passed, 5 skipped; Release UI tests: 335 passed, 1 skipped. All skips
  require unavailable symlink capability.
- Follow-up review regressions cover thread-less errors, bounded previews and descending history,
  page ordering/window eviction, new-thread cursor reset, live-event draining, dispatch-boundary
  outcome classification, transcript byte accounting, disconnect outcome visibility, and shared
  attachment pagination/thread deduplication.
- Debug and Release solution builds completed with zero warnings and errors.
- Pinned 0.159.1 and 0.155.1 schema-cache metadata, used-method surfaces, and stable schema-difference comparison passed.
- The Release VSIX contains the exact Release Extension, Worker, Protocol, and both Contracts
  assemblies. Embedded Chat tool-window XAML matches the source byte-for-byte. The manifest retains
  publisher `kkamegawa`, the existing extension identity, and `Preview=true`.
- Experimental Instance screenshots were not captured in that validation run. Visual acceptance remains pending; Visual Studio 2026 Enterprise 18.10.3 is installed in the current environment. Automated UI tests and source inspection do not substitute for visual acceptance.
- Changed files use UTF-8 BOM/CRLF; `git diff --check` passed.

## Issue #150: App-server protocol contract and transport core

Issue [#150](https://github.com/kkamegawa/vsextensionforcodex/issues/150) fixes the local stdio
contract at Codex CLI 0.159.1 while retaining 0.155.1 as the regression baseline. The checked-in
`app-server-contract.json` is the single manifest for release tags, Windows x64 asset names and
SHA-256 hashes, stable/experimental generator arguments, every method consumed by the Worker, and
the expected structural differences between the two CLI versions. Generated schema output remains
ignored and is cached under `schemas/<version>/<stable|experimental>/`; exact metadata plus the real
schema sentinel are required for a cache hit. Schema CI downloads the pinned official assets, verifies
both hashes, generates all four surfaces, verifies the used method tables, and compares normalized
schema structure. Build and release CI pass the pinned 0.159.1 executable as `CODEX_PATH`; the
latest stable Windows x64 asset is downloaded only for a non-blocking `scripts/smoke-app-server.ps1`
start-and-initialize check, so the pinned asset remains the only schema and build contract source.

The Worker now retains `codexHome`, `platformFamily`, `platformOs`, and `userAgent` as read-only
in-process initialization metadata without adding them to Remote UI or the Worker wire contract. Server requests use an exact method table. Command, file-change, and permission approvals
plus tool user input validate their 0.159.1 required shapes; malformed known requests return
`-32602`, and every unknown or near-match request returns `-32601` without entering an approval,
grant, or input path. Unknown notifications are redacted diagnostics only.

Each connection now owns a generation context containing its handlers, unsupported-method cache,
outbound responses, pending approvals and input, and turn state. Reinitialization and close detach
the old handlers and release pending interaction exactly once. Turn completion is keyed by
`(generation, threadId, turnId)`, with the turn id read from the wire shape
`turn/completed { threadId, turn: { id } }`, so completion-before-response, late start, duplicate
completion, other-thread events, and old-generation responses or notifications cannot revive or
replace current state. A thread whose `turn/start` is still in flight is tracked alongside the active
thread, so its early `turn/started` or `turn/completed` is not mistaken for another thread's event.
`serverRequest/resolved` accepts both string and integer request ids.

Both transports share `JsonRpcServerRequestDispatcher`, which registers server-request operations
before starting their handlers, ignores a reused outstanding id, cancels handlers with the connection
lifetime, observes every task, and suppresses response writes after close. Closing a transport fails
outstanding client requests with `JsonRpcConnectionClosedException` before canceling its lifetime.
Both transports resolve responses on their receive path and deliver notifications one at a time in
wire order through a bounded queue; a server request starts only after every earlier notification
was handled. A notification handler that awaits a request cannot block that request's response.
An attachment rejection crosses the Worker boundary as JSON-RPC error
`WorkerErrorCodes.AttachmentRejected`, so the Extension reports only that failure as "not sent". It tolerates isolated malformed frames and closes after three
consecutive ones, matching the stdio transport.

Validation on September 22, 2026:

- Official 0.154.0 and 0.155.1 stable/experimental schemas generated successfully; normalized
  expected-difference and target method-surface checks passed for both surfaces.
- Schema cache tests passed for an exact cache hit, CLI-version and generator-option metadata
  mismatch, and missing sentinel replacement. Version rejection is verified with stub executables
  that report a prerelease (`0.155.1-alpha.1`) and a different stable version (`0.154.0`).
- Focused session/transport tests: 72/72 passed. Full Core tests: 130/130 passed.
- `Codex.AppServer.Protocol`, `Codex.VisualStudio.Worker`, and the complete solution built in Release
  with zero warnings and zero errors.
- A live official 0.155.1 process completed initialize/initialized, thread/start, turn/start, and the
  completed-turn interrupt path. Generated schemas and the live comparison output were not added to
  Git.
- Worker contract v16 carries the remote endpoint, token-file path, and local/server roots
  (ADR-011 amendment). `ChatViewModel.RemoteProfiles` is bound by the connection-target flyout
  (design.md section 12). The package manifest changes only pin the existing dependencies; no new
  NuGet dependency was added.

### PR #157 review fixes (2026-09-23)

- Remote connection loss: `WorkerRpcService` observes the WebSocket transport's `Closed` event and
  publishes `Degraded` with a reconnect message. Intentional restart and disposal detach first, so
  they are not reported as a loss.
- Remote attachments: an explicit attachment outside the remote profile's local root is rejected
  before `turn/start` with the file name and a fix; `ChatViewModel` shows the reason in the
  transcript and keeps the attachment chip. IDE context outside the root is still omitted silently.
- Remote profiles: edits persist only through Save, which validates the endpoint (`wss`, or `ws` to
  a loopback host including `::1`), roots, token file, and unique names. Selection changes and
  removal write only saved profiles; restoring the selection on load does not rewrite settings.
  The unused DI registration that created a second settings instance was removed.
- Build: `ValidateCodexSchemas` uses MSBuild inputs/outputs, so pwsh runs only when the manifest,
  generator/validator scripts, or cache metadata/sentinel change.
- Validation: Release solution build with zero warnings; `Codex.VisualStudio.Core.Tests` 147/147
  (three consecutive runs) and `Codex.VisualStudio.Ui.Tests` 288/288 (one skipped);
  `scripts/test-schema-cache.ps1` and `scripts/smoke-app-server.ps1` passed against the local 0.155.1
  executable.

### PR #157 second review fixes (2026-09-30)

- Remote roots: `WorkerRpcService` refuses a remote connection without both `localRoot` and
  `serverRoot` as `Degraded` with a fix-it message, before any transport starts.
- Remote connection loss: the loss is published under the connection-transition gate and only when
  the closed connection is still the observed one, so a delayed close cannot overwrite a newer
  connect, restart, or dispose.
- Remote profile UI: the toolbar connection-target button opens a flyout bound to
  `ChatViewModel.RemoteProfiles` (list, Add/Remove, editor, Save) plus `ApplyRemoteProfileCommand`
  and `UseLocalAppServerCommand`. Applying reconnects the Worker even while Ready, refuses unsaved or
  disabled profiles, and is disabled during a connect or turn. `ConnectionTargetText` shows the
  target the connection actually uses.
- WebSocket notification ordering was already delivered by the single-consumer inbound queue;
  `Notifications_AreDeliveredOneAtATimeInWireOrder` covers it.
- Validation: Release solution build with zero warnings; `Codex.VisualStudio.Core.Tests` 153/153
  (three consecutive runs) and `Codex.VisualStudio.Ui.Tests` 295/295 (one skipped).

### Contract update to Codex CLI 0.159.1 (2026-09-30)

- `app-server-contract.json` targets 0.159.1 (SHA-256 pinned) with 0.155.1 as the regression
  baseline. `generate-schemas.ps1` and `validate-schema-cache.ps1` take the version list and
  default from the manifest, and the schema-cache guard accepts only manifest-pinned versions.
- 0.155.1-to-0.159.1 differences (stable: 4 added, 2 removed, 38 changed; experimental: 5 added,
  2 removed, 53 changed) are recorded as `knownDifferences`. Every used method still exists in both
  surfaces. For used methods, the changes are additive: `PlanType.promax`,
  `CodexErrorInfo.flexUnavailable`/`tooManyDenials`, `Model.availableAccessPrograms`,
  `disabledPluginIds`, MCP status fields, gateway OAuth methods, an image input that may carry a
  `fileId`, and the removal of the unused `thread/rollback`. No Worker parsing change was needed;
  contract tests cover the new plan type and the model catalog fields.
- With 0.159.1, `model/list` returns `gpt-6.1-sol` as the default model, so it appears in the picker
  without an extension change.
- The remote connection is labeled Preview in the flyout and README (ADR-012 amendment).
- Interrupt diagnostics (design.md section 13): the Extension logs the Stop click, and
  `CodexSessionService.InterruptTurnAsync` logs the `turn/interrupt` request, its acknowledgement,
  and, when that turn completes, its final status with the elapsed time since the request.
  `InterruptLogsRequestAcknowledgementAndTimeUntilTheTurnEnds` covers the log lines and confirms an
  unrelated completion is not reported.
- Validation: Release solution build with zero warnings; Core 155/155 and UI 295/295 (one skipped);
  `scripts/test-schema-cache.ps1`, `compare-schemas.ps1` and `verify-contract-surface.ps1` for both
  surfaces, and `scripts/smoke-app-server.ps1` passed with the pinned 0.159.1 executable.

### Secure remote App Server connection (Issue #151, 2026-10-02)

Implements `doc/secure-remote-connection-design.md`. The bundled Extension/Worker contract is v17.

- Contracts: `RemoteEndpointPolicy` (pure; canonical loopback literals, exact `localhost`, no user
  information/query/fragment, unspecified destinations rejected for every scheme),
  `BearerTokenPolicy` (RFC 6750 b64token, at least 32 characters), `TokenFilePathPolicy`
  (drive-qualified absolute path only), `RemoteProfileFingerprint` (SHA-256 over length-prefixed
  trimmed metadata), `ConnectionTargetSnapshot` on `WorkerStatus.Target`,
  `WorkerErrorCodes.ConnectionOperationRejected` (-32051) with `ConnectionOperationRejectionReason`,
  and the `worker/reconnect` and `worker/connection/diagnose` operations.
- Protocol: `WebSocketTransportSecurityPolicy` delegates to the shared policies.
  `SendReadOnlyRequestAsync` replaces `SendIdempotentRequestAsync`/`JsonRpcRetryPolicy`; the
  allowlist is owned by `ReadOnlyRequestAllowlist`. The `skills/list` false-only condition was
  checked against the pinned `schemas/0.159.1/stable/v2/SkillsListParams.json` ("When true, bypass
  the skills cache and re-scan skills from disk"). `WebSocketJsonRpcConnection` accepts a
  caller-owned `HttpMessageInvoker`, sets `KeepAliveInterval` to 30 seconds, records the upgrade
  status for authentication classification, and exposes `InboundActivitySequence`.
- Worker: `WorkerNetworking` (captured `HttpClient.DefaultProxy` behind a ws→http/wss→https
  mapping proxy; cookies, redirects, and default credentials disabled; a shared proxied invoker for
  remote endpoints, a direct invoker whose `ConnectCallback` refuses anything but loopback
  literals, and a per-attempt invoker pinned to the verified loopback address set for exact `localhost`; it tries
  each verified address in resolver order, so IPv6-only and IPv4-only listeners both connect),
  `BearerTokenFileReader`, `RemoteConnectionException` (fixed categorical text, no inner
  exception), `RemoteConnectionDiagnostics`, and `RemoteIdleWatchdog`. `ISecretRedactor.RegisterSecret`
  returns reference-counted leases; `WorkerDiagnostics` redacts each line before stderr and the
  shared `diagnostics.log`. The process host owns the lease and any pinned invoker per connection
  and releases them only after the connection is disposed.
- `WorkerRpcService`: every connect attempt gets a new generation and target snapshot; a PID is
  reported only for a local target. Remote startup runs under one 45-second deadline with stage
  caps; a failure after the socket exists retires the candidate. A stage timeout of the startup
  account read alone keeps the RPC connection Ready with an Unavailable account. `RestartAsync`
  refuses a remote target and `ReconnectAsync` validates the request against the bound options and
  current generation before stopping anything. The connection-loss handler produces its categorical
  text before queueing; queued close and watchdog callbacks are tracked and drained after the
  transition gate is released during disposal.
- Extension: profile Save uses the shared policies and never touches the token file. One operation
  gate serializes selection persistence, Save, delete, Apply, the local switch, reconnect snapshot
  validation, and the reconnect dispatch. The degraded action is labeled **Restart local
  app-server** or **Reconnect remote app-server** (tooltip, automation name, and help text bound
  to the same properties). The flyout adds **Check health** with separate Health, Ready, and RPC rows
  and the authority-root scope note; results are cleared on selection change, Save/delete, or a new
  connection generation, and stale completions are discarded. Diagnosis starts the Worker if
  needed but never the app-server.

Implementation notes:

- The idle watchdog timestamps each inbound activity signal with its own `TimeProvider` and waits
  only until 30 seconds after the last inbound message, so a silent peer is probed exactly 30
  seconds after its last message and detected after two further 10-second probes. One activity
  baseline covers the whole probe episode and is revalidated under the transition gate right
  before the socket is retired; activity that arrived meanwhile keeps the socket and restarts
  the idle window.
- The positive trusted-TLS handshake (both `wss://127.0.0.1` and the pinned `wss://localhost`
  path) and hostname-mismatch rejection are tested by passing a custom-root `X509ChainPolicy`
  through an internal `WorkerNetworking` constructor; production keeps the platform chain and
  hostname validation, and no store or machine trust changes.
- The handshake stage cap also covers a server that accepts TCP but never answers the upgrade.
- A redirect on the WebSocket upgrade or health routes is never followed. An upgrade that the server
  answers with any status other than 101 (wrong routing path, redirect, server error) is reported as
  `UpgradeRejected`; 401/403 is `AuthenticationRejected`.
- Besides checking the token path itself, the reader requires the final path of the opened handle
  (`GetFinalPathNameByHandle`) to be on a local drive, so a directory junction or symlink anywhere
  in the path cannot redirect the read to a network share.
- An independent review of the change (no high-severity findings) led to these fixes: handle-based
  final-path check, IPv4-mapped loopback literals connecting over IPv4, every Extension connect
  dispatch taking the profile operation gate, callback drain before the token lease is released on
  Worker disposal, Check health command-state refresh on a generation change, WHATWG-style detection
  of numeric hosts, preservation of the overload error at the retry deadline with close observed for
  the whole call, and the `UpgradeRejected` category.

Validation on 2026-10-02:

- Release solution build: 0 warnings, 0 errors.
- Core tests: 274 passed, 1 skipped (symbolic-link test requires Developer Mode or elevation),
  2 failed. The 2 failures (`FailedStartDoesNotLeaveUnsafeProcessReference`,
  `ResolverUsesExplicitExistingExecutable`) also fail on an unmodified worktree of the base commit.
- UI tests: 306 passed, 1 skipped.
- `verify-contract-surface.ps1` (stable and experimental) and `validate-schema-cache.ps1` passed.
- Release VSIX: manifest identity and `<Preview>true</Preview>` unchanged; packaged Contracts,
  Protocol, and Worker assemblies contain the new types; the embedded XAML contains the new
  bindings; the packaged Worker DLL hash matches the build output.
- `test-schema-cache.ps1` and `smoke-app-server.ps1` with the pinned 0.159.1 executable (the
  hash-verified release asset from `install-codex.ps1`): schema cache contract tests passed and
  `initialize` succeeded.
- Experimental Instance (2026-10-03, maintainer): screenshots confirmed the connection-target
  flyout for a local connection (Preview guidance, empty profile list, disabled Connect/Use local
  actions) and the widened usage popup (reset times with the UTC suffix no longer clipped). The
  maintainer accepted the remaining display items (remote actions, authentication/RPC failures,
  health states, themes, narrow width, keyboard focus) without recorded screenshots.

## Implemented Behavior

- Bidirectional JSON-RPC request, response, notification, and server-request handling
- Request timeout, cancellation cleanup, connection failure propagation, malformed JSON recovery, and 16 MiB line limit
- Initialize, thread start/resume/list, turn start/steer/interrupt, and conversation event mapping
- Approval risk classification, turn/thread/session approval scopes with auditable snapshots, five-minute approval timeout, resolved symlink/junction path boundaries, and secret redaction
- Future WebSocket policy that defaults off and requires loopback plus a capability/signed bearer token
- Idempotent-only exponential retry policy for app-server overload error `-32001`
- 75 ms streaming batches with bounded reasoning, command output, and diff buffers plus temporary overflow files
- WPF chat window with history, transcript virtualization, composer, approvals, connected Codex version status, interrupt, and restart controls
- Agent-only typed permission picker with stable persisted IDs, built-in approval/reviewer/sandbox tuples, capability-gated permission profiles, and theme-aware accessible confirmation for Full access and Custom thread transitions
- Empty-workspace scaffolding that creates a root-level empty `.slnx` without imposing a project template, while preserving the file-based app alternative
- Initialize-handshake version discovery from a bounded, validated app-server user-agent product token, propagated through Worker contract version 9
- Safe text rendering that removes HTML tags, ANSI escapes, and control characters
- Structured block rendering for agent/reasoning markdown with ordered-list numbering and nested-list indentation (capped at two extra indent steps)
- Bounded command-output projection with a non-serialized 2 MiB incremental buffer, a three-line/4,096-character collapsed preview, truncation-safe summaries, and a theme-aware standard WPF Expander
- Result-only transcript lines after approval and choice resolution ("Accepted — <target>", "Selected — <option>"), sanitized through SafeMarkdownService
- Local prose prompt detection for natural-language numbered choices and yes/no confirmation questions, independent of the experimental API toggle
- Pixel-based transcript scrolling (VirtualizingPanel.ScrollUnit=Pixel) to avoid variable-height item jumps
- VSIX packaging with PkgDef, WPF package dependencies, .NET 8 worker, and worker dependencies
- Model-aware Reasoning and Speed pickers with sanitized catalog content, stable persisted IDs, hidden-default capability metadata, and Remote UI accessibility bindings
- Contract version 13 turn-setting presence flags for omit/null/value semantics, plus effective reasoning and service-tier propagation
- Thread-scoped `/reasoning` and `/fast` one-turn overrides with success-only consumption and explicit sticky-value restoration

## Validation Status

Automated tests cover JSON-RPC round trips, server requests, cancellation, closed-stream disposal, redaction, relative/case-insensitive/symlink path boundaries, approval categories and scopes, WebSocket/retry policy, streaming overflow, thread-list parameters, stale steer rejection, duplicate approval prevention, and safe rendering.

Scaffolding tests additionally verify the exact generated path and bytes, UTF-8 BOM and CRLF,
non-overwrite behavior, absence of implicit project artifacts, XML validity, and compatibility with
the pinned `.NET` SDK's `dotnet sln` parser.

The picker keeps the desired default separate from the effective state reported by thread
responses and Worker status. `ask` maps to `on-request` + `user` + `workspaceWrite`, `auto`
maps to `on-request` + `auto_review` + `workspaceWrite`, and `full` maps to `never` + `user`
+ `dangerFullAccess`. Permission profiles use only the `permissions` turn override. Full access
is never restored silently after restart, and incomplete or transient profile discovery does not
overwrite the saved stable ID.

Live validation completed on June 10, 2026:

- `codex --version`: `codex-cli 0.139.0`
- `codex app-server generate-json-schema --out ./schemas`: 258 schema files generated
- `Codex.AppServer.Poc`: live `initialize`/`initialized`/`thread/start`/`turn/start` round trip completed
- Visual Studio Enterprise 2026 18.7.0 with Microsoft.VisualStudio.Extensibility SDK 17.14: .NET 8 OOP extension builds, VSIX packages, and the RemoteUserControl tool window loads in the Experimental Instance

Observed .NET 8 OOP Extensibility gaps:

- Browser shell launch is unreliable from the OOP extension host, so explicit sign-in browser launch is delegated to the Worker process.
- VS-specific theme resources require raw embedded XAML loaded by Remote UI rather than normal BAML compilation.
- Editor integrations that require legacy VSSDK/in-proc APIs remain assigned to the net472 placeholder package.
- WindowsApps Codex execution aliases can reject child-process launch; the PoC supports `--codex` with a standalone executable path.

The generated VSIX contents have been inspected and include:

- `Codex.VisualStudio.Package.dll`
- `Codex.VisualStudio.Package.pkgdef`
- `Worker/Codex.VisualStudio.Worker.dll`
- `Worker/Codex.VisualStudio.Worker.exe`
- Worker runtime configuration and dependency assemblies

## Remaining Manual Validation

Core and UI tests cover catalog sanitation, model fallback without preference loss, canonical persistent values, normal and Plan resolution, one-turn restoration, thread isolation, and failed-start retention.

- Verify the View menu command and WPF tool window under all supported Visual Studio themes.
- Confirm live approval request and response shapes against the installed Codex version.
- Add ActivityLog-backed durable audit persistence before treating the security boundary as production-ready.

## Debugging and Experimental Instance Deployment

`Codex.VisualStudio.Extension.csproj` is the startup project for debugging. Visual Studio owns the
F5 build, deployment to the Experimental Instance, launch, and debugger attachment through the
`ExtensibilityProjectExtension` capability supplied by the Extensibility SDK.

Do not add legacy VSSDK deployment or launch properties such as `DeployExtension`,
`VSSDKTargetPlatformRegRootSuffix`, `StartAction`, `StartProgram`, or `StartArguments`. Those settings
bypass or conflict with the SDK-managed out-of-process deployment path.

The Extension OOP process and Visual Studio run on different runtimes:

- F5 in Visual Studio attaches the debugger to the Extension's .NET 8 OOP process automatically
  (the Extensibility SDK handles process launch and IPC).
- `WorkerBridge` starts the packaged Worker DLL with the `dotnet.exe` beside the Extension's active
  .NET runtime, falling back to the packaged Worker apphost when that host is absent. The explicit
  runtime host avoids the Worker apphost's runtime discovery failure observed in the Windows ARM64
  Experimental Instance ([Issue #158](https://github.com/kkamegawa/vsextensionforcodex/issues/158)).
- The `codex app-server` child process can be attached separately when debugging protocol issues.
- If the in-proc `Codex.VisualStudio.Package` is ever activated, a second debugger attachment to
  the VS process (using the .NET Framework code type) is required for that component.

### Duplicate deployment diagnosis

The active extension identity and publisher must match `ExtensionIdentity.Id` / `ExtensionIdentity.PublisherName`
in `CodexExtension.cs` (see CLAUDE.md for the current values) exactly, everywhere: the packaged
manifest, diagnostics, and any Experimental Instance metadata cache or hot-load registration. Any
deployment reporting a different identity or publisher string is stale — this codebase's identity
and publisher have changed more than once (most recently to align with the `kkamegawa` Marketplace
publisher), and a leftover deployment from a prior value is the recurring failure mode described
below.

If both identities are present, a command contributed by the stale deployment can open a tool
window backed by an older assembly. Slash-command candidates are then unavailable even though the
current assembly and its view model behave correctly.

To recover without resetting the entire Experimental Instance:

1. Close the Experimental Instance and its extension hosts. A normal Visual Studio instance can
   remain open when it uses a different root suffix.
2. Under the Experimental Instance profile, identify the deployment folder whose manifest contains
   the former identity. Preserve the folder whose manifest contains the current identity.
3. Remove only the former deployment folder and its exact hot-load registration entry.
4. Run the Experimental Instance configuration update so the extension metadata cache is rebuilt.
5. Confirm that the former identity has no remaining cache or deployment hits and that the current
   identity still appears in both metadata and the current deployment.

Do not copy a build output manually over the deployment. After the cleanup, use the normal SDK-owned
F5 flow so the deployed assembly and packaged resources come from one deterministic build.

## Usage pipeline

`CodexSessionService` preserves an absent `usedPercent` as null and redacts credit balance text at
the Worker boundary. `UsagePresentation` selects only an unambiguous limit, computes remaining
percentage, and creates the bounded strings serialized by Remote UI. `ChatViewModel` owns the
connection generation, push version, 60-second TTL, and refresh gate so a stale read cannot replace
a newer push or survive lifecycle invalidation. After each `TurnCompleted` conversation event and
each completed `thread/compacted` event, the view model awaits a forced rate-limit read after
completing the existing transcript/status projection; this bypasses the TTL while preserving the
last successful snapshot when the read fails. A turn that ends via `Degraded` (no `TurnCompleted`)
is not a forced-refresh trigger; the last successful snapshot remains visible until reconnection.

`ExternalLinkOpener` maps commands to two compile-time destinations and validates their exact HTTPS
host and path before shell activation. No arbitrary URI crosses the view-model command boundary and
diagnostics do not include destination text. The raw embedded XAML provides the mutually exclusive
Usage popup with themed WPF controls, cyclic navigation after focus enters the popup, host- and
popup-level Escape commands, and UI Automation metadata. Raw Remote UI cannot execute VS-side
`Keyboard.Focus` from `Popup.Opened`; guaranteed focus transfer requires an in-process WPF host.

## Issue #140 unified slash menu

The Worker/Extension contract is v18; v15 introduced this skill interface. `skills/list` is cached for 60 seconds with `TimeProvider`,
single-flight locking, generation invalidation, and sticky `-32601` probing. Before a turn starts,
the Worker force-reloads the catalog and requires an enabled exact `(Name, Scope, Path)` identity;
the app-server receives only the structured skill item `{ type, name, path }`.

The Remote UI now uses one inline virtualized ListBox (built-in cap 8, every skill accepted by the
Worker's safety-bounded catalog) and a separate one-slot PendingSkill chip. Selecting a skill clears
only the slash query and leaves the composer visible. Ready skill-only turns are allowed;
Busy/WaitingForApproval selection and removal remain available, but pending skills disable
send/steer. The chip is cleared only after a matching successful `turn/start`. Brand color and the
default prompt are bounded and display-only; the default prompt requires an explicit empty-composer
action. Icon RPC/cache is still gated behind the Remote UI spike and therefore uses fixed-glyph
fallback.

Not yet implemented against `doc/design.md`:

- `SkillInfo.ToolDependencies` and `SkillInfo.HasIconSmall` are parsed, bounded, and carried across
  the v15 contract, but no Remote UI surface consumes them; the dependency badge/tooltip described
  in the design is still outstanding. Until it lands, these two fields are contract-only data,
  which is the pattern ADR-008 rejected, so they must either gain their surface or be removed.
- The brand-color accent binds `#RRGGBB` straight to a `Border.Background` with no High Contrast
  branch, so a High Contrast theme still renders the app-server colour instead of falling back to
  Visual Studio theme resources as ADR-009 requires.
- A `turn/start` rejected by Worker-side skill identity validation surfaces only through
  `ExtensionDiagnostics`, because `AsyncCommand` swallows the exception. The user's message is
  already in the transcript and the chip is retained, but no failure text is shown.

ADR-010 introduced a Worker-owned persistent stale-while-revalidate catalog snapshot under the local
application data profile. The cache is keyed by a workspace SHA-256, expires after 24 hours, is
limited to 4 MiB per workspace and 64 MiB overall, and is written atomically under a bounded
cross-process lock. It contains only validated catalog display/identity fields; default prompts,
dependency values, errors, raw app-server JSON, icon paths, and Remote UI selection IDs are not
persisted. A cached catalog is never authoritative: it is shown as stale and non-selectable until
the live generation is received, and `turn/start` always performs a live force reload and exact
identity validation.

ADR-013 supersedes the workspace-only key and disk-cache admission policy. Current pinned
sessions cannot establish stable authoritative account continuity and use memory only;
the retained file-store format is v2 with composite workspace/owner keys.

Validation on 2026-08-11:

- Debug and Release VSIX builds: 0 warnings, 0 errors.
- Core tests: 113 passed; UI tests: 279 passed.
- Debug Extension DLL SHA-256: `04B6F6BED4C2983EC241F1F86735505B0FAA47F4B0DC7F469F4114D15D7D2BE1`.
- Debug VSIX SHA-256: `58E3AC9F0B7C8B16C60B8A97531D97CAC22DA1B21895BA63093AA05C672E80DE`.
- Release Extension DLL SHA-256: `7511CD158CD3DCD122E5A4938349935EAE819806F1A3722C470F192AD5C66E7C`.
- Release VSIX SHA-256: `260191253EA4AB66BDC0C83A621D8AC9E4D7274158B486138DB2731E765E6249`.
- Embedded `ChatToolWindowContent.xaml` SHA-256 matches the source: `93AD99EE57AFB1D8A09C98071D69CB6D231D8FBBBB6345558876540076D726C0`.
### 2026-10-05: Questions, permissions, and MCP interaction (Issue #154)

The implementation adds Worker contract v20 interaction DTOs and owner-scoped RPCs, a shared
generation-scoped pending registry, independent question/permission/approval/MCP cards, validated
MCP form elicitation, and local Gateway OAuth state and action handling. The Worker retains
upstream request IDs and wire values, rejects stale or externally resolved requests, and makes
completion at-most-once. Secret-bearing and unsupported native-verification requests are refused
before payload projection. Remote Gateway OAuth remains status and guidance only. Attachment
metadata recovery belongs to #153; attachment actions and presentation belong to #155.

Final automated validation is complete. The first Core test run during implementation reported 336
passed, 18 failed, and 5 skipped; those were interim results and are superseded by the final run:

- Core Debug tests: 369 passed, 0 failed, 5 skipped (374 total).
- UI Debug tests: 342 passed, 0 failed, 1 skipped (343 total).
- Full solution Debug and Release builds: 0 warnings, 0 errors in each configuration; Release VSIX emitted.
- CLI contract surface verification: passed.
- Stable schema comparison from CLI 0.155.1 to 0.159.1: passed.
- Schema-cache tests: passed.
- Release assembly raw XAML SHA-256 matches the source XAML.
- Release VSIX SHA-256: `BA83A86AFA1988B8FA8A2C387415435F5F727054E0379C3E241ED252AF83AB85`.
- Visual Studio 2026 Enterprise 18.10.3 is installed in the current environment. Experimental Instance screenshots and Light/Dark/High Contrast, narrow-width, keyboard, read-aloud/accessibility, multiple-card, and authentication-state acceptance remain pending; no visual completion is claimed.
Tracking: parent [Issue #154](https://github.com/kkamegawa/vsextensionforcodex/issues/154); children [#165](https://github.com/kkamegawa/vsextensionforcodex/issues/165), [#166](https://github.com/kkamegawa/vsextensionforcodex/issues/166), [#167](https://github.com/kkamegawa/vsextensionforcodex/issues/167), and [#168](https://github.com/kkamegawa/vsextensionforcodex/issues/168).

## Issue #155: Daily-use App Server features

Approved [design](daily-use-app-server-design.md) and [package plan](daily-use-app-server-plan.md), with paired Japanese translations. Tracking: [Issue #155](https://github.com/kkamegawa/vsextensionforcodex/issues/155); [Japanese implementation record](implementation-issue155_ja.md). Target CLI 0.159.1 and regression fixtures 0.155.1; baseline commit 69deda1 (PR #169). The implementation advances Worker contract v20 to v21 without changing pinned CLI, SDK, runtime, or package versions.

P0–P5 are implemented and have automated validation. P6 automated integration and package verification is complete; Experimental Instance visual/accessibility acceptance remains pending.

| Package | Result |
|---|---|
| P0 | Typed plan/notice/artifact/shell/attachment/sandbox contracts; used-method classification and regression surface verification. |
| P1 | 75ms plan batching, authoritative completion and history reconciliation, bounded/coalesced notices, catalog fields, and composer/Worker modality admission using the actual effective model. |
| P2 | Typed mixed MCP/file/image parts in live events and history; opaque owner/generation-scoped actions; action-time mapped/physical containment; bounded fully decoded PNG/JPEG previews and scoped cleanup. |
| P3 | Existing attachment store reused; relaycodex.file.v1 provenance and normalized server-OS identity; explicit add/remove and Preview/Open/Reveal; unknown payloads read-only; uncertain delivery never retries a mutation. |
| P4 | Ninth built-in suggestion slot; exact /shell command preservation, independent timeouts, immutable confirmation, policy recheck, pending lock, and truthful acknowledgement/unknown outcomes. Observed external turns cannot enable Stop. |
| P5 | Local Windows-only readiness/setup, elevated/unelevated mode, active solution root or null, generation/owner completion gating, completion-before-ack handling, and one setup attempt per generation. |
| P6 | Automated builds/tests/schema/package checks complete; actual off-screen preview rendered and inspected; Experimental visual acceptance pending. |

### Final verification — 2026-10-08

| Check | Result |
|---|---|
| Full solution Debug and Release builds | Each: 0 warnings, 0 errors; VSIX produced. |
| Core Debug and Release | Each: 399 passed, 0 failed, 5 skipped; 404 total. |
| UI Debug and Release | Each: 370 passed, 0 failed, 1 skipped; 371 total. |
| Fixed schema comparisons/cache/used-method verification | Passed for 0.159.1 stable/experimental and declared 0.155.1 regression surfaces. |
| Debug/Release VSIX | Extension/Contracts and Worker/Contracts/Worker/Protocol DLL hashes match corresponding outputs; contract v21 confirmed. |
| Raw embedded XAML | Hash equals source in both configurations. |
| Preview rendering | Actual STA PNG/JPEG decode; generated fragment parsed with WPF XamlReader and rendered by RenderTargetBitmap. Output artifacts/issue155/ui-preview.png inspected successfully. |
| Experimental Instance | Pending; no screenshot or theme/keyboard/accessibility pass is claimed. |

Core reported five skips and UI reported one skip. The corresponding TRX files are not retained, so the reasons for these six skips cannot be independently verified; no cause is attributed here. Skipped tests are not recorded as passes. New malformed images, size limits, aggregate cache cap, cleanup, payload provenance, shell unknown acknowledgement/pending lock, stale generations, plan completion, and typed result projection tests pass. Earlier integration failures were repaired and are superseded by the final results above.

Release VSIX SHA-256: 0F34622C51C7BD8E3B9063E5567A2E42B52719AB58A86652E095A17AEAAE8F51. Source/raw embedded XAML SHA-256: BA66E0F8945F337A94E12FE64312D13DBFD6DAE49BA7B99401B72B247C128399.

Reproduction in PowerShell 7 (the fixed executable is supplied explicitly for the build; tests run without CODEX_PATH):

~~~powershell
$env:CODEX_PATH = "<PINNED_CODEX_EXECUTABLE>"
dotnet build CodexForVisualStudio.slnx -c Release --no-restore
Remove-Item Env:CODEX_PATH
dotnet test tests/Codex.VisualStudio.Core.Tests/Codex.VisualStudio.Core.Tests.csproj -c Release --no-build --no-restore
dotnet test tests/Codex.VisualStudio.Ui.Tests/Codex.VisualStudio.Ui.Tests.csproj -c Release --no-build --no-restore
~~~

Equivalent Bash commands (UI tests require Windows/WPF):

~~~bash
CODEX_PATH="<PINNED_CODEX_EXECUTABLE>" dotnet build CodexForVisualStudio.slnx -c Release --no-restore
dotnet test tests/Codex.VisualStudio.Core.Tests/Codex.VisualStudio.Core.Tests.csproj -c Release --no-build --no-restore
dotnet test tests/Codex.VisualStudio.Ui.Tests/Codex.VisualStudio.Ui.Tests.csproj -c Release --no-build --no-restore
~~~

Visual Studio 2026 Enterprise 18.10.3 is installed. Native CUA APIs are disabled, so Experimental Instance Light/Dark/High Contrast, narrow width, keyboard/focus, and accessibility states could not be inspected. The off-screen image is separate evidence and does not satisfy those criteria. Issue #155 is closed; its unfinished Experimental Instance visual/accessibility acceptance is carried into #156 as Local-required.

## Issue #156: Integrated release validation

Approved [design](release-validation-design.md) and [implementation plan](release-validation-plan.md), with Japanese translations. Tracking: [Issue #156](https://github.com/kkamegawa/vsextensionforcodex/issues/156), under [#149](https://github.com/kkamegawa/vsextensionforcodex/issues/149); [Japanese work record](implementation-issue156_ja.md). Baseline: main at b44e856, Worker contract v21, CLI 0.159.1, and 0.155.1 regression fixtures.

This change synchronizes the design, issue, repository documents, and bilingual Wiki. It does not implement or execute the integrated validation orchestrator, CI/release workflow changes, uncovered tests, External scenarios, or Experimental Instance acceptance. No runtime validation result is claimed here.

PR #170 evidence remains historical: Debug/Release builds reported zero warnings/errors; Core reported 399 passed and 5 skipped; UI reported 370 passed and 1 skipped; schema/cache/method and package/XAML/hash checks were recorded. Those results do not establish readiness for a later candidate. Issue #155 is closed, while its unfinished Experimental Instance visual/accessibility acceptance remains Local-required under #156.
