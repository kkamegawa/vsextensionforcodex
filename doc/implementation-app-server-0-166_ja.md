# Codex App Server 0.166.0 実装記録

追跡 Issue: [#241](https://github.com/kkamegawa/vsextensionforcodex/issues/241)。作業チェックリスト: [英語](task-app-server-0-166.md) · [日本語](task-app-server-0-166_ja.md)。英語版の実装記録: [Implementation record](implementation-app-server-0-166.md)。

## Contract と CLI パッケージ

`app-server-contract.json` を schema v4 にし、target を 0.166.0、回帰対象を 0.165.0、0.164.0、0.163.0、0.162.1、0.161.0 としました。各リリースには Windows 完全パッケージの asset 名と package / executable の SHA-256 を固定しています。インストーラーは完全パッケージを展開し、`codex-package.json` の entrypoint を読み取って各ハッシュを検証します。固定された補助ファイル一覧には依存しません。manifest validator は成功しました。

6 バージョンすべての公式完全パッケージを取得・検証し、実 CLI の初期化 smoke test に成功しました。各バージョンの stable / experimental schema を生成し、0.166.0 との回帰比較 10 件、method surface 検証 12 件がすべて成功しました。ホストへ採用していないバージョン固有の schema パスは `knownDifferences` に記録しています。

## 互換性とセッション動作

feature tier 0.165 / 0.166 は、対象 CLI バージョンを各機能の導入バージョンと比較して選択します。Worker contract を v27 にし、`ReviewDelivery` を `ReviewPlacement` に置き換え、review 要求から `detached` を削除しました。

item の `startedAtMs` と `completedAtMs` は、値がない場合に entry の timestamp を使います。provider の JSON-RPC エラーは構造化 code を保ち、通知と履歴に含まれる `policyCapacityUnavailable` も扱います。未知の code はサイズを制限し、秘密情報を除去して通常のエラーとして表示します。provider が `thread/resume` を拒否した場合も `thread/read` で読み取り専用の履歴を開けます。active thread では interrupt と goal の pause / clear を引き続き実行できます。

固定した 0.166.0 CLI で thread subscription を解除してアイドル状態にすると、約 60.3 秒後に `thread/closed` が届き、smoke test で同じ thread ID の再開に成功しました。テスト用に作成したローカル thread は終了後に削除しました。

## 検証結果

- Debug / Release のソリューションビルドが警告 0 件、エラー 0 件で完了。
- Debug / Release の Core / UI テスト証跡が、再試行不要ですべて成功。
- Release VSIX 検査に成功。埋め込み Worker contract v27 も確認。
- Contract manifest validator が成功。6 バージョンすべての stable / experimental schema 生成、回帰比較、method surface 検証が成功。
- 6 バージョンすべての完全パッケージ導入と実 CLI `initialize` が成功。0.166.0 のアイドルアンロードと再開も成功。
- `git diff --check` が成功。

証跡は `D:\temp\issue241-verify-2\` にあります。6 バージョンの固定 CLI 導入ログ、initialize ログ、schema 出力、Debug / Release テスト結果、VSIX 検査レポートを含みます。最初の統合 release-validation は、最終 manifest と protocol project の version 修正前に実行したため、最終証跡には含めていません。関連する各検証は修正後に個別に再実行しています。

## Visual acceptance の制約

変更対象に Visual Studio の UI 状態が含まれます。このセッションでは Computer Use で Visual Studio を確認しようとしましたが、`sky.list_apps()` が 2 回タイムアウトしました。skill の復旧手順に従い再試行後に停止したため、スクリーンショットによる表示確認はできていません。自動 UI テストは成功していますが、メンテナーの実画面確認は保留です。
