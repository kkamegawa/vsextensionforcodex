# スラッシュコマンド統合

## 対象範囲

Visual Studio拡張機能は、入力の先頭文字が`/`の場合だけCodexコマンドとして
解釈します。組み込みコマンドと構造化スキルは一つのインライン仮想化候補一覧に
表示します。組み込みコマンドはプロンプトとしてモデルへ送りません。先頭`//`はコマンドモードをエスケープし、先頭`/`を
1文字含む通常プロンプトとして送信します。

実装はGitHub Issue #46と4件のsub-issueで管理しています。

## 対応コマンド

| 分類 | コマンド | 動作 |
|---|---|---|
| App Server操作 | `/compact`, `/feedback`, `/fork`, `/goal`, `/mcp`, `/review`, `/shell` | 専用の型付きWorker RPCを呼び出します。`/shell`は明示的な入力と確認からだけ実行します。 |
| 次ターン設定 | `/fast`, `/model`, `/permissions`（`/approve`は互換エイリアス）, `/personality`, `/plan`, `/reasoning` | 次の`turn/start`へ渡す型付きフィールドを更新します。ピッカー選択以外は次のターン開始時に消費されます。 |
| Visual Studio操作 | `/ide-context`, `/init`, `/status` | 上限付きIDEコンテキスト、`AGENTS.md`の安全な生成、ローカル状態表示を行います。 |

公式の意味を現在のApp Serverまたは単一スレッドUIで維持できない
`/cloud`, `/cloud-environment`, `/local`, `/memories`, `/project`,
`/side`は候補から隠します。直接入力時はローカルの非対応メッセージを表示します。

`/review`は未コミット変更、基準ブランチ、コミット、自由指示を選べます。
`/goal`は表示（`show`または`get`）、設定、編集、一時停止、再開、クリアに
対応し、目的は1～4,000文字です。`/model`はカタログと大文字小文字を区別せず
照合し、正規のモデルIDを適用します。

承認モードの正式コマンドは`/permissions`で、`/approve`は互換エイリアスです。
引数なしでは希望する既定値、App Serverが報告した実効状態、利用可能な安定IDを表示します。
組み込みIDは`ask`、`auto`、`full`、`custom`で、実行時profileは`permission:<id>`です。
Full accessはCodex sandboxと通常の承認promptを無効化し、拡張へ承認要求が届かないまま
処理が実行される可能性があるため確認が必須です。turn overrideから`custom`へ戻す場合は
新規threadを開始し、値の省略や`null`をresetとして扱いません。

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
steerとして送ることはありません。選択スキルは独立チップに保持し、`turn/start`へ
`{ type: "skill", name, path }`だけを送ります。scopeとraw pathはWorker検証専用で、
Remote UIへバインドしません。pending中はチップを外すまでsend/steerを無効にします。

liveのApp Server `skills/list`応答をスキルカタログの正本とします。Workerは60秒の
memory snapshotを現在の所有者ごとに保持します。固定account契約では安定した所有者の
継続性を証明できないため、ディスクcacheの読み書きを無効化します。cacheはturnを許可しません。
`turn/start`ではliveカタログをforce reloadし、有効な`Name + Scope + Path`の
完全一致を必須とします。

## Worker契約

Worker契約バージョン18で、要求と通知に捕捉した所有者のscopeを追加しました。
バージョン15で、構造化skill呼び出し、catalog freshness、invalidation、
live identityの完全一致検証を追加しました。バージョン9で接続中Codex versionを、
バージョン8でcompact、review、fork、goal、MCP状態、feedback、
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

コンポーザー内に一つの仮想化候補一覧を表示し、Popupは使いません。組み込みは8件、
スキルは無効行も含め、Workerが安全に受理した重複のない全identityを表示します。
Workerが信頼できない入力として受理する上限は200件で、上限到達時は受動的な
catalog truncated行を表示します。UI独自の20件上限は設けず、キーボード操作と
UI Automationで21件目から最後の受理済み行まで到達できます。
スキル選択は独立チップへ変換し、`SetComposerText("")`で
検索文字列だけを消して通常Composerを表示し続けます。固定引数はテーマ対応ボタンで選択します。

