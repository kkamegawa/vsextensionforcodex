# 質問・権限・MCP 対話の設計

[English](interaction-design.md)

状態: 承認済み最終仕様（2026-10-05）。契約基準は Worker v19 とし、merge 時点で次に利用可能な version（変更がなければ v20）を割り当てます。CLI／SDK／runtime の version は維持します。

## 目的と担当範囲

非同期質問、範囲付き権限選択、正確なコマンド承認、MCP elicitation と認証回復、ローカル Gateway OAuth を実装します。秘密を含む要求は UI に投影する前に拒否します。ネイティブ本人確認の成功経路は、上流が Windows とこの client に対応するまで延期します。添付 metadata の上限付き復旧は Issue #153、添付操作と表示は Issue #155 が担当します。

## 要求ライフサイクル

owner、接続世代、要求種別、元の JSON-RPC ID をキーとする型付き保留レジストリを使用します。MCP elicitation は turn ID がない場合があります。回答を検証してから完了権を原子的に取得します。回答、キャンセル、timeout、切断、サーバー解決、接続破棄の競合でも完了は一回だけです。resolved 済みまたは古い要求には応答しません。応答の送信を開始した後の配送不明は終端状態とし、再送しません。

### Worker と Remote UI の interface

- 権限と MCP elicitation は別々の observer／resolver とし、payload と応答 method の取り違えを防ぎます。resolver には捕捉済み owner／generation／request identity を渡し、古い要求や完了済み要求を拒否します。
- 質問 DTO は安全化した prompt／選択肢文字列と opaque な選択 ID を公開します。自由入力と「その他」は明示的な回答 variant にします。Worker が元の wire 値を保持し、Submit された ID だけを応答値に対応付けます。
- コマンド承認 DTO はサーバーの `Choice` 全件を opaque な `ChoiceId`、label、関連する権限／ルール変更説明とともに公開します。resolver は当該要求に含まれる `ChoiceId` だけを受け付けます。
- Gateway OAuth 状態に `InteractionAuthStatus` を公開します。`ReadGatewayOAuth`、`LoginGatewayOAuth`、`CancelGatewayOAuth` と `OpenAuthorizationUrl(ActionId)` を提供します。`ActionId` は opaque、一回限り、owner／generation に結び付き、Worker 内で検証済み URL に対応付けます。UI command から生 URL を受け付けません。
- `StartMcpOAuthLogin` は明示的な MCP login を開始し、`DismissMcpOAuthLogin` は UI の待機／要求だけを閉じます。MCP OAuth 取消 RPC がないため、server-side 取消要求は送りません。
- resolver は要求に対する全回答の妥当性を検証してから完了権を原子的に取得します。Gateway OAuth の変更操作は local-only でゲートし、Remote は状態 read だけを許可します。

## 質問と承認

ターン継続中も使える独立カードを表示し、通常 composer を使える状態に保ちます。blocking／non-blocking 質問、自由入力、「その他」を扱います。既定値、focus、選択は表示状態とし、明示 Submit を必須にします。表示文字列を安全化し、選択 ID は Worker 内で保持して回答時に元の値へ対応付けます。secret 付き要求は UI 投影前に Worker で拒否し、理由を示します。秘密値を UI、会話、ログ、設定、診断、例外に含めません。

権限承認は要求されたネットワーク／ファイル権限の部分集合だけを返し、既定 scope は turn とします。session 継続は明示選択です。要求外の権限は拒否します。サーバーが提示するコマンド承認と関連権限の全選択肢を別の選択肢として表示し、ルール変更を通常の承認へまとめません。破壊的操作には既存の承認ポリシーを適用します。

## MCP elicitation と認証回復

正確な `mcpServer/elicitation/request` を dispatch します。string、number、integer、boolean、単一／複数選択の form field を扱い、required／型／長さ／範囲／形式／選択数を UI と Worker で検証します。未対応の拡張 schema は理由を示して拒否し、能力を宣言しません。URL は Worker で検証し、明示操作だけで開きます。ブラウザーを開いたことは認証成功の証拠ではありません。

期限切れ、失効、OAuth 完了失敗、`reauthenticationRequired` では再認証案内を表示し、古い elicitation を終了します。MCP に OAuth 取消 RPC がないため、UI 取消は UI 要求だけを取り消します。再認証後は新たな明示的ツール呼び出しを要求し、失敗した呼び出しを自動再送しません。

## Gateway OAuth

ローカル stdio では `explicitGatewayOauth` を宣言します。initialize 後かつ認証を要する RPC の前に、接続ごとに `account/gatewayOAuth/read` を成功させます。その後に login／cancel を有効にし、changed 通知を処理します。通知を有効な owner と接続世代に結び付けます。ブラウザー起動は明示操作です。login 待ちで他の RPC の処理を止めません。capability または read が使えない場合は認証 RPC を停止して理由を示し、自動ブラウザー認証へ移行しません。

Remote WebSocket では状態とサインイン案内だけを示します。`explicitGatewayOauth` を宣言せず、login／cancel も送信しません。認証操作は Remote サーバーが管理します。

## 認証ステータスの表示

Authentication 領域は、Gateway の状態概要と既存の回復操作を示すコンパクトな状態で開始します。Check status は MCP サーバーの詳細を明示的に展開して認証状態を更新します。Check status の隣に常時表示する MCP details トグルで詳細の展開状態を切り替えます。トグルは両方の状態で表示され、そのチェック状態が展開状態を表すため、折りたたみ時に操作中のコントロールが消えてキーボードフォーカスを失うことはありません。Check status は引き続き状態更新と詳細の展開に利用できます。

展開状態は、保持する Gateway／MCP の認証データとは独立した、シリアライズ対象の表示状態です。折りたたみはこの表示状態だけを変更し、サインアウト、認証操作のキャンセルや非表示化、保留中の操作識別子の破棄は行いません。通知や処理中だった照会の応答は、折りたたんだ詳細を開き直さずに保持データを更新します。owner の破棄ではコンパクトな状態へ戻し、既存の owner／接続世代の検証で古い結果を拒否します。Gateway の回復案内と操作はコンパクトな領域でも表示します。

追跡: [Issue #173](https://github.com/kkamegawa/vsextensionforcodex/issues/173)。

## ネイティブ本人確認

CLI 0.159.1 のネイティブ provider は macOS のみ対応し、この拡張 client は適格対象に含まれません。上流が Windows とこの client に対応するまでは capability を宣言・転送しません。要求は challenge 内容を UI に投影する前に拒否し、未対応理由を示します。challenge／proof／credential を UI、会話、ログ、設定、診断、例外に出しません。

## 検証

複数カード、明示 Submit、UI 投影前の秘密要求拒否、権限部分集合と scope、全コマンド選択肢、対応／未対応 form、ブラウザー結果、OAuth 回復、Gateway 起動順序と通知競合、Remote 読み取り専用動作、応答ライフサイクル競合を検証します。不確かな応答や失敗したツール呼び出しが自動再試行されないことを確認します。build と UI の受入証跡は Issue #154 と全体計画で追跡します。
