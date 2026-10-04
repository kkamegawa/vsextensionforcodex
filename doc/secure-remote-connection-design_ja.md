# 安全なリモート App Server 接続 — Issue #151

[English](secure-remote-connection-design.md)

## 目的と範囲

Issue #151 で追跡する安全なリモート接続の動作を完成させます。Extension は引き続きローカル Worker と通信し、Worker がローカル stdio または明示的に適用した WebSocket プロファイルを選択します。本設計では endpoint policy、接続とトークンの所有権、health 診断、読み取り RPC の過負荷時再試行、プロファイルの鮮度、ローカル再起動とリモート再接続の動作を定めます。リモートサーバーは引き続き外部管理です。上流の WebSocket が実験的機能のため、リモート機能は Preview とします。Issue #152 の所有者境界は併設する [パスと状態の設計](path-state-isolation-design_ja.md) に定めます。

Issue #150 は固定 Codex CLI 0.159.1 の契約（0.155.1 を回帰基準）、共通 JSON-RPC dispatch、接続世代の動作を提供します。Issue #152 は完全なパスマッピングと、アカウント・認証主体・ルートごとの状態分離を担当します。Issue #153 は自動再接続と履歴復旧、下書き保持、不確実な変更操作の扱いを担当します。本設計は後続 Issue の保証を先取りしません。

## コンポーネントと所有権

| コンポーネント | 既存の範囲 | 設計変更 |
|---|---|---|
| Contracts (`netstandard2.0`) | Extension/Worker 共通契約型 | 純粋な `RemoteEndpointPolicy` と型付き検証結果を追加します。使用できるのは `System.Uri` と `System.Net.IPAddress` で、Protocol への依存は禁止します。 |
| Protocol | `TransportPolicies`、`JsonRpcRetryPolicy`、`SendIdempotentRequestAsync`、`WebSocketTransportSecurityPolicy` | WebSocket security policy が共通 endpoint policy と token format policy を呼び出します。呼び出し側 boolean に依存する API を、許可リストを内部検証する `SendReadOnlyRequestAsync` に置き換えます。Protocol は Contracts を参照し、Contracts は Protocol を参照しません。 |
| Worker | `WorkerRpcService`、`File.ReadAllText`、transport 作成、Worker 診断 | 接続ゲートの検証、上限付き token reader、Worker 寿命の networking factory、型付き接続診断、idle generation watchdog を追加します。上限のない `File.ReadAllText` による token 読み込みを置き換えます。 |
| Extension | `RemoteProfilePresentation`、bridge 操作、接続先 flyout | 重複する URI 検証を削除し、共通 policy を使用します。未保存の編集状態と Worker に適用済みの厳密な profile snapshot を分離します。 |
| Secret redaction と出力先 | `ISecretRedactor`、キュー経由の診断、Worker/Extension 出力 | 参照数付き secret lease を追加し、callback queue または I/O に渡す前の生成時点で秘匿します。token lease 解放前に callback を退役させます。 |

新しい runtime package は不要です。ネットワーク処理は Worker に置きます。telemetry sink は追加しません。

## Endpoint policy とネットワーク

共通 endpoint policy は純粋な検証のみを行い、DNS、ファイルアクセス、ネットワーク要求、変更操作を実行しません。remote host には絶対 `wss` URI を許可し、`ws` は構文上 loopback として適格な形式だけを許可します。policy は DNS 検証を行いません。token を読む前の正確な `localhost` の名前解決と全結果の loopback 確認は Worker が担当します。その他の scheme、相対 URI、ユーザー情報、query string、fragment を拒否します。routing path は許可します。scheme を問わず unspecified literal destination（`wss` を含む）も拒否します。認証情報付き WebSocket handshake で TLS 検証を無効化したり redirect を追跡したりする設定・UI は設けません。

`ws` は次の loopback 形式だけを許可します。

