# task.md — 実装タスク（フェーズ別チェックリスト）

`plan.md` のフェーズ分割に対応する詳細タスク。各タスクは独立してレビュー可能な小さなスライスを意図する。

## 2026-08-11: Complete skill catalog and persistent cache (Issue #140, ADR-010)

- [x] Add ADR-010 and synchronize the repository design, plan, task, and English/Japanese slash-command specifications. ADR-010 supersedes only ADR-009's twenty-skill presentation cap and volatile-cache-only assumption.
- [x] Remove the UI `.Take(20)` path and render every distinct Worker-accepted skill identity, including disabled rows, while retaining the Worker safety cap of 200 and a distinct `IsTruncated` state.
- [x] Add a Worker-owned, versioned, per-workspace persistent stale-while-revalidate snapshot alongside the existing 60-second memory cache. Return cached rows as stale and non-selectable, single-flight the live refresh, and publish only the newest generation.
- [x] Implement bounded cache storage under `%LOCALAPPDATA%\Kkamegawa.CodexForVisualStudio\skill-catalog\v1`: at most 200 skills, 4 MiB per workspace, a 24-hour hard expiry, 64 MiB total, atomic replacement, LRU cleanup, bounded cross-process locking, and fail-open-to-live handling for corrupt or unavailable cache files.
- [x] Exclude `defaultPrompt`, dependency values, icon source paths, raw app-server JSON, and Remote UI selection IDs from persistence. Revalidate all loaded fields and keep raw paths outside Remote UI data members.
- [x] Preserve live `skills/list` as the catalog system of record. A `turn/start` must bypass memory and disk snapshots, force reload, require an enabled exact `Name + Scope + Path`, and retain the pending chip on refresh or validation failure.
- [x] Add Core/UI/file-store coverage for 0/1/20/21/200/201 entries, full keyboard/UI Automation reachability, stale-to-fresh replacement, empty/unsupported/failed/truncated states, workspace isolation, corrupt/expired/oversize files, generation races, concurrent instances, cleanup, and force-reload invocation safety.
- [ ] Update Issue #140 and both Wiki languages with the approved ADR-010 amendment. After implementation and validation, update `doc/implementation.md`, rerun Debug/Release and full tests, and replace all VSIX/DLL/embedded-XAML/deployed-artifact evidence.

## 2026-08-11: Unified slash menu and structured skill invocation (Issue #140)

This section records the original ADR-009 implementation. ADR-010 supersedes its skill presentation
cap and cache durability decisions without rewriting the completed history below.

- [x] Update Issue #140 and the English/Japanese Wiki plan with the reviewed v15 contract, busy-state, candidate limits, identity validation, metadata boundaries, and icon spike gate.
- [x] Add ADR-009. ADR-008 remains authoritative for capability probing, flattened catalogs, complete identity, and missing-data tolerance; only explicitly admitted metadata fields are superseded.
- [x] Raise the Worker contract to v15 with `SkillInvocationInfo`, metadata DTOs, skills invalidation observer, 60-second `TimeProvider` cache, generation guard, and exact enabled-skill revalidation before `turn/start`.
- [x] Add one unified virtualized slash list with built-in rows, skill rows, scope labels, loading/unsupported/empty/truncated states, stable ranking, and non-selectable state rows.
- [x] Add one independent pending skill chip. It replaces the previous chip, clears only the slash query, permits text-free Ready turns, blocks pending send/steer while Busy, and clears only after successful matching `turn/start`.
- [x] Bound and sanitize brand color, default prompt preview, and dependency badges. Keep default prompt insertion explicit and non-sending; keep icon data behind the fixed-glyph spike gate.
- [ ] Add final Remote UI screenshot and Experimental Instance hash verification after the icon spike is accepted.
- [x] Complete Core/UI regression coverage for cache TTL/generation, metadata fallback, ranking/collisions, chip lifecycle, and structured input serialization.

## 2026-07-21: Stabilize intermittent CI build failures (issue #110)

- [x] Replace the fixed `Task.Delay(250)` in `StreamingBufferTests.cs` (3 tests) with a poll-until-condition-or-timeout wait, removing the race against the `StreamingBuffer`'s 75ms flush timer.
- [x] Validate: solution builds with 0 warnings/0 errors (Release); `StreamingBufferTests` pass 8/8 consecutive local runs; 95 Core tests and 268 UI tests pass.
- [ ] Add inline PowerShell retry (max 3 attempts) around the `Test core` / `Test UI` steps in `.github/workflows/ci.yml` — deferred: `Edit(.github/workflows/**)` is denied by this environment's permission settings, so the change is provided as a diff for manual application instead of being committed by the agent.
- Ref: issue #110.

## 2026-07-20: PR #89 review and merge validation

