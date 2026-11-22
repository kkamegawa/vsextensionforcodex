# Codex CLI 0.169〜0.171 互換性検証

[English](task-app-server-0-171.md) · [追跡 Issue #331](https://github.com/kkamegawa/vsextensionforcodex/issues/331)

## 実 CLI の初期化

2026-11-23 に PR #335 の `004f733` でビルドした Release Worker から、公開されている Windows x64 CLI をローカル stdio と認証付きループバック WebSocket で初期化しました。各実行ファイルの SHA-256 は GitHub リリースの公開アセットと一致しました。テストごとに Codex ホームと作業ディレクトリを分離し、送信した JSON-RPC メソッド名と `initialized` の有無を記録しました。

| CLI | ローカル stdio | 認証付き WebSocket |
| --- | --- | --- |
| 0.167.2 | `initialized` 前に拒否 | `initialized` 前に拒否 |
| 0.168.0 | `initialized` 前に拒否 | `initialized` 前に拒否 |
| 0.169.0 | 受理 | 受理 |
| 0.169.1 | 受理 | 受理 |
| 0.170.0 | 受理 | 受理 |
| 0.171.0 | 受理 | 受理 |

受理した接続は `initialized` を 1 回送信しました。拒否した接続は `initialize` のみを送信しました。ローカルとリモートのバージョン境界、模擬プレリリース版の優先順位、ビルドメタデータ、検証対象より新しい版の案内、アカウント間の分離を含む Core の対象テスト 35 件が通過しました。

## スキーマ契約

0.169.0、0.169.1、0.170.0、0.171.0 の stable と experimental の両スキーマを生成できました。0.169.0 と 0.169.1 のスキーマは構造的に同等でした。2 つの回帰対象版から 0.171.0 への構造差分は定義と一致し、各バージョンとスキーマ種別の全 8 組み合わせで使用メソッドの確認に合格しました。

[スキーマ生成](../scripts/generate-schemas.ps1)、[スキーマ比較](../scripts/compare-schemas.ps1)、[メソッド検証](../scripts/verify-contract-surface.ps1)を使用しました。

## レビュー修正の統合時点

`004f733` での統合 Release ビルドは警告・エラーともに 0 件でした。Worker 契約 v29 の VSIX 検査に合格しました。ローカルの UI テストは 648 件が通過し、ファイルシステムのリンクに依存する 3 件がスキップされました。Core の初回実行では 707 件が通過、リンクに依存する 7 件がスキップされ、アカウント切り替えに関する 2 件が失敗しました。2 件とも個別の再実行で通過しました。Ready 通知のアサーションはレビュー修正前の `9ff68d5` でも失敗したため、新しいアカウントの Ready 通知を待ってから件数を検証するようにしました。同じレビュー統合コミットの [CI 実行 395](https://github.com/kkamegawa/vsextensionforcodex/actions/runs/45040239905) は成功しました。

## Visual Studio での受け入れ

Visual Studio 2026 Experimental Instance の Light・Dark テーマでのスクリーンショットは未取得です。Enterprise 環境での受け入れ確認は免除されています。
