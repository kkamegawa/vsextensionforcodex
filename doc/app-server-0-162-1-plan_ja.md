# Codex App Server 0.162.1 対応と MXC バックエンド選択

[English](app-server-0-162-1-plan.md)

## 概要

拡張機能は Codex CLI 0.162.1 の App Server 契約を対象とし、0.159.1 を回帰比較の基準として維持します。リモート App Server が 0.159.1 のまま動作している場合があるためです。また、ローカル App Server の Windows sandbox バックエンドとして MXC を明示的に選択できます。2026-10-12 承認。

## 追跡

| 種別 | Issue |
|---|---|
| 親 | [Issue #198](https://github.com/kkamegawa/vsextensionforcodex/issues/198) |
| 契約基準 | [Issue #199](https://github.com/kkamegawa/vsextensionforcodex/issues/199) |
| MXC バックエンド設定 | [Issue #200](https://github.com/kkamegawa/vsextensionforcodex/issues/200) |
| goal origin と MCP OAuth loginId | [Issue #201](https://github.com/kkamegawa/vsextensionforcodex/issues/201) |
| 受信側ペイロード変更 | [Issue #202](https://github.com/kkamegawa/vsextensionforcodex/issues/202) |
| 統合検証と文書 | [Issue #203](https://github.com/kkamegawa/vsextensionforcodex/issues/203) |

関連する完了済み作業: [Issue #149](https://github.com/kkamegawa/vsextensionforcodex/issues/149)、[Issue #156](https://github.com/kkamegawa/vsextensionforcodex/issues/156)。

## 契約

- `app-server-contract.json` は `targetVersion` を 0.162.1、`regressionVersion` を 0.159.1 に固定し、両リリースの公式 Windows x64 asset の SHA-256 を記録します。
- 0.159.1 から 0.162.1 への schema 差分は、stable が追加 3／削除 0／変更 31 ファイル、experimental が追加 7／削除 0／変更 35 ファイルです。使用中の RPC と server request の削除はありません。
- `config/read` と `configRequirements/read` を使用 client request に含めます。どちらも 0.159.1 と 0.162.1 に存在します。
- 拡張が使用しない 0.162.1 の新規要素（`thread/attachmentOwner/list`、`thread/prediction/*`、`account/bedrock/checkGovCloudRequirements`、raw response の `additional_tools`、`namespaceTools` の削除）は差分としてのみ記録します。

## 設計

### goal の origin

拡張が送るすべての goal 変更、すなわち作成、編集、一時停止、再開、解除、Goal Stop について、`thread/goal/set` と `thread/goal/clear` に `origin: "user"` を付けます。0.162.1 は origin がない場合にユーザー承認とみなしません。0.159.1 はこのフィールドを無視します。拡張は `automatic` を送りません。`thread/goal/updated` は origin を持たないため、読み取りません。

### MCP OAuth の完了

Worker は `mcpServer/oauth/login` が返す `loginId` を保持します。`mcpServer/oauthLogin/completed` は `loginId` が完全一致した場合だけ試行を完了させます。ログイン応答より先に届いた完了通知は、応答の `loginId` が判明するまで保持し、その後に適用または破棄します。異なる `loginId` の完了通知は古いものとして扱い、何も変更しません。不正な `loginId`（長さ制限内で制御文字を含まない文字列以外）は無視します。サーバーが `loginId` を省略する場合（0.159.1）は、サーバー名による照合にフォールバックします。

### メッセージの phase

`partial_answer`、`commentary`、`final_answer` は、それぞれ独自のテキストを持つ別々の `agentMessage` item です。拡張は、ライブ表示と履歴復元のどちらでも、すべての agent message item を順番どおりに 1 回ずつ表示し、turn の終了は `turn/completed` だけで判断します。`phase` から完了や承認を導かないため、Worker 契約に phase フィールドは追加しません。

### skill のパス

0.162.1 は skill の `path` を `LegacyAppPathString` として定義しており、ホストの絶対パスとは限りません。skill のパスはカタログの不透明な識別子として扱います。skill を指定する turn 入力にはそのまま送り返し、序数比較で照合し、拡張がファイルとして開くことはありません。空、長すぎる値（1024 文字超）、制御文字を含む値は引き続き除外します。skill の読み込みエラーのパスにも同じ規則を適用します。

### その他の受信専用の変更

- `reviewTarget` を含む未知の `CodexErrorInfo` の文字列と object は、失敗せずに読み取り、再実行や承認の根拠にはしません。
- `Turn.rootTurnId`、`turn/start.parentTurnId` と `rootTurnId`、sub-agent の `model`／`reasoningEffort` は読み取り互換です。拡張には代理・委譲の経路がないため、系譜は送信しません。
- `supportsIndependentSpeedModes` は、管理要件による速度制限に関する `configRequirements/read` の項目です。拡張は管理要件による速度制限を適用しません。`/fast` の可否はモデルの service tier で判断し、要件はサーバーが強制します。

### Windows sandbox バックエンド（MXC）

| 設定 | 起動 override | Codex の動作 |
|---|---|---|
| Codex 設定に従う（既定） | なし | Codex 設定と管理ポリシーの `windows.sandbox` と features を使用します。 |
| MXC 優先 | `-c features.prefer_mxc=true` | 管理ポリシーが MXC を許可し、`allowLocalBinding=false` を設定していない場合だけ MXC を使用します。それ以外は設定済みのバックエンドを維持します。 |
| MXC 必須 | `-c windows.sandbox=mxc` | 常に MXC を使用します。管理ポリシーが MXC を許可しない場合、Codex は設定の読み込みを拒否し、App Server は起動しません。 |

- 設定は安定した ID として拡張設定に保存し、次回のローカル App Server 起動時に反映します。リモートプロファイルは変更せず、Worker は override を渡しません。
- override の値は固定の許可リストです。値はクォートなしで TOML 文字列として解釈されるため、直接起動と `cmd.exe` 経由の起動のどちらでもクォートは不要です。
- **Check sandbox status** は、`windowsSandbox/readiness`、`config/read`（実効値の `windows.sandbox` と `features.prefer_mxc`）、`configRequirements/read`（`allowedWindowsSandboxImplementations` と `network.allowLocalBinding`）を読み取ります。読み取りの失敗や未対応は「不明」と表示し、推測しません。診断では、選択がポリシーと矛盾する場合にその旨を示し、readiness だけではどのバックエンドで各コマンドが実行されるかを証明できないことを明記します。
- 「MXC 必須」で初期化前に起動が失敗した場合、接続エラーで設定名を示し、「Codex 設定に従う」を選んで再接続するよう案内します。このため、ローカル起動に失敗した後も sandbox パネルを表示し続けます。
- 拡張は管理ポリシーを緩めず、失敗したコマンドを別のバックエンドで再実行しません。`windowsSandbox/setupStart` は elevated／unelevated の legacy setup として残します。MXC には setup は不要です。
- パネルには次の順に表示します。バックエンドの選択とその説明、現在のローカル App Server の起動時のバックエンドと選択が異なる場合の反映待ち通知、**Check sandbox status** による状態診断、legacy setup の操作。

Worker 契約のバージョンは 24 です。

## 実装フェーズ

1. 契約基準（#199）: 検証済み CLI の取得、schema キャッシュ、manifest、CI と Release workflow、ビルド時の schema 生成。
2. MXC（#200）: 契約と設定、起動 override、診断、sandbox パネル、テスト。
3. goal origin と OAuth（#201）: すべてのユーザー経路での origin、`loginId` による照合、Fake App Server の形状。
4. 受信側（#202）: 不透明な skill パスと受信専用の検証。
5. 検証と文書（#203）: ビルド、テスト、両 CLI バージョンでの smoke、リリース検証、描画確認、実機受入、文書。

## 検証

- schema 比較、契約 surface、schema キャッシュのスクリプトが成功し、0.162.1 と 0.159.1 の smoke が成功し、`verify-release.ps1` が Debug と Release で成功します。
- Core と UI のテストで、origin のすべての経路、`loginId` のすべての規則、ライブ表示と履歴での途中回答、未知の `CodexErrorInfo`、相対パスとレガシーの skill パス、直接起動と `cmd.exe` 経由の MXC 起動引数、ポリシーが判明している場合と不明な場合の readiness 診断、sandbox パネルの ViewModel を確認します。
- 本番テンプレートに実際の ViewModel をバインドしたオフスクリーン描画で、Light、Dark、High Contrast を 380 と 700 ピクセルで確認します。確認対象はレイアウトと情報構造だけです。
- Windows 実機での MXC 受入と、Visual Studio Experimental Instance での受入（テーマ、狭い幅、キーボード、スクリーンリーダー）は別途記録します。schema 検証だけではこの作業の完了としません。