- hostname が正確に `localhost` で、大文字小文字を無視した ordinal 比較に一致すること。末尾ドット付きと `localhost` のすべてのサブドメインは、DNS が loopback に解決しても拒否します。
- 第1 octet が 127 の canonical IPv4 literal。
- IPv6 loopback literal。
- 内包する IPv4 アドレスが loopback の場合に限る IPv4-mapped IPv6 literal。

unspecified address、DNS alias、そのほかすべての hostname/address による `ws` を拒否します。canonical literal の解析では、別表記や曖昧な文字列表現も拒否します。正確な `localhost` は token file を読む前に Worker が名前解決し、解決結果がすべて loopback であることを確認します。その検証済み authority の直接接続試行に限り、`ConnectCallback` で接続先を検証済み loopback address の集合に固定し、接続できるまで名前解決の順に各 address を試します（サーバーが `::1` または `127.0.0.1` の一方だけで待ち受ける場合があるため）。TLS/Host 用 authority は保持します。この callback を remote `wss` の IP literal や proxy target に適用しません。これにより二度目の DNS 解決による接続先変更を防ぎます。DNS alias を許可する名前解決はしません。proxy を迂回するのは Worker が loopback と検証した literal endpoint と正確な `localhost` endpoint だけです。host が IP literal でも remote `wss` endpoint は設定済み proxy を通します。検証済み loopback endpoint から派生した health probe だけが proxy を迂回し、それ以外の probe は捕捉済み proxy policy を使います。

Worker 寿命の networking factory は、WebSocket 接続と HTTP 診断の両方に共通の `HttpMessageInvoker` を提供します。両経路で同じ捕捉済み `HttpClient.DefaultProxy` resolver を使い、`HTTP_PROXY`、`HTTPS_PROXY`、`ALL_PROXY`、`NO_PROXY`、その後 Windows user proxy 設定を適用します。Worker が loopback と検証した literal endpoint と正確な `localhost` endpoint だけを迂回します。host が IP literal の remote `wss` endpoint は設定済み proxy の対象です。proxy 解決時には `ws` を HTTP、`wss` を HTTPS に対応付けます。両経路とも cookie、既定資格情報、redirect、および App Server bearer token の proxy 転送を無効化し、platform の TLS 既定値を維持します。proxy 認証を許可するのは、ユーザー既存の proxy 環境変数またはシステム設定で明示されている場合だけです。proxy 認証 UI や Extension 独自設定を追加しません。proxy 失敗後に直接接続へ fallback せず、proxy 設定をログに記録しません。新しい Worker は有効な proxy 設定と Visual Studio process の環境変数を取得します。OS の環境変数変更を継承する場合は Visual Studio を再起動し、system proxy 設定の再取得には Worker を再起動します。invoker を渡す場合、WebSocket option には transport 固有設定だけを含めます。

## Profile の保存、適用 profile の鮮度、操作ゲート

Save は空でない一意な表示名、endpoint policy、両方の root、および有効化時の空でない構文上有効な絶対 local token-file path を検証します。Save は token file を開いたり読み込んだりしません。ファイルの存在・読み取り可否・内容は、明示的な接続時に Worker が確認します。Worker は local/server root の不足を接続前に独立して拒否します。既存の attachment 検証は、設定 local root 外の local path を `turn/start` 前に拒否します。health 診断は保存済みかつ有効で未保存編集のない profile と妥当な endpoint metadata を要求しますが、token file の読み込みや token は不要です。認証なしで固定 health route にだけ要求します。これにより既知の profile に対する明示 opt-in を維持しつつ、health check で token を読んだり任意 endpoint を受け取ったりしません。

Worker と Extension は、適用済み profile の表示名、canonical な非秘密 metadata fingerprint（endpoint、token-file path、roots、name、enabled state）、接続世代をメモリー内に保持します。profile の保存・名前変更・無効化・削除によって稼働中 socket は変わりません。変わるのは今後の接続適格性だけです。token file 内容のローテーションは metadata fingerprint を変えません。