上下キーで移動、EnterまたはTabで候補選択、Escapeで閉じ、Ctrl+Enterで
実行します。候補が閉じているときのEnterは改行のままです。バインド型は
`DataContract`/`DataMember`、コマンドはRemote UIの`IAsyncCommand`を使います。

UIへ表示するApp Server由来文字列は`SafeMarkdownService`を通し、Workerログは
secret redactionを維持します。未加工のpayload JSONは表示しません。

file storeは将来の権威ある所有者識別に備え、workspaceとownerの複合SHA-256 keyを持つ
`%LOCALAPPDATA%\Kkamegawa.CodexForVisualStudio\skill-catalog\v2`形式を使用します。
最大200件、workspaceあたり4 MiB、HardExpiry 24時間、全体64 MiBに制限し、atomic replace、LRU cleanup、
時間制限付きcross-process lockを適用します。cacheファイルも信頼せず、読み込み時に再検証します。
現在のsessionではsnapshotを読み書きせず、旧v1も再利用しません。default prompt、dependency value、
icon source path、raw App Server JSON、Remote UI selection IDは永続化しません。
詳細は [所有者の設計](path-state-isolation-design_ja.md) を参照してください。

## 既知の未対応事項

skill iconは描画しません。`interface.iconSmall`は有無のflagにのみ縮退し、Remote UIの
image/cache containment spikeで安全なbindingが確認できるまで全行が固定glyphを使用します。
`dependencies.tools`はparseとbound済みですがbadge/tooltipが未実装のため、この2つのfieldは
現状consumerのないままcontractを越えています。brand color accentにはHigh Contrast分岐がなく、
High Contrast themeでもVisual Studio theme resourceではなくApp Serverの色が表示されます。
`turn/start`のlive identity検証でrejectされたskillはdiagnosticsに記録されますが、
chat surfaceには通知されません。

## 検証

CoreテストではApp Server method/parameterの完全一致、型付き通知、timeout、cancel、crash、
method-not-found capability fallback、非retry動作を確認します。UIテストではparse、escape、
複数行argument、alias、未知入力、入力上限、候補filter、queue順序と置換、thread分離、
disconnect時cancel、command/steer分離、`DataContract`/`IAsyncCommand`要件、XAML構造、
keyboard、accessibility、入力保持を確認します。

skill catalogテストではApp Server項目数0/1/20/21/200/201、全行への仮想化navigation、
stale-to-fresh置換、empty/unsupported/failed/truncated、workspace分離、破損・期限切れ・
oversize cache、generation race、複数instance、LRU cleanup、skill呼び出し前のlive
force-reload検証を確認します。

## 明示的なshellコマンド（Issue #155）

対応する形式は `/shell [--timeout-ms N] -- <command>` です。`/shell` は9番目の組み込み候補で、
skillを除外せず最大9件の組み込みを表示できます。候補の選択はコマンドchipを入れるだけで、
実行しません。確認画面の明示的な Execute 操作が必要です。parser は `--` より後の各文字を
そのまま保持し、空コマンド、重複／未知 option、負数／不正値、`int64` 超過を拒否します。
`--timeout-ms` 省略時は server 既定の1時間、0は即時 timeout、明示的な値なしは無制限です。

Join 済みで現 owner に属する idle thread だけ `/shell` を実行できます。確認画面には接続と
profile、thread、正確なコマンド（不活性テキスト）、server working directory、timeout、CLI
0.159.1 の `thread/shellCommand` は常に sandbox 外の full access で実行されることを表示します。
raw command text はログに書きません。Execute 後、Worker は対象、generation、cwd を再検証し、
ローカルで `IApprovalPolicyEngine` を評価します。policy 拒否は最終結果です。Full access と
過去の許可で確認を省略しません。

空の RPC 応答は受付確認であり完了を示しません。RPC 応答期限はコマンド timeout と独立します。
イベントは実際の thread/turn/item ID を保ち、送信要求と相関すると断定しません。thread あたり
shell 送信は最大1件保留できます。確定応答またはエラーで解除し、応答 timeout／切断後は
generation 終了までロックします。新 generation では thread status が idle と示した後だけ送信できます。
dispatch 前の取消では送信しません。dispatch 後の要求専用 Stop、時間／テキストからの中断推測、
再送、`command/exec` fallback、ローカル process 起動は行いません。
