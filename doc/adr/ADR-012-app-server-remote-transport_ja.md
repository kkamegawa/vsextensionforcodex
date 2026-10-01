# ADR-012: リモート App Server transport

- 日付: 2026-09-13
- 状態: Accepted
- 追跡: Codex App Server 更新・リモート接続の子 Issue

## 決定

- ローカル stdio を既定とし、すでに稼働している remote server に明示的に接続する WebSocket transport を追加します。
- `wss` を許可し、`ws` は loopback のみに許可します。bearer 認証には token file を使い、内容を公開しません。証明書検証を無効化できません。
- local connection は自身の child process を所有します。remote connection は socket のみを所有し、restart の代わりに reconnect を提示します。
- 接続遷移を直列化し、health check と RPC availability を分離し、読み取り RPC を上限付き exponential backoff と jitter で再試行します。

## 結果

remote lifecycle は Extension の外部に残し、transport policy は local と remote の client で共有します。

## Amendment (2026-09-23、PR #157 review)

- 対象: PR #157 で実装した Issue #151（remote transport）。
- 両 transport が同じ server-request dispatcher を共有し、要求所有権、error 整形、close 後の応答抑止が分岐しないようにします。すべての transport で通知を wire 順に 1 件ずつ処理します。streaming delta と turn lifecycle event はこの順序に依存します。
- Worker は remote transport の `Closed` event を監視し、child process の `Exited` signal がない remote 接続では `Degraded` と reconnect 操作を報告します。
- CI は固定 contract executable で build します。最新 stable release は start/initialize smoke test のみを non-blocking で実行します。最新 executable が pinned schema contract と異なる場合に build を失敗させないためです。
- 理由: これらは review による修正であり、方針変更ではありません。既存決定（共通 transport 動作、remote 独自状態、固定 contract）を実現します。

## Amendment (2026-09-30、PR #157 second review)

- 対象: PR #157 で実装した Issue #151（remote transport）。
- Remote profile editor は後日の Issue #151 ではなく PR #157 に含めます。tool window の connection-target flyout から保存 profile を編集し、明示操作で適用します（design.md section 12）。理由: 通常の UI から編集できなければ remote transport を利用できず、機能が未完成のまま公開されるためです。
- Remote 接続には `localRoot` と `serverRoot` の両方が必要です。どちらかがなければ Worker は `Degraded` とし、対応付けされていない local path を server へ送りません。理由: local path を server で有効に使う方法は path mapping だけであり、既存決定（ADR-013）を変更せず厳密化します。
- Remote 接続切断は接続遷移 gate の下で通知し、より新しい connect、restart、dispose がその接続を置き換えていない場合だけ公開します。理由: 遅れて届いた close が新しい `Ready` 状態を上書きしないためです。これは既存の遷移直列化を実現します。
- Remote 接続は Preview として公開します。flyout heading と README に Preview を表示し、health check、上限付き retry、account/endpoint 間の状態分離がまだないと説明します（Issue #151、#152）。理由: local stdio はリリース可能ですが、remote の残項目は未完了です。利用可能な opt-in 機能を保ちながら、公開範囲を正確に示します。

## 提案 amendment (2026-10-01、Issue #151 設計レビュー)

