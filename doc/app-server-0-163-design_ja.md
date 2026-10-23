# Codex App Server 0.163.0 対応設計

追跡先: Issue #217、子 Issue #218〜#223。2026-10-23 承認。

## 目的

Visual Studio 拡張機能を Codex App Server 0.163.0 の契約へ更新します。会話の既読状態を保持する操作、両 transport での競合情報の安全な保持、MXC 診断の是正、ローカル App Server 起動時の MXC 使用禁止を追加します。

リモートサーバー 0.162.1 と 0.159.1 との互換性を維持します。既読状態機能は experimental API を接続時に有効にした場合だけ利用可能にします。ExperimentalApiEnabled=false の既定値は変更しません。

## 契約とエラー処理

app-server-contract.json を schema version 2 にし、対象版を 0.163.0、回帰版を 0.162.1 と 0.159.1 にします。3版すべてのリリース情報と asset hash を固定し、版ごとに使用する stable／experimental の method 集合全体を記録します。回帰版ごとに対象版との差分を記録します。

schema 生成、比較、キャッシュ検証、契約 surface 検証、リリース検証を、各版と各 surface について行います。版別 schema は公式リリース実行ファイルから生成します。version 情報のないルート直下の schema に特定の版を割り当てません。契約 manifest の版管理と schema cache／検証証跡形式の版管理は分離します。

stable 接続では既読状態フィールドや method を必須にしません。生成 schema で experimental フィールドが experimental のままかを検証します。

JsonRpcRemoteException.ErrorData は Exception.Data と分離し、省略可能な JSON 値を複製して保持します。上限は 16 KiB とし、超過値を破棄します。stdio と WebSocket の両方の応答から保持します。生データをログ、UI、Worker 契約へ転送しません。

既読状態の競合と判定するのは、リモートエラーコードが -32600 かつ error.data.reason が readStateConflict の場合だけです。一般的な invalid request は通常のエラーとして扱います。競合データ内の状態も通常と同じ検証を通し、欠落または不正なら thread/read で再取得します。

## 既読状態モデルと操作

Worker 契約を v25 にし、所有者スコープ付き更新要求、型付き既読状態、generation 付き変更通知を追加します。内部状態は次のように分類します。

| 状態 | 意味と表示 |
| --- | --- |
| 既読 | firstUnread=null の有効状態。未読マーカーを表示しません。 |
| 会話開始から未読 | threadStart を指す有効状態。未読マーカーを表示します。 |
| turn から未読 | 最初の未読位置が turn を示す有効状態。未読マーカーを表示します。 |
| 対象外 | 一覧は既読状態に対応するが、この会話のキーがありません。マーカーは表示しません。 |
| 利用不可 | 接続で利用できない、または状態マップ／値全体が欠落・null。マーカーは表示しません。 |
| 不正 | 値を検証できません。対象行の操作を無効にし、選択時に状態を説明します。 |

履歴 DTO を ObservableObject を実装する ThreadRowViewModel に写します。行一覧は読み込み済みの一覧と同じ順序を位置ごとに保ちます。選択中の会話が読み込み済みのページから外れた場合は、行の状態を保持したまま、余分な行としては表示しません。行の更新は owner と thread ID を照合します。一覧置換後も thread ID で選択を維持します。会話を選択しても、より新しいサーバー応答で置き換わるまで直前の既読状態を保持します。既読状態の更新時は該当行だけを更新し、履歴を再読み込みしません。

履歴行には未読マーカーを表示します。選択中の会話では、会話に参加せず「既読にする」「未読にする」を明示操作できます。stable 接続では操作を隠し、履歴領域に一度だけ説明を表示します。experimental 接続でも、有効な状態と revision が取得できるまでは操作を無効にします。

ユーザーが実際に確認した revision を送信します。同一会話への更新は直列化し、成功応答後にだけ表示を変更します。競合時は検証済み最新状態を反映し、再操作を案内します。競合、timeout、切断後に更新を自動再送しません。timeout と切断は結果未確認として扱い、再送せずに現在の状態を1回だけ読み直します。revision は不透明値として扱い、大小比較しません。表示中と同じ状態を持つ通知は、自分の更新が確定したことを知らせる通知も含めて、並行した変更として扱いません。応答と異なる内容の通知が競合した場合は再取得し、整合するまで操作を無効にします。更新中かどうかが変わるたびに既読操作の表示状態を通知し直すため、更新が終わるとすぐに操作が再表示されます。

owner、接続 generation、要求 generation、選択 thread generation を照合し、A→B→A の切替や owner 変更時に古い結果を適用しません。会話の開始、再開、fork 後に thread/read で状態を取得します。履歴を開く時と明示更新時は thread/list を使います。常時購読や polling は追加しません。

表示中の会話も自動で既読にしません。新しい結果によって未読となった場合、ユーザーが明示的に操作するまでマーカーを維持します。

## 履歴一覧の読み込み

初回読み込み、更新、追加ページのすべてで owner と要求 generation を管理します。結果が現在の owner と一致することを確認します。重複要求を抑止し、正常な空結果、末尾到達、cancel、失敗を区別します。

更新は成功後に一覧を置き換えます。失敗時は既存行、選択、cursor を保持します。追加ページは thread ID で重複排除します。同じ owner で完全な一覧の取得に成功した時だけ、不在の会話に対する処理を取り消します。部分結果から削除を推測しません。

## MXC の選択と診断

allowedWindowsSandboxImplementations は legacy sandbox backend の一覧であり、MXC の許可／禁止を示す情報ではありません。readiness パネルはこの一覧から MXC ポリシーを推測しません。

local sandbox selector に「MXC を使用しない」を追加し、ローカル CLI に -c windows.allow_mxc=false を渡します。リモートサーバーの設定は変更しません。CLI version は既存の initialize 応答 user agent から取得し、別の --version process を起動しません。initialized を送る前に、ローカルの使用禁止設定では 0.163.0 以上と確認できる必要があります。版が不明または古い場合はローカル起動を終了し、設定画面を利用可能に保ちます。

config/read の windows.allow_mxc は省略可能な診断値として扱います。欠落時は不明です。App Server 設定 API は windows.require_mxc を公開しないため推測しません。明示的な sandbox=mxc との矛盾や管理ポリシーによる拒否は、CLI が返すサニタイズ済みエラーで説明します。起動失敗だけを根拠に MXC を拒否したポリシーを断定しません。readiness から個々のコマンドの backend を断定しません。

## 検証範囲

Fake App Server で競合 payload、更新通知、既読状態の各分類、0.162.1 回帰 surface、既存 MCP serverRequest/resolved 経路を確認します。コア側の MCP 質問破棄イベントがクライアントへ届くことは受け入れ条件にしません。実機 CLI が通知しない場合は、既存の5分 timeout と切断時の回収動作を記録します。

steer と turn 完了の競合、および cold resume 後の有効権限復元も検証します。Core／UI／Fake の各テストと Debug／Release build を警告ゼロで通します。Visual Studio Experimental Instance で各 theme、狭い幅、キーボード操作、読み上げを確認し、スクリーンショットで合否を判定します。