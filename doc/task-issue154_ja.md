# 作業記録（Issue #154）

## 2026-10-05：質問、権限、MCP 対話

- [x] v19 契約、固定 CLI 0.159.1 のプロトコル、既存実装、公開済み日英計画を確認し、Worker 契約を次の版 v20 に修正する。
- [x] 日英対話設計、ADR-015、Issue #154 の範囲、子 Issue #165〜#168 を承認・公開する。
- [x] 質問、範囲付き権限、コマンド選択肢、MCP elicitation、未対応対話、認証状態／操作の Worker／Extension 契約と RPC を追加する。
- [x] owner／generation／要求 ID 単位の共通 pending registry に検証、期限切れ、外部解決、世代終了、最大一回の完了処理を追加する。
- [x] 質問、権限、承認、対応 MCP form の独立カードと明示応答フローを追加する。
- [x] ローカル Gateway OAuth の状態確認と起動ゲート、明示 login／cancel／browser 操作を追加し、Remote は状態・案内に限定する。
- [x] 機密 payload が UI に渡る前に秘密入力と未対応のネイティブ本人確認要求を拒否する。成功経路の能力は宣言しない。
- [x] CLI 契約サーフェス確認、0.155.1 から 0.159.1 の安定スキーマ比較、スキーマキャッシュテストを完了する。
- [x] Core／UI 最終件数を記録する：Core Debug は369件成功、0件失敗、5件 skip（計374件）；UI Debug は342件成功、0件失敗、1件 skip（計343件）。
- [x] ソリューション全体の Debug／Release ビルドを警告0件・エラー0件で完了し、Release VSIX を生成する。
- [x] Release アセンブリの raw XAML hash とソースを照合し、VSIX SHA-256 を記録する：`BA83A86AFA1988B8FA8A2C387415435F5F727054E0379C3E241ED252AF83AB85`。
- [ ] Light／Dark／High Contrast、狭幅、キーボード、読み上げ／アクセシビリティ、複数カード、認証状態の Experimental Instance 画面を撮影・確認する。`vswhere` が Visual Studio を検出しなかったため、視覚合格は保留。

### 検証結果

- 実装中の初回 Core 実行：336件成功、18件失敗、5件 skip。暫定結果であり最終結果に置き換えられる。
- Core Debug：369件成功、0件失敗、5件 skip（計374件）。
- UI Debug：342件成功、0件失敗、1件 skip（計343件）。
- ソリューション全体の Debug／Release ビルド：各構成で警告0件、エラー0件。Release VSIX を生成。
- CLI 契約サーフェス確認：合格。0.155.1→0.159.1 安定スキーマ比較：合格。スキーマキャッシュテスト：合格。
- Release アセンブリの raw XAML SHA-256 はソース XAML と一致。Release VSIX SHA-256：`BA83A86AFA1988B8FA8A2C387415435F5F727054E0379C3E241ED252AF83AB85`。
- 視覚／アクセシビリティ確認：保留。`vswhere` が Visual Studio を検出せず、Experimental Instance の画面証拠は未記録。

追跡先：[Issue #154](https://github.com/kkamegawa/vsextensionforcodex/issues/154)、[#165](https://github.com/kkamegawa/vsextensionforcodex/issues/165)、[#166](https://github.com/kkamegawa/vsextensionforcodex/issues/166)、[#167](https://github.com/kkamegawa/vsextensionforcodex/issues/167)、[#168](https://github.com/kkamegawa/vsextensionforcodex/issues/168)。
