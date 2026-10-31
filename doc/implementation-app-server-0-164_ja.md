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
  - compaction が完了しなかったときは「Compaction failed.」と表示します。要求の送信前に追跡を始めるため、要求の応答待ちの間に通知された失敗も報告します。
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

## Experimental Instance での受け入れ確認（2026-10-25〜2026-10-26）

実装のセッションでは Visual Studio の操作権限が二度とも許可されず、スクリーンショットも取得していません。そのため、受け入れ確認はオーナーが別のセッションで実施しました。そのセッションは、Visual Studio Insiders 18.12 の Experimental Instance で Release 版の拡張機能を Computer Use で操作し、実際の CLI に対して確認しました。手順ごとにスクリーンショットと UI Automation のテキスト出力を残しており、受け入れ報告書とともにリポジトリ外（`<acceptance-evidence-folder>`）に保存しています。以下の結果は、その報告書に基づきます。

**合格した項目（スクリーンショットあり）**
- 接続
  - ローカルの 0.161.0 への接続
  - 認証付き WebSocket による 0.164.0 への接続
- モデルとプロンプト
  - GPT-6 luna／推論 high／速度 standard の指定
  - 実際のプロンプトによる WinForms ソリューションの生成とビルド
- プリセット
  - 保存と、実際の CLI からの読み戻し
  - 呼び出しても送信しないこと
  - 置き換えの確認を伴う編集
- 表示とアクセシビリティ
  - Dark、Light、High Contrast での表示（日本語表示とプリセット パネルを含む）
  - Tab キーによる移動
  - Narrator の読み上げ
- 診断：リモートでのログなしの診断 ZIP の保存。ZIP には `report.json` と `manifest.json` だけが含まれ、サーバー上のパスが表示されました。ローカル ファイルとして自動で開かれることはありませんでした。

**見つかった不具合（確認の後に修正済み）**
- **プリセットの削除が `missing field 'value'` で失敗した**：JSON-RPC の接続は、通常のオブジェクトの null のプロパティを送らないため、明示的な null が `config/batchWrite` に届きませんでした。
  - 編集内容を `JsonObject` で組み立てるよう変更し、null が残るようにしました。
  - 実際の 0.164.0 CLI に一時的な `CODEX_HOME` で確認しました。`value` のない編集では同じエラーが再現し、`"value": null` を付けた編集ではスロットを削除できました。
- **狭いペイン（論理ピクセルで約480）で、状態表示が「Ready / Sign…」に切り詰められた**：右側に寄せたツール バーが先に幅を取っていたためです。
  - ヘッダーで状態表示が幅いっぱいの専用の行を使い、ツール バーはその下に右寄せで置いて折り返すようにしました。
  - 本番のヘッダー XAML を実際のビュー モデルと組み合わせ、WPF 単体で描画して確認しました。変更前のレイアウトでは 300 と 360 ピクセルで不具合が再現し、変更後は 300・360・480・700 ピクセルのすべてで合格しました。描画結果は `artifacts/0164-header/before` と `after` にあります。
- **継承された `CODEX_INTERNAL_ORIGINATOR_OVERRIDE=Codex Desktop` により、対応している 0.164.0 のサーバーが非対応に見えた**：originator に空白が含まれ、`userAgent` を空白で区切った先頭のトークンに版が含まれていなかったためです。
  - 最初の `/` から次の空白までを版として読むよう変更し、設計書にもこの規則を記載しました。
  - 実際の CLI は、この上書きがある状態で `Codex Desktop/0.164.0 (Windows 10.0.26200; x86_64) ...` を返しました。

## レビュー後の修正：compaction の会話ごとの表示（2026-10-26）

追跡は #230、修正は #236 に入れ、#237 にも取り込みました。compaction の開始・完了・失敗・遅れて届く RPC 応答は、その操作の対象となる会話が選択中の場合にだけ表示します。別の会話が選択中でも追跡は終了し、完了時の使用量更新は行います。先に届いた結果表示を、遅い開始応答が上書きすることも防ぎます。

別会話が選択中の失敗・完了、遅い成功応答、会話を切り替えた後の RPC 例外の4件の回帰テストを追加しました。失敗・完了・RPC 例外の3件は、修正前に不合格となることを確認しました。修正後の compaction 関連テストは8件合格、スキップなしです。

スタックを統合した状態で、Debug と Release の UI テストはそれぞれ569件合格、シンボリックリンクの権限によるスキップ1件、失敗0件でした。Release の全体ビルドは警告0・エラー0で、`Test-VsixContents.ps1 -Configuration Release` も通りました。今回は UI のみの修正のため、Core テストは再実行していません。

実際のビュー モデルの `Items` を WPF 単体の会話欄に描画し、別会話の完了・失敗のどちらでも、選択中の会話の本文が残り、他の会話の結果が追加されないことを画像で確認しました。ローカルの無視対象の証跡は `artifacts/compaction-isolation/bin/Debug/net8.0-windows10.0.22621.0/` の `visual-result.json`、`screenshots/completion-thread-B.png`、`screenshots/failure-thread-B.png` です。これは標準 WPF での表示確認であり、Experimental Instance や Visual Studio のテーマ確認ではありません。2026-10-31 の Experimental Instance での追加確認を次に記録します。

