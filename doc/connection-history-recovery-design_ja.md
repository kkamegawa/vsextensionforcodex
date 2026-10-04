# 接続と会話履歴の復旧設計

- 日付: 2026-10-04
- 追跡先: [#149](https://github.com/kkamegawa/vsextensionforcodex/issues/149)（親計画）、[#153](https://github.com/kkamegawa/vsextensionforcodex/issues/153)（復旧）、[#155](https://github.com/kkamegawa/vsextensionforcodex/issues/155)（添付ファイル表示とファイル操作）
- プロトコル基準: Codex CLI 0.159.1、回帰確認基準 0.155.1
- リポジトリ根拠の基準: 517d991250889c19b1b2c86b38ae20dc08cebe9c

## 概要

一時的な通信障害または Worker 障害に限り、失われた app-server 接続を自動復旧する。Visual Studio のチャット画面が存続する間、Extension が復旧処理を一元管理し、同時に複数の復旧エピソードを開始しない。再接続と初期化の後、会話一覧を更新してユーザーの選択を待つ。固定されたプロトコルのアカウント情報から Codex CLI の安定した所有者 ID は証明できないため、同じ所有者だと推測しない。

旧所有者の未送信下書きは、メモリ上で変更不能なスナップショットとして隔離する。新しい composer へ自動で移さない。ユーザーが接続先を確認し、会話を選び、明示的にスナップショットを復元または破棄した場合だけ処理する。復元は composer へのコピーだけを行い、送信は別のユーザー操作とする。送信直前に、現在の所有者、ルート、スキルカタログ、モデルカタログに照らして内容を再検証する。

会話を resume せずに履歴を読み取る。最新の要約と項目ページを先に読み、過去のページを明示的に追加できるようにする。履歴と読取中の通知を安定した識別子で統合する。保存済み添付情報は上限付きカーソルページですべて再構築するが、任意のペイロードを保持せず、プロトコルが定義していない MIME 契約を推測しない。配送結果が不明な操作は再実行しない。

## プロトコルと所有者境界

再接続した CLI セッションが終了前と同じアカウント所有者であることを Extension は証明できない。そのため、接続が成功するたびに現在の thread list を更新し、履歴表示、resume、下書き復元の前に新たな会話選択を必須とする。以前選択していたスレッドは自動 resume しない。旧下書きは接続/プロファイルの明示確認と会話選択までメモリ内で隔離保持し、下書き項目、承認、認証情報、許可を現行所有者へ自動で移さない。

固定スキーマと実装に基づき、次のように解釈を具体化する。

- 0.155.1 と 0.159.1 の両方が、メタデータのみの thread/read、ページング可能な thread/turns/list と thread/items/list、excludeTurns 付きの thread/resume をサポートする。ページング対応スレッドでは、履歴全体の一括取得は非推奨である。
- 0.159.1 の thread/items/list は不透明な文字列カーソル、または排他的な項目位置を表す構造化アンカーを受け付ける。構造化アンカーはこの目標バージョンのみで使え、turnId が必須であり、継続取得ではサーバーが返す不透明カーソルを使う。回帰確認版は文字列カーソルのみを受け付ける。
- 実行中の threadId に対する resume は、その実行中スレッドへ再参加する。excludeTurns はレスポンスへの履歴埋め込みを抑止するだけで、read-only の resume にはならない。
- スレッド状態には idle/active と限られた待機フラグがあるが、所有クライアントの識別情報も、別クライアント所有を示す標準エラーもない。active は「使用中の可能性あり」と扱い、別クライアント所有を断定しない。
- 保存済み添付はクライアント定義のメタデータで、threadId、attachmentType、identityKey により識別される。0.159.1 サーバーは 1 ページを最大 100 件、1 スレッドを最大 100 件、直列化ペイロードを最大 65,536 バイト、attachmentType/identityKey を各 UTF-8 で最大 256 バイトに制限する。プロトコルに MIME フィールドも許可 MIME 一覧もない。

## 根拠の基準

設計資料: doc/app-server-update-plan.md、doc/adr/ADR-013-app-server-path-state.md、doc/adr/ADR-014-app-server-recovery.md、doc/path-state-isolation-design.md。契約根拠: app-server-contract.json、schemas/0.155.1/stable、schemas/0.159.1/stable。添付の上限値は固定 0.159.1 の state および app-server 添付実装で定義されている。リポジトリ基準 517d991250889c19b1b2c86b38ae20dc08cebe9c 時点では実装待ちである。

## 復旧コーディネーターとライフサイクル

有効なチャット画面と現在の Worker bridge に対して、Extension 所有の RecoveryCoordinator を 1 つだけ設ける。復旧エピソードは同時に 1 つだけ実行する。各エピソードは一意なエピソード ID、現在の bridge 識別子、接続世代、所有者世代/partition token、適用済みプロファイルのリビジョン、ローカル/サーバールートを取得する。

コーディネーターは、エピソードの取得、状態遷移の通知、結果の確定に限って短時間の状態ゲートを使う。bridge 停止、プロセス起動、ソケット接続、RPC、再試行待ち、通知処理、UI dispatch を await する間、状態ゲート、コールバックロック、UI dispatcher ロックを保持しない。各 await から復帰した時点でエピソード、bridge、接続世代、所有者世代、プロファイルリビジョン、ルート、キャンセル token を再確認してから状態やデータを反映する。旧エピソードの遅れて返った結果は破棄する。

予期しない Worker 終了、bridge 切断、通信リセット、タイムアウトは、適用済み接続/プロファイル/ルートの状態が変わらず、認証失敗または所有者変更の通知がない場合だけ復旧対象とする。ローカル復旧では既存のローカルライフサイクルに従って新しい Worker とローカル app-server プロセスを起動する。リモート復旧では既存設定の endpoint に対する Worker/socket 接続を作り直す。リモートサーバーを起動、停止、再起動してはならない。

復旧 RPC の前に、状態ゲート外で EnsureWorkerStarted を実行する。proxy オブジェクトが non-null でも process が終了している場合、または RPC が processExited/RpcDisconnected を返した場合は、その bridge を破棄して Worker process と proxy を作り直す。古い non-null RPC proxy を使い続けない。現行 bridge の process-exit と bridge-disconnect 通知を購読し、同じ通知を何度受けても一度だけ処理する。旧 bridge から遅れて届いた通知で置き換え後の bridge を終了させない。

認証失敗、logout/login またはアカウント所有者の変更、token file/プロファイル/endpoint/ルートの変更、無効化または削除済みプロファイル、未保存のプロファイル編集、ユーザーによるキャンセル、古いスナップショットは、自動試行を終了する。適切な明示的サインイン、プロファイル適用、再接続、会話選択の操作を表示する。これらを一時的な通信障害として扱わない。

### エピソードの状態

次の状態を別々にユーザーへ表示する。

| 状態 | 意味と許可する操作 |
|---|---|
| Ready | 現在の bridge が利用可能。 |
| Reconnecting | 同じ適用済み接続先を対象に、自動復旧エピソードを 1 つ実行中。 |
| Synchronizing history | 接続初期化が完了し、会話一覧または選択中スレッドの履歴を読み取り中。ユーザー変更操作は送信しない。 |
| Awaiting conversation selection | 接続済みだが所有者の継続性は未証明。thread/list を更新し、ユーザーに会話選択を求める。 |
| Manual reconnect required | 5 回またはエピソード期限に達した。明示的な再接続操作を表示する。 |
| Sign-in required | 認証情報が拒否された。明示的な認証と接続操作を待つ。 |
| Connection/profile confirmation required | 所有者、endpoint、ルート、プロファイルが変わったか検証できない。旧下書きは明示確認まで隔離する。 |
| History refresh required | 読み取りに失敗したか、通知バッファが上限に達した。現行履歴を古い状態として示し、明示的な再同期を提示する。 |

### 再試行の予定と上限

1 エピソードにつき接続試行は最大 5 回とする。試行前の待ち時間は 0、1、2、4、8 秒とし、0 以外には ±20% の jitter を加える。各試行の期限は 45 秒で、その間に Worker bridge の確立、通信ハンドシェイク、initialize/initialized、初回アカウント/状態読み取りを完了させる。待ち時間を含むエピソード全体の期限は単調時計で 5 分とする。キャンセル、認証失敗、所有者/プロファイル/ルートの変更、古いエピソードの検出時は直ちに終了する。期限切れは Manual reconnect required となり、バックグラウンド再試行を繰り返さない。

再試行できるのは通信/Worker が一時的に利用できない状態、および一時的な接続障害として明示分類された失敗だけとする。履歴 read の失敗は、原因が一時的な通信障害として分類され、すべてのスナップショットが一致する場合に限り、現在の復旧試行をやり直せる。変更操作の再送を許可しない。認証、権限、無効なプロファイル、接続先識別情報、その他のアプリケーションエラーは自動再試行を終了する。

既存の上限付き overload-retry allowlist には、read-only の thread/read、thread/turns/list、thread/items/list、thread/attachment/list だけを追加し、現在の再試行回数、待ち時間、期限ルールを適用する。timeout または overload 応答後に thread/resume を再試行しない。サーバー側ですでに実行中スレッドへ再参加している可能性があるためである。

## 下書きの隔離と不確定操作

最初の復旧対象の接続喪失時に、旧所有者の下書きを変更不能な RecoveryDraft として凍結する。内容は composer のテキスト、下書き再構成に必要な添付記述子、選択中スキルの識別情報、次ターンのモデル/推論/速度/パーソナリティ設定とする。Visual Studio の画面が存続する間だけ保持し、ディスクには保存しない。

RecoveryDraft に認証情報、token 内容、cookie、承認許可/履歴、保留中の承認や MCP 回答、ユーザー検証 proof、その他の secret を含めない。置き換わった所有者の composer へ自動コピーしない。CLI がアカウント継続性を証明できず、再接続後に会話一覧を更新するため、復元には以下すべてのユーザー操作を必要とする: 現在の接続/プロファイルを確認し、更新された一覧から会話を選び、Restore draft または Discard draft を選ぶ。

Restore draft は隔離スナップショットを消費し、安全な下書きフィールドを未送信状態で現行 composer にコピーする。スレッド選択や resume、スキル実行、送信は行わない。送信は別操作とする。送信直前に所有者/世代、添付ファイルの物理的な包含と読み取り可能性、スキルカタログ上の選択スキル、モデルカタログ上のモデル/推論/サービス階層を再検証する。検証に失敗した場合は composer の内容を表示したまま失敗項目を説明し、別所有者へ転送しない。

ユーザーが会話を選択した後、別個の Join 操作を表示する。Join は明示操作後に限り excludeTurns=true の thread/resume を呼ぶ。resume は自動復旧や overload-retry allowlist に含めない。Join が失敗または timeout になっても、read-only 履歴、ページカーソル、隔離 RecoveryDraft を保持し、セッションに参加できなかった理由を表示して明示的な再試行を可能にする。失敗時に履歴や下書きを消去しない。

各変更操作をローカルの一時 operation ledger で追跡する。app-server の要求には不透明な復旧フィールドを追加しない。記録項目はローカル操作 ID、所有者/世代、操作種別、対象会話、NotSent、InFlight、Confirmed、Rejected、OutcomeUnknown の状態とする。dispatch 境界で InFlight にする。確定応答で結果が決まる。dispatch 後、確定応答前に接続を失った場合は OutcomeUnknown とする。メッセージ、turn start/steer、承認回答、MCP 送信、shell command、添付 add/remove を自動再送しない。

復旧履歴にメッセージがないことは、受理されなかった証拠ではない。永続化履歴は遅延または欠落する可能性がある。テキスト文字列や概算時刻が一致しても、不確定操作が特定の項目を作った証拠にはならない。確定した操作応答がある場合は記録した結果を確定できる。切断前に当該操作と相関付けたサーバー項目 ID は受理だけを確定し、すべての副作用の完了は証明しない。NotSent と判定できるのは dispatch が開始していないことを確認できる場合に限る。それ以外の不確定テキストは確認可能な状態で保持し、再試行により操作が重複する可能性を伝える。再試行には明示的な復元/再検証と、その後の個別 Send 操作を必須とする。不確定な添付 add/remove は確認可能な状態で示して新しい一覧で照合するが、自動で再実行しない。

## 読み取り専用の履歴復旧

initialize/initialized の成功後、所有者単位の thread list を更新し、Awaiting conversation selection を表示する。以前選択していたスレッドを自動 resume しない。ユーザーがスレッドを選択したら次を実行する。

1. メタデータと状態を得るため thread/read に includeTurns=false を指定する。
2. thread/turns/list を sortDirection=desc、limit=50、itemsView=summary として最新 50 turn を取得する。
3. thread/items/list を sortDirection=desc、limit=100、turnId 省略としてスレッド全体の最新 100 項目を取得する。ThreadItemEntry を turnId でまとめ、item ID を基準に統合する。
4. 明示的な Load older 操作で返却された不透明 nextCursor を使い、古いページを取得する。各ページ/フィルターのカーソルを個別に保持する。特定 turn の展開では、その turn を対象として 1 ページ最大 100 項目取得し、item ID で統合する。
5. 既知の可視 turn 内の排他的項目位置から開始する場合に限って、目標版のみの構造化 item anchor を使う。turnId を必須とする。継続取得と 0.155.1 互換経路には常にサーバーが返す文字列カーソルを使う。

メモリ上の履歴ウィンドウは最大 1,000 項目かつ描画テキスト 16 MiB とする。表示は仮想化する。古いページを加えることでどちらかの上限を超える場合は、表示位置に近いウィンドウを維持し、表示位置から最も遠い項目を追い出し、Load older/Load newer の移動操作を提示する。描画前にバイト数を制限し、ローカル切り詰めをサーバー側切り詰めと区別する。再接続中にページング履歴全体を読み込まない。

最初の履歴要求より前に通知購読とバッファリングを開始する。各行と通知を所有者世代、接続世代、thread ID、turn ID、item ID で識別する。完了項目を先行する差分より優先し、安定した表示順を保つ。古い世代を無視する。通知バッファは 1,024 件かつ直列化サイズ 8 MiB を上限とする。どちらかの上限を超えた場合、履歴を同期外とし、不完全なバッファ適用を停止して History refresh required を表示する。履歴が完全であるかのように見せたまま通知を黙って破棄しない。

サーバー由来のテキストや構造化内容はすべて信頼できないデータとして扱う。表示内容は SafeMarkdownService で安全化し、ログは ISecretRedactor で secret を除去する。履歴読み取りは read-only であり、保留中の要求に回答しない。

## 保存済み添付の再構築

スレッド選択後、thread/attachment/list に limit=50 を指定し、同じ threadId とカーソルで nextCursor が null になるまでページを取得する。先頭ページだけで終わらせない。サーバーは 1 スレッドにつき最大 100 件を保持するため、通常は 50 件ずつ最大 2 ページとなる。それでもカーソル仕様に従い、ローカルの一意レコード数は 100 件を上限とする。type と identity は空でない UTF-8 文字列 256 バイト以内、直列化 payload は 65,536 バイト以内と検証する。固定 CLI からの応答であってもサーバーの値を信頼しない。

解析前に直列化 payload が 65,536 バイト以内であることを検証する。認識済み添付種別は、狭く定義した種別ごとの上限付きメタデータに変換し、解析後に元 JSON を破棄する。不明な種別については上限付きの識別子/種別/時刻のみ保持し、任意 payload JSON を保持も表示もしない。このフェーズでは汎用 MIME 解釈を追加しない。プロトコルが示すのは attachmentType とクライアント定義 payload であり、MIME プロパティや MIME 列挙値ではない。不明な添付種別、認識済み種別内の不明 MIME、形式不正な payload、サイズ超過、マッピングできないパスは、利用不可の理由とともにメタデータのみ表示する。型付きプレビュー、open/reveal、種別ごとの MIME 方針は #155 で扱い、#152 のパスマッパーとローカル物理境界を使う。

ページ取得前に添付通知の購読を始める。(threadId, attachmentType, identityKey) で統合し、識別情報ごとに各世代の attachmentId を追跡する。deleted 通知はその attachmentId の tombstone を作り、処理中の古いページから同じ ID の行が復活するのを防ぐ。同じ識別情報でも後続 created 通知が別 attachmentId を持つ場合は新しい行として表示し、以前の tombstone で隠さない。created 通知には識別情報しかなく payload は含まれないため、再取得対象として記録し、添付データを捏造せずページ一覧を更新する。現在の走査後に更新処理をまとめて実行する。上限付き整合確認中にも所属関係が変化し続ける場合は一覧を stale として明示し、手動更新を提示する。以前の所有者または接続世代から届いた通知は破棄する。

非 ephemeral fork は添付関係をコピーするが、添付ごとの更新通知は送らない。forkedFromId を含む thread/started 通知を受けた場合、新しいスレッドの添付一覧を明示的に全件更新する。元スレッドの行や ID が fork 先でも有効だと仮定しない。

## Worker 契約と manifest

#153 の復旧 API には Worker 契約 v19 を使う。v18 は承認済み #152 の所有者 partition 変更に割り当て済みである。Worker 契約の producer と consumer は同時に更新する。thread メタデータ、turn ページ、item ページ、添付ページ用の型付き owner/generation 付き read request/result、世代付き添付更新イベント、上限付き復旧状態/下書き/operation ledger callback を追加する。app-server の要求 payload は固定スキーマどおりとし、復旧状態や ledger ID を未定義の wire field として送らない。

同じ変更で app-server-contract.json の使用一覧に、実際に使う thread/read、thread/turns/list、thread/items/list、thread/attachment/list と thread/attachment/updated 通知を追加する。別途承認されたユーザー操作が実装されない限り、thread/attachment/add/remove は allowlist に追加しない。固定スキーマ両版の契約テストに excludeTurns、構造化/文字列カーソル差、ページング応答カーソル、添付上限/形状、MIME が未定義であること、通知識別子、新しい Worker payload を含める。

## 実装順序

1. v19 型付き契約、ローカル復旧/下書き/operation 記録を追加し、protocol method/notification allowlist とスキーマ契約テストを更新する。
2. Extension 所有の単一実行コーディネーター、短時間のゲート境界、bridge 終了通知処理、一時障害分類、エピソードキャンセル、jitter、試行/エピソード期限、終了状態の表示を実装する。
3. 下書きの隔離/復元/破棄とローカル不確定操作 ledger を実装する。復元前に接続/プロファイル確認と会話選択を必須とし、復元と Send を別操作にする。
4. メタデータのみの thread 読み込み、要約 turn ページ、全体 item ページ、turn 展開、通知バッファ/統合、履歴ウィンドウ上限、overflow/手動再同期、会話の個別選択フローを実装する。
5. 添付の全ページ取得、type/identity/payload 検証、通知 tombstone/更新対象化/整合確認、未対応種別のメタデータのみ表示を実装する。型付きプレビューとファイル操作は #155 に残す。
6. 競合、通信、所有者切替、復旧、ページング、payload、UI テスト、警告ゼロの Debug/Release ビルド、固定 0.159.1 契約確認と 0.155.1 回帰確認、VSIX/XAML 確認、再試行・再接続後の選択・隔離下書き復元/破棄・履歴ページ・overflow・添付状態のスクリーンショットで検証する。実際に確認した根拠だけを [implementation.md](implementation.md) と [task.md](task.md) に記録する。

## 受け入れ条件

- bridge 終了、切断、タイムアウト、callback が同時発生しても復旧エピソードは 1 つだけとなり、共有ロックを保持したまま callback がネットワーク/RPC を待たない。processExited/RpcDisconnected 後の stale non-null proxy を破棄して再作成する。
- 定義した一時的な通信/Worker 障害だけが、規定の 5 回と期限内で再試行される。認証、プロファイル/設定/ルート変更、アカウント所有者変更、キャンセル、古い世代は再試行を停止する。
- リモート復旧は外部サーバーを再起動しない。接続後に thread/list を更新し、明示的な会話選択を要求する。
- 旧下書きは明示的な復元/破棄まで変更不能かつ隔離され、復元によって送信されない。secret、承認、許可は所有者世代をまたがない。
- 不確定操作は自動再送しない。履歴に項目がないこと、テキストの一致、概算時刻の一致では OutcomeUnknown を解消しない。受理を確定できるのは確定応答、または切断前に当該操作と相関付けられたサーバー item ID だけとする。NotSent と判定できるのは dispatch が開始していないことを確認できる場合に限る。
- 初回履歴は最新ページから表示し、過去のページと turn 詳細は段階的に取得する。安定識別子、完了優先、世代境界、1,000 項目/16 MiB のウィンドウ、1,024 件/8 MiB の通知 overflow を検証する。
- 添付一覧は nextCursor が null になるまで取得し、サーバー/ローカル上限を守る。添付 ID ごとの削除 tombstone と作成通知の再取得要求を統合し、payload を捏造しない。fork 通知で新規スレッド一覧を更新する。任意 payload/MIME によって表示やローカルファイル操作を許可しない。
- 対応する失敗はユーザーに見え、次の操作が分かる。ディスク永続化は追加しない。
- UI 受け入れ確認では light/dark/high-contrast テーマ、狭い tool window 幅、キーボードのみの操作と focus 順序、Join、下書き復元/破棄、Send、過去履歴、更新、再試行を確認する。

| テーマ | 画面幅と操作方法 | シナリオ |
|---|---|---|
| Light | 狭い tool window 幅、キーボードのみ | Reconnecting、手動復旧、Join、下書き復元/破棄、個別 Send、Load older、履歴更新、添付の利用不可状態 |
| Dark | 狭い tool window 幅、キーボードのみ | Reconnecting、手動復旧、Join、下書き復元/破棄、個別 Send、Load older、履歴更新、添付の利用不可状態 |
| High contrast | 狭い tool window 幅、キーボードのみ | Reconnecting、手動復旧、Join、下書き復元/破棄、個別 Send、Load older、履歴更新、添付の利用不可状態 |
