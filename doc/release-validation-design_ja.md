# 統合リリース検証の設計

状態：2026-10-09 承認済み。後続実装の基準とする。基準：`b44e856`、Worker contract v21、Codex CLI 0.159.1、回帰 fixture 0.155.1。

## 目的

App Server 更新全体について再現可能な証跡を定義する。既存の契約、復旧、所有者分離、対話、パス、配布物のテストを再利用し、公開 CI では実行できない外部サービス検証とローカル受入を分ける。

## シナリオ区分と判定

| 区分 | 対象 | 完了条件 |
|---|---|---|
| Local-required | 固定 schema、Fake App Server、ローカルテスト、ローカル transport／TLS／token rotation 模擬、パス、配布物、Experimental Instance、同一マシン上の2インスタンス | 必須シナリオすべて合格。必須項目の skip／blocked は未完了。 |
| External | 認証を伴う固定 CLI 通信、実 MCP OAuth の失効・再認証、実 endpoint の証明書・token rotation | 合格、または影響と証跡を記録し、明示的に受け入れた blocked。 |

各シナリオを `passed`、`failed`、`blocked`、`not-run`、`flaky` で記録する。`failed`、`flaky`、`not-run` が残れば準備完了としない。External の `blocked` は、対象、理由、未検証リスク、代替証拠、承認者、日付、承認記録を残した場合に限り受け入れられる。Local-required の `blocked` を External に振り替えない。

PR #170 の結果は過去の基準証跡として扱い、最終候補の正確な revision で改めて結果を取る。

## 自動検証

Windows／PowerShell 7 の統合スクリプトで、固定 CLI の hash 検証、0.155.1／0.159.1 の stable／experimental schema 生成、schema 比較、used-method／cache 検査、Debug／Release build、Core／UI suite、VSIX 検査を実行する。`CODEX_PATH` は build/schema など必要な工程だけに渡し、テストプロセスには設定しない。

VSIX 内の manifest、Worker payload、contract version、DLL、埋め込み XAML を対応する build 成果物と照合する。結果 manifest に commit、環境／ツール版、CLI 版と hash、シナリオ区分／状態、コマンド、終了コード、件数、skip 理由、再試行結果、成果物 hash、受け入れた制約を記録する。

CI と Release workflow は初回結果を保存し、失敗したテストケースだけを最大1回再試行できる。再試行成功も `flaky` として gate を失敗させる。データ駆動の特定ケースを分離できない場合、または test host が異常終了した場合、suite 全体を再実行せず、失敗と診断情報を保存する。秘密を除去した TRX、schema report、hash、manifest を `if: always()` で保存する。認証情報や未加工の通信ログを公開成果物にしない。

symlink／junction は実際のテスト実行 ID で専用一時領域への作成能力を確認する。必須 link を作成できない場合は理由付きで setup を失敗させる。skip はテスト名・理由別に記録する。PR #170 の6件は対応する TRX なしに理由を断定しない。

## External シナリオ

実認証 CLI、実 MCP OAuth 失効／再認証、実 remote TLS／証明書／token rotation は公開 CI の外で手動実行する。専用 workspace を使い、endpoint は実行時に渡す。保存するのは秘密を除去した要約と manifest のみとし、生 credential／token／通信記録を残さない。Fake、loopback、TLS 模擬は Local-required のままとし、External の代替にしない。

## UI とインスタンス受入

SDK 管理の F5／Experimental Instance 手順と既存の重複 identity 防止手順を使う。Windows 上で Light／Dark／High Contrast、狭幅、接続／復旧／履歴／下書き、質問／権限／MCP、成果物、shell 確認、Windows setup を確認する。キーボード操作、focus 順、accessible name、Narrator／Accessibility Insights の通知、長い履歴の操作も検証する。同じマシン上で Experimental Instance を2つ起動し、会話、下書き、catalog、認証、承認が混ざらないことを確認する。

スクリーンショットにはシナリオ ID、環境、期待結果、合否を紐づける。キーボード／読み上げ確認には手順と観測記録も必要とする。オフスクリーン WPF 描画は補助証跡のみ。Experimental Instance が使えなくても画面受入は Local-required の未完了として残す。

## プラットフォーム境界

完全なリリース検証は Windows／PowerShell 7 で行う。固定 Codex CLI、schema 生成、WPF UI test、VSIX、Experimental Instance は Windows 対象である。Core／Worker は `net8.0` だが、他 OS では platform-specific case が inconclusive になることがあり、Windows 受入の代替にはならない。Bash 例は移植可能な command、または Windows 上の Bash 環境で実行する手順に限り、Linux で固定 CLI や WPF UI suite が実行できるように記載しない。

## 対象外

公開 RPC／DTO は追加しない。この Phase は証跡収集と release 判定の設計を定める。統合 script、workflow 変更、不足 test の実装は後続作業とする。
