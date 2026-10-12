# Codex App Server 更新対応とリモート接続の計画

[English](app-server-update-plan.md)

> 2026-09-20 に承認された計画。実装は親 Issue [#149](https://github.com/kkamegawa/vsextensionforcodex/issues/149) と7件の子 Issue で追跡する。

## 概要

Visual Studio 拡張機能の既存 C#／`codex app-server` 連携を CLI 0.159.1 の契約へ更新し、起動済みのリモート App Server へ安全かつ明示的に接続できるようにする。ローカル stdio は既定として維持する。厳密な要求振り分け、イベント順序の安全性、リモート転送、ルート対応付けと認証主体ごとの状態分離、再接続と履歴復旧、保存済みスレッド添付、非同期質問と範囲を限定した権限、ネイティブ本人確認、MCP 対話と認証復旧、日常利用イベント、shell 実行、型付き成果物、Windows sandbox 状態、統合リリース検証を対象とする。

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
| 高 | CLI 0.159.1 対象スキーマ、0.155.1 回帰比較、初期化メタデータ、capability 宣言・判定 | [#150](https://github.com/kkamegawa/vsextensionforcodex/issues/150) |
| 高 | 安全なリモート接続、接続所有権、診断、読み取り専用再試行 | [#151](https://github.com/kkamegawa/vsextensionforcodex/issues/151) |
| 高 | ローカル／サーバーパス対応付け、接続先／アカウント／認証主体／ルートごとの状態分離 | [#152](https://github.com/kkamegawa/vsextensionforcodex/issues/152) |
| 高 | 再接続、下書き保持、ページ履歴と添付復旧、配送不明な変更操作の扱い | [#153](https://github.com/kkamegawa/vsextensionforcodex/issues/153) |
| 高 | 非同期質問、権限の部分許可、秘密／本人確認要求の明示拒否、MCP form／認証回復、ローカル Gateway OAuth | [#154](https://github.com/kkamegawa/vsextensionforcodex/issues/154) |
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

- **CLI 0.159.1 正式版**を対象契約に固定する。**CLI 0.155.1 正式版**は回帰比較元として保持し、ローカル alpha のスキーマをどちらの正式契約としても扱わない。
- 0.155.1 と 0.159.1 のそれぞれで標準スキーマと実験的スキーマを別々に生成し、構造差分を取得する。
- CLI バージョンと生成オプションをスキーマキャッシュのメタデータに保存する。どちらかが異なる場合は、既存ファイルを有効なキャッシュとして扱わず再生成する。
- 生成したスキーマは Git に含めない。
- 使用する全メソッド、必須フィールド、列挙値、null 許容フィールド、未知 item、追加フィールド、不正 payload に加え、確認した 0.155.1→0.159.1 の全差分を契約テストへ追加する。

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

- Extension → ローカル Worker を維持し、Worker 内で所有する local stdio または明示的に有効な remote WebSocket を選択する。
- 共通 RPC 振り分け、メッセージ上限、通知順序、キャンセル、世代退役を維持する。profile はメタデータだけを保存し、トークン値を含めない。
- 詳細な最終契約は [secure-remote-connection-design_ja.md](secure-remote-connection-design_ja.md) と [英語版](secure-remote-connection-design.md)。UI 契約は `doc/design.md` section 12、判断記録は `doc/adr/ADR-012-app-server-remote-transport.md`。
- 既存 retry helper と endpoint・token 検証は部分実装。helper にメソッド・引数の適格性と単一 timeout 予算を追加し、無制限の token 読み取りと UI の重複 endpoint 検証を置換する。実装済み profile editor・transport 所有権は維持する。

### 接続先・認証・所有権

- endpoint 専用 `RemoteEndpointPolicy` を Contracts（`netstandard2.0`）に置き、Extension・Worker・Protocol が使う。remote `wss` と厳密に定義した loopback `ws` を許可し、URI 認証情報・query・fragment・未指定 bind address を拒否する。
- Save はメタデータと token-file path の要件を検証する。Worker だけが handshake 直前に、ファイルの存在・読み取り可否・encoding・上限付き内容・bearer 形式を検査する。
- 明示的な接続・再接続および #153 coordinator が開始した各 remote 試行でローカルトークンファイルを再読込する。token rotation だけでは自動復旧を開始しない。実値の lease 型秘匿、既定 TLS 検証、redirect なし、WebSocket と HTTP 診断で共通の proxy 解決方針を使う。
- local restart は子プロセスを所有し、remote reconnect は socket だけを所有する。remote への `worker/restart` は停止前に型付き接続操作拒否を返す。
- 再接続は適用済みの名前で最新保存 profile を読み、有効・同一メタデータ・期待世代を要求する。変更・無効化・削除された profile は明示的な適用・接続先選択を必要とする。同じ token file の内容更新だけではメタデータは変わらない。
- 同一 instance の設定変更と再接続 snapshot 検証・送信を直列化し、Worker 遷移ゲートでも再検証する。認証主体・cache の永続分離と instance 間設定 transaction は Issue #152 が追跡する。

### 診断・死活検知・再試行

- `/healthz`・`/readyz` GET は合計 5 秒、認証・Origin なし、redirect・本文表示なし、独立した型付き結果とする。authority root の診断範囲を表示し、path routing 先 App Server は root probe では未確認とする。health で RPC readiness・機能可否を判断しない。
- .NET 8 を維持する。30 秒の keepalive interval は相手の応答証拠ではありません。世代別 idle RPC watchdog は無通信 probe 2回で half-open socket を閉じます。watchdog は検知と close だけを行い、retry は所有しません。独立した #153 Extension coordinator は対象となる一時切断通知を再試行できます。どちらも変更操作を再送しません。probe 中に妥当な inbound request・response・notification があれば、30 秒の無通信監視に戻ります。
- remote 起動全体は 45 秒。token 読込最大 5 秒、handshake・initialize・起動時 account read は各最大 15 秒とし、すべて残り時間で制限する。
- retry 棚卸しは現在の 8 メソッド。`account/read` は明示的 `refreshToken=false`、`skills/list` は明示的 `forceReload=false` を必要とする。強制 skill refresh は cache 消去・再走査の追加処理を抑えるため 1 回とし、将来の history read は別途レビューして許可リストへ追加する。
- 完了した `-32001` だけを最大再試行 3 回・送信 4 回まで扱い、基準待機 250/500/1000 ms と ±20% jitter を使う。単一 monotonic timeout に送信・待機を含め、世代退役で保留再試行をキャンセルする。

### 実装順序と検証ゲート

1. 改訂した Issue #151 設計と ADR-012 amendment 案を確認する。本 Phase 2、`doc/design.md` section 12、英日詳細設計、Wiki 計画・索引を同期する。設計確認後に実装へ進む。
2. 共通 policy・retry を拡張し、無制限 token I/O を置換する。secret lease と出力生成時の秘匿、起動失敗時の決定的な後始末を追加する。既存 runtime・SDK・package version を維持する。
3. 型付き診断、再接続の拒否理由、接続先・世代 snapshot、.NET 8 watchdog を追加する。実際の merge base から次の Worker 契約 version を割り当て、全 producer・consumer と package を同時更新する。
4. profile 鮮度検証、独立 health/RPC 表示、local Restart・remote Reconnect を接続する。policy・token・TLS・proxy・read-only retry・寿命・serialization・command 状態と、拒否する引数 variant をテストする。
5. 固定 CLI 0.159.1 による警告ゼロ Release build、Core/UI tests、schema・contract 検証、VSIX manifest・assembly・XAML 確認、Experimental Instance screenshot を実施する。実証した内容を Issue #151 とともに `doc/implementation.md`・`doc/task.md` に記録し、全受け入れ条件の証跡が揃うまで完了扱いにしない。

## Phase 3 — パス対応付けと状態分離

詳細設計: [パスマッピングと接続状態の分離](path-state-isolation-design_ja.md) / [English](path-state-isolation-design.md)。承認済み ADR-013 の所有者境界を適用し、固定 account 契約で安定した所有者を確認できない場合も揮発状態で分離します。

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

## Phase 4 — 再接続と履歴復旧

追跡: [#153](https://github.com/kkamegawa/vsextensionforcodex/issues/153)

詳細設計: [English](connection-history-recovery-design.md) / [日本語](connection-history-recovery-design_ja.md)。Worker contract v19 で実装済みです。検証結果は [implementation.md](implementation.md) に記録しています。

### 接続回復と隔離した下書き

- Extension が単一の復旧管理を所有し、Worker 終了をまたいで1つの回復処理を行います。切断・終了した Bridge は、RPC proxy が non-null の場合も破棄して再作成します。各 Worker の接続試行には既存の遷移ゲートを使い、通知 callback は切断をキューに入れて戻り、回復を await しません。
- 自動回復は一時的な Worker／子プロセス終了、通信断、応答停止、予期しないサーバー閉鎖だけを対象とします。認証、TLS／証明書、profile／設定／ルート、既知の所有者変更、キャンセルで自動試行を終了します。#152 の所有者自身による sign-in／sign-out 後の接続処理は別に維持します。Remote は socket を再接続し、外部サーバーは外部管理のままとします。
- 試行は最大5回とし、各試行前の待機を順に0・1・2・4・8秒、0秒以外は ±20% の jitter とします。1試行45秒、全体5分を上限とし、手動操作との競合を直列化します。上限後は安定した手動再接続操作を示します。Remote の各試行で token file を再読込しますが、内容の更新だけでは回復を開始しません。
- Reconnecting、復元確認待ち、Synchronizing history、履歴閲覧のみ、手動再接続待ちを区別します。有効な所有者状態を消去する前に、旧下書きの本文、添付参照、skill、次ターンの model／reasoning／speed／personality 設定を Extension のメモリに隔離します。試行中に上書きせず、VS 画面の寿命の間だけ保持します。
- 新接続を initialize し、現在の所有者の thread 一覧を再取得します。接続先の確認と現在の所有者の会話選択の後、明示的な復元／破棄を提供します。復元は composer へのコピーだけとし、送信は別操作です。添付の対応付け・物理境界と model／skill／設定の catalog を再検証します。承認、cache、資格情報、保留 server request、秘密の proof は新所有者に移しません。ディスク保存は追加しません。

### 読み取り専用の履歴と添付の統合

- thread/read は includeTurns=false、thread/turns/list は sortDirection=desc・itemsView=summary で最新50 turns、thread/items/list は sortDirection=desc で最新100 items を取得します。過去ページと turn 内の詳細は明示操作で取得し、返された文字列 cursor を使います。0.159.1 の構造化 exclusive item anchor は turnId と既知の item 境界を必要とし、0.155.1 の回帰経路は文字列を使います。
- ページと保留通知は所有者、接続世代、thread／turn／item ID で統合します。完了 item を delta より優先し、重複や遅延 delta で表示を戻しません。表示は最大1,000 items・本文16 MiB の移動窓とし、通知は最大1,024件・8 MiB とします。超過時は同期失敗を明示し、読み取り専用の再取得操作を提供します。
- 閲覧と再参加を分け、明示操作だけが thread/resume を excludeTurns=true で呼びます。稼働中の会話は「使用中の可能性」とし、他クライアントの所有と断定しません。resume が失敗しても取得済み履歴と隔離下書きを保持し、安全に表示できる理由と明示操作を示します。
- thread/attachment/list は limit=50 で nextCursor が null になるまで取得します。1会話最大100件、1ページ最大100件、1件の serialized payload 最大64 KiB、attachmentType と identityKey は各256 UTF-8 bytes を検証します。#153 は payload の検証と上限付きの基本 metadata、#155 は詳細な解釈、preview、追加／削除、ファイル操作を担当します。未知・不正な形式は理由を示して操作を無効にします。protocol に共通 MIME field はありません。
- 添付は (threadId, attachmentType, identityKey) と attachment ID で統合します。作成通知に payload はなく、上限付きの一覧再取得を行います。削除 ID の tombstone で古いページによる復活を防ぎ、同じ identity の新 ID は再作成として扱います。明示的な非一時 fork 後にも再取得し、旧所有者・旧世代の通知は拒否します。
- 4つの read method と添付通知を契約 manifest と read-only overload allowlist に追加しました。既存の3回再試行／合計4送信は接続回復と分離し、resume と変更操作は overload retry の対象外です。

### 結果不明の操作と検証

- dispatch 境界で変更操作をローカルに記録します。NotSent は dispatch が始まっていない証明を必要とし、送信開始の可能性がある後の応答消失は OutcomeUnknown とします。履歴にないことや本文・時刻の一致では結果を確定しません。確定応答は記録した結果を確定でき、切断前に相関付けた server item ID は受理だけを示し、すべての副作用の完了は証明しません。
- メッセージ、承認、MCP、shell、ファイル変更、添付変更を自動再送しません。不確定な内容を確認可能に保ち、コピー／編集／再検証の後、新しい送信を別の明示操作とします。ローカル操作 ID を wire の idempotency field にせず、期限切れ request ID と秘密の proof を再利用しません。
- 回復対象の除外、5回失敗、旧世代、下書き復元／破棄、履歴上限、添付ページング、自動再送ゼロを Core／UI テストで確認しました。Debug／Release solution build は警告ゼロで、0.159.1／0.155.1 契約と Release VSIX 検査も成功しました。実装環境に Visual Studio がないため Experimental Instance の画面確認は未実施です。詳細は [implementation.md](implementation.md) を参照してください。

## Phase 5 — 質問・権限範囲・MCP 対話

追跡: [#154](https://github.com/kkamegawa/vsextensionforcodex/issues/154)

Worker 契約 v19 から開始し、merge 時点で次に利用可能な契約 version を割り当てます（途中変更がなければ v20）。CLI／SDK／runtime は既存の基準を維持し、CLI 0.159.1 を対象、0.155.1 を回帰比較用とします。

### 契約と要求ライフサイクル

- 非同期質問、権限要求、コマンド承認、MCP elicitation、Gateway OAuth、未対応の本人確認要求をそれぞれ別の要求／応答型で扱います。
- 接続世代と元の JSON-RPC request ID をキーとする保留要求レジストリを使用します。turn ID のない MCP 要求も管理し、すべての応答に既存の owner 検証を適用します。
- 回答を検証してから完了権を原子的に取得します。回答、キャンセル、timeout、切断、`serverRequest/resolved` の競合でも応答は最大1回とします。resolved 要求や破棄済み世代には応答せず、配送結果不明の応答を再送しません。

### 質問と権限

- ターンが継続中でも操作でき、通常 composer を塞がない独立カードを表示します。blocking／non-blocking 質問、自由入力、「その他」を扱います。選択・focus・既定値は表示状態であり、明示 Submit だけが回答です。
- secret マーカー付き質問は UI 投影を作る前に Worker で検出します。安全な専用入力経路がないため理由を示して拒否し、秘密内容を通常 UI、会話、ログ、設定、診断、例外へ渡しません。
- 要求されたネットワーク／ファイル権限のうち選択部分だけを返します。既定は turn scope、session scope は明示操作で選択します。サーバー要求にない権限は拒否します。
- コマンド承認の全選択肢と追加権限を表示し、ルール変更の選択肢も独立したサーバー選択肢として保持します。破壊的操作には既存の承認ポリシーを適用します。

### MCP elicitation と認証回復

- 正確な `mcpServer/elicitation/request` を処理します。対応する form field（string、number、integer、boolean、単一／複数選択）について required、型、長さ、範囲、形式、選択数を UI と Worker の両方で検証します。
- `openai/form` など未対応の拡張 schema は理由付きで拒否し、拡張フォーム capability を宣言しません。
- 認証 URL は Worker で検証・保持し、明示的なユーザー操作だけでブラウザーを開きます。ブラウザーを開いた事実だけでは認証成功とみなしません。
- `mcpServer/oauth/login`、`mcpServer/oauthLogin/completed`、起動状態通知を連携します。期限切れ、失効、`reauthenticationRequired` では再認証を案内し、古い elicitation を終了します。MCP に取消 RPC がないため、UI 取消をサーバー認証の取消として扱いません。
- 再認証後は新しい明示的なツール呼び出しを要求し、失敗したツール呼び出しを自動再送しません。

### Gateway OAuth とネイティブ本人確認

- ローカル stdio だけで `explicitGatewayOauth` を宣言します。初期化後、認証を要する RPC より先に `account/gatewayOAuth/read` を成功させます。このゲートを接続ごとに行い、その後に login／cancel／changed 通知と明示的なブラウザー操作を扱います。
- Gateway OAuth 通知を有効な接続と owner に結び付けます。login 待ちで他の RPC の処理を塞ぎません。未対応または初回 read 失敗時は認証付き RPC を止め、自動ブラウザー認証へ切り替えません。
- Remote 接続では状態とサインイン案内だけを提供し、capability の宣言や Gateway OAuth の login／cancel 変更要求を行いません。
- 固定版 CLI 0.159.1 の本人確認は macOS のみ対応し、この拡張 client は上流の適格対象に含まれません。Windows と拡張 client の上流対応までは成功経路を延期します。今回 capability を宣言・転送せず、要求を理由付きで拒否します。challenge、proof、credential を UI、会話、ログ、設定、診断、例外へ出しません。

### 添付の担当範囲

- Phase 5 は保存済み添付の契約拡張や UI 動作を追加しません。上限付き metadata の復旧は Phase 4／Issue #153、添付の操作と表示は Phase 6／Issue #155 が担当します。

## Phase 6 — 日常利用の App Server 機能

トラッキング: [#155](https://github.com/kkamegawa/vsextensionforcodex/issues/155)。承認済み最終設計と package 計画は [日常利用の App Server 機能](daily-use-app-server-design_ja.md) と[実装計画](daily-use-app-server-plan_ja.md)に記録する。

CLI 0.159.1 stable を対象とし、0.155.1 の回帰 fixture を使う。基準は commit `69deda1`（PR #169 マージ済み）、Worker contract v20 とし、統合時に次の利用可能版を割り当てる。CLI、SDK、runtime、package は固定する。#152 の mapping/ownership、#153 の上限付き履歴・保存済み添付 metadata 復旧、#154 の質問／権限／MCP 基盤を再利用する。

Phase 6 は、任意の experimental plan delta と上限付き notice、実行時カタログに基づく入力受け入れと 0.159.1 追加 field、正確なコマンド保持・確認・ローカル policy・独立 RPC／実行期限・thread ごとの pending lock を備えた `/shell [--timeout-ms N] -- <command>`、型付き上限結果・PNG/JPEG preview・操作時の物理検証付き mapped Open/Reveal、クライアント所有 `relaycodex.file.v1` payload を使う明示的な添付 add/remove、ローカル Windows のみの sandbox 設定を含む。CLI 0.159.1 の shell method は常に sandbox 外の full access で実行する。保存済み添付は metadata のままとし、composer input へ自動追加しない。

P0–P6 と依存関係は詳細計画に定める。検証は固定 0.159.1／0.155.1 fixture、対象から Core/UI 全テスト、warning-free Debug/Release build、contract/schema と VSIX integrity、Experimental Instance のテーマ・狭幅・keyboard/focus・accessibility のスクリーンショットを含む。スクリーンショットがなければ未達とする。PR #170 の実装と自動検証は完了し、Issue #155 は close 済み。未完了の Experimental Instance 表示／対話受入は Issue #156 の Local-required 条件へ引き継ぐ。

## Phase 7 — 統合検証とリリース準備

トラッキング：[Issue #156](https://github.com/kkamegawa/vsextensionforcodex/issues/156)。詳細条件は承認済み[統合リリース検証設計](release-validation-design_ja.md)と[実装計画](release-validation-plan_ja.md)に定める。

この計画の基準は main commit b44e856、Worker contract v21、CLI 0.159.1、回帰 fixture 0.155.1。PR #170 の結果は過去の証跡であり、後続 release candidate の結果には流用しない。

### シナリオ区分と証跡

- **Local-required:** 固定 schema 比較、Fake App Server とローカル suite、transport／TLS／token rotation 模擬、path 境界、VSIX integrity、Windows Experimental Instance、同一マシン上の2 instance 分離。必須項目すべての合格を必要とする。
- **External:** 認証を伴う固定 CLI 通信、実 MCP OAuth の期限切れ／再認証、実 remote TLS／証明書／token rotation。公開 CI 外で手動実行し、合格またはリスク、代替証拠、承認者、日付、承認元を記録した明示承認済み blocked とする。
- passed、failed、blocked、not-run、flaky をシナリオごとに記録する。failed／flaky／not-run が残れば準備完了としない。必須 skip は blocked とし、Local-required の blocked は未完了。環境制約だけで External を免除しない。
- 初回結果を保存し、失敗した test case だけを1回再試行する。再試行合格は flaky として gate を失敗させる。data-driven case を分離できない場合や test host が異常終了した場合、suite 全体を再実行しない。
- 実際の test identity で専用一時領域への symlink／junction 作成を試す。skip は test 名と実際の理由を記録し、PR #170 の6件は TRX なしに原因を推定しない。
- 秘密除去済み TRX、diagnostic、schema report、hash、結果 manifest を `if: always()` で保存する。認証済み生通信は公開 CI／成果物へ含めない。

### UI と Windows 受入

SDK 管理の F5／Experimental Instance 手順と重複 identity guard を使用する。Light／Dark／High Contrast、狭幅、接続／復旧／履歴／下書き、質問／権限／MCP、添付／成果物、shell 確認、Windows setup、keyboard／focus 順、accessible name、Narrator／Accessibility Insights の通知、長い履歴、同一マシン上の2 instance を確認する。

スクリーンショットには scenario ID、環境、期待結果、合否を付ける。keyboard／読み上げには手順と観測記録も必要とする。オフスクリーン WPF 描画は補助証跡。全 solution、WPF UI、固定 Windows CLI の schema 生成、VSIX、Experimental Instance の受入は Windows で実施し、Core／Worker net8.0 の他 OS 実行を Windows 証跡の代替にしない。

### ネイティブ本人確認の境界

capability 未宣言、payload 投影前の拒否、challenge／proof の非露出、遅延 resolve／cancel event の無視、principal 変更時の未完了状態破棄を検証する。enroll／verify／cancel／delete の成功フローは未対応であり受入条件にしない。新規 ADR は不要。

### 後続実装の範囲

本 Phase は証跡と準備完了基準を定める。PowerShell 7 統合 script、CI／Release workflow、不足 test、External 実行、Experimental Instance 受入は Issue #156 の承認済み計画に基づく後続実装とする。

## 完了条件

- 正確な最終 candidate commit で Local-required をすべて合格させる。failed／flaky／not-run を残さず、必須 skip／blocked は未完了とする。
- すべての External が合格、または承認者・日付・承認元を記録した証跡付きの明示承認済み blocked である。
- 結果 manifest と秘密除去済み証跡を記録し、PR #170 の件数／hash は現 candidate の結果と区別する。
- Experimental Instance のテーマ、狭幅、workflow、keyboard、focus、accessibility、長い履歴、同一マシン上の instance 分離が合格し、証跡がある。
- Issue #155 の close 状態で Phase 6 の未完了な画面／対話受入を完了扱いにしない。Issue #156 に Local-required として引き継ぐ。
- ネイティブ本人確認の拒否と秘密情報境界を検証する。成功フローは要求しない。

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
