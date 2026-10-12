# Goal 停止計画 — Issue #174

日付: 2026-10-11  
状態: 実装計画承認済み（2026-10-11）。  
追跡: [Issue #174](https://github.com/kkamegawa/vsextensionforcodex/issues/174)  
設計: [Goal 停止設計](goal-stop-design_ja.md) と[英語版](goal-stop-design.md)

## 概要

選択中 thread に Active Goal があれば、turn 間や自動継続中も composer の Stop を利用可能にする。Stop は最初に Goal を一時停止し、その後同じ thread の最新実行中 turn を中断する。操作は現在の owner と接続世代に限定し、再接続後に再送しない。

## 実装フェーズ

| Phase | 作業 | 成果物 |
|---|---|---|
| P0 — 契約と設計 | 承認済み動作を設計/計画記録へ反映し、Worker 契約 v21 を次の未使用版へ進める | 英語正本の設計/計画と日本語訳、型付き Worker request/result |
| P1 — Worker 停止処理 | Goal 読み取り、status のみの pause、同一 thread の最新実行 turn interrupt、owner/世代/thread 検査、queue 取消 barrier、各段階の個別結果 | Worker 実装、Fake App Server の順序/競合/失敗シナリオ |
| P2 — Goal 状態と composer | Goal 応答/通知/読み取りの同期を集約し、Active/停止中状態を単一 Stop 操作へ結び、停止中 keyboard 送信を禁止 | Extension ViewModel と Remote UI 動作 |
| P3 — 統合レビュー | Worker 競合と WPF 動作をレビューし、指摘を修正して定義済み検証を実施 | レビュー済み実装、自動検証結果、パッケージ検査、Experimental Instance 証跡 |

## 受け入れ条件

- turn 実行中、turn 間、自動継続、承認待ち、入力待ちで Active Goal の Stop を表示する。対象 turn の完了後、Paused/終了/制限/削除 Goal は通常 composer 状態に戻し、Resume 後 Active なら Stop を戻す。
- Stop は status のみの pause を先に試し、objective/budget/usage を維持する。pause 試行後は失敗/結果不明でも、同じ thread の最新実行中 turn を可能な限り interrupt する。明示的な Stop 指示に含まれるため追加確認しない。turn がなければ interrupt せず pause の結果を返す。
- Goal 読み取り応答で新しい通知や別 owner/thread/世代の状態を上書きしない。再接続や遅延結果による変更要求の再送をゼロにする。
- Stop は thread の未送信作業を取消し、取り出し済み Resume/command も送信前に止める。未送信の composer テキストと添付は保持し、二重クリックは一つの操作とする。
- pause/interrupt の結果を個別に扱う。pause 成功後の interrupt 失敗でも Paused を維持する。pause が失敗して interrupt が成功した場合は Paused と誤表示せず部分結果を報告する。受付応答を完了とみなさず、結果不明はユーザーが明示再試行するまで読み取り専用とする。
- Goal Stop は起動元に関係なく同じ thread の現在の自動継続 turn を中断できる。通常 Goal なし interrupt のローカル開始条件を維持する。別 thread と通常 Send/Steer の動作を変えない。
- Remote UI メンバーは serialize/change notification を備える。Stop の icon/tooltip/automation name/help と keyboard 動作を一致させ、二重表示しない。

## 検証

- Fake App Server/単体テスト: Goal 作成/更新/削除/Resume、初期読み取りと通知の競合、pause 試行→interrupt、turn 不在時の pause、フィールド保持、pause 試行後の同一 thread 最新 turn 選択、pause 失敗/不明後も interrupt 試行、ACK 後の完了、停止中 turn 変更、終了/削除競合、二重クリック、queue barrier、古い owner/thread/世代、再接続時の再送なし、pause/interrupt 個別失敗、部分結果、結果不明、未対応 method、認証状態変化。
- 回帰テスト: 通常 Send/Steer/interrupt、選択 thread の分離、未送信入力/添付保持、Stop 二重表示なし。
- Core/UI 全テスト、警告ゼロの Debug/Release solution build、固定 CLI 0.159.1 契約と CLI 0.155.1 回帰、VSIX DTO/XAML 整合性を確認する。
- Experimental Instance で Light/Dark/High Contrast、狭幅、keyboard/focus、UI Automation を確認する。Active Goal、停止中、停止後、再接続後の画面を記録し、実施できない視覚確認は未完了とする。

## 対象範囲と完了記録

Issue #173、CLI/SDK/NuGet 更新、後方互換対応、データ移行は含めない。実装・テスト結果は実測前に記録しない。完了時に `doc/task.md` と実装証跡を更新し、Issue #174 から参照する。未取得の画面証跡は未完了のまま残す。
