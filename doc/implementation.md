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

The publisher is `kazushikamegawa`. The VSIX identifier is `Kkamegawa.CodexForVisualStudio`.

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
- The `codex app-server` child process can be attached separately when debugging protocol issues.
- If the in-proc `Codex.VisualStudio.Package` is ever activated, a second debugger attachment to
  the VS process (using the .NET Framework code type) is required for that component.

### Duplicate deployment diagnosis

The active extension identity must be `Kkamegawa.CodexForVisualStudio` with publisher
`kazushikamegawa`. The former identity `CodexForVisualStudio.kkamegawa` and former publisher
`kkamegawa` must not appear in the Experimental Instance metadata cache or hot-load registration.

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
a newer push or survive lifecycle invalidation.

`ExternalLinkOpener` maps commands to two compile-time destinations and validates their exact HTTPS
host and path before shell activation. No arbitrary URI crosses the view-model command boundary and
diagnostics do not include destination text. The raw embedded XAML provides the mutually exclusive
Usage popup with themed WPF controls, cyclic navigation after focus enters the popup, host- and
popup-level Escape commands, and UI Automation metadata. Raw Remote UI cannot execute VS-side
`Keyboard.Focus` from `Popup.Opened`; guaranteed focus transfer requires an in-process WPF host.
