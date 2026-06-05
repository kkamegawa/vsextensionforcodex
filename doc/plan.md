# plan.md — Codex for Visual Studio 拡張機能 実装計画

## 1. 概要

ローカルにインストールされた **Codex app**（`codex app-server`）を呼び出し、Codex に
コーディングを担当させる Visual Studio 2022+ 拡張機能を開発する。UI/UX は GitHub Copilot の
Visual Studio 拡張機能（チャットツールウィンドウ + インライン補完 + スラッシュコマンド）に
準拠する。

本拡張は OpenAI の [Codex App Server プロトコル](https://developers.openai.com/codex/app-server)
（JSON-RPC 2.0 over stdio）を C# クライアントとして実装し、`codex app-server` をサブプロセスとして
起動・制御する。

## 2. ゴール

1. ローカル `codex` CLI（`codex app-server`）をサブプロセスとして起動し、JSON-RPC で通信する。
2. GitHub Copilot 拡張と同等のチャット UI（ツールウィンドウ）を提供する。
3. Codex CLI がサポートするスラッシュコマンド（`/review`、`/compact`、`/goal` など）をサポートする。
4. 必要なスキル・プラグイン・エージェントを GitHub `awesome-copilot` から **Microsoft apm** で管理・インストールする。
5. 拡張開発用エージェント `tfsugjp/skills/.github/agents/visual-studio-extension.agent.md` を apm 経由で参照する。

## 3. 技術スタックと制約

| 項目 | 決定 | 備考 |
|------|------|------|
| 拡張本体ランタイム | **.NET 8** | ユーザー要件。エージェント標準（.NET 10 out-of-proc）から意図的に逸脱 |
| in-process コンポーネント | **.NET Framework 4.7.2 許容** | VSSDK 依存の機能ギャップを埋める場合のみ |
| 言語 | **C#** | — |
| 対象 IDE | Visual Studio 2022 (17.x) 以降 | — |
| 拡張モデル | VisualStudio.Extensibility (out-of-proc) を第一候補、機能ギャップは VSSDK in-proc で補完 | エージェント方針に準拠 |
| Codex 連携 | `codex app-server` を spawn、stdio で JSON-RPC 2.0 (JSONL) | WebSocket/Unix socket は将来検討 |
| UI | Remote UI / WPF（テーマ対応、`EnvironmentColors`・`VsResourceKeys`） | 色のハードコード禁止 |
| 設定 | 新 Unified Settings 体験 | レガシー `DialogPage` 単独は避ける |
| パッケージ管理（エージェント資産） | **Microsoft apm**（`apm.yml` + `apm.lock.yaml`） | awesome-copilot をマーケットプレイスに登録 |

### 標準からの逸脱（明記）

- エージェント定義は **.NET 10 out-of-proc** を既定とするが、本件は **.NET 8 / .NET Framework 4.7.2** を
  採用する。理由：ユーザー要件。VisualStudio.Extensibility の .NET 8 対応範囲を Phase 0 で検証し、
  ギャップは in-proc (.NET Framework 4.7.2) フォールバックで対応する。

## 4. アーキテクチャ

```
┌───────────────────────────────────────────────────────────────┐
│ Visual Studio 2022+                                             │
│                                                               │
│  UI Layer                                                      │
│  ┌──────────────────┐   ┌──────────────────────────────────┐ │
│  │ Chat ToolWindow   │   │ Inline Completion Provider        │ │
│  │ (WPF / Remote UI) │   │ (IAsyncCompletionSource 等)       │ │
│  └────────┬─────────┘   └────────────────┬─────────────────┘ │
│           │                              │                    │
│  Presentation Layer                       │                    │
│  ┌────────▼──────────────────────────────▼─────────────────┐ │
│  │ ChatViewModel / StreamingBuffer / CommandSuggestion      │ │
│  └────────┬─────────────────────────────────────────────────┘ │
│           │                                                    │
│  Application Layer                                             │
│  ┌────────▼─────────────────────────────────────────────────┐ │
│  │ CodexSessionService / SlashCommandService /               │ │
│  │ ApprovalWorkflowService / WorkspaceContextService          │ │
│  └────────┬─────────────────────────────────────────────────┘ │
│           │                                                    │
│  Security Layer                                                │
│  ┌────────▼─────────────────────────────────────────────────┐ │
│  │ ApprovalPolicyEngine / PathAccessPolicy /                 │ │
│  │ SecretRedactor / AuditLogService                          │ │
│  └────────┬─────────────────────────────────────────────────┘ │
│           │                                                    │
│  Protocol Layer                                                │
│  ┌────────▼─────────────────────────────────────────────────┐ │
│  │ AppServerClient / JsonRpcDispatcher /                     │ │
│  │ SchemaVersionGuard / CodexProcessHost                     │ │
│  └────────────────────────┬──────────────────────────────────┘ │
└───────────────────────────┼────────────────────────────────────┘
                            │ stdin/stdout (JSONL)
                  ┌─────────▼──────────┐
                  │ codex app-server   │  ← ローカル Codex app
                  │ (サブプロセス)      │
                  └────────────────────┘
```

### 主要コンポーネント

- **AppServerClient**: `codex app-server` を `System.Diagnostics.Process` で起動。stdin に
  改行区切り JSON を書き込み、stdout を行単位で読んで `id` に対応する `result`/`error`、および
  `id` を持たない通知（`turn/*`、`item/*` 等）にディスパッチする。
- **JsonRpcDispatcher**: stdout reader、response resolver、notification dispatcher を分離し、
  `System.Threading.Channels` で背圧・キャンセル・app-server 終了時の pending request 失敗処理を扱う。
- **SchemaVersionGuard**: `generate-json-schema` 由来の型と実行中 app-server の protocol/schema version を
  起動時に検証し、非互換時は安全に機能を落としてユーザーへ通知する。
- **CodexSessionService**: `initialize` → `initialized` → `thread/start` → `turn/start` の
  ライフサイクルを管理。`turn/interrupt`、`turn/steer`、`thread/resume`、`thread/fork` を提供。
- **ApprovalWorkflowService**: サーバー起点リクエスト
  （`item/commandExecution/requestApproval`、`item/fileChange/requestApproval`、
  `tool/requestUserInput`）を受け、`ApprovalPolicyEngine` の判定結果を UI に表示して decision を返す。
- **ApprovalPolicyEngine**: command/file/network/OAuth/MCP 要求を `read-only`、`workspace-write`、
  `workspace-outside`、`network`、`destructive`、`credential/oauth` などに分類し、既定拒否・承認粒度・
  `acceptForSession` のスコープを一元管理する。
- **PathAccessPolicy**: workspace root、solution directory、temporary directory などの許可境界を解決し、
  symlink や相対パスを正規化して workspace 外ファイル変更を検出する。
- **SecretRedactor / AuditLogService**: stderr、command output、承認要求、監査ログに含まれる token、
  connection string、credential らしき値を redaction し、VS ActivityLog へ安全に記録する。
- **SlashCommandRouter**: 入力テキストの先頭が `/` の場合に CLI スラッシュコマンドへマッピング。
  スキル呼び出し（`$<skill-name>` + `skill` 入力アイテム）も処理。
- **StreamingBuffer / StreamingRenderer**: `item/agentMessage/delta`、`item/reasoning/summaryTextDelta`、
  `item/commandExecution/outputDelta`、`turn/diff/updated`、`turn/plan/updated` を 50-100ms 程度でバッチ化し、
  UI スレッドへの反映を抑制する。巨大な command output と diff は仮想化・折りたたみ・上限超過時の
  ログファイル退避を行う。
- **WorkspaceContextService**: solution、active document、selection、Git state などを収集する。収集範囲は
  設定と承認ポリシーに従い、秘密情報やバイナリ/巨大ファイルを送らない。

### セキュリティ設計

- **既定は最小権限**: `codex app-server` は stdio transport を既定とし、WebSocket/Unix socket は明示設定時のみ
  使用する。WebSocket を使う場合は loopback 限定、capability token または signed bearer token 必須とする。
- **承認境界の一元化**: UI は承認結果を表示するだけにし、危険判定は `ApprovalPolicyEngine` に集約する。
  destructive command、workspace 外書き込み、network access、OAuth/MCP tool call は既定で承認必須。
- **パス正規化**: file change 承認前に full path、symlink、case-insensitive 比較、solution/workspace root を
  正規化し、workspace 外への書き込みや traversal を検出する。
- **秘密情報保護**: ログ、stderr、command output、diff preview、telemetry は `SecretRedactor` を経由する。
  API key、OAuth token、connection string、private key 形式は保存・送信前にマスクする。
- **Supply chain 管理**: apm 由来の skill/plugin/agent は `apm.lock.yaml` 固定、導入元 allowlist、
  `apm audit`、MCP/plugin の明示有効化で管理する。
- **管理者ポリシー**: 企業利用向けに managed policy を設け、ユーザー設定より強い制約として自動承認、
  non-loopback transport、未承認 marketplace、MCP/OAuth を禁止できるようにする。

### 性能設計

- **非同期パイプライン**: stdout 読み取り、JSON 解析、response 解決、notification dispatch、UI 更新を
  `Channel<T>` で分離し、キャンセルと backpressure を明示する。
- **UI 更新のバッチ化**: ストリーミング delta は 50-100ms 単位でまとめて描画し、WPF/Remote UI の
  UI thread を高頻度更新から守る。
- **巨大出力の制限**: command output、reasoning summary、diff はメモリ上限を持つ。上限超過時は折りたたみ、
  truncated 表示、または一時ログファイルへの退避を行う。
- **キャッシュと invalidation**: `model/list`、`skills/list`、`plugin/list`、`app/list` はキャッシュし、
  `skills/changed` などの通知や明示 reload で再取得する。
- **障害時 fail fast**: app-server 終了、protocol mismatch、timeout 時は pending RPC をすべて失敗させ、
  Visual Studio の UI をブロックしない。

## 5. Codex App Server プロトコル対応マッピング

| 機能 | app-server メソッド/通知 |
|------|--------------------------|
| 接続初期化 | `initialize` → `initialized` |
| 会話開始/再開/分岐 | `thread/start` / `thread/resume` / `thread/fork` |
| 履歴一覧・読み取り | `thread/list` / `thread/read` / `thread/turns/list` |
| ターン実行 | `turn/start`（`input`: text/image/localImage） |
| ストリーミング | `turn/started`・`item/started`・`item/*/delta`・`item/completed`・`turn/completed` |
| 中断/追記 | `turn/interrupt` / `turn/steer` |
| 差分・計画 | `turn/diff/updated` / `turn/plan/updated` |
| レビュー（`/review`） | `review/start`（`uncommittedChanges`/`baseBranch`/`commit`/`custom`） |
| 圧縮（`/compact`） | `thread/compact/start`（`contextCompaction` item） |
| ゴール（`/goal`） | `thread/goal/set` / `get` / `clear` |
| シェル実行 | `thread/shellCommand` / `command/exec`（sandbox） |
| モデル一覧 | `model/list`（picker UI） |
| スキル | `skills/list` / `skills/config/write` / `skills/changed` |
| プラグイン/マーケット | `plugin/list` / `plugin/install` / `marketplace/add` |
| アプリ（コネクタ） | `app/list` / `$<app-slug>` mention |
| MCP | `mcpServerStatus/list` / `mcpServer/tool/call` / `mcpServer/oauth/login` |
| 承認 | `item/commandExecution/requestApproval` / `item/fileChange/requestApproval` |
| 設定 | `config/read` / `config/value/write` / `config/batchWrite` |

## 6. UI 仕様（GitHub Copilot 拡張準拠）

- **チャットツールウィンドウ**: 会話履歴、ストリーミング応答、推論サマリー折りたたみ、
  コマンド実行ログ、差分プレビュー（承認/拒否ボタン付き）、計画（plan）ステップ表示。
- **入力欄**: スラッシュコマンド補完、`@`/`$` でスキル・アプリ mention、モデル/努力度セレクタ。
- **インライン補完**: エディタ内ゴースト テキスト（Phase 4 で検討、初期は任意）。
- **テーマ対応**: `EnvironmentColors` / `VsResourceKeys`、テーマ変更に追従。
- **ローカライズ**: 英語ソース + 日本語リソース（英語フォールバック）。

## 7. apm によるエージェント資産管理

`apm.yml` に以下を宣言し、`apm install` で再現可能にする。`apm.lock.yaml` をコミットする。
対象 runtime はディスク使用量を抑えるため必要な **Codex / GitHub Copilot / Claude** のみに限定する。

```yaml
name: codex-visual-studio-extension
version: 0.1.0
target:
  - codex
  - copilot
  - claude
dependencies:
  apm:
    # 拡張開発用エージェント（ユーザー指定）
    - tfsugjp/skills/.github/agents/visual-studio-extension.agent.md
    # awesome-copilot からのスキル/プラグイン/エージェント（例、要件に応じ確定）
    - github/awesome-copilot/agents/api-architect.agent.md
  mcp:
    # 必要に応じて GitHub MCP 等
    - name: io.github.github/github-mcp-server
      transport: http
```

手順：
1. `apm marketplace add github/awesome-copilot`
2. `apm search "<keyword>@awesome-copilot"` で候補確認
3. `apm install <package>@awesome-copilot` で導入（`apm.yml`/`apm.lock.yaml` 更新）
4. `apm-policy.yml` でトランジティブ MCP/Unicode をガバナンス
5. CI に `microsoft/apm-action` を組み込み再現性を担保

セキュリティ方針：
- `apm.lock.yaml` を必ずコミットし、CI では lockfile から再現する。
- 導入元 marketplace は allowlist 化し、未知の MCP/plugin は既定で無効化する。
- `apm audit` を CI の必須チェックにし、Unicode spoofing、transitive MCP、未固定参照を検出する。
- plugin/agent/skill の有効化はユーザーまたは管理者ポリシーで明示する。

## 8. フェーズ分割

| Phase | 目的 | 主な成果物 |
|-------|------|-----------|
| **Phase 0** | リポジトリ準備・PoC | 公開リポジトリ初期化、`apm.yml`、`codex app-server` 起動疎通 PoC |
| **Phase 1** | プロトコル基盤 | `AppServerClient`（JSON-RPC/JSONL）、initialize→thread→turn の最小往復 |
| **Phase 2** | チャット UI（MVP） | ツールウィンドウ、ストリーミング表示、承認ハンドリング |
| **Phase 3** | スラッシュコマンド/スキル | SlashCommandRouter、`/review` `/compact` `/goal`、`skills/list` 連携 |
| **Phase 4** | 拡張機能・統合 | インライン補完、モデルピッカー、MCP/アプリ、設定 UI |
| **Phase 5** | パッケージング/品質 | VSIX 検証、ローカライズ、CI、apm 統合、ドキュメント |

各フェーズの詳細タスクは `task.md` を参照。

## 9. 主要リスクと対応

- **VisualStudio.Extensibility の .NET 8 対応範囲**: Phase 0 で検証。ギャップは in-proc
  (.NET Framework 4.7.2) フォールバック。
- **app-server プロトコルのバージョン差**: `codex app-server generate-json-schema` で
  実行中バージョンのスキーマを取得し、クライアントの型を検証。
- **承認フローの UX**: 破壊的操作は必ず承認 UI を経由。`acceptForSession` 等の粒度を UI に反映。
- **`clientInfo.name`**: エンタープライズ利用時は OpenAI の known clients 登録が必要になる点を明記。
- **サンドボックス/権限**: `thread/shellCommand`・`command/exec` はサンドボックス外実行があるため
  既定で承認必須にする。
- **UI フリーズ/メモリ増加**: streaming delta、command output、diff を直接 UI に追記し続けると
  Visual Studio が重くなるため、バッチ化・仮想化・出力上限を必須にする。
- **秘密情報の漏えい**: stderr、command output、diff、telemetry に credential が含まれる可能性があるため、
  保存・表示・送信前に redaction する。
- **WebSocket transport の露出**: WebSocket は将来検討に留め、使用時は loopback 限定と認証必須にする。
- **apm/plugin/MCP の supply chain**: lockfile、allowlist、audit、既定無効化で未検証資産の実行を防ぐ。

## 10. 参考

- Codex App Server: https://developers.openai.com/codex/app-server
- Codex IDE Slash commands: https://developers.openai.com/codex/ide/slash-commands
- Codex CLI Slash commands: https://developers.openai.com/codex/cli/slash-commands
- 拡張開発エージェント: https://github.com/tfsugjp/skills/blob/main/.github/agents/visual-studio-extension.agent.md
- Microsoft apm: https://github.com/microsoft/apm / https://microsoft.github.io/apm/
- awesome-copilot: https://github.com/github/awesome-copilot