- [x] Confirm that the PR branch already contains the current `main` commit without conflicts.
- [x] Re-base plain text inputs on the Visual Studio TextBox style so slash-command arguments keep a themed foreground/background pair.
- [x] Preserve hidden default model metadata when only the top-level default identifier is reported.
- [x] Make the reasoning override test independent from persisted user settings.
- [x] Validate the integrated Release outputs with 95 Core tests and 244 UI tests passing.

## 2026-07-20: Reasoning and service-tier pickers (#85, #86, #93-#98)

- [x] Upgrade the Extension/Worker contract to version 13 with effort and service-tier presence flags.
- [x] Preserve hidden default model capabilities separately from the visible catalog.
- [x] Track effective turn settings across thread lifecycle and settings updates.
- [x] Add sanitized, model-aware persistent Reasoning and Speed pickers to Remote UI.
- [x] Make `/reasoning` and `/fast` thread-scoped, canonical, success-consumed, and sticky-restoring.
- [x] Update the Fake app-server and add Core/UI regression coverage.

---

## Phase 0: リポジトリ準備・PoC

### 0.1 公開リポジトリ初期化
- [x] `.gitignore`（Visual Studio / .NET 用）を追加
- [x] `LICENSE`（MIT 等）を追加
- [x] `README.md`（概要・前提条件・ビルド手順の雛形）を追加
- [x] `.editorconfig`（UTF-8 BOM、CRLF、C# 規約）を追加
- [x] `.github/copilot-instructions.md`（publisher 名一貫性、英語ソース等）を追加

### 0.2 apm セットアップ
- [x] apm CLI をインストール（macOS では `uv tool install apm-cli`）
- [x] `apm marketplace add github/awesome-copilot`
- [x] `apm.yml` を作成し `tfsugjp/skills/.github/agents/visual-studio-extension.agent.md` を宣言
- [x] GitHub Copilot / Claude / Codex 向け target を設定し、必要最小限の agent だけを展開
- [x] `apm install` 実行、`apm.lock.yaml` を生成・コミット
- [x] `apm-policy.yml` を追加（トランジティブ MCP / Unicode ガバナンス）
- [x] `apm audit --ci --policy apm-policy.yml` で lockfile・allowlist・drift を検証

### 0.3 codex app-server 疎通 PoC
- [x] ローカルに `codex` CLI が存在することを確認（`codex --version`）
- [x] `codex app-server generate-json-schema --out ./schemas` でスキーマ取得
- [x] 最小 C# コンソールで `codex app-server` を spawn → `initialize`/`initialized`/`thread/start`/`turn/start` 往復を確認
- [x] VisualStudio.Extensibility の **.NET 8** 実機対応範囲を検証し、機能ギャップを記録

**完了条件**: 公開リポジトリが初期化され、apm でエージェントが導入でき、C# から app-server と最小往復ができる。

---

## Phase 1: プロトコル基盤（AppServerClient）

### 1.1 プロセス管理
- [x] `CodexProcessHost`: `codex app-server`（stdio）を起動/終了、再起動、終了コード監視
- [x] codex 実行パス解決（PATH / 設定で上書き可能）
- [x] stderr をログへ転送
- [x] `ProcessStartInfo` は `UseShellExecute=false`、引数配列、固定 working directory、最小環境変数で構成
- [x] stderr / exit code / crash reason を `SecretRedactor` 経由で VS ActivityLog に記録（OutputChannel 経由）
- [x] app-server 終了時に pending RPC と active turn を fail fast し、UI をブロックしない

### 1.2 JSON-RPC レイヤ
- [x] `JsonRpcMessage` 型（request/response/notification、`jsonrpc` ヘッダはワイヤ上省略）
- [x] stdin へ改行区切り JSON 書き込み（JSONL）
- [x] stdout 行単位読み取り → `id` 対応の `result`/`error` を `TaskCompletionSource` に解決
- [x] `id` なし通知を購読者へディスパッチ（`IObservable` / event）
- [x] WebSocket 過負荷エラー（`-32001`）等のリトライ方針（将来 WS 用に抽象化）
- [x] stdout reader / JSON parser / response resolver / notification dispatcher を `Channel<T>` で分離
- [x] request timeout、cancellation、orphan `TaskCompletionSource` cleanup を実装
- [x] 1 行あたりの最大 JSON サイズと malformed JSON 時の復旧方針を定義
- [x] WebSocket は既定無効。使用時は loopback + capability token / signed bearer token を必須化

### 1.3 ライフサイクル
- [x] `InitializeAsync`（`clientInfo.name` = `codex_visual_studio`、`optOutNotificationMethods` 対応）
- [x] `experimentalApi` opt-in をオプション化
- [x] `ThreadStartAsync` / `ThreadResumeAsync` / `ThreadForkAsync`
- [x] `TurnStartAsync`（text/image/localImage 入力、model/effort/sandbox オーバーライド）
- [x] 通知 → ドメインイベント変換（`turn/*`, `item/*`）