明示的な remote reconnect の前に、Extension は適用済み profile 名で最新の設定を読み直し、適用 snapshot と完全に同一の metadata fingerprint を持つ有効 profile を要求します。さらに、reconnect 対象 profile に未保存編集がある場合は拒否します。不一致・削除時は `ProfileChanged` または `ProfileUnavailable` を返し、現在保存済みの profile の明示 apply、または local への明示切り替えを求めます。古い options で再接続したり、暗黙に local へ fallback したりしません。

選択状態の永続化、Save、rename、enable/disable、delete、local への明示切り替えを含む同一 instance 内のすべての profile/settings 変更を、snapshot validation および Extension RPC dispatch と共に同じ操作ゲートで直列化します。profile snapshot と期待 generation を持つ immutable `RemoteReconnectRequest` を渡します。token 読み取り、socket 停止、send の前に Worker 接続ゲートが、bound options と期待 generation に対する有効 metadata を再検証します。状態・診断結果を publish できるのは現在の generation だけです。これはプロセス内の鮮度保証であり、Issue #152 が担当するプロセス間 registry、cache、account、principal の分離を保証しません。

## Token file と secret の寿命

token path は絶対 local path とします。UNC path、network drive、device namespace、その他 local でない形式を拒否します。symlink と junction を解決し、最終 target が local であることを要求します。開いた stream にサイズ上限を適用します。ACL の検査・変更・警告は行いません。local token file を非公開に保つ責任は運用者にあります。

明示 handshake および Issue #153 の各回復試行の直前に、Worker は strict UTF-8 で最大 16 KiB を非同期読み込みます。UTF-8 BOM は任意で許可して除去し、前後の空白だけを除去します。32 文字以上の単一 ASCII RFC 6750 bearer-token 値を要求します。空、短すぎる、上限超過、encoding 不正、ASCII 以外、内部 whitespace、制御文字を拒否します。file 不在、directory、読み取り失敗も拒否します。内容を cache せず watcher も設けません。token rotation は次の明示 reconnect または開始済みの一時障害回復試行で反映し、内容の更新だけでは接続や操作の再送を開始しません。

値は WebSocket handshake の bearer Authorization header としてだけ送信します。Extension、settings、contract DTO、Remote UI、transcript、diagnostics に渡すのは token-file path までとし、token 値を含めません。`ISecretRedactor` は `RegisterSecret(string) -> IDisposable` を提供し、Worker ごとに参照数付き lease を使って、同じ値の重複登録が別の有効 lease を解除しないようにします。読み込み直後・handshake 処理前に登録します。診断文字列は enqueue または書き込みより前の生成時点で秘匿します。queue と callback が受け取るのは、すでに秘匿済み文字列か分類済み error だけです。cleanup で transition gate を保持している間に callback を unsubscribe または抑止し、候補 connection を dispose します。同じ gate を必要とする可能性のある callback を gate 保持中に await してはいけません。queue 済み close callback を追跡し、gate 解放後に drain してから lease を解放します。同等の secret 寿命を保つ callback-owned lease 方式も使えます。lease の寿命を超えて raw exception や header 文字列を保持しません。

現在の出力先は Worker/Extension stderr、共有 `diagnostics.log`、Extension の Codex Diagnostics Output Channel です。Worker と Extension の各 writer は同じ `diagnostics.log` に追記し、それぞれが追記直前に redaction します。stderr と Output Channel の各 writer も書き込み前に redaction します。telemetry sink は存在せず、本設計で診断出力先を追加しません。raw header、token 値、health body、proxy 設定、未秘匿の例外 chain を出力しません。

## 接続寿命、health、失敗結果

