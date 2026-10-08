# ADR-016: 日常利用の App Server 機能

- 日付: 2026-09-13、2026-10-08 改訂
- 状態: 承認済み
- トラッキング: [Issue #155](https://github.com/kkamegawa/vsextensionforcodex/issues/155)、[Issue #149](https://github.com/kkamegawa/vsextensionforcodex/issues/149) の子 Issue

## 決定

- `turn/plan/updated` を構造化 step の snapshot として維持し、experimental な `item/plan/delta` は任意の暫定テキストとして扱う。完了 item を正とする。
- 実行時モデルカタログを正本とし、広告された推論レベル、入力 modality、access metadata を使う。`turn/start` 前に有効モデルの入力を検証し、モデルを暗黙に変更しない。
- `thread/shellCommand` を使う明示的な `/shell [--timeout-ms N] -- <command>` を追加する。コマンド文字列を正確に保持し、ローカル承認ポリシーを評価し、RPC と実行の期限を分離し、スレッドごとに保留送信を一つにする。CLI 0.159.1 ではこの method は sandbox 外の full access で実行される。
- 型付き上限コンテンツと、対応付け済みファイルへのローカル操作を表示する。操作時にもパスを検証し、画像プレビューを上限付き PNG/JPEG に限る。
- 保存済み添付はメタデータとして扱い、モデル入力へ自動追加しない。既存の上限付き復旧ストアに対して明示操作で追加・削除する。クライアント所有 payload は `relaycodex.file.v1` を使い、未知の記録は表示のみ・読み取り専用とする。
- Windows sandbox 設定は所有するローカル stdio 接続だけに提示し、モードを明示させ、結果不明状態を正確に表示する。
- daemon/worktree lifecycle、voice/Realtime、dynamic tools、ExternalMessage、plugin import、attestation は本計画に含めない。

## 影響

日常利用機能は上限とポリシーで保護する。承認済みの Issue #155 設計と実装計画は [daily-use-app-server-design.md](../daily-use-app-server-design_ja.md) と [daily-use-app-server-plan.md](../daily-use-app-server-plan_ja.md) に記録する。CLI、SDK、runtime、package の版は変更せず、Worker contract は統合時に次の利用可能版を割り当てる。Phase 6 は Issue #155 で追跡し、Issue #156 が求める統合検証と画面証跡を完了条件とする。
