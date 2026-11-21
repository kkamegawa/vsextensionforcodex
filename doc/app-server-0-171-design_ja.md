# Codex App Server 0.171 対応設計

[English](app-server-0-171-design.md)

[ADR-024](adr/ADR-024-codex-cli-0-169-minimum_ja.md) が最低対応・ターゲット・固定版を定める。[0.169 の機能設計](app-server-0-169-design_ja.md) は対応バージョンで共通の Phase 4 の動作を定める。改定した[実装計画](https://github.com/kkamegawa/vsextensionforcodex/wiki/codex-cli-0-170-support_ja)に機能変更の追跡 Issue を記載する。

## App-server 契約

| 契約変更 | 拡張の動作 |
| --- | --- |
| 0.171.0 stable の `modelProvider/default/set` | 0.171 で `providerId` を指定し、0.169 と 0.170 では承認済みの `config/value/write` を使う。利用者の操作ごとに書き込みは1回とする。[#302](https://github.com/kkamegawa/vsextensionforcodex/issues/302) |
| 0.171.0 experimental の `thread/search/stream` | 途中結果とキャンセルを検索要求と接続に紐付け、最終応答で確定したページに置き換える。以前の版で実験 API を有効にした場合は一括検索を使い、stable ではタイトル検索を使う。[#304](https://github.com/kkamegawa/vsextensionforcodex/issues/304) |
| 0.170.0 の回答表示 v1 | アシスタントメッセージの本文を項目完了まで保留し、待機中は進行状態を表示する。表示・コピー・エクスポートの前に表示指示を適用する。モデレーションが明示され、表示指示が欠損または未対応なら本文を伏せる。中断時や履歴の復元には確定した履歴を使う。[#328](https://github.com/kkamegawa/vsextensionforcodex/issues/328) |
| 0.171.0 の MCP 登録スコープ | 対応する操作をスレッド、サーバー名、登録環境で指定する。宛先が曖昧な操作は抑止する。[#329](https://github.com/kkamegawa/vsextensionforcodex/issues/329) |
| 0.171.0 のアカウント更新理由と終端エラー | `workspaceRoutingReady` では現在の所有者を確認して更新する。理由が欠損・不明、または所有者が異なる場合は通常の無効化を行う。`bioPolicy` は再試行しない拒否として表示する。[#305](https://github.com/kkamegawa/vsextensionforcodex/issues/305) |
| Enterprise MCP と OTLP | 既存の CLI サインインとテレメトリ設定を使う。0.171 CLI は更新トークンを発行しない IdP とローカル管理下の OTLP ヘルパーに対応する。対応上の制限と、Enterprise 環境での受け入れ確認を免除することを記録する。[#330](https://github.com/kkamegawa/vsextensionforcodex/issues/330) |

Worker 契約の変更とパーサーのテストは該当する機能 Issue で扱う。ベースライン移行では現在利用しているメソッドを3つの固定版で使う。manifest は生成した両スキーマの構造差を記録する。

## 検証済みスキーマ差分

公式 Windows x64 CLI 配布物を使ってスキーマを生成し、構造を比較した。最低境界の 0.169.0 は stable と experimental の両スキーマで 0.169.1 と構造的に同等である。そのため 0.169.0 は最低版のスモークと同等性の確認に使い、独立した回帰対象版としては扱わない。

| 比較 | stable 変更ファイル数 | experimental 変更ファイル数 | Worker の判断 |
| --- | ---: | ---: | --- |
| 0.169.1 → 0.170.0 | 18 | 23 | 0.170 以降では版付きの `AgentMessageDisplay` v1 の表示判断を採用し、0.169 のストリーミング動作は維持する。本文は完了まで保留し、モデレーション対象に認識可能な表示指示がなければ fail closed とする。（#328） |
| 0.170.0 → 0.171.0 | 35 | 50 | 型付き既定プロバイダー変更、ストリーミング検索とキャンセル、環境スコープ付き MCP 登録 ID、Enterprise MCP・管理 OTLP の新動作は、対応する版とネゴシエート済み capability でのみ使う。（#302、#304、#329、#330） |
| 0.169.1 → 0.171.0 | 35 | 51 | 統合差分は上記の機能別バージョン・capability gate で扱う。無関係なスキーマ追加によって、Worker が未使用メソッドを呼び出したり解釈したりすることはない。 |

0.171 スキーマでは `ModelProviderDefaultSetParams`、`ThreadSearchPartialNotification`、`ThreadSearchCancelParams`/`Response`、`ThreadRealtimeListParams`/`Response` が追加される。MCP 操作には登録スコープとして `environmentId` も含まれる。Worker はこれらのフィールドを該当機能が使う場合だけ引き渡す。MCP ID が欠落または曖昧な場合、名前だけの経路へフォールバックせず、影響する操作を無効にする。`workspaceRoutingReady` は所有者を確認してから更新するシグナル、`bioPolicy` は終端拒否として扱う。（#305、#329）

## 検証済みスキーマ差分

公式 Windows x64 CLI 配布物を使ってスキーマを生成し、構造を比較した。最低境界の 0.169.0 は stable と experimental の両スキーマで 0.169.1 と構造的に同等である。そのため 0.169.0 は最低版のスモークと同等性の確認に使い、独立した回帰対象版としては扱わない。

| 比較 | stable 変更ファイル数 | experimental 変更ファイル数 | Worker の判断 |
| --- | ---: | ---: | --- |
| 0.169.1 → 0.170.0 | 18 | 23 | 0.170 以降では版付きの AgentMessageDisplay v1 の表示判断を採用し、0.169 のストリーミング動作は維持する。本文は完了まで保留し、モデレーション対象に認識可能な表示指示がなければ fail closed とする。（#328） |
| 0.170.0 → 0.171.0 | 35 | 50 | 型付き既定プロバイダー変更、ストリーミング検索とキャンセル、環境スコープ付き MCP 登録 ID、Enterprise MCP・管理 OTLP の新動作は、対応する版とネゴシエート済み capability でのみ使う。（#302、#304、#329、#330） |
| 0.169.1 → 0.171.0 | 35 | 51 | 統合差分は上記の機能別バージョン・capability gate で扱う。無関係なスキーマ追加によって、Worker が未使用メソッドを呼び出したり解釈したりすることはない。 |

0.171 スキーマでは ModelProviderDefaultSetParams、ThreadSearchPartialNotification、ThreadSearchCancelParams/Response、ThreadRealtimeListParams/Response が追加される。MCP 操作には登録スコープとして environmentId も含まれる。Worker はこれらのフィールドを該当機能が使う場合だけ引き渡す。MCP ID が欠落または曖昧な場合、名前だけの経路へフォールバックせず、影響する操作を無効にする。workspaceRoutingReady は所有者を確認してから更新するシグナル、bioPolicy は終端拒否として扱う。（#305、#329）

## 受け入れ確認

0.171.0 でビルドとスキーマ生成を行う。manifest を検証し、stable と experimental の回帰スキーマを比較し、固定した各 CLI で利用メソッドを確認する。最低版と各固定版の実 CLI 初期化、および `initialized` 前に 0.168.0 が拒否されることを確認する。ローカル・リモート接続判定、プレリリースとビルドメタデータの境界、未検証の新版の案内を確認する。[#331](https://github.com/kkamegawa/vsextensionforcodex/issues/331) で統合受け入れと Visual Studio 2026 のライト・ダークテーマでの UI 確認を追跡する。