明示的な接続または Issue #153 の回復を含め、各 remote 接続試行に monotonic 45 秒 budget を適用します。各段階の上限は token 読み込み 5 秒、WebSocket handshake 15 秒、`initialize` 15 秒、起動時 `account/read` 15 秒です。各段階には全体残り時間を超えない timeout を渡します。キャンセルは接続失敗ではなくキャンセルとして扱います。候補 socket 作成後の失敗では候補を退役させ、保留処理を完了してから戻ります。新しい generation を古い失敗で上書きしません。account-read 失敗は `initialize` 失敗と区別します。正常に返った `SignedOut` account state は接続状態を維持し、既存の sign-in 操作を可能にします。initialize 後に `Unavailable` と分類された account-read error だけでは RPC 接続を degraded にせず、Worker の既存 Ready state を維持します。transport close、caller cancellation、startup 全体 deadline は起動を失敗させ、保留処理を cleanup して候補接続を退役させます。

Worker 所有の `RemoteConnectionDiagnostics` service は App Server 接続と独立して診断します。networking factory を使い、`ws` から HTTP、`wss` から HTTPS を導出し、authority と port を保持して固定 `/healthz` と `/readyz` だけへ要求します。認証なし GET 2 件を、monotonic 5 秒 budget で並行実行します。Authorization と Origin は送信せず、認証 header を継承しません。`ResponseHeadersRead` で status を分類し、body は読まずに response を破棄します。redirect と cookie は無効です。health 失敗で正常な RPC 接続を妨げません。

WebSocket URI に root 以外の routing path がある場合でも、probe は authority root へ送ります。結果に `HealthScope = AuthorityRoot` と `RouteCoverage = Unverified` を設定します。UI は root listener の診断と明記し、routing path の application が healthy とは表示しません。health response 成功は JSON-RPC、認証、機能対応の証明にはなりません。

型付き結果に含めるのは probe 状態、任意の数値 HTTP status、所要時間、観測日時、固定または秘匿済みの理由だけです。health check はサーバー・transport の起動、停止、再接続、initialize、account credential 更新、変更 RPC を実行せず、Worker 接続状態を変えません。active かどうかに関係なく、選択中の保存済み・有効 profile に対して実行できます。inactive profile の RPC 状態は health から推測せず `Not connected` と表示します。

UI は診断対象 profile と実際の接続先を分けます。profile 選択または保存済み endpoint metadata の変更で診断を消去・cancel します。別診断、接続・世代変更、dispose 後に完了した古い結果は破棄します。実行中は重複 check を無効化し、既存の polite live region に状態を出します。動的表示はすべて `SafeMarkdownService` で処理します。

transport は有効に parse された inbound JSON-RPC response、notification、server request のすべてを activity として watchdog に通知し、monotonic inbound-activity sequence と、watchdog が時刻を記録する activity signal を提供します。silence は最後の inbound message から測ります。watchdog は時刻を更新するたびに自身の activity generation を進め、期限判定の前にその generation を記録します。これにより判定と競合した activity が probe の基準に取り込まれません。active remote 接続で最後の inbound message から 30 秒 activity がない場合、 `refreshToken: false` の `account/read` を overload retry なしで 10 秒 deadline で送ります。有効な parsed inbound message はすべて silence count をリセットします。最初の deadline 時に基準から activity generation が進んでいれば2回目を送らず、count を戻して通常の30秒 idle wait に戻ります。最初の probe 中に activity がなかった場合のみ、直ちにもう1回を送ります。2回目も probe episode 開始後に activity がないまま timeout し、socket を停止する直前に connection-transition gate の内側で再確認しても activity がない場合だけ、捕捉済み socket を閉じ、その generation が現行なら `Degraded` を通知します。その間に activity があれば socket を維持し、idle wait をやり直します。watchdog 自身は再接続や変更操作の再送を行いません。計画する Issue #153 の Extension 側復旧管理は、この一時切断通知を受けて上限付きの接続回復を行えます。新しい generation に置き換わった後の close は state を上書きしません。`SignedOut` account state は接続を維持し、既存の sign-in 操作を可能にします。

失敗は endpoint 不正、profile 変更・利用不可、token file 不在・読取不可・内容不正、認証拒否、証明書拒否、DNS/ネットワーク失敗、timeout、RPC initialize 失敗、account-read 失敗、health route の結果に分類します。秘匿が必要な詳細は redaction までの短時間だけ保持し、ユーザー表示は固定分類文言とします。

