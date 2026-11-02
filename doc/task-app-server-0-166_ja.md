# Codex App Server 0.166.0 作業記録

追跡 Issue: [#241](https://github.com/kkamegawa/vsextensionforcodex/issues/241)。詳細な実装・検証記録: [英語](implementation-app-server-0-166.md) · [日本語](implementation-app-server-0-166_ja.md)。英語版の作業記録: [Task record](task-app-server-0-166.md)。

## 完了

- [x] Contract manifest を schema v4 に更新し、target 0.166.0、回帰確認用 0.165.0、0.164.0、0.163.0、0.162.1、0.161.0、Windows 完全パッケージの asset 名とハッシュを記録。
- [x] 6 バージョンすべてで完全パッケージを確認。インストール処理は `codex-package.json` の entrypoint を読み、パッケージと実行ファイルのハッシュを検証。
- [x] 6 バージョンすべての stable / experimental schema を生成し、各回帰バージョンと 0.166.0 を比較。method surface を検証し、未採用差分を `knownDifferences` に記録。
- [x] feature tier 0.165 / 0.166 を追加し、各機能の導入バージョンとの境界比較に変更。Worker contract を v27 に上げ、`ReviewDelivery` を `ReviewPlacement` に置き換え、`detached` を廃止。
- [x] item の timestamp を entry の値で補完。provider の構造化エラー、capacity policy 通知と履歴、サイズ制限と秘密情報除去を行う未知エラー、resume 拒否時の read-only 履歴を実装。
- [x] 0.166.0 CLI のアイドルアンロードを検証。約 60 秒後の `thread/closed` と同じ thread の再開を確認。
- [x] contract / 互換性テスト、Debug / Release の Core・UI テスト、警告・エラー 0 件の Debug / Release ビルド、Release VSIX 検査に成功。
- [x] 0.166.0、0.165.0、および古い回帰対象バージョンで実 CLI の `initialize` に成功。

## 保留中のメンテナー受け入れ確認

- [ ] Visual Studio Experimental Instance で変更後の画面を表示してスクリーンショットを確認する。Computer Use のアプリ一覧取得が許可された再試行でもタイムアウトし、このセッションではスクリーンショットを得られなかった。自動 UI テストは成功したが、実画面の確認の代わりにはならない。

証跡: `D:\temp\issue241-verify-2\`。

## Stage 2: 安定機能

追跡 Issue: [#242](https://github.com/kkamegawa/vsextensionforcodex/issues/242)。「未確認」の項目が完了するまで、この Stage は完了扱いにしません。

### 実装済み(自動テスト成功)

- [x] `/review --new-thread` を、`thread/start` と `inline` 配信の `review/start` からなる 1 つの Worker 操作として追加。
- [x] `thread/list.cwd` を使う履歴スコープ「All」「Current working folder」「Same repository worktrees」を追加。
- [x] 質問文と選択肢の説明を安全な Markdown 経路で表示し、送信値は `OptionId` のまま維持。
- [x] 読み取り専用エディタータブに固定スナップショットを開く Open conversation text を追加。
- [x] 会話の項目に時刻を表示。
- [x] 0.166 以降で `threadId` 付きの `account/usage/read` を読み、実額 USD、推定 USD、推定クレジットを区別して表示。
- [x] ローカルの 0.166 接続で `codex doctor --network-only` を実行し、リモート接続では手順を表示する `/doctor` を追加。
- [x] 全ページの履歴を検索する Alt+Shift+F の会話内検索を追加。結果を揃えてから表示し、キャンセル、キーボード操作可能な結果一覧、会話切替時の検索状態リセットに対応。
- [x] 接続復旧・bridge 切替中のターン送信をガードする。Worker RPC が利用できない要求は null 参照にせず、未送信として扱う。
- [x] 通常接続と復旧でアカウント・モデル・権限・使用量の初期化を共有する。修正前のモデル一覧取得漏れ（0 回）を再現し、復旧と新規会話後の一覧再取得・選択保持を確認。読み込んだ拡張機能と Worker のビルド識別子・DLL の場所をログへ記録する。
- [x] `thread/start` と `turn/start` 中の標準エラーによる所有世代失効を再現。修正前は両方の回帰ケースで `StaleGeneration` が発生した。任意の MCP 通信エラーや WebSocket フォールバックの診断情報では接続復旧を開始せず、秘密情報を除去した標準エラーを診断ログに残す。
- [x] 両方の復旧修正を含む最新の Release 検証: Core テスト 544 件合格・5 件スキップ、UI テスト 596 件合格・1 件スキップ、ソリューションビルドは警告・エラー 0 件、VSIX 内容検査に合格。

### 実 CLI の確認

- [x] 0.161.0: 新しい空の会話で `review/start` が動作し、レビューは同じ thread で進むことを確認。
- [x] 0.161.0: 履歴一覧では name が空で、レビュー指示文がプレビューとして表示されることを確認。

- [x] 0.166.0: 生成した schema が `threadUsage.nativeUsageUsdMicros`(nullable で、値なしは不明を意味する)、`account/usage/read.threadId`、`thread/start`(`permissions` と `activePermissionProfile` は Experimental のみ)、`inline` のみの delivery について実装と一致することを確認。
- [x] 0.166.0: 新しい空の会話で 2 回のレビューターンが同じ thread で完了し、履歴行は 0.161.0 と同じであることを確認。テスト用の会話は削除済み。
- [x] 0.166.0: `codex doctor --network-only --no-color --ascii` がフラグを受理し、TTY なしでプレーンテキストを出力して終了コード 0 で終了することを確認。
- [x] 0.161.0 と 0.166.0 の両方の面で `verify-contract-surface.ps1` が成功。
- [x] 0.165.0、0.164.0、0.163.0、0.162.1 の stable / experimental 両面で `verify-contract-surface.ps1` が成功。固定されたパッケージのハッシュを検証済み。スキーマ生成は一時的な PATH エイリアスへのアクセス警告が出たものの完了。

### 観測不能

- `review_model` の優先順位: `thread/start.config` で設定しても、プロトコルが報告する内容は変わらなかった。レビューターンが使ったモデルを示す通知や `thread/read` の項目がないため、優先順位は不明のまま。拡張機能はこの動作に依存しない。

### 未確認

- [ ] Visual Studio で会話検索を確認する。Alt+Shift+F 後のフォーカス、キーボード操作、長い履歴、テーマのコントラスト、狭い幅での結果ペインを対象とする。自動テストと VSIX 検査は実画面確認の代わりにならない。
- [ ] Visual Studio 上の拡張機能から `/doctor` を実行する。
- [ ] Experimental Instance で Light、Dark、High Contrast、日本語、狭い幅、キーボード、Narrator のスクリーンショットを取得して判定する。会話テキストタブの選択、コピー、Ctrl+F、F3、閉じたときの削除も対象とする。このセッションには最新の Experimental 配置がなく、Visual Studio の操作権限もクリックのみだった。