- 状態: 提案中。実装開始前のユーザー確認待ちです。
- 対象: `doc/secure-remote-connection-design_ja.md` を規範設計とし、Issue #151 の安全な remote 接続動作を完成させます。上記 Accepted amendment は各日付時点で利用できた動作の記録として維持します。特に2026-09-30の health 診断・上限付き retry が未対応との記述は、その時点の実装不足を記録したものであり、本 amendment を事前承認するものではありません。
- 決定案: 純粋な Contracts project に endpoint validation を置き、Protocol、Worker、Extension が共通で使います。`ws` は構文上 canonical loopback として適格な形式だけ許可し、純粋 policy は DNS 検証をしません。全 scheme（`wss` を含む）で unspecified literal destination と credential/query/fragment を含む URI を拒否します。`ws` はさらに DNS alias、末尾ドット付き localhost、`localhost` サブドメイン、すべての non-loopback 形式を拒否します。token access 前に Worker が正確な localhost を解決し、すべて loopback であることを検証して、検証済み authority の直接接続試行だけを pin します。remote `wss` IP literal や proxy target には `ConnectCallback` pinning を適用しません。WebSocket と health traffic で Worker 寿命の同一 proxy resolver を使い、platform TLS を維持し、redirect/cookie/既定 credential を無効化します。proxy failure 後に直接通信へ fallback しません。proxy を迂回するのは検証済み loopback literal と正確な localhost endpoint だけです。同じ verified local endpoint から派生した health probe だけが proxy を迂回します。
- 決定案: Save では profile metadata を検証し、token file は開きません。適用済み profile の fingerprint と generation をメモリー内で保持します。reconnect 前に最新保存 profile を厳密な snapshot と比較し、未保存または変更済み metadata を拒否します。選択状態の永続化、Save、rename、enable/disable、delete、local への明示切り替えを含む同一 instance 内のすべての profile/settings 変更を、snapshot validation と RPC dispatch と共に直列化します。token 読み込み・socket 停止・send より前に Worker でも immutable reconnect request を再検証します。token 内容は Worker だけが各 handshake の直前に絶対 local file から読み、16 KiB 上限、strict UTF-8、任意 BOM、上限付き RFC 6750 token 構文を適用します。ACL は調査・変更しません。すべての socket、診断 queue、callback、保留操作を退役させるまで参照数付き secret-redactor lease を保持し、queue に渡す前に秘匿します。Worker と Extension の両 writer は共有 `diagnostics.log` に追記し、それぞれ追記直前に redaction します。その他の既存出力 writer も書き込み前に redaction します。transition gate を保持したまま同じ gate を待つ callback を await しません。gate 内で callback を抑止/unsubscribe して dispose し、gate 解放後に close callback を追跡・drain してから lease を解放するか、同等の callback-owned lease を使います。telemetry は追加しません。
- 決定案: remote startup 全体に monotonic 45 秒 deadline を適用し、token 読み込み、WebSocket、initialize、起動時 account read に個別上限を設けます。account が SignedOut、または account-read 失敗が Unavailable の場合も RPC Ready を維持します。transport close・caller cancellation・起動全体 deadline は候補接続を退役させます。health 診断は未認証の authority-root `/healthz` と `/readyz` GET を並行して実行し、合計 5 秒に制限して response body を読まないようにします。routing path がある場合は authority-root scope と未検証 route coverage を報告します。health は RPC state や機能可否を変えません。.NET 8 WebSocket の `KeepAliveInterval` を 30 秒に設定します。unsolicited PONG frame は peer 生存の証明ではなく、`KeepAliveTimeout` は利用できません。有効に parse した response、notification、server request をすべて inbound-activity sequence で監視します。30 秒の inbound silence 後に `account/read(refreshToken: false)` を 10 秒 deadline で送ります。probe 開始後に sequence が進んで timeout した場合は2回目を送らず、通常の30秒 idle wait を再開します。traffic がない場合のみ直ちに2回目を送信し、watchdog probe に overload retry は適用せず、silent timeout が2回続いた場合に現在の generation の捕捉済み socket だけを閉じます。自動 reconnect や mutation replay はしません。
- 決定案: 呼び出し側が制御する retry boolean を廃止し、厳密な method 許可リストと `account/read.refreshToken`・`skills/list.forceReload` predicate を所有する `SendReadOnlyRequestAsync` に置き換えます。他の6つの既知 read-only method に完全 shape 検証を追加しません。明示 `refreshToken` false の `account/read`、指定された account/model/thread/permission/MCP read、明示 `forceReload` false の `skills/list` に対し、完了した `-32001` response だけを retry します。その他の引数、method、すべての mutation は一度だけ送信します。初回後 3 回まで、250/500/1000 ms の基準待機時間、±20% jitter、既存 per-call monotonic deadline、キャンセル、generation 固定を適用します。将来の history/attachment read は別途根拠を確認するまで許可しません。
- 決定案: 同梱する Extension/Worker contract を v16 から次の利用可能 version へ一括更新します（base が変わらなければ v17）。merge 前に最新 version へ rebase し、binary 不一致を拒否します。`worker/restart` と `worker/reconnect` を分け、stop/send 前に拒否します。型付き `-32051` connection-operation rejection reason を返します。診断と retry 実装後に Preview 説明を更新し、Issue #152 の account/principal 分離制限は維持します。
- 理由: 既存計画はすでに endpoint security、独立した health/RPC 状態、上限付き read-only retry、local/remote 所有権の分離を要求しています。本 amendment はそれらを安全に成立させる層間の規則を具体化します。endpoint policy の不一致、DNS rebinding、古い profile での reconnect、非同期診断経由の secret leak、誤った health 判定、cache/discovery 入力の retry、世代をまたぐ無応答状態の混入を防ぎます。Issue #152 のプロセス間 cache/account/principal 分離や Issue #153 の復旧保証は追加しません。
