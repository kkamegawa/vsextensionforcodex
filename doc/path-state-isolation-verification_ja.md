# Issue #152 の検証

[English](path-state-isolation-verification.md)

実装は承認済み Wiki Phase 3、ADR-013、[パスと状態の設計](path-state-isolation-design_ja.md)
に従っています。作業ブランチは `codex/152-path-state-isolation` です。

## 2026-10-03 の検証結果

| 検証 | 結果 |
|---|---|
| Debug / Release の全体ビルドと VSIX 生成 | 警告・エラーともに 0 件 |
| Release Core 全件テスト | 成功 311 件、スキップ 5 件、失敗 0 件 |
| Release UI 全件テスト | 成功 317 件、スキップ 1 件、失敗 0 件 |
| Windows ジャンクションの実動作 | ルート内の既存・未作成ファイルを許可、ルート外を拒否 |
| CLI 0.159.1 stable / experimental 契約 | 成功 |
| schema-cache 契約 / 実際の initialize | 成功 |
| VSIX の契約と同梱 DLL | v18、Worker・Extension・Contracts 両コピーが Release 出力と一致 |
| 埋め込み raw XAML | ソースと一致 |
| VSIX 識別子 | 既存 ID、publisher `kkamegawa`、Preview 属性を維持 |
| テキスト形式 / 差分 | UTF-8 BOM・CRLF、`git diff --check` 成功 |

スキップ 6 件は、この環境でシンボリックリンクを作成できないためです。
実ジャンクションの検証で物理包含の確認を補いましたが、スキップしたリンク・循環ケースの
実行に代わるものではありません。

Release VSIX SHA-256: `9F35C339BC4A1566553D9BFDBD5AAC28299F8BAD8DD08B068214FB04F867F73A`。

## 残る受け入れ確認

Experimental Instance で接続切り替え、選択状態の消去、添付の拒否、リモートスキル選択を
スクリーンショットで確認する作業は未実施です。Visual Studio の探索結果にインストール済み
インスタンスはなく、選択した `orca` 実行ファイルも認識されませんでした。
computer-use スキルは、選択した実行ファイルが動かない場合にその操作を停止するよう定めています。
単体テストとパッケージ検査だけで、実際の表示の受け入れ確認を済ませたとは扱いません。

## フォローアップの検証（2026-10-04）

ブランチ: `fix/152-path-state-followups`。範囲: 所有者が開始したアカウント変更時の自動再接続、
添付のターン全体拒否、スレッド作業ディレクトリのマッピング、Worker の rate limit フィルター、
不足していた remote モードと junction のテスト。

| 確認 | 結果 |
|---|---|
| Debug / Release のソリューションビルド | 警告・エラー 0 |
| Release の Core 全テスト | 合格 327、スキップ 5、失敗 0 |
| Release の UI 全テスト | 合格 318、スキップ 1、失敗 0 |
| junction の自動テスト | ルート内の既存・将来のファイルを受理し、外へ出る junction を拒否 |
| スキーマキャッシュ契約・実 initialize（CLI 0.159.1） | 合格 |
| テキスト形式・差分 | UTF-8 BOM、CRLF、`git diff --check` 合格 |

スキップ 5 件は symlink 作成権限が必要なケースです。新しい junction テストはこの権限なしで実行できます。

## 未完了の受け入れ（フォローアップ）

サインイン／サインアウト後の再接続、リモートプロファイルの切り替え、選択状態のクリア、
添付の拒否、リモートスキルの選択の Experimental Instance スクリーンショットは未確認です。
Visual Studio 2026 Insiders はインストール済みですが、このセッションにはデスクトップ操作の手段がありません。
また、サインインのシナリオにはメンテナー自身の ChatGPT アカウントとリモート app-server が必要です。
