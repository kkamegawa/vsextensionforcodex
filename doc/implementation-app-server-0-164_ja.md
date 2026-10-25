# Codex App Server 0.164.0 実装記録

追跡: [#226](https://github.com/kkamegawa/vsextensionforcodex/issues/226) とサブ Issue [#227](https://github.com/kkamegawa/vsextensionforcodex/issues/227)〜[#231](https://github.com/kkamegawa/vsextensionforcodex/issues/231)。承認済みの設計と計画: [設計](app-server-0-164-design_ja.md)、[計画](app-server-0-164-plan_ja.md)、[ADR-019](adr/ADR-019-app-server-0-164_ja.md)。英語版: [English](implementation-app-server-0-164.md)。

## 提供内容

作業は stacked PR として提供し、下から順にマージします。順番は、設計 #232、契約 #233、バージョン判定と Worker #234、入力とプリセット #235、状態と診断 #236、この検証記録です。

- **契約（#227）**
  - 契約マニフェストはスキーマ バージョン 3 です。`minimumVersion` は 0.161.0、`targetVersion` は 0.164.0、回帰版は 0.163.0・0.162.1・0.161.0 です。
  - 版ごとにリリースのハッシュ、メソッドの一覧、既知の差分を記録しています。0.159.1 は、マニフェスト、CI、Fake App Server、セットアップ文書から削除しました。
  - `scripts/validate-contract-manifest.ps1` がマニフェストを検証し、CI のバージョン マトリクスを生成します。
- **バージョン判定と Worker（#228）**
  - Worker は `initialize` の `userAgent` の先頭トークンからサーバーの版を読み取り、Semantic Versioning 2.0 の優先順位で比較します。
  - 版が不明な場合と 0.161.0 未満の場合は、`initialized` の前に拒否します。失敗の種別は再試行しない `UnsupportedServerVersion` です。
  - 接続状態は機能段階（Baseline、Tier163、Tier164）を持ちます。
  - Worker 契約 v26 では、次を追加しました。
    - プリセット（`config/read` のレイヤーと `config/batchWrite`）
    - `feedback/save`
    - MCP の実行時の詳細
    - 生成画像の参照
    - 再開時に保存された設定
  - 0.159.1 向けの代替処理は削除しました。
- **入力とプリセット（#229）**
  - `/model [model] [--reasoning <level>] [--speed <tier>]` は、Codex CLI 0.164.0 と同じ規則で解析します。分割処理は shlex 1.3.0 の移植です。
  - 何かを適用する前にコマンド全体を検証します。`standard` は `default` として送ります。
  - `/permissions` の確認待ちの間は、スラッシュ コマンドのキューを止めます。
  - `/presets` と入力欄のボタンでプリセット パネルを開きます。CLI と同じ編集・削除の規則を適用し、変更の前に確認します。書き込みが競合したときは編集中の内容を残します。プリセットを選んでも送信はしません。
- **状態、MCP、診断、compaction（#230）**
  - 0.164 では、会話の再開時に、保存されたモデル・推論レベル・速度をまとめて反映します。推論レベルが null ならモデルの既定値、速度が null なら通常速度です。
  - 接続の所有者が変わったとき、新しいモデル一覧に前のモデルがあれば選択を維持します。
  - `/mcp` は、認証、実行状態、ツール検出の結果を分けて表示します。
  - `/diagnostics` は `feedback/save` で診断 ZIP を保存します。
  - compaction が完了しなかったときは「Compaction failed; the conversation history is unchanged.」と表示します。
  - 0.164 未満で会話の再開に失敗したときは、CLI 0.164.0 なら圧縮済みの履歴も再開できるという補足を添えます。

## 検証

2026-10-25 に、Windows 11（10.0.26200）x64、.NET SDK 10.0.402、PowerShell 7.6.7 の環境で検証しました。使った公式リリースの実行ファイルと、マニフェストに記録したハッシュとの一致は次のとおりです。

| 版 | SHA-256 |
| --- | --- |
| 0.164.0 | `6f267830b029ee25d9339864aa867cd45de9f8fd4c5ea007d2e63c13a0c66d36` |
| 0.163.0 | `2692c8c7bb2cf8b0aefe638741fc90236f4b824c59578ce84994f9de58200993` |
| 0.162.1 | `dd13bdb162a184fa04039488b234a8258ef9b76f84e926c505d5742b19249a6a` |
| 0.161.0 | `a0f89acca07a511734cac7fddee26b2106fab68918717bc90df16104a0760a30` |

| 確認項目 | 結果 |
| --- | --- |
| マニフェストの検証 | `validate-contract-manifest.ps1` は成功しました。最低版は 0.161.0、目標版は 0.164.0、回帰版は 0.163.0・0.162.1・0.161.0 です。 |
| スキーマの生成と、使用するメソッドの検査 | `generate-schemas.ps1` と `verify-contract-surface.ps1` は、4つの版すべての stable と experimental で成功しました。 |
| スキーマの比較 | `compare-schemas.ps1` は、各回帰版と 0.164.0 の比較を両方のサーフェスで行い、6件すべて成功しました。 |
| 実際の CLI の起動 | `smoke-app-server.ps1` は、0.164.0・0.163.0・0.162.1・0.161.0 で `initialize` を完了しました。 |
| 0.164.0 の MXC | `-c windows.allow_mxc=false` を付けた起動で `initialize` は完了しました。検証機のユーザー設定は `windows.sandbox = "mxc"` なので、`config/read` は CLI の競合エラーを返しました。内容は、`windows.allow_mxc = false` のときは `windows.sandbox = "mxc"` を使えない、というものです。これは 0.163 の設計にある「明示的な競合」の場合にあたります。MXC の設定のまま通常起動した状態で、次の行の実ターンを実行しました。 |
| 0.164.0 での実際の compaction と再開 | ChatGPT でサインインした状態で、実際のターンが完了しました。`thread/compact/start` は、完了した `contextCompaction` 項目と完了したターンで終わりました。サーバーのプロセスを止めて起動し直した後、compaction 済みの会話を `thread/resume` で再開できました。保存された `reasoningEffort` が返り、`serviceTier` は null でした。続けて実行したターンも完了しました。 |
| Core テスト | Debug と Release のどちらも、508件合格、5件スキップ、0件失敗。 |
| UI テスト | Debug と Release のどちらも、558件合格、1件スキップ、0件失敗。 |
| ソリューション全体のビルド | Debug と Release のどちらも、警告 0、エラー 0。 |
| Release VSIX の検査 | `Test-VsixContents.ps1` は成功しました。パッケージ内のハッシュと埋め込み XAML が一致し、Worker 契約 v26 を確認しました。 |

テスト結果の記録スクリプト（`Invoke-TestSuitesWithEvidence.ps1`）は、両構成とも状態を `blocked` と報告しました。失敗はありません。スキップした6件は、シンボリック リンクを必要とするテストです。この検証機では、開発者モードか管理者権限がないとシンボリック リンクを作れません。これらはスキップとして扱い、合格には数えていません。最終の実行より前の Release の実行で一度、Core テスト `FakeAppServerExercisesConcurrentInteractionsAndAuthenticationRecoveryOverStdio` が負荷によるタイムアウトで失敗しました。単体での再実行とスイート全体の再実行は成功しています。

## 未完了の受け入れ確認

- **Experimental Instance**：スクリーンショットは撮っていません。このセッションでは、UI 自動操作のための Visual Studio へのアクセスが許可されなかったためです。次の項目は、Visual Studio 上での UI の合格を主張しません。
  - プリセット パネル、`/model`、`/diagnostics`
  - サポートするテーマ、狭い幅、日本語表示、キーボード操作、スクリーン リーダーの出力
  - 拡張機能自身から実際の CLI への接続
- **アカウントの変更**：アカウント変更時の通知には自動テストがありません。テスト用の偽ブリッジでは、セッションの途中で接続の所有者を入れ替えられないためです。
- **実際の CLI での確認範囲**：実際の CLI での実行は、検証用スクリプトから JSON-RPC を直接送ったもので、拡張機能は経由していません。リモートのプリセット、リモートでの診断 ZIP の保存、実際の CLI を使ったプリセットの読み書きの往復は確認していません。
