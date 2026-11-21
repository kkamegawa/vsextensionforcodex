# ADR-024: Codex CLI の最低対応を 0.169.0、ターゲットを 0.171.0 に変更

[English](ADR-024-codex-cli-0-169-minimum.md)

- 日付: 2026-11-20
- 状態: 承認済み
- 追跡: [#326](https://github.com/kkamegawa/vsextensionforcodex/issues/326)、[#327](https://github.com/kkamegawa/vsextensionforcodex/issues/327)、[#331](https://github.com/kkamegawa/vsextensionforcodex/issues/331)
- 現行のバージョン方針について [ADR-023](ADR-023-codex-cli-0-167-minimum_ja.md) を置き換える

## 決定

| 用途 | CLI バージョン |
| --- | --- |
| ローカル・リモート app-server の接続下限 | 0.169.0 |
| 回帰確認用の固定契約 | 0.169.1 と 0.170.0 |
| ターゲットと推奨版 | 0.171.0 |

バージョン判定は `initialize` の後、`initialized` の前に行う。認識できないバージョンと 0.169.0 未満を拒否する。Semantic Versioning の優先順位を使い、プレリリースの順序を適用し、ビルドメタデータを比較から除く。0.171.0 より新しいバージョンの接続を受け付け、従来どおり未検証の新版であることを案内する。

0.169 系は 0.169.1 を固定し、生成した両方のスキーマを 0.169.0 と比較して同等性を確認する。0.169.0 は最低版のスモーク確認にも使う。0.168.0 は接続拒否の確認に使う。

最低版で利用できる機能をベースラインに統合する。対応バージョンで必要な機能の有効化条件、任意フィールドの扱い、プロトコルのフォールバックを維持する。承認済みの機能実装がより新しい app-server 契約を利用する時点で、そのバージョン条件を追加する。

## 影響

新規インストールでは 0.171.0 の公式フルパッケージを使う。0.167 系と 0.168 系の利用者は、接続する前に CLI を更新する。manifest は配布物の固定、stable と experimental の利用メソッド表、回帰確認用のスキーマ差分を管理する。ビルド時のスキーマ検証は 0.171.0 の stable キャッシュを使う。

[Codex CLI 0.171.0](https://github.com/openai/codex/releases/tag/rust-v0.171.0)・[0.169.0 から 0.169.1 の差分](https://github.com/openai/codex/compare/rust-v0.169.0...rust-v0.169.1)
