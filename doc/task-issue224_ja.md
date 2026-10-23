# Worker 置き換え後も自動復旧を最新の状態に保つ

[English work record](task.md#2026-10-23-keep-automatic-recovery-current-after-the-worker-is-replaced)

対象: [Issue #224](https://github.com/kkamegawa/vsextensionforcodex/issues/224)。設計は `doc/connection-history-recovery-design.md` の「A previous bridge's late signal cannot retire the replacement bridge.」の次の段落と、日本語版の対応する段落です。

## 原因

- 置き換え後の Worker は、owner と connection の世代番号を 0 から数え直し、新しい状態パーティションを使います。
- そのため、置き換え後の Worker が返す Ready 状態は、旧 Worker のより大きい owner 世代と比較されて破棄されていました。
- 結果として、ツールウィンドウは Degraded のまま止まり、Restart を 2 回実行するまで戻りませんでした。

## 修正内容

- 復旧で bridge を差し替えた後、旧 owner の基準を退役させます。
- 状態、メッセージ、接続先の種類と表示名は維持します。connection 世代、owner 世代、状態パーティションは消去します。
- 基準を消去する前に、旧 bridge のコールバックの購読を解除します。

## 検証

- 回帰テスト `ChatViewModel_TransientRecoveryAppliesReplacementWorkerWithLowerGenerations` を追加しました。Ready への復帰、置き換え後の owner、会話一覧の同期、その後の Busy 通知の反映を確認します。修正を外すと失敗します。
- Debug のソリューションビルドは警告 0、エラー 0 です。Core 464 件成功／5 件スキップ／失敗 0、UI 490 件成功／1 件スキップ／失敗 0 でした。Release 構成のビルドとテストは実行していません。
- Codex 0.163.0 を使った Experimental Instance で、Worker の子プロセス `codex app-server` を終了させました。新しい Worker プロセスで約 4 秒後に自動で Ready に戻りました。ヘッダーに Connection restored が表示され、会話履歴の一覧も読み込まれました。診断ログに古い世代による拒否はありませんでした。
