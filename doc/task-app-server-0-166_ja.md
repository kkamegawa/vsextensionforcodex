# Codex App Server 0.166.0 作業記録

追跡 Issue: [#241](https://github.com/kkamegawa/vsextensionforcodex/issues/241)。詳細な実装・検証記録: [英語](implementation-app-server-0-166.md) · [日本語](implementation-app-server-0-166_ja.md)。英語版の作業記録: [Task record](task-app-server-0-166.md)。

## 完了

- [x] Contract manifest を schema v4 に更新し、target 0.166.0、回帰確認用 0.165.0、0.164.0、0.163.0、0.162.1、0.161.0、Windows 完全パッケージの asset 名とハッシュを記録。
- [x] 6 バージョンすべてで完全パッケージを確認。インストール処理は `codex-package.json` の entrypoint を読み、パッケージと実行ファイルのハッシュを検証。
- [x] 6 バージョンすべての stable / experimental schema を生成し、各回帰バージョンと 0.166.0 を比較。method surface を検証し、未採用差分を `knownDifferences` に記録。
- [x] feature tier 0.165 / 0.166 を追加し、各機能の導入バージョンとの境界比較に変更。Worker contract を v27 に上げ、`ReviewDelivery` を `ReviewPlacement` に置き換え、`detached` を廃止。
- [x] item の timestamp を entry の値で補完。provider の構造化エラー、capacity policy 通知と履歴、サイズ制限と秘密情報除去を行う未知エラー、resume 拒否時の read-only 履歴を実装。
- [x] 0.166.0 CLI のアイドルアンロードを検証。約 60 秒後の `thread/closed` と同じ thread の再開を確認。
- [x] contract / 互換性テスト、Debug / Release の Core・UI テスト、警告・エラー 0 件の Debug / Release ビルド、Release VSIX 検査に成功。
- [x] 0.166.0、0.165.0、および古い回帰対象バージョンで実 CLI の `initialize` に成功。

## 保留中のメンテナー受け入れ確認

- [ ] Visual Studio Experimental Instance で変更後の画面を表示してスクリーンショットを確認する。Computer Use のアプリ一覧取得が許可された再試行でもタイムアウトし、このセッションではスクリーンショットを得られなかった。自動 UI テストは成功したが、実画面の確認の代わりにはならない。

証跡: `D:\temp\issue241-verify-2\`。