.NET 8 の WebSocket `KeepAliveInterval` を 30 秒に設定します。これは unsolicited PONG frame を送るだけで、peer の生存証明にはなりません。.NET 8 では `KeepAliveTimeout` を利用できません。idle-generation watchdog が peer silence を検出します。

## 読み取り overload retry

呼び出し側が idempotency boolean を渡す API を廃止し、`SendReadOnlyRequestAsync` に置き換えます。厳密な method 許可リストは helper 自身が検証します。許可リスト以外の parameter 判定は `account/read` の `refreshToken` と `skills/list` の `forceReload` だけに適用し、残り6つの既知 read-only method に新たな完全 shape 検証を要求しません。それ以外の全メソッド（変更操作と未知メソッドを含む）は最大 1 回だけ送信します。

| 許可するメソッド | 再試行条件 |
|---|---|
| `account/read` | `refreshToken` が明示的に boolean false。省略、不正、true は再試行不可。 |
| `account/rateLimits/read` | 既知の read-only method。呼び出し側 override なし。 |
| `thread/list` | 既知の read-only method。呼び出し側 override なし。 |
| `thread/goal/get` | 既知の read-only method。呼び出し側 override なし。 |
| `model/list` | 既知の read-only method。呼び出し側 override なし。 |
| `permissionProfile/list` | 既知の read-only method。呼び出し側 override なし。 |
| `mcpServerStatus/list` | 既知の read-only method。呼び出し側 override なし。 |
| `skills/list` | `forceReload` が明示的な boolean false の場合のみ。省略・不正・true は cache を消去して discovery を再走査できるため、一度だけ送信して再試行しません。その動作を永続変更とは表現しません。 |

false のみを再試行可能とする前に、固定 upstream の `codex-rs/app-server/src/request_processors/catalog_processor.rs`（`skills_list_response`）、schema `codex-rs/app-server-protocol/schema/json/v2/SkillsListParams.json`、test `codex-rs/app-server/tests/suite/v2/skills_list.rs` で `skills/list` parameter の意味を検証します。将来追加される history/attachment read（`thread/read`、item/turn listing、attachment listing など）は暗黙に許可しません。明示的な契約根拠とテストを追加した場合だけ許可リストを拡張します。

完了した JSON-RPC overload response の code が `-32001` の場合だけ retry します。transport error、認証/TLS failure、切断、timeout、不正 response、`-32601`、その他 RPC error、変更・未知 method は再送しません。retry は最大 3 回、送信合計は最大 4 回です。基準待機時間は 250、500、1000 ms で、各々に一様な ±20% jitter を加えます。policy 値を検証し、有限かつ上限付きにします。既存の各 call timeout に基づく単一 monotonic deadline がすべての send と wait を制限し、各 attempt には残り時間だけを渡します。cancel、接続終了、generation 退役時には処理・待機を速やかに cancel します。retry は最初に捕捉した generation でのみ実行します。上限到達時は最後の元 RPC error を維持します。決定的な境界テストのため time、delay、jitter source を注入します。

## 契約とユーザーインターフェイス

Issue #151 の本設計は、同梱 Extension/Worker 契約を v16 から v17 へ更新した履歴を記録します。Issue #152 が v18 を導入し、Issue #153 は bounded history と添付 metadata 読み取りを含む v19 を導入しました。この履歴上の Phase 2 version を将来の基準にしません。Extension/Worker の不一致は fail closed とし、互換性交渉を追加しません。

Worker status に型付き connection target と diagnostic snapshot を追加します。snapshot は local/remote 種別、設定表示名、metadata fingerprint、接続試行・接続 generation を持ち、token 値を含みません。その generation の Ready/Busy/WaitingForApproval だけが接続済み target を確定します。Disconnected/Connecting/Degraded は意図した target を報告します。`worker/connection/diagnose` と接続のみを置換する `worker/reconnect` を追加します。remote reconnect は最後に適用した snapshot を使い、最新 token file を読みます。`worker/restart` は Worker が所有する local process に限定し、remote 状態では停止や送信の前に拒否します。

