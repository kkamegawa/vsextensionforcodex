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
- WPF chat window with history, transcript virtualization, composer, approvals, status, interrupt, and restart controls
- Safe text rendering that removes HTML tags, ANSI escapes, and control characters
- Structured block rendering for agent/reasoning markdown with ordered-list numbering and nested-list indentation (capped at two extra indent steps)
- Result-only transcript lines after approval and choice resolution ("Accepted — <target>", "Selected — <option>"), sanitized through SafeMarkdownService
- Pixel-based transcript scrolling (VirtualizingPanel.ScrollUnit=Pixel) to avoid variable-height item jumps
- VSIX packaging with PkgDef, WPF package dependencies, .NET 8 worker, and worker dependencies

## Validation Status

Automated tests cover JSON-RPC round trips, server requests, cancellation, closed-stream disposal, redaction, relative/case-insensitive/symlink path boundaries, approval categories and scopes, WebSocket/retry policy, streaming overflow, thread-list parameters, stale steer rejection, duplicate approval prevention, and safe rendering.

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

- Verify the View menu command and WPF tool window under all supported Visual Studio themes.
- Confirm live approval request and response shapes against the installed Codex version.
- Add ActivityLog-backed durable audit persistence before treating the security boundary as production-ready.

## Debugging

`Codex.VisualStudio.Extension.csproj` is the startup project for debugging. It is configured to start
`devenv.exe /RootSuffix Exp` with an explicit activity log path
(`%APPDATA%\Microsoft\VisualStudio\CodexForVisualStudio-Exp-ActivityLog.xml`).

Deployment to the experimental instance happens automatically when building inside Visual Studio in
Debug configuration (`DeployExtension` is gated on `$(BuildingInsideVisualStudio) == true AND
$(Configuration) == Debug`). No manual `Directory.Build.user.props` override is needed.

The Extension OOP process and Visual Studio run on different runtimes:

- F5 in Visual Studio attaches the debugger to the Extension's .NET 8 OOP process automatically
  (the Extensibility SDK handles process launch and IPC).
- The `codex app-server` child process can be attached separately when debugging protocol issues.
- If the in-proc `Codex.VisualStudio.Package` is ever activated, a second debugger attachment to
  the VS process (using the .NET Framework code type) is required for that component.
