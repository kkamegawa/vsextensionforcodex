# スラッシュコマンド統合

## 対象範囲

Visual Studio拡張機能は、入力の先頭文字が`/`の場合だけCodexコマンドとして
解釈します。コマンドは許可リスト付きカタログで解決し、プロンプトとして
モデルへ送りません。先頭`//`はコマンドモードをエスケープし、先頭`/`を
1文字含む通常プロンプトとして送信します。

実装はGitHub Issue #46と4件のsub-issueで管理しています。

## 対応コマンド

| 分類 | コマンド | 動作 |
|---|---|---|
| App Server操作 | `/compact`, `/feedback`, `/fork`, `/goal`, `/mcp`, `/review` | 専用の型付きWorker RPCを呼び出します。 |
| 次ターン設定 | `/fast`, `/model`, `/personality`, `/plan`, `/reasoning` | 次の`turn/start`へ渡す型付きフィールドを更新します。ピッカー選択を変更する`/model`を除き、次のターン開始時に消費されます。 |
| Visual Studio操作 | `/ide-context`, `/init`, `/status` | 上限付きIDEコンテキスト、`AGENTS.md`の安全な生成、ローカル状態表示を行います。 |

公式の意味を現在のApp Serverまたは単一スレッドUIで維持できない
`/approve`, `/cloud`, `/cloud-environment`, `/local`, `/memories`, `/project`,
`/side`は候補から隠します。直接入力時はローカルの非対応メッセージを表示します。

`/review`は未コミット変更、基準ブランチ、コミット、自由指示を選べます。
`/goal`は表示（`show`または`get`）、設定、編集、一時停止、再開、クリアに
対応し、目的は1～4,000文字です。`/model`はカタログと大文字小文字を区別せず
照合し、正規のモデルIDを適用します。

引数なしの`/plan`は次ターンをPlanモードにします。引数がある場合は、その
内容をプロンプトとしてPlanモードのターンを開始します。

## ルーティングとキュー

`SlashCommandParser`は通常プロンプト、エスケープ済みプロンプト、対応コマンド、
非対応コマンド、未知コマンドを分離します。未知コマンドは編集距離の上限内で
最大3件の候補を返し、`turn/start`にも`turn/steer`にも送りません。

ターン実行中は`/status`、`/mcp`、goal表示だけ即時実行します。それ以外は
スレッド単位の最大10件FIFOキューへ入れます。スレッド未確定時のコマンドは
セッション単位のキューを使います。同じ設定コマンドが待機中なら、キュー位置を
維持したまま最新値に置換します。ターン完了後は完了ターンのスレッド、選択中
スレッド、セッションキューの順に実行し、失敗したコマンドの後続も継続します。
キュー項目が新しいターンを開始した時点で次の完了まで停止します。

キューはメモリ内だけに保持します。切断、Worker再起動、スレッド消失確定時に
取り消します。通常プロンプトのsteer動作は維持しますが、スラッシュコマンドを
steerとして送ることはありません。

## Worker契約

Worker契約バージョン8で、compact、review、fork、goal、MCP状態、feedback、
rate limitsの型付きDTOとRPCを追加しました。`StartTurnRequest`にはreasoning
effort、personality、service tier、collaboration mode、上限付きIDE contextを
追加しています。モデル情報には対応effort、既定effort、personality対応、
service tierを含めます。

App Serverの`-32601`は該当コマンドだけをセッション中無効化し、接続全体を
Degradedにしません。非冪等コマンド操作は再試行しません。

compaction、review mode、goal変更、rate limitsは専用の型付きイベントとして
処理し、未加工のJSON payloadをトランスクリプトへ渡しません。ターンが
実行中でないときにcompactionが完了した場合、WorkerはReady状態へ復帰し、
待機中コマンドが完了済みcompactionの背後で滞留しないようにします。

## IDEコンテキストと初期化

IDEコンテキストは`/ide-context`で切り替え、既定は有効です。ワークスペース
直下のパスだけを許可し、参照ファイルは最大10件、選択テキストはUTF-8で
最大32 KiBです。Remote UIコマンドのVisual Studio client contextから、
アクティブ文書と主選択範囲を取得します。

`/init`はワークスペース直下だけを対象とします。英語の`AGENTS.md`全文を
プレビューして確認を求め、create-newで作成するため既存ファイルを上書きしません。

## Remote UIと安全性

コンポーザー内に高さ制限付き候補一覧を表示し、Popupは使いません。選択した
コマンドはチップへ変換し、自由入力引数は別TextBoxへ置くため、非同期の
`ComposerText`反映によるキャレット移動を避けられます。固定引数はテーマ対応
ボタンで選択します。

上下キーで移動、EnterまたはTabで候補選択、Escapeで閉じ、Ctrl+Enterで
実行します。候補が閉じているときのEnterは改行のままです。バインド型は
`DataContract`/`DataMember`、コマンドはRemote UIの`IAsyncCommand`を使います。

UIへ表示するApp Server由来文字列は`SafeMarkdownService`を通し、Workerログは
secret redactionを維持します。未加工のpayload JSONは表示しません。