型付き `-32051` の `WorkerErrorCodes.ConnectionOperationRejected` 応答に、理由を 1 つ含めます: `LocalProcessRequired`、`RemoteConnectionRequired`、`ProfileUnavailable`、`ProfileChanged`、`StaleGeneration`。自由形式 path、endpoint、token file 内容、raw transport exception は返しません。local restart 操作は現状を維持します。degraded 時の local 操作名は `Restart local app-server`、remote は `Reconnect remote app-server` とし、tooltip、automation name、help text、command-state notification も合わせます。PID は実際に所有する local process の場合だけ表示します。

接続先 flyout は health/ready 結果と RPC state を分離し、診断対象と active target を区別し、pathful endpoint の authority-root 診断を説明します。Usage/History との排他、keyboard 操作、Escape/Tab、Visual Studio theme resource、accessibility name、live status を維持します。health 結果で Connect や機能の利用可否を変えません。

Preview の説明を更新し、health diagnostics と許可済み read-only RPC の上限付き retry が利用できると伝えます。account/principal 状態分離は併設する Issue #152 の設計に従って説明し、外部管理 remote server・Worker 所有 socket の区別を維持します。

## Issue #153 の回復との統合

[合意した復旧設計](connection-history-recovery-design_ja.md)は、Worker 置換をまたぐ Extension 所有の単一回復管理を追加します。最大5試行、各試行前の待機は順に0・1・2・4・8秒、1試行45秒、全体5分です。各試行で既存の endpoint／profile／token／TLS policy を維持し、handshake 直前に token を読みます。認証・証明書・設定・既知の所有者変更では自動試行を停止します。Remote server は外部管理のままで、変更操作は再送しません。現行契約 v18 は、予定する v19 の実装まで変更しません。

## 検証と受け入れ

