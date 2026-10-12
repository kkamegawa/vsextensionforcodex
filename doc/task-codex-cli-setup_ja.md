# Codex CLI 設定・利用ガイドの作業記録 — 2026-10-12

[English](task.md#2026-10-12-codex-cli-setup-and-usage-documentation)

## 承認済み範囲と追跡

ユーザーが承認した範囲は文書化です。[既存の App Server 更新／Issue #149](task.md#2026-09-13-codex-app-server-update-and-remote-connection-approved-plan)を追跡先として再利用しました。Wiki の日英計画と Home 差分を `artifacts/codex-cli-docs/wiki` に準備・検証しましたが、公開しないというユーザーの指示に従い、ローカル原稿のまま残しています。

## 完了した内容

- [日本語ガイド](codex-cli-setup_ja.md)と[英語ガイド](codex-cli-setup.md)を作成しました。完全パッケージ導入、CODEX_PATH、認証、Codex ホーム、設定の役割、初回利用、権限、Connection target の全項目、Windows／macOS／Linux の認証付きサーバー起動例、トークン、パス対応付け、復旧、診断を説明しています。
- README 両言語からリンクし、同じ実行ファイルで確認・ログインする手順と、現行の自動復旧説明に整合させました。
- ソースレビューに基づき、選択の永続化、新規プロファイルの無効状態、適用中プロファイル削除後の再接続条件を明記しました。固定版 0.159.1 の公開ヘルプで起動・認証オプションを確認しました。

## 検証

- PowerShell 例5個の構文解析、bash 例1個の bash -n、TOML 例の tomllib 解析：成功。導入・認証・リスナー起動例自体は実行していません。
- 日英コードブロックの一致、見出し・表の構成対応、ガイドのローカルリンク・目次アンカー、UTF-8 BOM／CRLF、空白検査：成功。
- Wiki のページルート・分類表・日付検証、git diff --check：成功。検証概要は `artifacts/codex-cli-docs/validation.json` に残しました。
- アプリケーションコード・パッケージの変更はありません。この文書変更ではビルド、プロバイダーへの実ログイン、実リモート TLS 接続、Experimental Instance の受け入れ確認は実施していません。

## 2026-10-12: 同一 PC での実リモート接続検証

- 依頼に従い、Visual Studio Experimental Instance の実画面を操作して検証しました。公式の完全パッケージ 0.159.1 の SHA-256 を照合し、既存の ChatGPT ログインを利用してテスト専用ワークスペースと一時トークンでループバックサーバーを起動しました。
- UI のヘルスチェックは HTTP 200。直接の WebSocket 検証ではトークンなし／誤りが HTTP 401、有効なトークンで接続・initialize が成功。Worker の診断も認証・初期化・アカウント取得後の Ready を確認しました。
- Local からの切り替えと新規 IDE 起動の両方で、UI が Connecting / Checking account のままになり、送信と再接続が無効になる不具合を再現しました。トークン読取後に所有者の識別情報が変わる一方で世代が進まないため、成功した状態を UI が拒否します。受け入れテストは不合格で、チャット、パス対応付け、再接続の成功は未検証です。
- `artifacts/codex-cli-docs/remote-test` に画面、機密を除いた診断抜粋、接続プローブ結果を保存し、日英ガイドに実測結果を反映しました。アプリケーションコードは変更していません。別 PC／TLS、新規プロバイダーログイン、Wiki 公開は実施していません。
- 元の拡張設定とワークスペースを復元し、テスト用サーバーを停止して一時トークン2ファイルを削除しました。

## 2026-10-12: リモート起動時の所有者確定を修正

- 承認済み修正案に従い、[Issue #152](https://github.com/kkamegawa/vsextensionforcodex/issues/152) を再利用しました。日英計画原稿は `artifacts/codex-cli-docs/remote-fix` に保持し、Wiki は公開していません。
- 日英設計を更新しました。認証成功後に資格情報に結び付いたパーティションが変わる場合だけ所有者世代を進め、session／target／履歴／世代カウンターをまとめて更新します。接続世代と UI の厳格な所有者検証を維持し、初期化・アカウント通知より前に確定所有者の Connecting を通知します。認証前後の失敗もそれぞれの所有者に所属させます。
- Worker の通知順序・世代・アカウント通知・旧要求の拒否・認証／初期化失敗の回帰を追加し、対象48件が成功しました。UI では Ready とアカウントの受理、旧所有者と同世代の異なるパーティションの拒否を追加しました。Release 全体は Core 419件成功／5件スキップ、UI 401件成功／1件スキップです。スキップは既存の環境依存リンク検証です。
- Debug／Release ビルドは警告・エラーともゼロ。0.159.1 の両スキーマキャッシュと stable／experimental 契約面の 0.155.1 回帰比較、VSIX／配置済み Worker のハッシュ一致、raw XAML 埋め込み、UTF-8 BOM／CRLF、空白検査が成功しました。
- SDK 標準 F5 の Release 配置で実画面を検証しました。保存済み有効プロファイル、両ヘルスルート HTTP 200、Local→Remote の Ready／Signed in、指定文字列のモデル応答、ツールによる試験マーカー読取、不正トークンの Degraded、正しいトークンに戻した後の明示的 Reconnect による Ready／Signed in が成功しました。外部サーバーは同じプロセスのままでした。画面証跡は `artifacts/codex-cli-docs/remote-fix` に保存しました。
- リモート選択済みの新規 IDE 起動は、当初は画面操作ツールが `foreground window did not report a process id` で失敗しましたが、同日に再試験しました。保存済みの有効プロファイルを選択した状態で Canary Experimental Instance を新規起動し、Ready、Codex 0.159.1、Signed in、Connected to: Loopback test の表示を確認しました。Worker が外部ループバックリスナーに接続し、ローカル CLI 子プロセスは起動していません。配置済み Worker のハッシュは Release 出力と一致しました。IDE はクリックのみの操作権限のため、入力欄からの送信は再試験していません。証跡と後片付け結果（設定のバイト単位復元、テスト IDE／サーバー停止、一時トークン削除）は `artifacts/codex-cli-docs/remote-fresh` に保存しました。再接続成功後に認証失敗時の補助バナーが残る点は、今回の Connecting 不具合とは分けて記録しました。

- 後片付けとして、今回起動したテスト IDE とサーバーを停止し、元の拡張設定をバイト単位で復元して一時トークン2ファイルを削除しました。ローカルの検証証跡と、レビュー用にソースを開いた開発 IDE は保持しました。
