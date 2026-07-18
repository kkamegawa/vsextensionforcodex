# task.md — 実装タスク（フェーズ別チェックリスト）

`plan.md` のフェーズ分割に対応する詳細タスク。各タスクは独立してレビュー可能な小さなスライスを意図する。

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
  - Slash-command hover and selection states use paired Visual Studio background and foreground theme resources, preserving contrast across themes.
  - Debug solution build completed with zero warnings and zero errors; Core tests passed 70/70 and UI tests passed 169/169 with `--no-build`.
  - The VSIX manifest, packaged assembly, embedded Remote UI XAML, and SDK-managed Experimental deployment were inspected; packaged, build, and deployed assembly hashes matched.

### 3.2 スキル
- [ ] `skills/list`（`cwds` スコープ、`forceReload`）でスキル一覧取得（キャッシュ + invalidation）
- [ ] `$<skill-name>` + `skill` 入力アイテムでスキル明示呼び出し
- [ ] `skills/config/write` で有効/無効切替
- [ ] `skills/changed` 通知で一覧を再取得（invalidation）

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

**完了条件**: モデル選択・MCP/アプリ・設定 UI が動作し、必要に応じインライン補完を提供できる。

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
