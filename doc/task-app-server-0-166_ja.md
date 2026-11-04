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
- [x] ボタンから開き、全ページの履歴を検索する会話内検索を追加。結果を揃えてから表示し、キャンセル、キーボード操作可能な結果一覧、会話切替時の検索状態リセットに対応。
- [x] 会話、Markdown ブロック、生成された Plan 本文、コマンド出力を読み取り専用の選択可能なテキストに変更。文字選択はテキストブロック単位とし、会話全体の選択には Open conversation text のエディターを利用。
- [x] 履歴から Reasoning の要約と Command のコマンド・出力を復元。秘密情報のマスキングと 64 KiB 上限を維持し、区切りの改行も切り詰め判定のバイト数に含める。
- [x] ユーザー発言の `text` を読み取り、履歴を統合する各経路で Markdown ブロックを再構築し、保存された Reasoning に要約がない場合はその旨を表示。スナップショットの改行を CRLF に統一し、読み取り専用ファイルの改行変換をエディターが要求しないよう修正。
- [x] 接続復旧・bridge 切替中のターン送信をガードする。Worker RPC が利用できない要求は null 参照にせず、未送信として扱う。
- [x] 通常接続と復旧でアカウント・モデル・権限・使用量の初期化を共有する。修正前のモデル一覧取得漏れ（0 回）を再現し、復旧と新規会話後の一覧再取得・選択保持を確認。読み込んだ拡張機能と Worker のビルド識別子・DLL の場所をログへ記録する。
- [x] `thread/start` と `turn/start` 中の標準エラーによる所有世代失効を再現。修正前は両方の回帰ケースで `StaleGeneration` が発生した。任意の MCP 通信エラーや WebSocket フォールバックの診断情報では接続復旧を開始せず、秘密情報を除去した標準エラーを診断ログに残す。
- [x] 標準エラー・HTTP HTML の生データを会話へ出さず、秘密情報と HTML を除去した 2,048 文字以内の診断ログと、最大 1 件の短いネットワーク・MCP 案内を残す。修正前は 3 件の送信ケースで生データと案内が重複し、修正後は合格。独立した差分レビューで対象範囲に残る要修正事項なし。
- [x] 実行中の 0.166 ターンと Plan モードの送信・完了経路をレビュー。20:07:45 にターン完了と Ready 復帰を確認し、ライセンス確認はモデルの最終回答であり拡張機能の完了処理の不具合ではない。Plan モードは読み取り専用のまま維持。
- [x] Stage 2 のレビュー修正コミット `b3b23e4` を検証。Debug・Release のビルドは警告・エラー 0 件。各構成の Core は 553 件合格・5 件スキップ、UI は 602 件合格・1 件スキップで、失敗は 0 件。Release の VSIX 内容検査に合格。Windows アカウントにシンボリックリンク作成権限がなく、必須 6 件が未実行のためリリース判定は保留。証跡は `app-server-0166-acceptance-evidence/release-validation.json`。

### 実 CLI の確認

- [x] 0.161.0: 新しい空の会話で `review/start` が動作し、レビューは同じ thread で進むことを確認。
- [x] 0.161.0: 履歴一覧では name が空で、レビュー指示文がプレビューとして表示されることを確認。

- [x] 0.166.0: 生成した schema が `threadUsage.nativeUsageUsdMicros`(nullable で、値なしは不明を意味する)、`account/usage/read.threadId`、`thread/start`(`permissions` と `activePermissionProfile` は Experimental のみ)、`inline` のみの delivery について実装と一致することを確認。
- [x] 0.166.0: 新しい空の会話で 2 回のレビューターンが同じ thread で完了し、履歴行は 0.161.0 と同じであることを確認。テスト用の会話は削除済み。
- [x] 0.166.0: `codex doctor --network-only --no-color --ascii` がフラグを受理し、TTY なしでプレーンテキストを出力して終了コード 0 で終了することを確認。
- [x] 0.161.0 と 0.166.0 の両方の面で `verify-contract-surface.ps1` が成功。
- [x] 0.165.0、0.164.0、0.163.0、0.162.1 の stable / experimental 両面で `verify-contract-surface.ps1` が成功。固定されたパッケージのハッシュを検証済み。スキーマ生成は一時的な PATH エイリアスへのアクセス警告が出たものの完了。

- [x] 0.166.0 の実 CLI を隔離し、ループバックの Responses 模擬応答先でインラインレビューの送信モデルを確認した。`review_model` を指定するとそのモデルが、省略すると会話のモデルが送信された。この試験の範囲は送信モデルの確認まで。

### 管理者による Visual Studio の受け入れ確認

- [x] 会話検索で一致する内容を取得できる。2026-11-02 に管理者がフォーカス、キーボード操作、Light/Dark、狭い幅でのアクセシビリティを確認し、検索画面のスクリーンショットを提供。
- [x] 拡張機能に `/doctor` が表示され実行できる。2026-11-02 に管理者が受け入れを確認。

- [x] 長い履歴の検索、生成された Plan の選択とコピー、High Contrast、Narrator、日本語会話の選択とコピー、Command の復元は PR #248 に管理者の確認結果が記録されている。

### 未確認

- [ ] 空でない Reasoning 要約が保存された会話を Visual Studio で復元する。隔離した 0.166.0 の実 CLI では `model_reasoning_summary="auto"` がループバックの Responses 要求に反映された。以前の Visual Studio セッションで `none` が記録された原因は未確認。
- [ ] 会話テキストのエディタータブで選択、コピー、Ctrl+F、F3、日本語、長い履歴、閉じた際のファイル削除を確認する。

### Stage 2 のレビュー対応

- [x] レビュー開始前に、新しい会話の有効な承認ポリシー、承認担当、権限プロファイル、sandbox 設定全体を比較。設定が異なる場合や作成中に元の会話が変わった場合は、レビューを開始しない。
- [x] 会話を切り替えると直前の会話の使用量を消去し、使用量ポップアップが開いていれば選択先の使用量を取得する。
- [x] 履歴スコープの選択中も履歴ポップアップを維持する。Escape または Close history で閉じられる。
- [x] 会話テキストの自動削除では、リダイレクトされた保存先ルートとオーナーフォルダーを対象から外す。外部の古いファイルを含む junction で回帰を検証。
- [ ] Visual Studio Experimental Instance で履歴スコープの ComboBox を確認する。

### Issue #250: 会話テキストの文字保持

- スナップショットで履歴本文の文字を保持し、フォールバックの説明と MCP のテキストを表示する。
- 通常の識別子の一部を認証情報のキーとして検出せず、認証情報の除去を維持する。
- Release ビルドは警告・エラー 0 件。Core テストは 555 件合格・シンボリックリンク関連 5 件スキップ、UI テストは 606 件合格・同 1 件スキップ。
- 実際の長い履歴を使った Experimental Instance の確認は未実施。
