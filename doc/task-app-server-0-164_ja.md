# Codex App Server 0.164.0 作業記録

追跡: [#226](https://github.com/kkamegawa/vsextensionforcodex/issues/226)、[#227](https://github.com/kkamegawa/vsextensionforcodex/issues/227)〜[#231](https://github.com/kkamegawa/vsextensionforcodex/issues/231)。実装の詳しい根拠: [実装記録](implementation-app-server-0-164_ja.md)。英語版: [English](task-app-server-0-164.md)。

## 完了

- [x] 設計、計画、ADR-019、ADR-011 と ADR-018 の改訂（英語・日本語、#232）。
- [x] 契約マニフェスト v3（#233）。最低版、目標版 0.164.0、回帰版 0.163.0・0.162.1・0.161.0 を記録し、マニフェストの検証と CI のマトリクスを追加し、0.159.1 を削除しました。
- [x] `userAgent` からの Semantic Versioning による版の判定、0.161.0 未満の拒否（再試行なし）、機能段階、Worker 契約 v26 の経路（#234）。VSIX の検査が契約 v26 を前提にするよう修正しました。
- [x] CLI と同じ規則の `/model` 引数、`/permissions` の確認待ちの間のキュー停止、プリセット パネル（#235）。
- [x] 再開時の設定の反映、アカウント変更時に選択を変えた場合の通知、MCP の3項目表示、`/diagnostics`、compaction の結果の表示（#236）。
- [x] 4つの版・両サーフェスでの契約スクリプトの実行、6件のスキーマ比較、4つの版すべての実際の CLI での `initialize`。
- [x] 0.164.0 での MXC 起動の確認、0.164.0 での実際の compaction と、サーバー再起動後の会話の再開。
- [x] Core と UI のテスト、Debug と Release のビルド（警告 0）、Release VSIX の検査。

- [x] オーナーによる、実際の CLI を使った Experimental Instance での受け入れ確認。別セッションの Computer Use で実施し、手順ごとのスクリーンショットをリポジトリ外に保存しています。
  - 接続、モデルの指定、実際のプロンプト
  - プリセットの保存と CLI からの読み戻し、呼び出し、編集
  - Dark、Light、High Contrast での表示
  - 日本語表示、キーボード操作、Narrator の読み上げ
  - リモートでの診断 ZIP の保存
- [x] 受け入れ確認で見つかった不具合の修正
  - プリセットの削除で、明示的な null を送るようにした（#234）。
  - originator に空白を含む `userAgent` でも版を正しく読むようにした（#234）。
  - ヘッダーで、状態表示を専用の行に置き、その下のツール バーを折り返すようにした（#237）。
  - 削除の修正は実際の CLI で確認し、ヘッダーの修正は WPF 単体の描画により4つの幅で確認しました。

- [x] #230 のレビュー後の修正：compaction の状態と会話欄への表示を、対象の会話が選択中の場合に限定しました。別会話が選択中でも追跡の終了と使用量更新は維持します。回帰テスト4件を追加し、compaction 関連8件が合格しました。Debug／Release の UI はそれぞれ569件合格、シンボリックリンクによるスキップ1件。Release 全体ビルドは警告0・エラー0で、VSIX 検査も合格しました。WPF 単体の会話欄の描画も確認しました。ローカルの証跡と確認範囲は実装記録に記載しています。

## 未完了

- [ ] 狭い幅のスクリーンショットによる受け入れ：実施時にヘッダーが不合格でした。修正後に、プリセットの削除とあわせて Experimental Instance で再確認します。
- [ ] 0.164.0 でのローカルの診断 ZIP の保存。

アカウント変更時の通知の自動テストは、合意のうえで対象外にしました。
