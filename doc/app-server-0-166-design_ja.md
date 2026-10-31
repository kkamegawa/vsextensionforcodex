# Codex App Server 0.166.0 対応設計

[English](app-server-0-166-design.md)

追跡: [Issue #240](https://github.com/kkamegawa/vsextensionforcodex/issues/240) と子Issue [#241](https://github.com/kkamegawa/vsextensionforcodex/issues/241)、[#242](https://github.com/kkamegawa/vsextensionforcodex/issues/242)、[#243](https://github.com/kkamegawa/vsextensionforcodex/issues/243)、[#244](https://github.com/kkamegawa/vsextensionforcodex/issues/244)。2026-10-31 承認。

## 目的

Visual Studio拡張機能をCodex App Server 0.166.0で検証し、0.165と0.166の機能のうちVisual Studioで役立つものを追加します。最低対応バージョンは0.161.0のままです。作業は段階ごとに提供するため、互換対応が機能追加を待つことはありません。

判断は[ADR-020](adr/ADR-020-app-server-0-166_ja.md)と、同日のADR-011追補に記録しています。一次資料は上流リリース `rust-v0.164.0`、`rust-v0.165.0`、`rust-v0.166.0` です。

## 1. 範囲と対応バージョン

### 採用する変更

| 導入版 | 変更 | 扱い | 段階 |
| --- | --- | --- | --- |
| 0.164 | 既存の対応 | 維持し、回帰対象にする | 1 |
| 0.161 | `thread/list.cwd` の配列指定 | worktree範囲に利用する。版による制限なし | 2 |
| 0.165 | 会話中の推論強度変更、Vertex AI、Terra | 既存の送信経路と動的モデル一覧で対応する | 1 |
| 0.165 | 記憶の参照 | Experimental有効時に利用する | 3 |
| 0.166 | `detached` レビュー配信の削除(openai/codex#57667) | `Detached` を廃止し、別会話レビューを新機能として追加する | 1, 2 |
| 0.166 | 項目時刻、構造化プロバイダーエラー、`policyCapacityUnavailable` | 投影し、復旧導線を設ける | 1 |
| 0.166 | スレッドのアンロード既定60秒(openai/codex#57242) | 回帰対象にする | 1 |
| 0.166 | 質問のMarkdown、会話の選択と検索 | 実装する | 2 |
| 0.166 | USD利用額、ネットワーク診断 | 実装する | 2 |
| 0.166 | 関連分岐(openai/codex#57256) | Experimental有効時に利用する。導入版は0.166とする | 3 |
| 0.166 | ワークスペース切替 | Experimental有効時に利用する | 3 |

### 採用しないもの

- Vertex AIとBedrockの認証ウィザード、および `account/gcp/setup`。契約差分への記録のみとします。
- `turn/moderationMetadata` の書き換え候補。メタデータは型がなく不安定なため記録のみとし、拡張機能は理由と復旧案内だけを表示します。
- メモリv2の既定変更とインポート。
- TUI固有の操作と内部実行基盤の修正。

### バージョン方針

| 役割 | バージョン |
| --- | --- |
| 最低対応(ローカル、リモート) | 0.161.0 |
| 検証対象、推奨 | 0.166.0 |
| 回帰 | 0.165.0、0.164.0、0.163.0、0.162.1、0.161.0 |
| 非対応 | 0.161.0未満のすべて |

### 機能階層

| 階層 | 条件 | 機能 |
| --- | --- | --- |
| Unsupported | 不明、または0.161.0未満 | 接続を拒否する |
| Baseline | 0.161.0以降 | 既存機能、別会話レビュー、worktree範囲、質問Markdown、会話テキスト |
| 0.163 | 0.163.0以降 | 既存の0.163機能 |
| 0.164 | 0.164.0以降 | 既存の0.164機能 |
| 0.165 | 0.165.0以降 | Experimental交渉時の記憶参照 |
| 0.166 | 0.166.0以降 | USD利用額、ネットワーク診断、Experimental交渉時の関連分岐とワークスペース切替 |

`GetFeatureTier` は各階層の導入版と比較し、検証対象バージョンとは比較しません。検証対象より新しいバージョンは0.166階層を使います。Experimental APIは既定で無効のままとし、Experimental機能には導入版と交渉済みcapabilityの両方を必須とします。

## 2. 別会話レビュー

`/review --new-thread [uncommitted|base <branch>|commit <sha>|custom <instructions>]` は新しい会話でレビューを実行します。このオプションなしの `/review` は、これまでどおり現在の会話でレビューします。オプションは入力候補とヒントに表示します。

- **プロトコル:** すべての対応版で、`thread/start` のあと、新しいスレッドIDへ `inline` 配信の `review/start` を送ります。`detached` は送りません。
- **引き継ぐ設定:** 作業フォルダー、モデル、service tier、承認ポリシー、承認レビュアー、sandboxモードまたは権限プロファイルです。権限は広げません。引き継げない設定があれば、会話を作らず理由を表示します。
- **履歴:** 新しい会話は元の履歴を引き継ぎません。履歴が必要な場合は `/fork` のあとに指示します。
- **選択:** レビューが開始するまで、UIの選択と `ActiveThreadId` は元の会話のままです。成功すると新しい会話へ切り替えて参加し、進行状況を表示します。
- **失敗:** 会話の作成後に失敗した場合は、会話を残して状況を説明します。自動削除も自動再送もしません。
- **結果不明:** 切断や応答なしは開始済みとみなしません。履歴の確認を案内します。
- **重複:** 実行中は2回目の開始を防ぎます。

## 3. 関連分岐

Experimentalが有効で0.166以降の場合、「関連する分岐を作成」を提供します。

- 親には「関連する会話」一覧を表示して子へ移動でき、子からは親へ移動できます。
- 子は通常の履歴一覧に表示しません。
- 対象は永続paginated履歴かつローカル保存の会話です。拒否された場合は理由を表示します。
- 子の履歴に親のターンは含まれません。
- 子が残っている間、親は削除できません。
- 子は `thread/list.attachedToThreadId` で取得します。`parentThreadId` や `forkedFromId` で代用しません。

## 4. 履歴範囲と記憶

### 履歴範囲

履歴一覧には「すべて」「現在の作業フォルダー」「同じリポジトリのworktree」の範囲があります。既定は「すべて」です。

- worktree範囲は、正規化後に異なるworktreeが2件以上あるローカル接続でのみ表示します。リモート接続とGit管理外では表示しません。上流の `repository_cwd_filter` と同じ動作です。
- 相対フォルダーを各worktreeのパスへGitの読み取り専用コマンドで解決し、配列として `thread/list.cwd` へ渡します。
- 照合は完全一致のため、区切り文字、大文字小文字、`MapLocalPathForServer` の正規形を送信前に統一します。
- 対象はページング開始時に固定します。範囲を変更するとカーソルを破棄して再取得します。
- 一覧にない会話を削除済みとして扱いません。

### 記憶の参照

参加済みの会話に「この会話が参照する記憶」を提供します。明示操作で `thread/memory/read` を呼び出し、結果を別のペインに表示します。

- 履歴の閲覧で会話を再開しません。
- 編集も自動送信もなく、内容をログに記録しません。
- `summary` が `null` の場合は「参照可能な記憶はありません」と表示し、原因を断定しません。

## 5. ワークスペースとプロバイダー

### ワークスペース切替

アカウント領域に現在のワークスペース名と「切り替え」を表示します。一覧は `accountSession/list` を `refreshWorkspaceMetadata: true` で取得します。複数アカウントの管理は行いません。

- **利用条件:** 実行中のターン、未解決の承認、進行中のログインがある間は無効です。自動で中断しません。
- **確認:** 切替により読み込み済みの会話がすべて止まるため、ダイアログで確認します。リモート接続では、他のクライアントに影響する可能性も表示します。現在のワークスペースを選んだ場合は何もしません。
- **ログイン:** ローカル接続は `allowBrowserLogin: true` を送ります。`loginId` が返るとSSOへ進み、既存の安全なブラウザー経路で `authUrl` を開いて `account/login/completed` を待ちます。取り消しには `account/login/cancel` を使います。リモート接続は `allowBrowserLogin` を送りません。
- **結果不明:** `accountSession/list` で確認します。切替を自動で再試行しません。
- **アカウント指紋:** `account/read.workspaceRouting.chatgptAccountId` のワークスペースIDを加えます。Experimentalが無効の間、外部での切替は検知対象外です。
- **成功後:** 所有者境界を更新し、保留中の操作を取り消し、モデル一覧、使用量、MCP認証状態を再取得します。
- **閉じた会話:** `thread/closed` で閉じた会話は再開可能として表示し、自動で再開しません。

### プロバイダー

Vertex AI、Bedrock、Terraはサーバーのモデル一覧から取得します。不一致があっても、設定の上書きや自動切替は行いません。

`thread/resume` が `ModelProviderError` で拒否された場合は、`thread/read` により読み取り専用で会話を開き、復旧方法を案内します。割り込みとgoalの一時停止・クリアは使用可能なままです。

## 6. 質問、テキスト、使用量、診断

### 質問

質問文、選択肢の説明、回答の表示には、安全なMarkdown経路 `ToSafeTextAndBlocks` を使います。送信値は既存の `OptionId` のままで、最後のSubmit手順も維持します。

### 会話テキスト

「会話テキストを開く」は、Visual Studioの読み取り専用エディタータブに会話を表示します。選択、Ctrl+C、Ctrl+F、F3、単語・行の移動が使えます。

- **スナップショット:** 履歴全体をページングで読み込み、固定のスナップショットにします。進捗表示とキャンセルに対応します。失敗時に不完全な内容は開かず、内容はライブ更新しません。
- **更新:** 更新のたびに別のスナップショットファイルを作ります。
- **関連分岐:** 子には子の履歴だけが含まれます。
- **内容:** 安全化し、秘密情報を除去した表示テキストから生成します。画像などは説明文で示します。
- **保存先:** `ArtifactPreviewCache` の所有者分離を再利用し、現在のユーザーだけが読める場所に置きます。ファイル名に会話タイトルは使わず、タブ名は `CustomDocumentWindowCaption` で設定します。
- **読み取り専用:** `IsUserReadonly` を使います。
- **清掃:** `SubscribeAsync` と `ClosedAsync` で閉じたファイルを削除します。起動時に24時間を超えた未使用ファイルを削除します。
- **依存関係:** 新しいNuGetパッケージは不要です。

### 項目時刻

項目の詳細に時刻を表示します。`item.startedAtMs` と `completedAtMs` を優先し、エントリー側の値へフォールバックします。旧形式も有効で、どのフィールドも省略または `null` を許容します。

### 使用量

0.166以降では、`account/usage/read` に `threadId` を渡します。

- 実額のUSDは `threadUsage.nativeUsageUsdMicros` が返った場合のみ表示します。
- `estimatedUsageUsdMicros` と `estimatedUsageCreditsMicros` は推定として表示します。
- 値は整数のmicro-dollarで保持します。
- 未取得や `null` の値をゼロと表示しません。

### ネットワーク診断

0.166以降のローカル接続では、解決済みのCLIパス、タイムアウト、`ISecretRedactor` を使って `codex doctor --network-only` を実行します。リモート接続では、サーバー上で実行する手順を表示します。

## 7. 契約と実装構造

### Worker契約 v27

履歴の絞り込み条件、分岐条件、記憶の読み取り、ワークスペースの一覧と切替、利用額、構造化エラー、項目時刻、レビュー配置を追加します。質問のwire形式は変更しません。

`ReviewDelivery` は、`CurrentThread` と `NewThread` を持つ `ReviewPlacement` に置き換えます。`NewThread` は `thread/start` と `review/start` を1つのWorker操作として実行します。

- すべての手順で所有者世代を照合します。
- 成功するまで `ActiveThreadId` は変更しません。
- 結果には、会話が作成されたかどうかを含めます。

### 新規RPC呼び出し

| メソッド | surface | 再試行方針 |
| --- | --- | --- |
| `thread/memory/read` | Experimental | `ReadOnlyRequestAllowlist` に追加する |
| `threadId` 指定の `account/usage/read` | Stable | `ReadOnlyRequestAllowlist` に追加する |
| `refreshWorkspaceMetadata: true` の `accountSession/list` | Experimental | 再試行しない |
| `accountSession/switch` | Experimental | 再試行しない |
| `account/login/cancel` | Stable | 再試行しない |

いずれも `methodsByVersion` に記録します。関連分岐は `thread/fork.attachToSource` と `thread/list.attachedToThreadId` を使います。

### エラーの投影

| 経路 | 取得元 | 値 |
| --- | --- | --- |
| 通知と履歴 | `error`、`turn/completed`、履歴項目 | `policyCapacityUnavailable`。既存の安全ポリシー理由と区別する |
| JSON-RPCエラーの `data` | 拒否された要求 | `modelProviderMismatch`、`modelProviderNotConfigured`、`modelProviderPolicyConflict`。未知の値は通常のエラーとして表示する |

サイズ制限と秘密情報の除去は維持します。

### 世代の照合

すべての非同期処理で、所有者、接続、要求、選択の世代を照合します。既読化は明示操作でのみ行います。

### 契約マニフェスト v4

`app-server-contract.json` は既存の実行ファイル情報を維持し、`codex-package-x86_64-pc-windows-msvc.tar.gz` の資産名とSHA-256を追加します。インストーラーと実機検証は完全パッケージを使います。

- 補助実行ファイルはマニフェストに列挙しません。`codex-package.json` を正とします。
- npm配布物に含まれないため、`codex-exec-server` は実行時に必須としません。
- 全6版のstableとexperimentalのスキーマを生成して比較し、未採用の機能も差分に記録します。

## 8. 提供段階

各段階は単独でリリースできます。

| 段階 | 内容 | 追跡 |
| --- | --- | --- |
| 1. 互換対応 | バージョン階層、完全パッケージ、スキーマ差分、Worker v27、`Detached` 廃止、時刻とエラーの投影、プロバイダー拒否時の読み取り専用表示、アンロード回帰 | [#241](https://github.com/kkamegawa/vsextensionforcodex/issues/241) |
| 2. Stable機能 | 別会話レビュー、worktree範囲、質問Markdown、会話テキスト、USD利用額、ネットワーク診断 | [#242](https://github.com/kkamegawa/vsextensionforcodex/issues/242) |
| 3. Experimental機能 | 関連分岐、記憶参照、ワークスペース切替 | [#243](https://github.com/kkamegawa/vsextensionforcodex/issues/243) |
| 4. 統合と記録 | 段階ごとの実機・UI受入、VSIX検査、設計・実装・作業記録 | [#244](https://github.com/kkamegawa/vsextensionforcodex/issues/244) |

## 9. 検証と完了条件

- **契約:** 全6版の両surface、版境界とprerelease、無効時にExperimental要求を送らないこと、未対応の保存方式での分岐拒否。
- **レビュー:** すべての対応版での別会話レビュー、権限を広げない設定の引き継ぎ、引き継げない場合の中止、作成後の失敗、切断と結果不明、重複防止、失敗時に元の会話が選択されたままであること、履歴一覧での見え方。
- **履歴と分岐:** 複数worktree、サブフォルダー、Gitなし、リモートでの非表示、パス正規化、範囲変更中の応答、ページング、関連分岐とsubagentの区別、親子間の移動。
- **アカウント:** 同じメールとプランのワークスペース間の切替、確認ダイアログ、SSOと取り消し、失敗、結果不明、外部変更、遅延応答、キューの失効、切替後の `thread/closed`。
- **表示:** 質問Markdownと送信値、履歴全体の選択・コピー・検索、日本語、長大な履歴、更新時の別ファイル、閉じたときの削除、清掃、時刻の欠損、USDの実額・推定・ゼロ・未知の値。
- **実行状態:** 会話中の推論強度変更、再開・分岐後の承認設定、StopとPlan切替後の保留操作、安全ブロック、プロバイダー拒否時の読み取り専用表示、60秒アンロード後の `thread/closed` と再開。CLI内部の修正は再実装しません。
- **品質:** Core・UI・Fakeテスト、警告ゼロのDebugとReleaseビルド、VSIX内容の検査、0.165、0.166、既存版への実CLI接続。
- **UI受入:** 実際のVisual Studio Experimental Instanceで、Light、Dark、High Contrast、日本語、狭い幅、キーボード操作、Narratorを確認し、スクリーンショットと判定を記録します。

### 実機環境の線引き

段階1と段階2で未実施の確認が残っている場合は完了としません。次の環境は、Fakeテストに合格し、リリース記録に未検証と明記すれば完了とします。

- SSOが必須のワークスペース。
- 同じメールとプランの複数ワークスペース。
- ネイティブUSDを返すプラン。
- Vertex AIまたはBedrockのみの環境。
