# 設計書の第16節（Issue #154）

参照先：[設計書](design.md) / [詳細な対話設計](interaction-design_ja.md)

## 16. 質問、権限、MCP 対話

Worker 契約は v20 とする。統合時までにブランチ上の契約版が変わった場合は、次に利用可能な版を割り当てる。CLI、SDK、ランタイムの版は固定する。

app-server の質問、権限、コマンド承認、MCP elicitation は、それぞれ独立した保留中の対話として扱う。Worker は上流 JSON-RPC ID と元の wire 値を保持し、Remote UI には owner／generation に結び付けた要求 ID と安全化済み表示情報だけを渡す。チャットには要求ごとの独立カードを表示し、ターンと通常 composer は独立して操作できる。カード群はプロトコル受信ポンプとは別であり、通知と要求は第1節の有界な wire 順序ディスパッチに従う。明示的な送信を必須とし、既定値、フォーカス、選択だけでは回答しない。不透明な選択 ID は応答時に Worker 内でのみ元のサーバー値へ解決する。

Worker は保存済み要求に照らして応答を検証してから、完了権を原子的に取得する。回答、取消、期限切れ、外部解決、切断、世代終了は競合しても応答は最大一回とする。解決済み・旧世代の要求へは応答せず、送達が不確実な応答は再送しない。turn ID がない MCP elicitation も要求 ID で管理する。

質問カードは blocking／non-blocking、自由入力、Other に対応する。秘密入力と未対応の本人確認要求は、機密 payload を Remote UI へ渡す前に拒否する。表示理由に challenge、proof、秘密値を含めない。ネイティブ本人確認の成功経路は、上流が Windows とこの拡張のクライアント識別子をサポートするまで延期する。

権限応答は要求されたネットワーク／ファイル権限の部分集合だけを返し、既定の適用範囲は turn とする。session への永続化には明示選択を求める。コマンド承認はサーバーが提示したすべての選択肢と関連する権限・規則変更を保持する。要求外の権限付与や、規則変更を通常の承認として見せることは禁止する。破壊的操作は既存の承認ポリシーを通す。

MCP form は文字列、数値、整数、真偽値、単一選択、複数選択を検証付きで扱う。未対応の拡張 schema は安全な理由を付けて拒否し、その能力を宣言しない。認証 URL は Worker で検証し、明示操作でのみ開く。ブラウザーを開いただけでは認証成功としない。MCP に OAuth 取消 RPC がないため UI の dismiss はローカルの待機だけを終了し、再認証後に失敗したツール呼び出しを再実行しない。

ローカル stdio では `explicitGatewayOauth` を宣言し、各接続で initialize 後、認証が必要な RPC の前に `account/gatewayOAuth/read` を成功させる。未対応または確認失敗時は認証 RPC を停止し、自動ブラウザー認証へ切り替えない。login、cancel、変更通知、ブラウザー操作を現行 owner と generation に結び付け、ログイン待ちは他の RPC を塞がない。Remote WebSocket は状態と案内のみを表示し、Gateway login／cancel の能力を宣言せず操作もしない。添付メタデータの復元は #153、添付操作と表示は #155 の担当とする。

実装・検証の追跡先は [Issue #154](https://github.com/kkamegawa/vsextensionforcodex/issues/154) と子 Issue [#165](https://github.com/kkamegawa/vsextensionforcodex/issues/165)、[#166](https://github.com/kkamegawa/vsextensionforcodex/issues/166)、[#167](https://github.com/kkamegawa/vsextensionforcodex/issues/167)、[#168](https://github.com/kkamegawa/vsextensionforcodex/issues/168)。
