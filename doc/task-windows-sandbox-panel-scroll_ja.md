# Windows Sandboxパネルを会話スクロール領域へ移動

[English work record](task-windows-sandbox-panel-scroll.md) · 承認済みの[英語設計](daily-use-app-server-design.md) / [日本語設計](daily-use-app-server-design_ja.md)。

関連Issue: [#200](https://github.com/kkamegawa/vsextensionforcodex/issues/200)（終了済み）。承認済みの実装計画は[英語Wiki](https://github.com/kkamegawa/vsextensionforcodex/wiki/windows-sandbox-panel-scroll)と[日本語Wiki](https://github.com/kkamegawa/vsextensionforcodex/wiki/windows-sandbox-panel-scroll_ja)に登録済み。

## 実装

- Windows Sandboxパネルを仮想化された会話リストの合成先頭行へ移し、会話と同じ縦スクロール領域に配置した。
- `Items`を会話・履歴の正本として維持し、画面用の`TranscriptItems`投影を追加した。合成パネル行は履歴、履歴サイズ計算、保存される`Items`状態に含めない。
- Add、Remove、Replace、Moveの通知では合成行を安定させて投影を同期する。Resetや範囲変更の不整合では変更箇所だけを再構成し、共通する行を維持する。
- 合成行には型付きの`DataContract`表示モデルを渡し、祖先参照でroot ViewModelを再取得しない。sandbox状態、コマンド、歓迎表示、プロパティ変更通知を所有ViewModelから転送する。
- 通常の仮想化メッセージとsandboxパネルでDataTemplateを分け、通常行にsandbox操作を生成しない。合成行はフォーカス不可とし、パネルが非表示の非空スレッドから除く。
- 空会話の歓迎表示を同じ行へ移し、sandbox操作を覆わないようにした。リサイクル仮想化、ピクセルスクロール、App Server通知とshellコマンド確認の固定領域を維持した。
- sandbox設定の永続化、backend選択、setupコマンド、次回のローカルサーバー起動時に適用する動作は変更していない。

## 検証

| 確認項目 | 結果 |
|---|---|
| Transcript表示・投影の対象確認 | 9成功、0スキップ、0失敗 |
| Release UIテスト全体 | 654成功、3スキップ、0失敗（計657件） |
| Debugソリューションビルド | 警告0、エラー0 |
| Releaseソリューションビルド | 警告0、エラー0 |
| XAML・表示契約テスト | 対象および全UIテストで合格 |
| Claude最終差分レビュー | 実行可能な指摘なし |
| 文字コード・XAML確認 | UTF-8 BOMとCRLFを維持。XAML XML parseと`git diff --check`合格 |
| Visual Studio Experimental Instanceのスクリーンショット | 2026-11-26にユーザー承認済み。処理開始後の会話画面でWindows Sandboxパネルが表示領域から消えていることを確認。先行する起動試行では`IToolWindowProvider`／ServiceHubエラーでコンテンツ表示前に失敗したが、その後のスクリーンショットで画面受け入れが完了した。 |

ユーザー確認: 処理を開始するとsandboxパネルが現在の表示領域から消える。受け入れ済み。
