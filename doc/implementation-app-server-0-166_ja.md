# Codex App Server 0.166.0 実装記録

追跡 Issue: [#241](https://github.com/kkamegawa/vsextensionforcodex/issues/241)。作業チェックリスト: [英語](task-app-server-0-166.md) · [日本語](task-app-server-0-166_ja.md)。英語版の実装記録: [Implementation record](implementation-app-server-0-166.md)。

## Contract と CLI パッケージ

`app-server-contract.json` を schema v4 にし、target を 0.166.0、回帰対象を 0.165.0、0.164.0、0.163.0、0.162.1、0.161.0 としました。各リリースには Windows 完全パッケージの asset 名と package / executable の SHA-256 を固定しています。インストーラーは完全パッケージをステージングディレクトリへ展開し、危険なパスとリンクを拒否します。`codex-package.json` の entrypoint を読み取って各ハッシュを検証してから、ファイルを配置します。固定された補助ファイル一覧には依存しません。manifest validator は成功しました。

6 バージョンすべての公式完全パッケージを取得・検証し、実 CLI の初期化 smoke test に成功しました。各バージョンの stable / experimental schema を生成し、0.166.0 との回帰比較 10 件、method surface 検証 12 件がすべて成功しました。ホストへ採用していないバージョン固有の schema パスは `knownDifferences` に記録しています。

## 互換性とセッション動作

feature tier 0.165 / 0.166 は、対象 CLI バージョンを各機能の導入バージョンと比較して選択します。Worker contract を v27 にし、`ReviewDelivery` を `ReviewPlacement` に置き換え、review 要求から `detached` を削除しました。

item の `startedAtMs` と `completedAtMs` は、値がない場合に entry の timestamp を使います。provider の JSON-RPC エラーは構造化 code を保ち、通知と履歴に含まれる `policyCapacityUnavailable` も扱います。未知の code はサイズを制限し、秘密情報を除去して通常のエラーとして表示します。provider が `thread/resume` を拒否した場合も `thread/read` で読み取り専用の履歴を開けます。active thread では interrupt と goal の pause / clear を引き続き実行できます。

固定した 0.166.0 CLI で thread subscription を解除してアイドル状態にすると、約 60.3 秒後に `thread/closed` が届き、smoke test で同じ thread ID の再開に成功しました。テスト用に作成したローカル thread は終了後に削除しました。

## 検証結果

- Debug / Release のソリューションビルドが警告 0 件、エラー 0 件で完了。
- Debug / Release の Core / UI テスト証跡が、再試行不要ですべて成功。
- Release VSIX 検査に成功。埋め込み Worker contract v27 も確認。
- Contract manifest validator が成功。6 バージョンすべての stable / experimental schema 生成、回帰比較、method surface 検証が成功。
- 6 バージョンすべての完全パッケージ導入と実 CLI `initialize` が成功。0.166.0 のアイドルアンロードと再開も成功。
- `git diff --check` が成功。

証跡は `D:\temp\issue241-verify-2\` にあります。6 バージョンの固定 CLI 導入ログ、initialize ログ、schema 出力、Debug / Release テスト結果、VSIX 検査レポートを含みます。最初の統合 release-validation は、最終 manifest と protocol project の version 修正前に実行したため、最終証跡には含めていません。関連する各検証は修正後に個別に再実行しています。

## Visual acceptance の制約

変更対象に Visual Studio の UI 状態が含まれます。このセッションでは Computer Use のアプリ一覧取得が2回タイムアウトし、スクリーンショットを取得できませんでした。その後、メンテナーが [Issue #242](https://github.com/kkamegawa/vsextensionforcodex/issues/242) と [Issue #244](https://github.com/kkamegawa/vsextensionforcodex/issues/244) に記録された Visual Studio Experimental Instance の受け入れを確認しています。この更新で新しいスクリーンショットを撮影したとは記録しません。ADR-021で受け入れた細かな実行状態の組み合わせは追加の手動受け入れ条件ではありません。証拠と受け入れ済み・未テスト項目は作業記録に記載します。

## Stage 2: 安定機能

追跡 Issue: [#242](https://github.com/kkamegawa/vsextensionforcodex/issues/242)。設計どおり Worker contract は v27 のままです。メンテナーの受け入れは #242 に記録済みです。作業記録では検証済みの証拠と、未テストのまま受け入れた細かな実行状態の組み合わせを区別します。

- **レビュー:** `ReviewPlacement.NewThread` は `CodexSessionService.Review.cs` で処理します。設定は有効な会話の実効状態から取得し、対応付けられない値があれば `thread/start` の前に止めます。名前付きプロファイルの項目は Experimental 面にしかないため、Stable 面では `sandbox` を送ります。応答は元の会話と比較し、異なる権限が報告された場合は会話を残してレビューを開始しません。`ActiveThreadId` は `review/start` の成功後にのみ移動します。結果は Started、NotCreated、CreatedNotStarted、Unknown に分かれ、自動再送はしません。
- **履歴スコープ:** `GitWorktreeScope` は読み取り専用の Git コマンドをタイムアウト付きで実行し、失敗時はスコープを表示しません。フォルダーの組はスコープ選択時に固定し、スコープを変えるとカーソルを破棄し、遅れた応答は世代で破棄します。絞り込み中の一覧を根拠に、キュー済みコマンドを削除扱いで取り消すことはありません。開いている会話の行も残します。
- **質問:** 質問文と選択肢の説明は `ToSafeTextAndBlocks` を使います。`AppendUserInputResultItem` は送信した回答を固定の確認文としてのみ表示し、合成回答は既存の `ToSafeText` 経路を通るユーザーメッセージとして送るため、回答専用の描画処理は不要です。
- **会話テキスト:** 履歴全体を先にメモリへ読み込むため、失敗やキャンセルでは何も開きません。`ConversationTextStore` は所有者ごとに分けた一時フォルダーへ、生成したファイル名で書き込みます。タブを閉じるとファイルを削除し、起動時に 24 時間より古い未使用ファイルを削除します。
- **利用額:** 値は `long?` のマイクロ単位で保持します。上限情報は 60 秒キャッシュしますが、別の会話に移ると金額は必ず読み直します。
- **診断:** `BoundedProcessRunner` は `/doctor` をシェルなしで起動し、出力に上限を設け、60 秒のタイムアウトでプロセスツリーを終了します。`--no-color --ascii` で端末制御コードを除き、出力は `ISecretRedactor` を通します。

実 CLI の確認は 0.161.0 と 0.166.0 を使い、使い捨てリポジトリで実施しました。テスト用の会話は削除済みです。0.161.0 のターンは 150 秒で打ち切ったため、結果の範囲は開始と履歴行の表示のみです。0.166.0 の 2 ターンは完了しました。0.166.0 CLI は `scripts/install-codex.ps1` で導入し、固定ハッシュを検証しました。App Server の応答にはレビューのモデルが含まれません。別の隔離した 0.166.0 の試験では、ループバックの Responses リクエストを確認しました。`review_model` を指定するとそのモデルが、省略すると会話のモデルが送信されました。Release VSIX 検査の結果は `doc/task.md` に記録しています。

### Stage 2 のレビュー対応

Worker は承認状態とともにサーバーが返した sandbox オブジェクトを保持する。別会話レビューは、作成先の有効な承認ポリシー、承認担当、権限プロファイル、sandbox 設定が元の会話と一致し、元の会話が引き続き選択されている場合に開始する。一致しない場合は作成先を履歴に残し、元の会話の選択を維持する。

会話を選択すると直前の会話の使用量を消去する。使用量ポップアップが開いていれば選択先の使用量を取得する。履歴ポップアップはスコープの選択中も開いたままで、明示的に閉じられる。スナップショットの自動削除では、リダイレクトされた保存先ルートとオーナーフォルダーを対象から外す。
