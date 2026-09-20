# Codex App Server 更新対応とリモート接続の計画

[English](app-server-update-plan.md)

> 2026-09-20 に承認された計画。実装は親 Issue [#149](https://github.com/kkamegawa/vsextensionforcodex/issues/149) と7件の子 Issue で追跡する。

## 概要

Visual Studio 拡張機能の既存 C#／`codex app-server` 連携を CLI 0.155.1 の契約へ更新し、起動済みのリモート App Server へ安全かつ明示的に接続できるようにする。ローカル stdio は既定として維持する。厳密な要求振り分け、イベント順序の安全性、リモート転送、ルート対応付けと認証主体ごとの状態分離、再接続と履歴復旧、保存済みスレッド添付、非同期質問と範囲を限定した権限、ネイティブ本人確認、MCP 対話と認証復旧、日常利用イベント、shell 実行、型付き成果物、Windows sandbox 状態、統合リリース検証を対象とする。

## 背景と判断

この拡張機能はすでに `codex app-server` を利用しているため、非推奨 MCP Server からの移行は不要である。Python SDK の `.params` や `HookMetadata.root` の変更も、この C# リポジトリには直接影響しない。一方、イベント順序、履歴読み取り、要求形式、capability の挙動、エラー処理は影響するため、App Server 契約に照らして検証する。

承認済みの運用モデルは次のとおり。

- **Extension → ローカル Worker** を維持する。Worker 内でローカル stdio または WebSocket を選択し、JSON-RPC の振り分け、制限、未完了要求の終了、シャットダウン処理を転送方式間で共有する。
- ローカル stdio を既定とし、子プロセスを所有する。
- リモートプロファイルは起動済みサーバーへ接続する。サーバーの起動・更新、SSH／トンネル管理、ファイル同期は外部の責務とする。
- 上流の WebSocket が実験的位置付けであるため、リモート転送は明示的に有効化する。
- Visual Studio ホストとリモートサーバーは同じ作業ツリーを参照できるものとする。設定したローカルルートとサーバールートを対応付け、拡張機能はファイル同期しない。
- リモートホストには `wss` を使用する。平文 `ws` は loopback に限定する。Bearer トークンはファイルから読み取り、証明書検証は無効化できない。

## 優先度とトラッキング

| 優先度 | 項目 | トラッキング |
|---|---|---|
| 最優先 | 要求を厳密に振り分け、未知の要求を汎用承認へ流さない | [#150](https://github.com/kkamegawa/vsextensionforcodex/issues/150) |
| 最優先 | 接続世代とターン順序の競合を解消する | [#150](https://github.com/kkamegawa/vsextensionforcodex/issues/150) |
| 高 | CLI 0.155.1 対象スキーマ、0.154.0 回帰比較、初期化メタデータ、capability 宣言・判定 | [#150](https://github.com/kkamegawa/vsextensionforcodex/issues/150) |
| 高 | 安全なリモート接続、接続所有権、診断、読み取り専用再試行 | [#151](https://github.com/kkamegawa/vsextensionforcodex/issues/151) |
| 高 | ローカル／サーバーパス対応付け、接続先／アカウント／認証主体／ルートごとの状態分離 | [#152](https://github.com/kkamegawa/vsextensionforcodex/issues/152) |
| 高 | 再接続、下書き保持、ページ履歴と添付復旧、配送不明な変更操作の扱い | [#153](https://github.com/kkamegawa/vsextensionforcodex/issues/153) |
| 高 | 非同期質問、権限の部分許可、ネイティブ本人確認、MCP フォーム／認証失効、秘密入力 | [#154](https://github.com/kkamegawa/vsextensionforcodex/issues/154) |
| 高 | 計画／スレッド／設定／モデル／MCP 状態、保存済み添付操作、型付き成果物表示 | [#155](https://github.com/kkamegawa/vsextensionforcodex/issues/155) |
| 中 | モデル modality、推論量、明示的 shell 実行、独立した timeout | [#155](https://github.com/kkamegawa/vsextensionforcodex/issues/155) |
| 中 | ローカル Windows sandbox 初期設定状態、対応付け済み画像／ファイル操作 | [#155](https://github.com/kkamegawa/vsextensionforcodex/issues/155) |
| リリース条件 | 契約、競合、復旧、リモート、対話、UI、ビルド、配布物、Experimental Instance の証跡 | [#156](https://github.com/kkamegawa/vsextensionforcodex/issues/156) |

## アーキテクチャ・セキュリティの不変条件

- App Server からの全メッセージを信頼しない入力として扱う。表示テキストをサニタイズし、ログを秘匿化し、テキスト、配列、履歴ページ、コマンド出力、画像プレビューに上限を設ける。
- 破壊的操作は既存の承認ポリシーを経由させる。
- 実装済みクライアント capability、既知の契約、読み取り専用 API の結果、`-32601` を組み合わせて機能対応を判定する。対応確認だけのために変更系 API を呼ばない。
- 配送結果が不明な場合、ユーザー入力、承認回答、MCP 送信、shell コマンド、その他の変更操作を自動再送しない。
- 接続／WebSocket 状態、スキル、モデル、使用量、承認記録、選択中会話、下書き、添付、復元履歴を、接続プロファイル、アカウント、認証主体、作業ルートごとに分離する。
- アカウントまたは認証主体が変わった場合、旧主体のリモートセッション、未完了要求、WebSocket 状態、モデルカタログ、キャッシュ、遅延イベントを無効化してから新しい主体を有効にする。
- リモート sandbox ポリシーはリモートサーバーが実施する。ローカルの保護ディレクトリ判定でリモートホストを保護できるとは扱わない。
- キャッシュ境界、承認の意味、継承／永続／次ターン設定に関する既存 ADR を維持する。変更が必要な場合は実装前に記録する。
- Bearer トークン本文を設定、Remote UI、会話、ログ、診断、テレメトリ、クラッシュテキストへ保存しない。

## Phase 1 — プロトコル契約と転送基盤

トラッキング: [#150](https://github.com/kkamegawa/vsextensionforcodex/issues/150)

### 契約・スキーマ基準

- **CLI 0.155.1 正式版**を対象契約に固定する。**CLI 0.154.0 正式版**は回帰比較元として保持し、ローカル alpha のスキーマをどちらの正式契約としても扱わない。
- 0.154.0 と 0.155.1 のそれぞれで標準スキーマと実験的スキーマを別々に生成し、構造差分を取得する。
- CLI バージョンと生成オプションをスキーマキャッシュのメタデータに保存する。どちらかが異なる場合は、既存ファイルを有効なキャッシュとして扱わず再生成する。
- 生成したスキーマは Git に含めない。
- 使用する全メソッド、必須フィールド、列挙値、null 許容フィールド、未知 item、追加フィールド、不正 payload に加え、確認した 0.154.0→0.155.1 の全差分を契約テストへ追加する。

### 初期化と機能判定

- サーバーのプラットフォーム種別／OS などの初期化メタデータを保持する。
- `initialize` が汎用的なサーバー capability 一覧を返すとは仮定しない。
- クライアント capability は end-to-end で実装済みのものだけ宣言する。
- 宣言 capability、既知の契約、読み取り結果、`-32601` を組み合わせて対応状況を判定する。
- 利用可否を確認するために変更を生じるメソッドを呼ばない。

### 要求振り分けとライフサイクル順序

- サーバー要求をメソッド名の完全一致と期待する payload 形式で振り分ける。
- 未対応要求にはプロトコル上適切なエラーまたは仕様上の拒否を返す。未知の要求を汎用承認や既存許可へ流さない。
- 接続世代、thread ID、turn ID、item ID、server-request ID ごとに状態を追跡する。
- `turn/start` 応答と `turn/completed`／`turn/started` 通知の前後逆転を吸収する。
- 接続終了時に、未完了のクライアント要求とサーバー要求を決定的にキャンセルまたは終了させる。
- 古い接続世代から遅れて届く応答、通知、解決イベントをすべて無視する。

## Phase 2 — 安全なリモート接続

トラッキング: [#151](https://github.com/kkamegawa/vsextensionforcodex/issues/151)

### 転送・プロファイルモデル

- ローカル Worker 構成を維持し、Worker 内で stdio または WebSocket を選択する。
- JSON-RPC 振り分け、メッセージ上限、キャンセル、未完了要求の終了、シャットダウン処理を転送方式間で共通化する。
- プロファイルに表示名、接続先、トークンファイルのパス、ローカルルート、サーバールート、有効状態、選択中プロファイルを保存する。
- トークン本文は接続時に Worker 内だけで読み取る。

### 接続先・所有権ポリシー

- リモート接続先は `wss` を受け入れ、平文 `ws` は loopback だけ受け入れる。
- WebSocket ハンドシェイク時に Bearer 認証を付与し、認証情報をログへ出力しない。
- 証明書検証を無効化する設定は提供しない。
- ローカルプロファイルは子プロセスの再起動を所有する。リモートプロファイルは接続だけを閉じ、操作名を「再接続」とする。
- connect、reconnect、close を直列化し、1つの接続世代だけを有効にする。

### 診断・再試行

- health endpoint は診断だけに利用する。health 成功を JSON-RPC や個別機能の利用可否とは扱わない。
- ローカルプロセス状態とリモート接続状態を区別する。
- 過負荷（`-32001`）後の指数バックオフと jitter は、明示的に冪等／読み取り専用の RPC にだけ上限付きで適用する。
- 変更操作は自動再試行しない。

## Phase 3 — パス対応付けと状態分離

トラッキング: [#152](https://github.com/kkamegawa/vsextensionforcodex/issues/152)

### パス領域と対応付け

- ローカルパスとサーバーパスを異なる型で表現する。
- 正規化したルートをパス要素単位で対応付ける。`C:\repo` が `C:\repo2` に一致してはならない。
- Windows／POSIX の区切り文字、大文字小文字、ルート同値性、`.`／`..`、長いパス、Unicode、ドライブ／共有、symlink／junction の挙動を定義する。
- 作業ディレクトリ、IDE コンテキスト、添付、`localImage`、変更ファイルリンク、ファイル成果物、ローカルで開く／表示する操作に同じ対応付けサービスを使用する。
- 対応付けできない添付は送信前に拒否し、対処可能な理由を表示する。
- サーバーパスをローカルファイルとして直接開かない。

### サーバー所有識別子と状態キー

- スキルパスはサーバーが提供した識別情報として保持する。Visual Studio ホスト上に存在することを要求しない。
- スキルキャッシュ、モデルカタログ、使用量、承認許可／監査、選択中会話、下書き、添付、履歴、WebSocket／セッション状態を接続先、アカウント、認証主体、作業ルートごとに分離する。
- 接続先、アカウント、認証主体、ルートが変わった場合、選択中状態を決定的に切り替えるかクリアする。
- 有効な接続世代と送信結果を、取得時の認証主体に結び付ける。アカウント切替、logout、所有主体変更時は、旧リモートセッションを閉じるか無効化し、未完了応答、WebSocket キャッシュ、モデルカタログ、通知を破棄する。
- リモート sandbox の実施をリモートサーバーの責務として扱う。

## Phase 4 — 再接続と履歴復元

トラッキング: [#153](https://github.com/kkamegawa/vsextensionforcodex/issues/153)

### 復旧状態と下書き保持

- 復旧処理を直列化し、「再接続中」と「履歴同期中」を別の状態として表示する。
- 自動復旧は最大5回とし、その後は手動再接続を表示する。
- Visual Studio の画面が存続する間、入力本文、添付、選択中スキル、次ターンのモデル／推論量／速度／personality 設定を保持する。
- この Phase では下書きの新しいディスク永続化を追加しない。

### 再初期化と履歴再構築

- 転送を再接続し、初期化を再実行して、選択中会話を再開する。
- 明示的な履歴読み取りで会話表示を復元する。対応する場合はページ取得を使用し、無制限の履歴を一度に実体化しない。
- 履歴同期中に届いた通知を保持し、履歴と通知を thread、turn、item ID で統合する。
- 完了 item の内容を以前の delta より確定的な情報として扱い、表示順序を安定させる。
- `thread/attachment/list` をページ取得して、スレッドを再開せずに保存済み添付を再構築する。ページと `thread/attachment/updated` 通知を添付 identity で統合し、MIME、payload、対応付け状態を上限付きの信頼しない入力として保持する。

### 配送不明な変更操作と複数クライアント所有

- 配送結果が不明なメッセージ、承認回答、MCP 応答、shell コマンド、その他の変更操作を自動再送しない。
- `thread/attachment/add` と `thread/attachment/remove` を明示的な変更操作として扱う。切断後に自動再送せず、結果不明状態をユーザーが確認できる形で保持する。
- 配送不明な入力を確認可能な状態で表示し、ユーザーが明示的に再試行できるようにする。
- 別のクライアントが会話を使用中の場合、履歴のみの表示、理由、明示的な再試行を提供する。
- 新しい世代が有効になった後は、古い世代の全イベントを破棄する。

## Phase 5 — 質問・権限範囲・MCP 対話

トラッキング: [#154](https://github.com/kkamegawa/vsextensionforcodex/issues/154)

### Worker 契約と一回答ライフサイクル

- Worker 契約を v15 から更新し、質問、権限要求、MCP 入力、本人確認、保存済み添付状態を別の型で表す。
- 接続世代と request ID をキーとする共通の未完了要求レジストリを使用する。
- 回答、キャンセル、timeout、切断、`serverRequest/resolved` の競合があっても、応答を最大1回に保証する。

### 非同期質問

- 質問を独立した回答カードとして表示し、処理が継続していても回答可能にする。
- 選択済み／既定候補は表示情報としてだけ保持する。フォーカス、既定値、時間経過、事前選択を回答として扱わない。
- 自由入力の「その他」や secret マーカーなど、サーバーの選択肢メタデータを扱う。

### 権限の部分許可とコマンド承認

- 要求されたネットワーク／ファイル権限のうち、選択した範囲だけを返す。
- 権限許可は既定でターン単位とし、セッションへの継続許可は明示選択を必要とする。
- サーバーが提示するコマンド承認の全選択肢と追加権限要求を表示する。
- ルール変更を伴う選択肢を汎用的な「承認」にまとめない。

### ネイティブ本人確認

- 実験的な `openai/userVerification` MCP elicitation は、ローカルの本人確認経路を完全に実装し、`experimentalApi` を有効化した場合だけ対応する。
- 型付きの `userVerification/status`、`userVerification/enroll`、`userVerification/verify`、`userVerification/cancel`、`userVerification/delete` を通じて確認する。ネイティブ UI を表示する前に challenge、title、description の上限を検証する。
- ネイティブ本人確認は対応するローカル stdio／in-process host に限定する。WebSocket や remote-control peer には宣言・転送せず、platform または transport が未対応の場合は理由を表示して拒否する。
- キャンセル、切断、認証主体変更、timeout、`serverRequest/resolved` を一回答の競合として扱う。ネイティブ操作を明示的にキャンセルし、解決後に遅れて届いた proof を破棄する。
- 本人確認 proof と credential 情報を会話、設定、ログ、診断、テレメトリ、クラッシュテキストへ出力しない。

### MCP フォーム・URL・秘密入力フロー

- 仕様化された MCP フォームのフィールド型に対応し、応答前に必須、型、範囲、選択肢を検証する。
- 未対応スキーマは理由を表示して拒否し、拡張フォーム capability を先に宣言しない。
- MCP の URL／ブラウザー認証は明示的なユーザー操作からだけ開き、完了後に状態を更新する。
- 認証または elicitation 後に、失敗した MCP ツール呼び出しを自動再試行しない。
- 保護された秘密入力経路を使用する。安全な経路がない場合は secret を含む要求を拒否する。
- `mcpServer/startupStatus/updated` の `failureReason: "reauthenticationRequired"` と OAuth 完了失敗を認証の終端状態として扱う。再ログイン／再接続の案内を表示し、再接続後に未完了 elicitation 状態をリセットし、新しい明示的なツール呼び出しを要求する。

## Phase 6 — 日常利用の App Server 機能

トラッキング: [#155](https://github.com/kkamegawa/vsextensionforcodex/issues/155)

### 計画と状態

- `item/plan/delta` を逐次表示し、重複ステップや無制限の増加を生じさせず、完了 plan item と統合する。
- スレッド状態、設定警告、モデル変更／追加確認、MCP 実行状態を用途ごとに区別した上限付き UI で表示する。
- 表示する全テキストをサニタイズし、診断情報を秘匿化する。

### モデル capability の扱い

- モデル ID、`max`／`ultra` を含む推論量、速度／service tier、入力 modality はモデルカタログを正本とする。
- 既存の継承、永続、一時的な次ターン上書きの意味を維持する。
- 未対応の画像／ファイル modality はターン開始前に説明または無効化する。

### 明示的な shell 実行

- `thread/shellCommand` を利用する `/shell [--timeout-ms N] -- <command>` を追加する。
- 明示的なユーザー操作からだけ実行する。
- 実行前に接続／プロファイル、正確なコマンド、作業ディレクトリ、サーバーが報告する sandbox 動作を表示する。
- コマンド実行期限と JSON-RPC 応答期限を分離する。
- timeout 省略時はサーバー既定、`0` は即時 timeout、負数は入力エラーとする。

### 型付き成果物と Windows sandbox 状態

- MCP 結果、画像、ファイル成果物を型付きの上限付きコンテンツとして表示する。
- サーバーが報告した切り詰めとローカル表示上限を区別する。
- 画像プレビューに上限を設け、対応付け済みファイルだけ操作可能にする。
- ローカル Windows sandbox の初期設定、進行、完了、対処可能な失敗理由を表示する。
- ローカル設定フローをリモートサーバーの設定エディターとして公開しない。

### 保存済みスレッド添付

- `thread/attachment/list` を cursor でページ取得し、サーバーの上限に従って、スレッドを再開せずに保存済み添付を表示する。
- 添付の追加・削除は明示的なユーザー操作からだけ `thread/attachment/add`／`thread/attachment/remove` を呼び、`thread/attachment/updated` 通知を重複行なく統合する。
- preview、open、add、remove を有効にする前に、対応 MIME、payload／size 上限、ローカル／サーバーパス対応付けを検証する。対応付け不能または未対応の添付は、理由を表示した非 open 状態にする。
- 重複 add と存在しない remove の冪等性を維持し、再接続、履歴復旧、非 ephemeral fork 後に添付状態を再構築する。

## Phase 7 — 統合検証とリリース準備

トラッキング: [#156](https://github.com/kkamegawa/vsextensionforcodex/issues/156)

### 契約と順序

- CLI 0.154.0 と 0.155.1 の標準／実験的スキーマを生成して構造比較し、0.155.1 の対象契約を代表的な実送受信の要求、応答、通知と照合する。
- 未知メソッド／item／列挙値、追加フィールド、不正 payload、必須フィールド欠落、null 許容の変化、スキーマキャッシュ無効化を検証する。
- 完了通知が開始応答より先に届く場合、履歴取得中通知、item 重複、resolved／response 競合、古い接続世代イベントを再現する。

### 復旧・転送・分離

- Worker 終了、通信断、認証失敗、トークン更新、過負荷、接続先／ルート／プロファイル変更、他クライアント所有を再現する。
- 下書き保持と、配送不明な変更操作が一切自動再送されないことを確認する。
- リモート `wss`、loopback `ws`、禁止されるリモート `ws`、証明書失敗、health／RPC 不一致、再試行上限、手動再接続を検証する。
- Windows／POSIX ルート、区切り文字混在、大文字小文字、兄弟 prefix 脱出、traversal、symlink／junction 脱出、対応付け不能添付、対応付け済みファイル操作を検証する。
- 複数の接続先、アカウント、ルート、Visual Studio インスタンス間でキャッシュ／セッション状態が混在しないことを確認する。
- 同じ接続先で認証主体を切り替え、旧主体のリモートセッション、未完了要求、WebSocket 状態、モデルカタログ、キャッシュ、遅延イベントを再利用できないことを確認する。
- 保存済み添付のページ境界と上限、重複 add、存在しない remove、MIME 拒否、対応付け済み／不能パス、通知統合、切断時の結果不明、復旧再構築、fork 複製を検証する。

### 対話と秘密情報保護

- 非同期回答、権限の部分許可、ターン／セッション範囲、MCP の対応／未対応フォーム、URL フロー、キャンセル、切断、解決競合を検証する。
- 対応／未対応 platform のネイティブ本人確認、enroll／verify／cancel／delete、切断・resolved 競合、認証主体変更、遅延 proof 破棄、secret／proof の非表示を検証する。
- MCP OAuth の期限切れ／失効、`reauthenticationRequired`、再ログイン成功／キャンセル／失敗、elicitation 状態リセット、失敗したツール呼び出しが自動再送されないことを検証する。
- 会話、Remote UI DTO、ログ、診断、設定、失敗テキストを検査し、秘密情報が一切現れないことを確認する。

### UI・ビルド・配布物の証跡

- 各 Phase の重点テスト後、Core と UI の全テストを実行する。
- Debug と Release の solution build を警告ゼロで実行する。
- VSIX 内容、Worker payload、manifest、生成スキーマ／キャッシュメタデータ、埋め込み XAML、関連 hash を検査する。
- インストールした拡張機能を Visual Studio Experimental Instance で実行する。
- Light、Dark、High Contrast の実表示、キーボード操作、フォーカス順序、accessible name／live region、virtualization、再接続／履歴状態、対話カード、成果物、shell 確認を検証する。
- 必要なテーマ／状態のスクリーンショットを取得し、ソース確認だけに頼らず合否の証跡を記録する。
- 証跡と受容した制限を `doc/implementation.md` と `doc/task.md` に記録し、この Issue 階層へリンクする。

## 完了条件

- 対応メッセージはメソッド／型の厳密な契約を使用し、未対応要求にはプロトコル上適切な拒否を返す。
- 古い接続からの応答または通知によって、完了済みターンが実行中へ戻ったり、現在状態が変更されたりしない。
- 安全なリモート接続、ルート対応付け、認証主体ごとのキャッシュ／状態分離、再接続、ページ履歴、保存済み添付復旧が、配送不明な変更操作を再送せず動作する。
- 権限の部分許可、非同期質問、resolved 競合、対応 MCP フォーム、対応ローカル platform のネイティブ本人確認、ブラウザーフロー、MCP 再認証案内、安全な秘密入力が end-to-end で動作する。
- shell 実行は明示的で上限があり、接続先を表示し、既存の承認ポリシーに従う。
- Core／UI テスト、警告ゼロの Debug／Release build、VSIX 検査、Experimental Instance の表示／アクセシビリティ検証が合格する。

## 後続計画とする機能

次の機能は有用だが、追加の製品設計、ライフサイクル設計、信頼境界設計が必要なため別計画とする。

| 機能 | 扱い |
|---|---|
| Windows 共有 daemon、自動 worktree 作成・管理 | 外部サーバー接続が安定した後の後続。サーバー lifecycle と Git 操作は分離する |
| 会話の名前変更、ピン、アーカイブ | 履歴復旧の完成後に追加する |
| Voice／Realtime、dynamic tools | UI、転送、権限を別途設計する |
| `ExternalMessage` | 直接のユーザー入力との権限差を先に定義する |
| プラグイン管理、他エージェント設定の取り込み | 正本と変更確認を別計画で定義する |
| Attestation | attestation provider と trust model が定まるまで opt-in のままとする |

## 参照

- [親 Issue #149](https://github.com/kkamegawa/vsextensionforcodex/issues/149)
- [App Server 公式ドキュメント](https://learn.chatgpt.com/docs/app-server)
- [公式変更履歴](https://learn.chatgpt.com/docs/changelog)