## 2026-10-31 Experimental Instance での追加確認

オーナーが Windows 11（`10.0.29695.0`、PowerShell 7.6.5、.NET SDK 10.0.403）で、公式 Codex CLI 0.164.0（`F:/temp/issue226-20261031/cli/codex.exe`、SHA-256 `6f267830b029ee25d9339864aa867cd45de9f8fd4c5ea007d2e63c13a0c66d36`）を使ってローカルの受け入れ確認を再実施しました。Visual Studio Enterprise 18.11.12328.437 の Experimental Instance `18.0_3b4a7eadExp` は、Experimental Instance 内に配置した拡張機能 0.2.1.0 をロードしました。ロード済み DLL の SHA-256 は `DD4BD666EF5C0979E2445591C04AC20AA2CB6AAA9909121BD70098BEEA82C934` です。使い捨ての Windows Forms ソリューション `F:/temp/issue226-20261031/WinFormsAcceptance/Issue226Acceptance.slnx` は Release で警告0・エラー0でビルドしました。

**合格:**
- ローカルの `/diagnostics`：保存ダイアログと確認にローカルの保存先と内容が表示されました。ログは既定で無効です。保存ダイアログをキャンセルした場合、ファイルは作られませんでした。保存した ZIP（SHA-256 `586d237af6935df4f1c210db6b39295bc5cbc2a602ee7d1badec9a56a323b4da`）には `report.json` と `manifest.json` のみが含まれ、rollout やログはありませんでした。既存の保存先は拒否され、ZIP は変化しませんでした。
- プリセット：テスト用プリセットを保存し、CLI 設定を読み戻してラベルと入力を確認しました。確認ダイアログを経て削除し、CLI 設定でスロットが存在しないことを確認しました。一時的に使った CLI 設定は元のバックアップと同じ SHA-256 `6ea2e8c2610200d3b3963391c7ef6cf1cf7cab2eeb42ab584eba8fb5f31fd420` に復元しました。
- Dark テーマ：論理ピクセル幅694と479のヘッダーを撮影し、状態表示が専用の行にあり、折り返したツールバーが重ならず読めることを確認しました。スクリーンショットと UI Automation の出力は `F:/temp/issue226-20261031/screenshots/` にあります。

**メンテナーによる受け入れ（2026-10-31）：**
- 300論理ピクセル幅でのクリッピングを、外側 Grid の最小幅を360から300に下げて修正しました。既存の狭幅回帰テストも更新しました。修正後の Release 全体ビルドは警告0・エラー0、Core は516件合格・シンボリックリンク権限によるスキップ5件、UI は569件合格・dangling solution symlink によるスキップ1件でした。Release VSIX 検査も合格しました。
- 修正後の Experimental Instance で、CLI 0.164.0 の Light と CLI 0.161.0 の Dark における約300px幅のヘッダーを撮影しました。メンテナーがキーボード操作を手動確認し、狭幅での確認に基づいて他の幅を受け入れ、High Contrast も受け入れました。これらについて追加の修正後スクリーンショットは求められませんでした。
- 2026-10-25/26 の Experimental Instance 確認では、Light・Dark・High Contrast、日本語、キーボード、Narrator の画面も撮影済みです。オーナーは2026-10-31に残る視覚的な受け入れ確認を完了としました。

2026-10-31 のスクリーンショットと機械可読な証跡は `F:/temp/issue226-20261031/` にあります。試験用 Windows Forms ソリューションは `F:/temp/issue226-20261031/WinFormsAcceptance/` にあります。

## 受け入れ完了（2026-10-31）

Issue #226 で追跡する受け入れ確認はすべて完了しました。

- オーナーが Experimental Instance でキーボード操作を手動確認しました。
- 狭い幅での確認をもとに残るペイン幅を受け入れ、High Contrast も受け入れました。これらについて修正後の追加スクリーンショットは求められませんでした。修正後のスクリーンショットは、CLI 0.164.0 の Light と CLI 0.161.0 の Dark で約300論理ピクセル幅を記録しています。以前のスクリーンショットには Light、Dark、High Contrast、日本語、キーボード操作、Narrator の読み上げが含まれます。
- 修正後の Debug 全体ビルドは警告0・エラー0でした。Debug Core は516件合格・シンボリックリンク権限によるスキップ5件・失敗0件、Debug UI は569件合格・dangling solution symlink によるスキップ1件・失敗0件でした。証跡スクリプトは、権限が必要な6件を実行できなかったため `blocked` と報告しています。Release の結果は上記に記録しています。
- アカウント変更通知の自動テストは合意により対象外です。所有者変更と古い応答の破棄の検証は #230 に記録しています。
