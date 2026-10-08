# Issue #155 実装・検証記録

[英語正本](implementation.md#issue-155-daily-use-app-server-features)・[設計](daily-use-app-server-design_ja.md)・[Issue #155](https://github.com/kkamegawa/vsextensionforcodex/issues/155)。

Worker 契約を v20 から v21 に更新しました。CLI は0.159.1を固定し、0.155.1の回帰対象を維持しています。SDK・ランタイム・パッケージは更新していません。P0–P5 の実装と自動検証、および P6 の自動検証部分を完了しました。

計画は75msで差分を集約し、完了通知の内容で置換します。ライブと履歴で同じ型付き成果物を表示し、所有者・接続世代付きの不透明な操作 ID を通してプレビュー・Open・Reveal を行います。PNG／JPEG はサイズ・寸法・完全デコードを確認し、キャッシュの物理パスと所有者を検証します。保存添付は既存ストアを再利用し、relaycodex.file.v1 の形式・来歴・OS別正規化キーを検証します。/shell は確認した文字列・対象・作業ディレクトリを維持し、不確定な送信は再送しません。外部ターンを Stop 対象にしません。Windows Sandbox はローカル Windows でのみ実行し、確認したモードと実際のソリューションルートを使用して接続世代ごとに一度開始します。

| 検証 | 最終結果 |
|---|---|
| Debug／Release 全ビルド | 両方とも警告0・エラー0、VSIX生成 |
| Core Debug／Release | 各399合格・0失敗・5スキップ、計404 |
| UI Debug／Release | 各370合格・0失敗・1スキップ、計371 |
| スキーマ・キャッシュ・メソッド登録 | 固定0.159.1と宣言済み0.155.1回帰対象で成功 |
| VSIX | DLLハッシュ・契約v21・埋め込みXAMLとソースの一致 |
| 画像表示 | STAでPNG/JPEGをデコードし、実XamlFragmentをWPFで描画して画像確認 |
| Experimental Instance | 未取得・未完了 |

6件のスキップは、このWindows環境で必要なファイルシステムリンクを作成できない既存のパス境界・ソリューション作成テストです。合格には含めません。

Release VSIX SHA-256: 0F34622C51C7BD8E3B9063E5567A2E42B52719AB58A86652E095A17AEAAE8F51。ソース／埋め込み XAML SHA-256: BA66E0F8945F337A94E12FE64312D13DBFD6DAE49BA7B99401B72B247C128399。再現コマンドは英語正本の PowerShell 7／Bash 例を参照してください。

Visual Studio 2026 Enterprise 18.10.3 は存在します。ネイティブ CUA API が無効のため、Experimental Instance の Light／Dark／High Contrast、狭幅、キーボード・フォーカス、アクセシビリティの画面受け入れは未完了です。artifacts/issue155/ui-preview.png のオフスクリーン画像は別の証跡です。Issue #155 は open のまま、#156 をリリースゲートとします。