| 項目 | 必要な証拠 |
|---|---|
| 共通 endpoint policy | Contracts の純粋テストで絶対 scheme、構文上の loopback 適格性、正確な localhost 判定、末尾ドット・`*.localhost` 拒否、canonical loopback IPv4/IPv6、mapped IPv6、全 scheme の unspecified destination、DNS alias、userinfo、query、fragment、routing path、non-loopback `ws` を確認します。Protocol、Worker、Extension が共通結果を利用し、DNS 検証は Worker が行います。 |
| DNS と proxy の安全性 | token 読み込み前の localhost 解決、全結果 loopback 条件、検証済み exact-localhost authority/接続試行だけを検証済み address 集合へ pin し接続できるまで各 address を試すこと（IPv6 のみ・IPv4 のみの listener）、authority 保持、DNS alias 非許可、proxy 失敗時 fallback なしを確認します。proxy resolver の一貫性、検証済み loopback literal/exact-localhost endpoint だけの bypass（remote `wss` literal は proxy 経由）、検証済み local endpoint から派生した health probe のみ bypass、cookie/既定 credential/redirect 無効化、proxy への bearer 転送なしも検証します。 |
| Save と鮮度 | Save は metadata のみを確認し、token を読まないこと。選択状態の永続化、Save、rename、enable/disable、delete、local への明示切り替えが snapshot validation と RPC dispatch と直列化されること。未保存編集、fingerprint 変更、stale generation、profile 削除後の reconnect が token 読み込み・socket 操作より前に拒否されること。明示 apply/local switch で復旧すること。token rotation は明示 reconnect で読み込まれること。 |
| Token と redaction | local path と最終 link 検証、file 不在/読取不可/directory、16 KiB 境界、BOM 有無の strict UTF-8、ASCII token grammar、長さ、空白・制御文字拒否、handshake のみの header を確認します。同値 concurrent lease、callback queue、exception text、共有 `diagnostics.log` に追記する両 writer と追記前 redaction、すべての既存 output sink、cleanup 順序、DTO/settings/UI/log に token がないことを検証します。initialize 失敗+close callback+secret echo を試し、gate release 後に deadlock せず出力が秘匿されることも証明します。 |
| TLS と認証 | 信頼済み TLS、hostname/chain 拒否、認証拒否、cross-origin redirect 拒否を確認します。一時証明書によって machine trust を変えません。 |
| Health と RPC | 並行 probe、5 秒合計上限、health 200 かつ upgrade/initialize 失敗、health failure と正常 RPC、redirect、timeout、auth/origin/body なし、pathful route の authority-root/unverified 表示、inactive profile の `Not connected` を検証します。 |
| Idle watchdog | transport がすべての有効 parsed response/notification/server request を報告すること、30 秒 idle、30 秒 `KeepAliveInterval` と unsolicited PONG を生存証明に使わないこと、10 秒 probe、最初の timeout 中に inbound sequence が進んだ場合は2回目を抑止して idle wait を再開すること、無応答 probe 2回だけで捕捉 socket を閉じること、遅延 generation 無視、`SignedOut` でも接続維持、自動 reconnect/変更操作なしを確認します。 |
| Retry 許可リスト | 許可 method 全件、account/read と skills/list の false/省略/不正/true、`-32001` 限定、4 回上限、jitter 境界、共通 monotonic deadline、cancel/close/generation 退役、すべての変更・未知 method が一度だけ送信されることを確認します。 |
| 接続寿命と所有権 | 起動 45 秒上限と各段階上限、socket 作成後の cancel/failure、initialize/account-read の区別、`Unavailable` account state でも RPC Ready を維持すること、transport close/caller cancellation/全体 deadline の cleanup、有効 generation 1 つ、保留 client/server 要求完了を確認します。remote reconnect/dispose 後も外部 server が動作し、local restart は所有 process だけを置換し、複数 Worker が socket/lease を共有しないことを検証します。 |
| 契約と UI | 次の contract version を一括適用し不一致を拒否すること、status DTO serialization、型付き error reason、local/remote action binding と通知、表示文字列の秘匿、health/RPC 独立表示を確認します。 |
| 実表示 | Experimental Instance の screenshot で local/remote、認証/RPC failure、health 結果、Dark/Light/High Contrast、狭幅、keyboard/focus、health check の無効/実行中/完了を確認します。 |
| Release | 固定 CLI 0.159.1、警告ゼロ Release solution build、Core/UI tests、schema cache と method surface checks、VSIX manifest/assembly/embedded-XAML 確認、`git diff --check`。 |

テストは loopback listener と一時証明書を所有します。ユーザーの proxy 設定、file ACL、user profile、通常の Visual Studio instance、system certificate trust を変更しません。既存の固定 package/build workflow を使い、新しい runtime dependency は追加しません。

## 参考

- Repository source: `src/Codex.AppServer.Protocol/TransportPolicies.cs`（`ReadOnlyRetryPolicy`、`ReadOnlyRequestAllowlist`、`SendReadOnlyRequestAsync`、`WebSocketTransportSecurityPolicy` を含む）、`src/Codex.VisualStudio.Worker/WorkerRpcService.cs`、`ISecretRedactor`、既存 diagnostics writer。
- 固定 upstream source: health/ready route は `codex-rs/app-server-transport/src/transport/websocket.rs`、force-reload の動作は `codex-rs/app-server/src/request_processors/catalog_processor.rs` の `skills_list_response`、schema `codex-rs/app-server-protocol/schema/json/v2/SkillsListParams.json`、test `codex-rs/app-server/tests/suite/v2/skills_list.rs`。
- Microsoft Learn: .NET `ClientWebSocket.ConnectAsync` の `HttpMessageInvoker` overload、`HttpClient.DefaultProxy`、`SocketsHttpHandler.ConnectCallback`、WebSocket keep-alive と unsolicited PONG の動作。
0
0
