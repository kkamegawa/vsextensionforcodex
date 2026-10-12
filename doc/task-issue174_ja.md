# Issue #174: Goal 停止

[English](task.md)

日付: 2026-10-11
追跡: [Issue #174](https://github.com/kkamegawa/vsextensionforcodex/issues/174)

- 承認済みの設計と計画を [goal-stop-design_ja.md](goal-stop-design_ja.md)／[goal-stop-plan_ja.md](goal-stop-plan_ja.md)（英語版あり）に記録し、`design.md` と `plan.md` に概要を追加しました。統合レビュー後、composer の停止状態表と、Worker の読み取り失敗・timeout・pause 未対応・重複 Stop・開始中 turn の規則を追記しました。
- Worker 契約を v22 に上げ、`worker/thread/goal/stop`（`StopThreadGoalRequest`／`StopThreadGoalResult`、pause と interrupt の結果を個別に返す）を追加しました。
- Worker: Goal を読み取り、status のみの pause の後に同じ thread の最新 turn を中断します。各呼び出しの前後で owner・世代・thread を再検証します。timeout は結果不明として扱い、自動再試行しません。pause が未対応でも interrupt は試行し、重複 Stop は直列化します。server の turn ID がない開始中の turn は結果不明として返します。
- Extension: コマンド結果・通知・読み取りから Goal 状態を反映し、revision で古い応答を除外します。停止状態（None／Stopping／AwaitingTurn／Unknown／Retry）はすべて出口を持ち、一つのロック内で更新します。主操作の Stop は一つだけで、アイコン・ツールチップ・UI Automation 名と説明が一致します。Ctrl+Enter は送信専用の `SendKeyCommand` です。Goal が Active の間も slash command を使え、停止が未確定の間だけ拒否します。キュー済み command は取り消して fence で保護します。Goal の再取得は再接続時だけで、変更要求は再送しません。
- Fake App Server の `Issue174GoalStop` シナリオと結合テスト、Worker の順序・失敗・競合テスト、ViewModel の停止状態・通知・古い結果・未対応 server・キーボード・slash 表示のテストを追加しました。
- サブエージェントで Worker の競合制御と ViewModel／WPF を並列レビューしました。timeout で interrupt が送られない問題、pause 未対応の扱い、重複 Stop の誤った失敗、Check Stop Status が解決しない問題、Stopping のまま固まる問題、完了時の変更通知漏れ、Unsupported／Retry が解除されない問題、thread 切り替え後の古い操作、slash command の過剰な禁止、送信ボタンの二重表示、古いヘルプテキスト、停止メッセージの残存を修正し、テストで確認しました。
- Debug／Release のソリューションビルドは警告 0・エラー 0。Core は 415 件成功・5 件スキップ（420 件）、UI は 399 件成功・1 件スキップ（400 件）でした。0.155.1→0.159.1 の stable／experimental スキーマ比較、0.159.1 両サーフェスの契約検証、スキーマキャッシュ検証はすべて成功しました。
- Release VSIX の整合性: 同梱の Extension DLL と Contracts DLL（ルートと `Worker/`）はビルド出力と一致し、埋め込み XAML はソースと一致しました（`3B6475D56A21E8E68AAF64B84A94AF9426AF4C55A3780E1D05E855CA4AF84805`）。`ContractVersions.Current` は 22 です。VSIX SHA-256: `8F2FB7C6013815448D8C327D8F8EC508DF3DB097EA0A1DB5A18EA3BFDEB617C8`。
- 未完了: Experimental Instance での受け入れ確認（Light／Dark／High Contrast、狭幅、キーボード／フォーカス、UI Automation、Active Goal・停止中・停止後・再接続後のスクリーンショット）。このセッションではデスクトップ操作を利用できなかったため実施していません。