### 1.4 型生成
- [x] `generate-json-schema` 出力から C# DTO を生成 or 手書き（バージョン整合チェック）
- [x] `SchemaVersionGuard` で実行中 app-server とクライアント DTO の互換性を起動時に検証（`InitializeAsync` で `serverInfo.version` を確認）
- [x] 未知の method / notification / enum 値はクラッシュせずログ記録し、可能なら degraded mode で継続

### 1.5 信頼境界・安全性
- [x] `ApprovalPolicyEngine` を実装し、command/file/network/oauth/MCP 要求を統一判定
- [x] risk category（`read-only` / `workspace-write` / `workspace-outside` / `network` / `destructive` / `credential/oauth`）を定義
- [x] `PathAccessPolicy` で full path、symlink、relative path、case-insensitive 比較を正規化
- [x] workspace 外書き込み、破壊的コマンド、資格情報らしき文字列を検出
- [x] 承認決定を session/thread/turn 単位でスコープ管理し、`acceptForSession` の有効範囲を監査可能にする
- [x] `SecretRedactor` で token、connection string、private key、OAuth credential を表示/保存前にマスク
- [x] `AuditLogService` で承認要求・決定・拒否・policy block を VS ActivityLog に記録（OutputChannel 経由で `WorkerBridge` / `ChatViewModel` に実装）

### 1.6 ストリーミング性能
- [x] `StreamingBuffer` を実装し、delta を 50-100ms 程度でバッチ化
- [x] command output / diff / reasoning summary にメモリ上限と truncation 表示を導入
- [x] 長い command output は折りたたみ、リングバッファ、または一時ログファイル退避に切り替える
- [x] notification burst 時に UI thread へ直接連続 dispatch しないことをテストで確認

**完了条件**: `AppServerClient` で thread/turn を開始し、ストリーミング通知をイベントとして受け取れる。app-server 終了・大量 delta・危険操作要求でも Visual Studio が固まらず、承認ポリシーが一元的に適用される。

---

## Phase 2: チャット UI（MVP）

### 2.1 拡張プロジェクト雛形

> **アーキテクチャ変更（Session 2 実施済み）**: 当初の「in-proc Package + out-of-proc Worker」構成から
> `Microsoft.VisualStudio.Extensibility` SDK による完全 OOP 構成に移行した（`design.md` 参照）。

- [x] OOP Extension プロジェクト（`Codex.VisualStudio.Extension`、net8.0-windows10.0.22621.0）を作成
  - `Microsoft.VisualStudio.Extensibility` SDK を使用
  - `CodexExtension : Extension`、`ShowCodexWindowCommand : Command`、`CodexToolWindow : ToolWindow`
  - `RemoteUserControl`（`ChatToolWindowContent` + `ChatToolWindowContent.xaml`）による Remote UI
- [x] in-proc Package（`Codex.VisualStudio.Package`、net472）を将来の差分ビュー用プレースホルダとして維持
- [x] ビルド設定（Central Package Management、arm64 マニフェスト、experimental instance デプロイガード）を整備
- [x] 拡張メタデータ（拡張名・publisher・VSIX ID、`ExtensionConfiguration.Metadata`）を設定
- [ ] DI コンテナで `AppServerClient` / `CodexSessionService` を登録

### 2.2 ツールウィンドウ
- [x] チャットツールウィンドウ（Remote UI / WPF）を実装
  - XAML は `EmbeddedResource`（`<Page>` でなく生 XML）として埋め込み — `EnvironmentColors` 等 VS 固有型をランタイムで解決するため
- [x] テーマ対応（`EnvironmentColors` / `VsResourceKeys`、色ハードコード禁止）
- [x] MVVM 構成（ViewModel / async コマンド / CancellationToken 対応）

