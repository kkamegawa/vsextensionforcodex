# 実装記録（Issue #154）

## 2026-10-05：質問、権限、MCP 対話

Worker 契約 v20 の対話 DTO／owner scope RPC、世代単位の共通 pending registry、独立した質問・権限・承認・MCP カード、検証付き MCP form elicitation、ローカル Gateway OAuth 状態と操作を実装した。Worker は上流要求 ID と wire 値を保持し、旧世代または外部解決済み要求を拒否して、完了を最大一回に制限する。秘密入力と未対応のネイティブ本人確認要求は payload を UI に渡す前に拒否する。Remote Gateway OAuth は状態と案内だけを提供する。添付メタデータ復元は #153、添付操作と表示は #155 の担当である。

最終自動検証は完了した。実装中の初回 Core テストは336件成功、18件失敗、5件 skip だったが、暫定結果であり次の最終結果に置き換えられる。

- Core Debug テスト：369件成功、0件失敗、5件 skip（計374件）。
- UI Debug テスト：342件成功、0件失敗、1件 skip（計343件）。
- ソリューション全体の Debug／Release ビルド：各構成で警告0件、エラー0件。Release VSIX を生成。
- CLI 契約サーフェス確認：合格。
- CLI 0.155.1 から 0.159.1 への安定スキーマ比較：合格。
- スキーマキャッシュテスト：合格。
- Release アセンブリ内の raw XAML の SHA-256 はソース XAML と一致。
- Release VSIX の SHA-256：`BA83A86AFA1988B8FA8A2C387415435F5F727054E0379C3E241ED252AF83AB85`。
- Visual Studio：このホストでは `vswhere` がインスタンスを検出しなかった。Experimental Instance の画面、Light／Dark／High Contrast、狭幅、キーボード、読み上げ／アクセシビリティ、複数カード、認証状態の確認は保留であり、視覚検証済みとはしない。追跡先：[Issue #154](https://github.com/kkamegawa/vsextensionforcodex/issues/154)、[#165](https://github.com/kkamegawa/vsextensionforcodex/issues/165)、[#166](https://github.com/kkamegawa/vsextensionforcodex/issues/166)、[#167](https://github.com/kkamegawa/vsextensionforcodex/issues/167)、[#168](https://github.com/kkamegawa/vsextensionforcodex/issues/168)。
