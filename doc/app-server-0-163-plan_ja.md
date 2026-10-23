# Codex App Server 0.163.0 実装計画

追跡先: Issue #217、子 Issue #218〜#223。2026-10-23 承認。

## 成果

app-server-0-163-design_ja.md の設計を実装し、0.162.1 と 0.159.1 を独立した回帰基準として維持します。experimental API は明示 opt-in のままにします。

## 作業順序

| Phase | 作業 | 追跡先 |
| --- | --- | --- |
| 1. 契約と Protocol | manifest を schema v2 にし、3版を固定します。stable／experimental schema を版別に生成し、複数基準比較と検証に対応します。stdio／WebSocket の JSON-RPC error.data を上限付きで保持します。 | #218、#219 |
| 2. Worker と履歴取得 | Worker 契約を v25 にします。既読状態の型付き解析、更新、競合回復、通知、ローカル MXC 起動条件、owner／要求 generation 対応の一覧取得を実装します。 | #220、#221 |
| 3. 履歴 UI | observable な履歴行、未読マーカー、既読／未読の明示操作、stable server 向け説明、選択維持、古い結果の遮断、theme／accessibility 対応を実装します。 | #221 |
| 4. MXC 設定と診断 | legacy backend 診断の誤りを修正し、ローカル MXC 使用禁止と windows.allow_mxc 診断を追加します。公開されないポリシーやコマンド単位の backend は推測しません。 | #222 |
| 5. 統合検証と記録 | 対象版と両回帰版、Fake と実 App Server、競合・復旧、全 test suite と build、配布物／schema 整合性、Experimental Instance UI を検証します。証跡を implementation と task に記録します。 | #223 |

## 受け入れ確認

- 契約 script は 0.163.0、0.162.1、0.159.1 の stable／experimental を個別検証し、回帰差分を版ごとに出力します。schema cache と証跡形式は形式変更がない限り据え置きます。
- Protocol は両 transport の有効な error.data を保持します。過大・不正な競合データと無関係な -32600 を既読競合として扱いません。
- 既読、会話開始から未読、turn から未読、対象外、利用不可、不正を区別します。experimental 無効または旧サーバーでは既読操作を表示しません。
- 状態変更、選択 A→B→A、owner 変更、通知、競合、切断、古い要求により他の行や owner の状態が更新されません。表示中の会話も新しい結果後は未読となり、明示操作まで維持されます。
- 一覧取得失敗時に行、選択、cursor を保持します。別 owner の結果を破棄し、部分一覧から会話の不在を確定しません。
- legacy 許可一覧に mxc がないことを MXC 禁止と誤診しません。MXC 使用禁止、明示指定、管理ポリシー、CLI 旧版／不明の組合せを検証します。使用禁止はローカルだけです。
- MCP は既存の serverRequest/resolved 経路を Fake で検証します。質問破棄イベントの受信を保証条件にしません。
- steer／turn 完了競合と cold resume の権限復元を検証します。
- Core、UI、Fake の各 test suite と Debug／Release build が警告ゼロで成功します。
- Experimental Instance の各 theme、狭い幅、キーボード操作、読み上げを screenshot で確認します。
- 英語／日本語の design、plan、implementation、task 記録を UTF-8 BOM／CRLF で保存します。Issue checklist は検証済みシナリオだけを完了扱いにします。

## 検証証跡

各 Phase の終了時に、正確な command、configuration、件数、skip、生成対象版、asset hash、screenshot path を doc/task.md と doc/implementation.md に記録します。必須検証が blocked または skipped の場合、その Phase を完了扱いにしません。