### 2.3 ストリーミング表示
- [x] `item/agentMessage/delta` のバッチ追記（`StreamingBuffer` 経由）
- [x] `item/reasoning/summaryTextDelta` の折りたたみ表示（長文上限あり）
- [x] `commandExecution` 実行ログ（`item/commandExecution/outputDelta`、仮想化/折りたたみ/上限あり）
- [x] `fileChange` 差分プレビュー（`turn/diff/updated`、巨大 diff は折りたたみ）
- [x] `turn/plan/updated` の計画ステップ表示（状態更新でレイアウトが跳ねない）
- [x] `turn/completed` / `error` のステータス表示
- [x] 順序付きリスト（`1.` 番号保持）・ネストリスト（深さ別インデント、2 段でキャップ）のブロック描画 (#25)
- [x] トランスクリプトの `VirtualizingPanel.ScrollUnit=Pixel`（可変高アイテムのスクロール跳ね防止） (#25)

### 2.4 承認ハンドリング
- [x] `item/commandExecution/requestApproval` → `ApprovalPolicyEngine` 判定付き承認 UI（accept/acceptForSession/decline/cancel）
- [x] `item/fileChange/requestApproval` → path 正規化と workspace 境界判定付き承認 UI
- [x] `networkApprovalContext` 用のネットワーク承認 UI（host、port、protocol、session scope を表示）
- [x] risk category、承認スコープ、有効期限、policy block 理由を UI に表示
- [x] 承認対象の command/file/network 内容を `SecretRedactor` 経由で表示
- [x] `serverRequest/resolved` の整合処理
- [x] 承認/選択の解決後にカードを消し、結果のみの 1 行（"Accepted — <対象>" / "Selected — <選択肢>"）をトランスクリプトへ表示（Copilot Chat 準拠） (#25)

### 2.5 操作
- [x] 送信 / 中断（`turn/interrupt`）/ 追記（`turn/steer`）ボタン
- [x] コンポーザーの Ctrl+Enter で送信（`SendCommand`、Enter 単独は改行を維持）(#5)
- [x] 会話履歴一覧（`thread/list`）と再開（`thread/resume`）
- [x] app-server 未起動/クラッシュ/非互換時の degraded UI と再起動導線
- [x] `account/read` によるログイン状態表示と `account/login/start` による ChatGPT ブラウザ認証導線
- [x] UI thread ブロック、過剰メモリ使用、長大出力表示の回帰テスト

**完了条件**: GitHub Copilot 風チャット UI で Codex と対話でき、承認・中断・差分表示が機能する。

---

## Phase 3: スラッシュコマンド / スキル

### 3.1 スラッシュコマンドルーター
- [x] 入力先頭 `/` を検出してコマンドへルーティング（GitHub Issue #46）
- [x] `/review`（`review/start`: uncommittedChanges / baseBranch / commit / custom）
- [x] `/compact`（`thread/compact/start`、専用compaction event表示）
- [x] `/goal`（`thread/goal/set` / `get` / `clear`、専用goal event）
- [x] Codex IDEコマンドの許可リスト、非対応コマンド非表示、`//`エスケープ
- [x] コマンド補完 UI（入力時サジェスト、コマンドチップ、固定引数）
- [x] 実行中のスレッド別FIFOキュー、設定置換、切断・再起動・スレッド消失時取消
- [x] Worker契約v8と型付きcompact/review/fork/goal/MCP/feedback/rate-limit RPC
- [x] `/ide-context`、`/init`、`/status`のVisual Studio内処理
- [x] レビュー指摘対応: 次ターン設定の消費、キュードレイン網羅（失敗後継続・選択スレッド・セッションキュー）、compaction完了時のReady復帰、`/fork`後の履歴復元、`/model`大文字小文字非区別、`/goal show`エイリアス、候補の編集距離閾値（GitHub Issue #51、sub-issues #52-#56）
- [x] Remove the stale Experimental Instance registration, centralize extension identity diagnostics, and add slash-command display and packaging regression coverage (GitHub Issue #51, sub-issue #58)
  - The former identity has zero remaining Experimental Instance metadata or deployment hits; the current identity remains registered.
  - Slash-command normal, hover, and selection states use paired Visual Studio theme resources without reduced text opacity, preserving contrast across themes.
  - Worker diagnostics cancellation, process teardown, and output shutdown are serialized and awaited to prevent exceptions when a debugging session ends.
  - Debug solution build completed with zero warnings and zero errors; Core tests passed 70/70 and UI tests passed 171/171 with `--no-build`.
  - The VSIX manifest, packaged assembly, embedded Remote UI XAML, and SDK-managed Experimental deployment were inspected; packaged, build, and deployed assembly hashes matched.

### 3.2 スキル
- [x] `skills/list`（`cwds` スコープ、`forceReload`）でスキル一覧取得（60秒memory cache + invalidation）
- [x] 統合Slashメニューの独立チップ + `skill` 入力アイテムでスキル明示呼び出し
- [ ] `skills/config/write` で有効/無効切替
- [x] `skills/changed` 通知で一覧を再取得（invalidation）
- [ ] ADR-010に従い、全件仮想化表示とWorker永続stale-while-revalidate cacheを実装

### 3.3 apm との連携（スキル/プラグイン導入）
- [ ] awesome-copilot から必要スキル/プラグイン/エージェントを `apm install` で導入する手順をドキュメント化
- [ ] 導入済み資産が codex の `skills/list` / `plugin/list` に反映されることを確認
- [ ] `apm.lock.yaml` 固定、marketplace allowlist、未知 plugin/MCP の既定無効化をドキュメント化
- [ ] `apm audit` で Unicode spoofing、transitive MCP、未固定参照を検出する運用を定義

**完了条件**: 主要スラッシュコマンドが UI から実行でき、スキル呼び出しと apm 管理が機能する。

---

## Phase 4: 拡張機能・統合

### 4.1 モデル / 努力度
- [x] `model/list`（`includeHidden`）でモデルピッカー UI（`ChatViewModel.PopulateModelsAsync`、起動時 1 回ロード）
- [x] Load the startup model catalog before Remote UI account synchronization can block initialization (#39)
- [x] Add bounded model discovery diagnostics across the extension, Worker RPC, and app-server request boundaries (#39)
- [ ] モデル一覧の明示的な再取得（refresh）コマンド
- [x] スラッシュコマンドのreasoning effort、personality、service tier選択にモデル能力を反映（GitHub Issue #46）

### 4.2 インライン補完（任意）
- [ ] エディタ内ゴーストテキスト補完プロバイダ（in-proc が必要なら .NET Framework 4.7.2 フォールバック）
- [ ] 補完要求の debounce、キャンセル、active document 変更時の stale response 破棄
- [ ] 送信する editor context のサイズ上限と秘密情報 redaction

### 4.3 MCP / アプリ（コネクタ）
- [x] `/mcp`から`mcpServerStatus/list`でMCPサーバー状態表示（GitHub Issue #46）
- [ ] `mcpServer/oauth/login`（OAuth、`mcpServer/oauthLogin/completed`）
- [ ] `app/list` でアプリ一覧、`$<app-slug>` mention 入力（キャッシュ + invalidation）
- [ ] OAuth は PKCE / MSAL public client（client secret を拡張に埋め込まない）
- [ ] MCP tool call と OAuth login は `ApprovalPolicyEngine` と managed policy の対象にする
- [ ] token は OS/VS の安全な資格情報ストアを使い、ログ・設定ファイルに保存しない

### 4.4 設定 UI（新 Unified Settings）
- [ ] codex 実行パス、既定モデル、サンドボックス/承認ポリシー、ローカライズ等
- [ ] `config/read` / `config/value/write` / `config/batchWrite` で codex 設定連携
- [ ] managed policy を読み込み、ユーザー設定より強い制約として自動承認・non-loopback transport・未承認 marketplace・MCP/OAuth を制御
- [ ] 設定変更時に app-server restart が必要な項目と即時反映項目を明示

### 4.5 承認モードピッカー（GitHub Issue #75）

ChatGPT デスクトップと同等の承認方法選択 UI。レビュー済みの wire マッピングと設計詳細は #75 を参照。

- [x] Sub-issue A (#76): 組み込みモード（Ask for approval / Approve on my behalf / Full access / Custom (config.toml)）を、表示名と安定 ID を分離した Remote UI DTO で追加する。Agent モード時のみ有効にし、設定ストアを注入可能にして永続化する。`turn/start` には手動承認=`on-request` + `user` + `workspaceWrite`、代理承認=`on-request` + `auto_review` + `workspaceWrite`、Full access=`never` + `user` + `dangerFullAccess` を送る
- [x] Sub-issue B (#77): 手書き TOML 解析は行わず、対応する app-server の `permissionProfile/list`（`cwd`、ページング）で `[permissions.<id>]` を取得し、実験 API と runtime capability が利用できる場合だけ turn の `permissions` override で選択する。未対応時はプロファイル項目を表示せず組み込みモードを継続する
- [x] Sub-issue C (#78): `/permissions` を正式名、`/approve` を互換エイリアスとして実装し、`/status`、候補表示、`doc/slash-commands*.md`・`doc/design.md`・`doc/implementation.md` を更新する
- [x] Full access は Codex の sandbox と承認プロンプトを無効化し、Worker のポリシーは app-server が承認要求を送った場合だけ評価されることを、確認 UI・ToolTip・Automation HelpText・ドキュメントで正確に警告する
- [x] 保存する「希望する既定値」と thread start/resume/fork response および `thread/settings/updated` から得る「実効状態」を分離し、`/status` で両者の差を表示する。Full access は再起動後に無確認で復元しない
- [x] `turn/start` override は後続 turn に残るため、Ask / Auto / Full / profile から Custom に切り替える場合は新規 thread の作成を確認し、null/省略を reset として扱わない
- [x] profile カタログの非同期ロード中は保存済み選択を保持し、取得成功後に限って欠落 profile を Custom へフォールバックする。RPC 一時失敗で設定を上書きしない
- [x] XAML バインド対象の option collection / selected ID / enablement に `[DataMember]` を付け、Remote UI シリアライズ、アクセシビリティ、`/status` の実効値、Fake/実 app-server の wire 値を回帰テストする

**完了条件**: モデル選択・承認モードピッカー・MCP/アプリ・設定 UI が動作し、必要に応じインライン補完を提供できる。

---

## Phase 5: パッケージング / 品質

### 5.1 ローカライズ
- [ ] 英語ソース + 日本語リソース（英語フォールバック）

### 5.2 パッケージング検証
- [ ] VSIX 内容物の検証（出力ディレクトリではなく VSIX 実体）
- [ ] 依存 DLL が各コンポーネント横に配置されることを確認
- [ ] Experimental Instance での読み込み検証（`ActivityLog.xml` 活用）
- [ ] 決定論的パッケージング（その場限りの登録ハック排除）

### 5.3 CI / 再現性
- [ ] ビルド/テスト CI（GitHub Actions）
- [ ] `microsoft/apm-action` で apm 資産の再現性を CI に組み込み
- [ ] `apm audit` をコンテンツセキュリティチェックとして実行
- [ ] lockfile drift、未知 marketplace、未固定 plugin/skill 参照を CI で失敗させる
- [ ] unit test: JSON-RPC timeout/cancel/crash、policy 判定、path 正規化、secret redaction
- [ ] UI/perf test: 大量 delta、巨大 command output、巨大 diff で UI が固まらないことを検証

### 5.4 ドキュメント
- [ ] README に前提（ローカル codex CLI / apm）・セットアップ・OAuth アプリ登録手順
- [ ] アーキテクチャ図・プロトコルマッピング・トラブルシューティング
- [ ] セキュリティモデル（承認カテゴリ、managed policy、ログ redaction、transport 制約）を記載
- [ ] 性能モデル（streaming buffer、出力上限、キャッシュ/invalidation、既知の制限）を記載

**完了条件**: VSIX が検証済みで配布可能、CI と apm 統合で再現性が担保され、ドキュメントが整備される。

---

## 横断タスク（全フェーズ）

- [ ] 英語ソースコード・コメント、UTF-8 BOM・CRLF を維持
- [ ] async-first / CancellationToken 対応
- [ ] 認証・設定・拡張アクションのサービス抽象化
- [ ] 破壊的操作は必ず承認フローを経由
- [ ] `codex app-server` のバージョン差をスキーマ生成で検証
- [ ] すべての外部入力（app-server 通知、command output、diff、apm metadata、MCP/app metadata）を untrusted として扱う
- [ ] UI に表示する動的文字列は markdown/HTML/ANSI escape の扱いを明確にし、意図しないリンク・装飾・制御文字を無害化
- [ ] long-running operation は CancellationToken、timeout、progress/error reporting を持つ
- [ ] telemetry/logging は opt-in 方針、redaction、保存期間、管理者ポリシーを明確にする

---

## Work log

### 2026-07-20: Implemented bounded collapsible command output (issue #80)

Implemented issue #80 and sub-issues #81, #82, and #83. Sanitized command deltas now accumulate
in a non-serialized extension buffer capped at 2 MiB of characters. Output remains inline through
three logical lines and 4,096 characters, then starts collapsed with only that bounded preview
published to Remote UI. Hidden streaming deltas no longer republish the accumulated full text.

The transcript uses a standard WPF Expander with TwoWay state, native keyboard/UI Automation
behavior, Visual Studio dynamic theme resources, non-wrapping monospace text, and horizontal
scrolling. CRLF split across deltas is counted once, truncated output avoids unverified total-line
claims, and no third-party control or package was added. ADR-003 records the projection boundary.

The expanded header now retains its normal themed surface instead of remaining in the pressed
state. Hover and pressed foregrounds can override the inherited normal foreground, so every state
keeps a matching Visual Studio foreground/background pair. Non-truncated items also publish an
empty truncation notice across Remote UI.

- Validation: Release solution build completed with zero warnings and zero errors.
- Tests: Core tests passed 95/95 with `--no-build`.
- Tests: UI tests passed 267 with one symlink test skipped when the Windows test process lacked
  symlink privilege.

### 2026-07-20: Implemented empty SLNX-only scaffolding (issue #88)

Implemented issue #88 and sub-issues #102, #103, and #104. The empty-workspace prompt now
offers a root-level empty solution that contains no implicit project or source layout. The SLNX
file uses the sanitized workspace name, exact empty-solution XML, UTF-8 BOM, and CRLF, and the
existing non-overwrite and file-based app behaviors remain intact. ADR-006 records the decision.

- Validation: Release solution build completed with zero warnings and zero errors.
- Tests: eight focused scaffold tests are included; Core tests passed 95/95 and UI tests passed
  251 with one symlink test skipped when the Windows test process lacked symlink privilege.
- Compatibility: generated SLNX files passed XML parsing and `dotnet sln ... list` validation.

### 2026-07-20: Implemented usage presentation and freshness (issue #87)

Implemented issue #87 and sub-issues #99, #100, and #101. The Worker now preserves missing usage
percentages, while the extension presents clamped remaining limits, known window labels, Unix reset
times, and sanitized credits in both the popup and `/status`. Signed-in connection generations fetch
once; a 60-second popup TTL, monotonic push versions, and lifecycle invalidation prevent stale reads.
Transient refresh failures preserve the last-good snapshot and remain retryable. The themed Usage
popup is mutually exclusive with History, binds Escape at both host and popup levels, and opens only
compile-time approved destinations through an exact allowlist. ADR-005 records the freshness and
Remote UI focus contracts.

- Validation: project-scoped Release builds completed with zero warnings and zero errors. Core and UI
  tests passed with `--no-build`, covering parser, presentation, freshness, read/push races,
  lifecycle invalidation, links, and embedded XAML structure.

### 2026-07-20: Implemented the approval mode picker (issue #75)

Implemented issue #75 and sub-issues #76, #77, and #78. The Agent composer now exposes
stable built-in approval modes and capability-gated permission profiles, while Chat keeps
the exact read-only tuple. Contract version 12 carries the approval reviewer, mutually
exclusive permission-profile selection, and app-server-reported effective thread state.
Full access and Custom transitions use explicit confirmation, saved profile selections
survive asynchronous discovery failures, and `/permissions` plus `/approve` share the same
safe selection path.

- Validation: Release UI build completed with zero warnings and zero errors.
- Tests: Core tests passed 89/89 and UI tests passed 206/206 with `--no-build`.
- Packaging: the VSIX contains the Worker and both matching Contracts assemblies; packaged
  binaries match their Release outputs and the approval picker XAML remains a raw embedded
  `DataTemplate` resource.

### 2026-07-19: Addressed attachment and presentation review feedback (PR #74)

Disposed completed file-suggestion refresh cancellation sources without racing newer
refreshes, made temporary workspace cleanup reliable when tests fail, and restored exact
cardinality checks for slash-command key bindings so duplicate bindings are detected.

### 2026-07-19: Improved chat author label contrast (issue #73)

Set the transcript author label foreground directly to the Visual Studio tool-window text
theme resource and restored full opacity. This keeps the `You` and `Codex` labels paired
with the existing tool-window card background across light, dark, and High Contrast themes,
including live theme changes in the Remote UI host.

### 2026-07-19: Implemented file attachment support (issue #67)

Implemented the approved file attachment plan with SDK-backed multi-file selection,
removable attachment chips, workspace file suggestions triggered by `#`, and typed
`mention`/`localImage` turn inputs. Explicit selections are validated at both process
boundaries, capped and de-duplicated, while steering remains text-only and preserves
attachments for the next turn. ADR-001 records the Remote UI constraints and trust-boundary
decisions.

### 2026-07-18: Verified and closed slash command review findings (issue #51)

All fixes for issue #51 and its sub-issues (#52, #53, #54, #55, #56, #58) had already been
implemented on the `fix/51-slash-command-review-findings` branch and merged to `main` via
PR #60 (squash commit 2b005c6), but the issues remained open. Verified each fix against
current `main` and closed every issue with an evidence comment.

- Verification: Release build with 0 warnings (`TreatWarningsAsErrors=true`); full test
  suite passed without rebuilding (Core.Tests 70/70, Ui.Tests 171/171), including the
  regression tests named in each sub-issue.
- Closed: #52 (next-turn settings consumption), #53 (queue drain gaps), #54 (Ready state
  after compaction), #55 (/fork history load), #56 (model matching / goal alias /
  suggestion threshold), #58 (stale Experimental deployment), and parent #51.

### 2026-07-18: Displayed the connected Codex version (issue #61)

Implemented issue #61 and sub-issues #62, #63, and #64. The Worker now reads a
bounded, validated version from the app-server initialize user agent, carries it
through contract version 9, and clears stale values outside connected states.
The Remote UI header displays the sanitized value as `Ready · Codex <version>`
and preserves it for busy and approval states with narrow-width truncation and
one accessible live-region announcement.

- Validation: Release build completed with 0 warnings and 0 errors.
- Tests: Core.Tests 75/75 and Ui.Tests 179/179 passed from the Release build.
- Documentation: implementation notes, worker contract notes, the security
  policy, and the approved Wiki plan were updated.

### 2026-07-20: Release readiness — docs, VSIX identity, and CI (issue #105)

Prepared the first Marketplace-bound release. Rewrote `README.md` for end users
(requirements, setup, limitations, FAQ, release flow), added `README_ja.md`, moved the
VSIX identity to `relaycodexforvs.KazushiKamegawa.<GUID>`, bundled the English
license and the extension icon into the VSIX, and made the VSIX version follow the git
tag through the generated assembly version.

- Issues: #105 (parent), #106 (README), #107 (VSIX identity and bundled assets),
  #108 (CI and release workflows).
- Documented limitations: Codex CLI older than the verified 0.145.0 is unsupported,
  multiple Codex installations can launch an older build (`CODEX_PATH` pins it), and npm
  installs are known to misbehave so winget is recommended.
- Validation: Release build with 0 warnings; Core.Tests 95/95 and Ui.Tests 268/268 passed.
  A build with `-p:Version=1.2.3.4` produced a VSIX whose `Identity Version` was `1.2.3.4`.
- Decision record: `doc/adr.md` ADR-007.

### 2026-07-20: Fixed a dangling-symlink write bypass found by CI (PR #109)

The GitHub-hosted Windows CI runner has symlink-creation privilege that local
development machines typically lack, so `CreateEmptySolution_DoesNotFollowDanglingSolutionSymlink`
had always been skipped locally and never actually exercised. On CI it failed for real:
`ProjectScaffolder.WriteFileIfMissing` opened the target path with
`FileMode.CreateNew` without first checking for an existing leaf entry, and Windows
transparently follows a dangling symbolic link for that open mode, so scaffolding could
write a new file at the link's target instead of leaving the existing link alone.
Added an upfront `PathEntryExists` check before the open.

- Validation: Release build 0 warnings; Ui.Tests 268/268 passed locally (the symlink
  test itself still reports Inconclusive/skipped locally, lacking the OS privilege).

### 2026-08-10: Restored selected-surface foreground contrast (issue #137)

Paired the active slash-command chip background with the Visual Studio selected glyph
foreground and generalized the selected-chip icon style for both attachment removal and
slash-command clearing. Fixed slash-command option labels and thread-history text now inherit
their owning selectable control's state foreground without an implicit `TextBlock` foreground
overriding it. Structural regression coverage verifies the exact selected, hover, and pressed
theme-resource pairs and both inheritance paths.

- Validation: focused UI test compilation was blocked before source compilation because the
  sandbox denied access to the local Windows SDK discovery directory.
- Formatting: modified XAML, C#, and Markdown files retain UTF-8 BOM and CRLF line endings.

### 2026-08-12: Refresh usage after conversation turn completion

Added the approved turn-completion usage refresh. The Extension now forces the existing
`worker/account/rateLimits` read after every `TurnCompleted` event, after the transcript projection
has finished. `TurnCompleted` covers turns the app-server reports as interrupted (it still arrives
as `turn/completed`); a transport-level failure instead reports `Degraded`, under which no forced
read is attempted (see the ADR-005 amendment). Existing connection-generation, TTL, push-version,
cancellation, and last-good-snapshot behavior remain unchanged; no Worker, RPC contract, XAML, or
package changes are required.

- Tests: added UI regression coverage for TTL-bypassing refresh, unavailable-account no-op behavior,
  and retry after a failed post-turn read.
- Validation: CI (`ci.yml`, commit `f04cfd0`) built the solution in Release with zero warnings and
  ran the full `Codex.VisualStudio.Core.Tests` and `Codex.VisualStudio.Ui.Tests` suites, not only the
  focused usage/turn subset reported at design time. Result: `build` check SUCCESS. The Visual Studio
  Experimental Instance check (real turn completion, header/popup/updated-time sync) remains to be
  run interactively and is tracked as its own sub-issue rather than closed by this entry.
- Tracking: no parent/sub-issue set was created before this branch was pushed, so the branch name
  omits an issue number (`codex/feature-refresh-usage-after-turn` instead of
  `codex/feature-<parent-issue>-refresh-usage-after-turn` per the original plan). A parent issue and
  sub-issues were opened retroactively and linked from PR #142; the branch itself was not renamed to
  avoid disrupting the open PR.

### 2026-08-12: Also refresh usage after context compaction

Code review of PR #142 found that `/compact` consumes model calls but does not always raise
`TurnCompleted` — the app-server may report completion only through `context/compacted`
(`WorkerRpcService.PublishContextCompactedAsync` already special-cases this for `Ready` recovery).
`ChatViewModel.OnContextCompactedAsync` now also forces a `worker/account/rateLimits` read when
`IsCompleted` is true, using the same post-projection ordering and `force: true` gate as the
turn-completion path. In-progress compaction events remain a no-op.

- Tests: added `ChatViewModel_ContextCompacted_ForcesUsageRefreshWithinTtl` and
  `ChatViewModel_ContextCompacted_InProgressDoesNotRefreshUsage`.
- Validation: `dotnet build CodexForVisualStudio.slnx -c Release` — 0 warnings, 0 errors. Full suite
  run locally: `Codex.VisualStudio.Core.Tests` 113/113, `Codex.VisualStudio.Ui.Tests` 285/285.
  Visual Studio Experimental Instance check still pending (tracked in the sub-issue above).
