# Issue #328: Codex CLI 0.170 以降の assistant message 表示制御

## 実装内容

Worker が、履歴と完了した live item から versioned moderation display decision を取り出すようにしました。CLI 0.169 は従来の live streaming を維持し、CLI 0.170 以降は assistant delta の本文を完了まで画面や診断へ出さず、完了 item の `show`、`hide`、`block` 判定を適用してから表示します。moderated な item で指示が欠落、不正、未対応、または未知の場合は本文を fail closed で抑止します。moderation がない場合と明示的な `null` の場合は通常の未 moderation message として扱います。

復元した履歴と会話 export にも同じ判定を適用します。切断時と turn 完了時に未完了本文を破棄し、再接続後は authoritative history から復旧します。選択肢検出には承認済みの完了本文だけを渡します。承認済み本文またはfallback artifact partsは表示し、Show以外ではpartsを抑止します。agent message eventには本文を含む raw `PayloadJson` を載せず、診断にはthread・turn・item IDだけを出します。Baseline以外の将来tierも保守的にbufferします。サニタイズとsecret redactionは維持しています。Worker contract version は 28 へ更新しました。

## 検証

- 初回 Core 関連テスト: 3 件成功。最終 focused Core テストは 5/5 件成功。
- 関連 UI テスト: 15 件成功。`ConversationTextFormatterTests` 全件、履歴復元、0.170 の buffer と表示判定、切断後の復旧、0.169 の streaming、選択肢検出、診断の redaction を含みます。最終 UI/formatter テストは 14/14 件、将来tierのbufferテストは 4/4 件成功し、parts-only item、null表示文言、PayloadJson除去、item完了なしでturnが終了するケースを確認しました。PR #333 の初回CIではdelta-only前提のUIテスト15件が失敗しました。旧来のstreamingテストではBaselineを明示し、0.170以降のケースではShow判定付きItemCompletedを送るよう修正しました。失敗対象の再実行は15/15件成功しました。Claudeレビューの追加指摘に対し、重複完了後も承認済み本文を選択肢検出用に保持し、完了後の遅延deltaを無視する回帰テストを2件追加しました。追加指摘のfocused setは8/8件成功し、最終のhistory/live重複テスト追加後も4/4件成功しました。最終コードでRelease UI suite全体は632/632件成功しました。history済みitemとlive完了の重複では承認済み本文をchoice検出に保持し、空item行を作らず、item ID欠落時はfail closedで処理します。
- `git diff --check` 成功。変更ファイルは UTF-8 BOM / CRLF。
- Visual Studio の画面確認は computer-use service の timeout により未実施です。実 IDE での受け入れ確認は Issue #331 に残しています。
