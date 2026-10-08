# Issue #155 作業記録

[英語記録](task.md)・[Issue #155](https://github.com/kkamegawa/vsextensionforcodex/issues/155)・[承認済み計画](daily-use-app-server-plan_ja.md)を参照してください。

- [x] P0: 契約 v21、メソッド分類、固定スキーマ・回帰 fixture、型付き DTO。
- [x] P1: 計画差分と完了の整合、通知、実効モデルの入力モダリティ検証。
- [x] P2: ライブ・履歴の型付き成果物、安全な画像プレビューとファイル操作。
- [x] P3: 既存ストアへの明示的添付追加・削除、来歴と不確定結果、成果物操作。
- [x] P4: /shell の文字列保持、明示確認、ポリシー、応答と pending lock。
- [x] P5: Windows Sandbox のモード・実際のソリューションルート確認、世代別一回の開始。
- [x] P6 自動検証: 両構成の全ビルド・テスト、スキーマ、VSIX、実 WPF 描画。
- [ ] P6 画面検証: Experimental Instance の各テーマ・狭幅・キーボード／フォーカス・アクセシビリティ。

Debug／Release とも Core は399合格・0失敗・5スキップ、UI は370合格・0失敗・1スキップ。両ビルドは警告・エラー0件。VSIX 内 DLL・埋め込み XAML・契約 v21 の整合を確認しました。

Visual Studio 2026 Enterprise 18.10.3 はインストール済みですが、ネイティブ CUA API が無効で Experimental Instance の画面を取得できません。オフスクリーンのプレビュー画像は確認済みであり、画面受け入れ条件とは分けて記録します。Issue #155 は未完の画面証跡のため open を維持し、#156 をリリースゲートとします。
