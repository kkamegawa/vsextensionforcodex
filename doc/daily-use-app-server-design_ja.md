# 日常利用の App Server 機能 — Issue #155

日付: 2026-10-08
トラッキング: [Issue #155](https://github.com/kkamegawa/vsextensionforcodex/issues/155)、[Issue #149](https://github.com/kkamegawa/vsextensionforcodex/issues/149) の配下。全体計画は [Codex App Server update and remote connection](app-server-update-plan_ja.md) の Phase 6。
状態: 設計承認済み。

## 概要

CLI 0.159.1 stable を対象とし、0.155.1 の回帰 fixture を用いて、#152（マッピングと所有者）、#153（履歴と保存済み添付メタデータの復旧）、#154（質問、権限、MCP 対話）で導入済みの基盤を拡張する。基準は commit `69deda1`（PR #169 マージ済み）、Worker contract v20 とする。統合時に次の利用可能な contract 版を割り当てる。CLI、SDK、runtime、package は更新しない。

既存のモデルカタログ、既定／非表示の既定値、推論レベル、service tier と、継承／永続／次ターン設定を再利用する。構造化 `turn/plan/updated` step、composer 添付と `localImage`、#153 の上限付き添付ページング・tombstone・single-flight 復旧、#154 の MCP 認証・elicitation も再構築せず再利用する。

## Plan と状態

- `turn/plan/updated` を構造化 step の snapshot として維持する。item ごとに上限付き暫定テキストを `item/plan/delta` から追加する。完了 plan item が暫定テキストを置き換え、最終テキストは delta の連結結果と異なる場合がある。テキストを解析して重複 step を作らない。
- 固定 stable schema では `item/plan/delta` は EXPERIMENTAL と記述されている。任意機能として扱い、delta なしでも完了 plan を正しく表示し、contract file では experimental-described と記録する。
- 完了状態を重複・遅延 delta および履歴／live の重複より優先する。plan テキストは UTF-8 で 64 KiB、構造化 step は 200 件に制限し、既存の履歴／通知上限を再利用して 50–100 ms で更新をまとめる。
- `thread/status/changed`、`configWarning`、`model/rerouted`、`model/verification`、`item/mcpToolCall/progress` は上限付き notice 領域に表示する。スレッドごとに最大 50 件、notice あたり 4 KiB とし、kind と identity で集約する。接続準備状態と thread status は別に表示する。`model/verification` は情報表示のみであり、回答やネイティブ検証を開始しない。

## カタログと入力受け入れ

- 実行時カタログを正本とする。`max`／`ultra` は広告された場合だけ受け入れ、既定モデルをハードコードしない。
- model projection に上限付き `inputModalities` と `availableAccessPrograms` を追加する。`inputModalities` 省略時は schema の既定（text、image）を使う。明示的な空配列や未知値は対応許可とみなさない。
- composer と Worker の両方で有効モデルを検証し、`turn/start` 直前にも確認する。未対応入力は理由付きで composer に残し、モデルを暗黙に変えない。画像以外のファイルは `mention` 入力（対応付け済み server path 参照）のままとし、text 対応だけを必要とする。画像には image 対応が必要。汎用 file modality はなく、audio と未知 modality は表示専用とする。

## 明示的な shell

- 唯一の入口を `/shell [--timeout-ms N] -- <command>` とする。候補選択では実行しない。`/shell` を9番目の組み込みコマンドとして追加し、skill を除外せず最大9件の組み込み候補を表示する。
- `--` より後のコマンドを正確に保持する。空コマンド、重複／未知オプション、負数、不正値、`int64` 超過 timeout を拒否する。省略時は server 既定（1時間）、0 は即時 timeout、値なしは無制限。
- Join 済みで現 owner に属する idle thread だけを対象にする。確認画面に接続／profile、thread、正確なコマンド（不活性テキストとして表示し、raw 値をログに書かない）、server cwd、timeout、および 0.159.1 の `thread/shellCommand` が常に sandbox 外の full access で動くことを表示する。
- Execute 後に対象、generation、cwd を再検証し、ローカルで `IApprovalPolicyEngine` を評価する。ポリシー拒否は上書きできない。Full access や過去の許可で確認を省略しない。
- 空の RPC 応答は受付確認であり完了ではない。RPC 応答期限は実行 timeout と独立させる。イベントは実際の thread/turn/item ID で表示し、要求との相関を断定しない。
- thread あたり shell 送信を1件だけ保留できる。確定応答またはエラーで解除する。応答 timeout／切断後は generation 終了までロックを維持する。新 generation でも `thread/status` が idle と報告した後だけ送信可能とする。
- dispatch 前の取消では何も送らない。dispatch 後に要求専用 Stop を送らず、時間やテキストから中断を推測しない。再送、`command/exec` への fallback、ローカル process 起動を行わない。

## 型付き結果とローカルファイル操作

- project text、MCP content、`imageView`／`imageGeneration`、`fileChange` を型付き上限パーツに投影する。generic JSON は理由を示す fallback とする。server truncation とローカル表示上限を別々に追跡する。
- raw payload と server path は Worker 内に保持する。Remote UI には安全化済み metadata と owner/generation に結び付いた不透明な action ID だけを渡す。
- 変更ファイルリンク、artifact、Open、Reveal は `RemotePathMapper` と `LocalPathBoundary` を通し、操作直前に存在、型、owner、symlink、junction を再検証する。raw server path を開かない。
- PNG/JPEG のみプレビューする。MIME と signature の一致を要求し、全デコード前に入力を 10 MiB 以下、寸法を 4,096 × 4,096 以下、画素数を 1,600 万以下に制限する。owner/generation ごとに後片付けする。SVG/HTML、remote fetch、自動 browser 起動、raw URI binding は許可しない。`fileId` は不透明値とし、`mcpAppUi` は埋め込み web runtime ではなくテキスト fallback を使う。

## 保存済み添付

- 保存済み添付は metadata 記録であり、upload や composer chip ではない。モデル入力へ自動挿入しない。wire contract は `attachmentType`、`identityKey`、任意 `payload` のみ。
- クライアント所有 type を `relaycodex.file.v1` とし、payload を `{ version: 1, serverPath, mimeType, displayName }` とする。`serverPath` は server domain の絶対正規化パス。`identityKey` は初期化時に得た server OS の規則で正規化した server path の version prefix 付き SHA-256 とし、local profile mapping に依存させない。
- `text/plain`、`application/pdf`、`image/png`、`image/jpeg` を認識し、preview は PNG/JPEG のみ。未知 type/version と不正 payload は理由を示して表示し、読み取り専用とする。
- Add 時に source、保護ディレクトリ規則、profile/root mapping、物理的 containment を再検証し、既存ファイルを上書きせず created/existing を尊重する。Remove は type と identity key を送り、確認とポリシー評価を必須にする。owner/profile/root と payload provenance は検証するが、対象ファイルの存在は要求しない。未存在の削除は成功扱い。
- #153 の上限と reconcile を維持する。limit 50、1ページ100件、thread あたり有効100件、payload 64 KiB、type/key 各256 byte。NotSent、確定、OutcomeUnknown を記録する。冪等性は再試行を許可しない。不確実な結果は read-only list で照合し、因果関係を断定しない。

## ローカル Windows sandbox 設定

- 初期化後に Windows と確認された、所有するローカル stdio だけに提示する。remote profile には提示しない。
- elevated／unelevated mode を明示させて Start する。任意の `cwd` は開いている solution の root を `LocalPathBoundary` で検証し、Start 前に表示する。solution がない場合は null。
- 状態は Not observed、Starting、Running、Succeeded、Failed、Unsupported、OutcomeUnknown とする。`started=true` は成功を意味せず、`started=false` も成功を否定しない。完了通知が応答より先の場合もある。進行表示は不定とする。
- failure や outcome unknown 後も generation ごとに1回だけ試行できる。旧世代や未要求の完了通知を無視する。エラーは上限化・秘匿化し、自動再試行しない。

## 0.159.1 追加フィールドと対象外

`availableAccessPrograms` は読み取り専用で表示する。`disabledPluginIds` は plugin editor なしで表示する。MCP の `serverName`、`httpOrigin`、`serverCapabilities` はフィルター済み status metadata として扱う。`mcpAppUi` はテキスト fallback を持つ。`image.fileId` は不透明値とする。`flexUnavailable` と `tooManyDenials` は再試行せず区別して表示する。`promax` は quota なしで表示する。`rollout/compress`、daemon/worktree、Realtime、dynamic tools、ExternalMessage、plugin import/editor、native verification の成功経路、attestation は対象外。

## 作業パッケージ

| パッケージ | 内容 | 依存 |
|---|---|---|
| P0 | contract 版、method 登録（stable／experimental）、固定 schema fixture、型付き DTO、payload registry | — |
| P1 | plan delta/final、thread/config/model/MCP notice、Bridge の両側での modality 判定、追加 field | P0 |
| P2 | 型付きパーツ、PNG/JPEG preview lifecycle、操作時検証付き changed-file Open/Reveal | P0、P1 envelope |
| P3 | `relaycodex.file.v1` reader/writer、明示的 add/remove、既存 store の outcome tracking | P2 |
| P4 | `/shell` parser、確認、policy gate、dispatch、保留ロック | P0、P1 |
| P5 | ローカル Windows 設定 state machine | P0 |
| P6 | 統合、サブエージェント review、全検証、スクリーンショット | P1–P5 |

## 対象コンポーネント

Worker: `CodexSessionService`、`WorkerRpcService`、notification parser。Contract: `WorkerContracts`、`InteractionContracts`。Extension: `WorkerBridge`、`ChatViewModel`、tool window XAML、`FilePickerService`。共有: `RemotePathMapper`、`LocalPathBoundary`、`IApprovalPolicyEngine`、`SafeMarkdownService`、`ISecretRedactor`。Contract manifest: `app-server-contract.json`。

## 検証基準

Fake App Server と固定 0.159.1／0.155.1 fixture を使う。delta なしの完了 plan、late delta より先の final、status burst、catalog load 中の owner 切替を検証する。未対応 modality が composer に残ることも確認する。shell の parse／timeout／policy 拒否／dispatch 前 cancel／Stop なし／他 client の turn／pending lock 解除／再送なしを検証する。型混在結果、不正・過大 media、表示後操作前の junction escape を確認する。添付の created/existing、未存在 remove、古い page、未知 type、missing target、reconnect/history/fork、不確実な送達を検証する。sandbox の応答前完了、`started=false`、未対応 method、cwd 検証、generation 終了、remote 拒否も確認する。

対象テストから Core/UI 全テスト、warning-free Debug/Release build、固定 schema と used-method 検査、VSIX DTO/XAML integrity へ進む。Experimental Instance の Light、Dark、High Contrast、狭い幅、keyboard/focus、accessibility をスクリーンショットで確認する。スクリーンショットがなければ未達とする。全証跡が揃うまで Issue #155 は open のままとし、Issue #156 の release gate を維持する。
