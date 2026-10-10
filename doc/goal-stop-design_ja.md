# Goal 停止設計 — Issue #174

日付: 2026-10-11  
状態: 設計承認済み（2026-10-11）。  
追跡: [Issue #174](https://github.com/kkamegawa/vsextensionforcodex/issues/174)  
計画: [Goal 停止計画](goal-stop-plan_ja.md) と[英語版](goal-stop-plan.md)

## 背景

現在、Goal 通知は transcript の履歴に反映されるだけで、composer の操作が Goal のライフサイクルと同期していない。通常の turn 中断経路は Extension が開始した turn に限定されるため、Goal による自動継続を確実に停止できない。Goal 状態は App Server に thread 単位で保存される。

この設計は承認済みの Issue #174 計画に従う。明示的な Goal Stop は、最初に Goal を一時停止し、その後同じ thread について把握している最新の実行中 turn を中断する。Goal 状態と操作は owner、接続世代、thread で分離する。

## ユーザーに見える動作

- 選択中 thread に Active Goal がある間、turn 間でも composer の主操作を Stop にする。自動継続中、承認待ち、入力待ちでも利用できる。
- Stop 要求は single-flight とする。停止中を表示し、対象 turn の完了または確定的な失敗まで Send、Steer、Resume、重複 Stop を禁止する。
- pause 後も対象 turn が実行中なら停止中表示を維持する。実行中 turn がなければ pause だけで停止完了とする。
- 対象 turn が終わった後、Goal が Paused、Complete、Blocked、使用量/予算制限、削除の場合は通常の composer 操作に戻す。Resume で Active になれば再び Stop を表示する。
- Stop 操作は一つだけ表示する。アイコン、ツールチップ、アクセシビリティ名/説明、有効状態、keyboard 操作を一致させる。
- 未送信のテキストと添付は composer に保持する。Goal Stop はその thread の未送信 command を取り消す。
- Ctrl+Enter は送信専用とし、Stop を実行しない。主操作が Goal 操作の間は無効にする。
- Goal が Active の間も `/goal pause` や `/goal clear` などの slash command は利用できる。その thread の停止が未確定の間だけ、表示付きで拒否する。
- App Server が Goal に対応していない場合、composer は通常の Send と Interrupt に戻り、理由を表示する。

## Goal 状態と同期

`ChatViewModel` は選択中 thread の表示用スナップショットを持つ。識別子は owner、接続世代、thread ID を含む。状態取得元は同じ更新経路に集約する。

1. `ExecuteGoalAsync` が返す Goal。
2. `thread/goal/updated` と Goal の clear/deleted 通知。
3. thread の明示的な選択または Join 後に行う `thread/goal/get`。

非同期読み取り時に完全な識別子と現在の Goal 更新 revision を記録する。応答を適用する際に識別子と revision が一致しなければ、より新しいイベントを維持する。thread または owner/世代が変わると旧スナップショットを消してから新しい状態を取得する。再接続（実行中以外の状態から Ready への遷移）の場合だけ、既存の明示的な選択/Join の復元後に Goal を読み取り専用で再取得する。turn の境界は Goal 通知で反映する。Stop や他の変更要求は再送しない。

Stop 処理中は古い Active 読み取りや通知よりローカルの停止状態を優先する。Goal 終了/削除は Goal 表示へ反映してよいが、捕捉した対象 turn がまだ実行中であれば停止中状態を解除しない。確定的な turn 完了か接続の終了で対象 turn の待機を解決する。

## Composer の停止状態

Goal 状態は Worker 通知スレッドと RPC の継続処理の両方から更新されるため、停止状態は一つのロック内で更新し、変更通知はロック解放後に発行する。各停止操作は Stop と表示対象のリセットごとに進む操作 ID を持ち、古い ID の結果は無視する。

| 状態 | 主操作 | 遷移 |
|---|---|---|
| None | Goal が Active なら Stop、それ以外は Send/Steer | Stop で Stopping |
| Stopping | Stopping（無効） | Worker 結果: 成功 → AwaitingTurn、失敗 → Retry、結果不明または例外 → Unknown、未対応 → None |
| AwaitingTurn | Stopping（無効） | 対象 turn の完了、または thread 状態が turn なしを示す → None。同じ thread で別の turn が実行中 → Retry |
| Unknown | Check Stop Status（読み取り専用） | 確定的な Goal 読み取りまたは通知: 同じ thread の turn が実行中か Goal が Active → Retry、それ以外 → None。読み取り失敗時は Unknown のまま再確認できる |
| Retry | Retry Stop | 明示的な Retry で Stopping。同じ thread の turn がなく Goal が Active でない → None |

## Stop 要求契約と順序

既存の owner-scoped request envelope と選択中 `ThreadId` を使う Extension–Worker 専用操作 `StopThreadGoalAsync` を追加する。結果は捕捉した世代、観測/現在の Goal 状態、pause 結果、interrupt 結果、対象 `TurnId` を返す。Worker 契約は v21 から実装時点の次の未使用番号へ進める。

Extension は Stop を受け付けた時点で owner、接続世代、thread、active turn ID を捕捉する。Worker も世代が現在のもので thread が Join 済みであることを検証する。App Server 呼び出しの前後で再検証し、owner・接続・thread が変わった場合に後続呼び出しを別対象へ向けない。

捕捉した thread について Worker が現在の Goal 状態を読む。Active の場合、または読み取りに失敗した場合は `status: "paused"` だけを指定して `thread/goal/set` を試みる。objective と budget を省略して既存 Goal と使用履歴を保持する。pause 試行後、同じ thread の最新実行中 turn を特定し、ユーザーが明示的に Stop を指示しているため、追加確認なしで `turn/interrupt` を送る。pause が失敗または結果不明でも、可能な限り同じ thread の turn を中断する。実行中 turn がなければ interrupt は送らず、pause 結果を返す。その thread の `turn/start` が server の turn ID なしで処理中の場合、interrupt 段階を結果不明として返す。自動継続 turn や、クリック後に終了・削除された Goal の turn も対象とし、誰が turn を開始したかで対象を推測しない。

重なった Stop 要求は直列に処理し、後の要求は先の結果を観測する（失敗とは報告しない）。要求の timeout は結果不明とし、App Server のエラー応答だけを確定失敗とする。pause メソッドが未対応の場合は pause 段階の失敗として扱い、interrupt は試行する。何も試行しなかった場合だけ未対応として返す。

pause と interrupt の結果は独立して扱う。interrupt に失敗しても成功した pause は維持する。pause が失敗しても interrupt を試し、pause 成功を表示せず部分的な結果を報告する。再度の Stop はユーザーの新しい明示操作を要求する。interrupt の受付応答と turn 完了を区別し、対象 `turn/completed` を待つ。pause 試行後に捕捉済み thread の最新実行中 turn を取得して対象とし、別 thread へ中断を転送しない。

## キュー、失敗、再接続

- Stop はその thread の未送信 command を取り消す。取り出し済み command も送信直前に stop barrier を確認し、キュー済み Resume やユーザー command を Stop 開始後に送信させない。composer のテキストと添付は保持する。
- 二重クリックは一つの処理に集約する。他 thread の command は変更しない。
- pause 失敗、interrupt 失敗、結果不明を区別する。pause の成否にかかわらず pause 試行後に同じ thread の interrupt を試す。pause 成功後に interrupt が失敗しても Paused を維持し、pause 失敗後に interrupt が成功した場合は Paused と誤表示せず部分的な結果を示す。timeout/切断後に変更要求を自動再試行しない。
- 結果不明の場合、可能なら読み取り専用で状態を確認し、未確定段階を表示する。変更要求の再試行にはユーザーの明示操作を要求する。
- 再接続後は既存の明示的な選択/Join 後に Goal と thread 状態を読み取る。Stop、Resume、turn、キュー済み変更要求を自動送信しない。
- owner/世代検査は維持しつつ、認証状態の変化だけを理由に専用 Stop と読み取り専用の Goal 取得を拒否しない。ログは既存の secret redaction 経路を通す。

## インターフェイスと実装境界

- Remote UI に公開する Goal/停止プロパティに `DataMember` と変更通知を付ける。Worker 専用の turn/owner 情報は、上限付き表示状態に縮約しない限り Remote UI に出さない。
- Worker v22 契約結果では世代、Goal 状態、pause 結果、interrupt 結果、対象 turn を区別する。各段階は成功、確定失敗、結果不明を明示する。
- Goal pause の App Server 更新 payload は objective と budget を省略する。CLI 0.159.1 に固定し、CLI、SDK、パッケージを更新しない。
- Goal のない通常 turn の Send/Steer と通常 interrupt のローカル開始条件は変更しない。

## 検証と受け入れ

自動テストでは Goal のライフサイクルと各状態取得元の突合、pause 試行が interrupt より先であること、pause 失敗/結果不明後も interrupt を試すこと、turn がない場合の pause、objective/budget/usage の保持、追加確認なしで pause 試行後の同一 thread 最新 turn を選ぶこと、受付応答と完了の分離、終了/削除との競合、queue barrier、二重クリック、古い owner/thread/世代の応答、自動再送なし、部分結果/個別失敗/結果不明、通常 Send/Steer/interrupt の不変を確認する。

Core/UI テスト、警告ゼロの Debug/Release ビルド、固定 0.159.1 契約と 0.155.1 回帰、VSIX DTO/XAML 整合性を検証する。Experimental Instance では Light/Dark/High Contrast、狭幅、keyboard/focus、UI Automation を確認し、Active Goal・停止中・停止後・再接続後のスクリーンショットを記録する。テストやビルドを画面証跡の代わりとしない。

## 根拠

- Codex App Server — “Manage a thread goal” (`<APP_SERVER_REFERENCE_URL>`、2026-10-11 確認。リポジトリ文書では URL をプレースホルダにしており、公開リンクは [Wiki の計画](https://github.com/kkamegawa/vsextensionforcodex/wiki/goal-stop_ja) の参考資料に記載): `thread/goal/set`、`thread/goal/get`、Goal 更新通知を定義している。status 更新時に objective を省略すると使用履歴を維持する。これは status のみの pause と状態同期の根拠であり、Stop 順序と UI 動作は本リポジトリで承認された設計である。
