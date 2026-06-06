# Phase 0-2.5 Implementation Notes

> **アーキテクチャ変更（Session 2）**: out-of-proc Worker（WorkerBridge 経由のサイドカー）から
> `Microsoft.VisualStudio.Extensibility` SDK ネイティブの OOP 拡張に移行済み。
> 詳細は `design.md` を参照。

## Runtime Boundaries

- `Codex.VisualStudio.Extension` は OOP プロセスとして .NET 8 で動作する（`Microsoft.VisualStudio.Extensibility` SDK）。
  コマンド・ツールウィンドウ・ビジネスロジックはすべてここに置く。
- `Codex.VisualStudio.Package` は net472 in-proc パッケージ（将来の差分ビュー等 VSSDK 依存機能用プレースホルダ）。
  現時点では実装なし。
- `Codex.VisualStudio.Worker` は net8.0 コンソールアプリ。将来の `codex app-server` 仲介役候補。
  現時点では Extension 内の `WorkerBridge` が直接 spawn する。
- Extension と `codex app-server` は stdio（JSONL）で通信する。

The publisher is `kazushikamegawa`. The VSIX identifier is `Kkamegawa.CodexForVisualStudio`.

## Implemented Behavior

- Bidirectional JSON-RPC request, response, notification, and server-request handling
- Request timeout, cancellation cleanup, connection failure propagation, malformed JSON recovery, and 16 MiB line limit
- Initialize, thread start/resume/list, turn start/steer/interrupt, and conversation event mapping
- Approval risk classification, process-session approval scope, five-minute approval timeout, path boundary checks, and secret redaction
- 75 ms streaming batches with bounded reasoning, command output, and diff buffers plus temporary overflow files
- WPF chat window with history, transcript virtualization, composer, approvals, status, interrupt, and restart controls
- Safe text rendering that removes HTML tags, ANSI escapes, and control characters
- VSIX packaging with PkgDef, WPF package dependencies, .NET 8 worker, and worker dependencies

## Validation Status

Automated tests cover JSON-RPC round trips, server requests, cancellation, redaction, path boundaries, approval categories, streaming overflow, thread-list parameters, stale steer rejection, duplicate approval prevention, and safe rendering.

The generated VSIX contents have been inspected and include:

- `Codex.VisualStudio.Package.dll`
- `Codex.VisualStudio.Package.pkgdef`
- `Worker/Codex.VisualStudio.Worker.exe`
- Worker runtime configuration and dependency assemblies

## Remaining Manual Validation

- Install the VSIX into a Visual Studio Experimental Instance.
- Verify the View menu command and WPF tool window under all supported Visual Studio themes.
- Run against a normally accessible local Codex CLI and record the generated app-server schema.
- Confirm live approval request and response shapes against the installed Codex version.
- Add ActivityLog-backed audit persistence and full symlink resolution before treating the security boundary as production-ready.

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
