# ADR-014: 再接続と履歴復旧

- 日付: 2026-09-13
- 状態: 承認済み
- 対象: Codex App Server 更新の復旧 Issue

## 決定

- VS surface が存在する間、composer 本文、添付、選択 skill、次ターン設定をメモリに保持する。
- 再接続後に初期化し、利用可能な場合は明示的なページ取得で履歴を復旧する。通知は thread、turn、item identity で統合する。
- 保存済み添付はページ取得で再構築し、thread/attachment/updated 通知を添付 identity で統合する。thread は自動 resume しない。
- 配信結果が不確かな入力、承認、変更操作を自動再送しない。履歴確認と、ユーザーが確認した再試行を提示する。
- thread/attachment/add と thread/attachment/remove は明示的変更操作とし、結果が不確かな場合は自動再送せずユーザー確認を求める。
- 自動復旧は最大5回とし、その後は手動再接続を提示する。

## 結果

重複した副作用を防ぎながら、下書きと安全に再構築した添付を意図的な確認・再試行に利用できる。

## 現行の復旧契約

- 日付: 2026-10-04
- タスク: Issue #153
- 承認参照: この会話でユーザーが承認した計画
- 上記は当初の決定を記録したものです。以下の追補が現在の実装対象です。
- Worker 交代後も継続する Extension 所有の single-flight episode が Worker/transport の一時切断だけを再試行します。試行前の待機は順に0、1、2、4、8秒（0以外は±20% jitter）、各試行上限45秒、episode 全体は5分です。認証、TLS/証明書、profile/settings/root、owner 変更、キャンセルでは終了します。idle watchdog は検知して接続を閉じるだけで、#153 coordinator が対象の一時切断を処理します。所有者起点の #152 login/logout lifecycle は分離します。
- 接続後は現在の target と thread 一覧を再取得し、ユーザーが明示的に会話を選択した後に、その会話の最新履歴ページを読みます。以前の thread を自動 resume しません。thread/resume はユーザーが明示した場合だけ呼び、excludeTurns=true を指定します。置換 Worker は旧 Worker 内部 owner fingerprint との継続性を証明できないため、旧下書きを opaque owner の下に quarantine します。target の確認、現在の履歴からの会話選択、明示的な復元または破棄を求めます。旧 partition、承認、cache、request、credential、thread ID、proof は移行しません。
- 最新の上限付き履歴ページを先に読み、古いページはユーザー要求で取得します。server ID と generation で統合し、履歴は最大1,000 items/16 MiB、通知は最大1,024 events/8 MiB とします。
- #153 は基本的な添付 metadata だけを読みます。1ページ最大50件、cursor が null まで取得し、thread ごとに最大100件、直列化 payload は最大64 KiB、type と identityKey は各最大256 UTF-8 bytes とします。list、cursor、field、payload を検証し、未知・利用不可の理由を表示します。通知に MIME/payload はありません。ID は所有 thread 内で扱い、削除後の再作成や fork 後は再取得します。型付き解釈・操作は #155 の対象です。
- transport write が始まった可能性がある操作は再送しません。operation ID はローカルだけで使います。本文・時刻の類似や履歴ページにないことは証明になりません。確定応答は記録した結果を確定でき、切断前に相関付けた server item identity は受理だけを示して、すべての副作用の完了は証明しません。NotSent は dispatch が開始していない証明を必要とします。それ以外は不確定のまま、明示的な Copy/Edit/Send 確認を求めます。承認 ID/proof は再利用しません。
- 追加する4つの read-only history method は overload allowlist へ個別審査し、既存8メソッドの policy と5回の接続試行から分けます。

詳細設計: [接続・履歴の復旧](../connection-history-recovery-design_ja.md)。これは計画であり、実装証跡は未取得です。