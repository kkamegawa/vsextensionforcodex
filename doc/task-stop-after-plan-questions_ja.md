# Planから実装・問い合わせへの移行後のInterrupt修正記録

日付：2026-10-14。

追跡：[Issue #210](https://github.com/kkamegawa/vsextensionforcodex/issues/210)。関連：[Issue #22](https://github.com/kkamegawa/vsextensionforcodex/issues/22)、問い合わせ受入確認：[Issue #188](https://github.com/kkamegawa/vsextensionforcodex/issues/188)。承認済み計画は[日本語Wiki](https://github.com/kkamegawa/vsextensionforcodex/wiki/stop-after-plan-questions_ja)と[英語Wiki](https://github.com/kkamegawa/vsextensionforcodex/wiki/stop-after-plan-questions)に登録済み。Wikiコミットは`0021eb5`。

## 修正内容

- 応答で確認できたローカルの開始記録をowner・接続世代・会話・ターンに紐づけ、一時的な空・別IDの状態通知や問い合わせ待ちでは失わないようにした。通常送信、質問への回答後の送信、直接の`/plan <prompt>`で記録処理を共有する。報告されたIDE内の通知順序自体は未確定。
- 対応するターンの完了で記録を破棄し、開始応答より先の完了でも復活させない。owner・接続の変更、Disconnected・Degradedでは開始記録と応答待ち記録を消す。各記録には保持上限を設けた。
- 通常の割り込みは、参加済みの選択会話で現在BusyまたはWaitingForApprovalになっている、開始の根拠を持つ厳密に同じターンだけ許可する。他クライアントのターンは対象にしない。Goal Stopの既存動作を維持する。
- 可否が変わる際にRemote UIへ通知する。診断ログには状態と可否の真偽値だけを変更時に記録し、依頼文・回答・パス・生のIDは記録しない。

## 検証結果

- [x] 旧実装で3件の回帰を再現：直接のPlan開始でInterrupt不可、一時通知による開始記録の消失、完了後の遅い開始応答による復活。修正後のStop/Goal対象28件はすべて成功。
- [x] Planから実装、構造化質問と文章質問への回答、実際のInterrupt要求の識別子、状態遷移、完了順序、owner・接続・会話の分離について8ケースを追加。
- [x] Debug・Releaseのsolutionビルドは、それぞれ警告0・エラー0。各構成でUIは447成功・1スキップ、Coreは451成功・5スキップ。既存の6スキップは未完了として扱う。
- [x] 初回のサンドボックス内UI実行では画像キャッシュへの書き込みで失敗したが、通常のファイルアクセス権で再実行して成功。画像プレビューの実装やテストは変更していない。
- [x] Release VSIXのDLL・埋め込みXAML・contract v24の整合性が成功。VSIX SHA-256：`884943a51f123cbcdc795fbd8aa2ea5ed6b5e675f37175dce51ab648b7455eb3`。証跡は`artifacts/stop-fix/vsix-release.json`と同ディレクトリのビルド・TRXログ。
- [x] 本番ViewModelと埋め込みXAMLから取得したボタンのバインディングを使う単独WPFハーネスを実行。問い合わせ回答後とReady/nullからBusyへの復帰後にInterruptが表示・有効になるスクリーンショットを確認した。ボタン操作で送信される会話・ターン・ownerが現在の対象と一致することも確認。証跡は`artifacts/stop-fix/visual/bin/Debug/net8.0-windows10.0.22621.0/screenshots`と`visual-result.txt`。ボタンの外観は標準WPFスタイルを使用。
- [x] 日英Wikiの索引・経路・日付と、Home・ページ・言語切替の公開リンク50件をすべて確認。証跡は`artifacts/stop-fix/wiki-public-verification.json`。
- [x] 修正後の Plan から実装・問い合わせへの流れについて、メンテナーが Visual Studio Experimental Instance で実際のVSホスト操作を確認済み。

英語記録：[task.md](task.md#2026-10-14-preserve-ordinary-interrupt-after-plan-implementation-and-questions)。
