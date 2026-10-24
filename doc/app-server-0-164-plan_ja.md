# Codex App Server 0.164.0 実装計画

[English](app-server-0-164-plan.md)

追跡: [Issue #226](https://github.com/kkamegawa/vsextensionforcodex/issues/226)。2026-10-25 承認。

## 目的

[app-server-0-164-design_ja.md](app-server-0-164-design_ja.md) の設計を、[ADR-019](adr/ADR-019-app-server-0-164_ja.md) と 2026-10-25 付の ADR-011 改定に沿って実装する。主な内容は次のとおり。

- 対応バージョンを次のように定める。
  - 契約基準: 0.164.0
  - 最低対応: 0.161.0
  - 回帰対象: 0.163.0／0.162.1／0.161.0
- 0.159.1 と、バージョンを判定できないサーバーへの接続を拒否する。
- 新機能は機能段階に応じて有効にする。
- experimental API は明示的に有効化した場合だけ使う方針を維持する。

## 作業順序

| フェーズ | 作業 | 追跡 |
| --- | --- | --- |
| 1. 契約とバージョン | マニフェストを `minimumVersion` 付きのスキーマ v3 に上げる。0.164.0 と 0.161.0 を固定し、0.159.1 を削除する。4 バージョンの stable／experimental スキーマを生成・比較する。スクリプト、CLI 導入補助、CI、Fake App Server、セットアップ文書がマニフェストから値を読むようにする。Worker のバージョン定数と、マニフェストとの一致テストを追加する。 | [#227](https://github.com/kkamegawa/vsextensionforcodex/issues/227) |
| 2. バージョン判定と Worker | `userAgent` から Semantic Versioning でバージョンを判定し、再試行しない拒否と機能段階の算出を実装する。0.159.1 専用のフォールバックを削除する。Worker 契約を v26 に上げる。次の Worker 側の経路を追加する: プリセット（`config/read` のレイヤー、許可リスト化したキーと `expectedVersion` による `config/batchWrite`）、`feedback/save`、MCP の実行時状態と `toolsError`、生成画像の参照、段階に応じた再開時の設定反映。 | [#228](https://github.com/kkamegawa/vsextensionforcodex/issues/228) |
| 3. 入力とプリセットの UI | CLI と同じ規則の `/model` 解析と一括解決、速度のフォールバックを実装する。`/permissions` の確認中は送信待ちキューを止める。`/presets` コマンドと入力欄のボタン、プリセットパネル（編集・削除の規則、競合時の処理、置換を確認したうえで入力欄へ展開、必要な段階の案内）を追加する。 | [#229](https://github.com/kkamegawa/vsextensionforcodex/issues/229) |
| 4. 状態・画像・MCP・診断・compaction の UI | 再開時の設定反映（null の tier は通常速度として扱う）を実装する。アカウント変更時は選択を維持するか既定値に戻し、その旨を通知する。画像の参照・失敗の表示、MCP 状態の 3 区分表示、ローカルとリモートの診断 ZIP ダイアログ、compaction 失敗と旧段階での再開に関するメッセージを追加する。 | [#230](https://github.com/kkamegawa/vsextensionforcodex/issues/230) |
| 5. 統合検証と記録 | 4 バージョンについて、Fake と実際の App Server、拒否されるケース、全受け入れ条件、ビルド、VSIX 検査を確認し、Experimental Instance のスクリーンショットを撮る。証跡を実装記録と作業記録に反映する。 | [#231](https://github.com/kkamegawa/vsextensionforcodex/issues/231) |

主な対象ファイル:

- `app-server-contract.json` と契約・スキーマのスクリプト、`.github/workflows/ci.yml`、`doc/codex-cli-setup*.md`
- `src/Codex.VisualStudio.Contracts/WorkerContracts.cs`、`DailyUseContracts.cs`
- `src/Codex.VisualStudio.Worker/CodexSessionService*.cs`、`WorkerRpcService.cs`
- `src/Codex.VisualStudio.Extension/ChatViewModel.cs`、`SlashCommands.cs`、`WorkerBridge.cs`、Remote UI の XAML
- `src/Codex.AppServer.Fake/Program.cs`

再利用する既存の実装:

- バージョン判定の `ReadCodexVersion` と `IsValidCodexVersion`。比較処理は `IsCodexVersionAtLeast163` を一般化して作る。
- `/permissions` の解決処理と Full Access の確認。
- 画像の `AddServerPathPart` と `AddImageGenerationFallback`。
- 既存の `feedback/upload` の経路と、classification の検証。
- MCP の `McpAuthStatuses` と `loginId` による照合。
- 所有者スナップショットの照合（`CaptureOwnerSnapshot`／`IsCurrentOwner`）。

## 受け入れ条件

- **契約**
  - スクリプトが、0.164.0／0.163.0／0.162.1／0.161.0 の stable／experimental をそれぞれ検証し、回帰版ごとの差分をバージョン付きで出力する。
  - `minimumVersion` が欠落している場合、不一致の場合、それより低いバージョンが固定されている場合は、検証で拒否される。
  - Worker の定数がマニフェストと一致する。
  - マニフェスト、CI、Fake App Server、セットアップ文書のどこにも 0.159.1 が残っていない。
- **バージョン判定**
  - 次の場合はローカル・リモートとも拒否され、自動回復を繰り返さず、設定画面は使える状態のまま残る: 0.159.1、0.160.x、0.161.0 のプレリリース版、`0.0.0`、`userAgent` の欠落・不正。
  - 0.161.0〜0.164.0 と、それより新しいバージョンは接続できる。
  - `0.164.0-alpha` は 0.163 段階として扱われる。
- **入力**
  - `/model` の解析が CLI とすべてのケースで一致する。
  - 不正なコマンドでは設定が何も変わらない。
  - 速度のフォールバック時には通知が出る。
  - `/permissions` の確認中は、送信待ちのメッセージが送られない。
- **プリセット**
  - 入力検証の境界（8,000 バイトと 8,001 バイトを含む）が守られる。
  - project／system／session の各レイヤーがある場合や、ユーザーレイヤーがない場合でも、編集・削除の規則が成り立つ。
  - 競合時は編集内容が保持され、自動では再送されない。
  - 呼び出しても送信されない。
  - CLI が書き込んだスロットを読み込み、そのまま書き戻せる。
  - リモート接続のプリセット操作が、ローカルの設定に影響しない。
  - 0.164.0 未満の接続では、必要なバージョンが案内される。
- **状態・画像・MCP**
  - 再開時の設定は 0.164 段階でだけ反映され、null の tier は通常速度として扱われる。
  - 古い所有者に対する応答は破棄される。
  - 画像のすべての形式と失敗のケースが、通知でも履歴の再生でも正しく表示される。
  - `toolListMeta` は、Worker 契約にも UI にもログにも含まれない。
  - MCP 状態の組み合わせが設計どおりに表示される。
- **診断**
  - ローカル・リモートの両方で保存できる。
  - 既に存在するパスは拒否される。
  - ログ同梱は既定でオフになっている。
  - 切断やタイムアウトの場合は「結果未確認」と表示し、再送しない。
  - 0.164.0 未満の接続では使えない。
- **compaction**
  - Fake の失敗シナリオと、旧段階での再開エラーのシナリオが通る。
  - 実際の 0.164.0 CLI で、compaction 後に再起動して再開できる。
- **実機**: Windows 上で 0.164.0 の MXC 起動と、0.161.0 での回帰接続を確認する。
- **品質**
  - Core／UI／Fake テストと、Debug／Release ビルドが警告ゼロで完了する。
  - VSIX 検査に合格する。
  - Experimental Instance のスクリーンショットで、各テーマ、狭い幅、日本語表示、キーボード操作、スクリーンリーダー出力を判定する。
- **記録**: 英語版と日本語版の設計、計画、ADR、実装記録、作業記録が、BOM 付き UTF-8 と CRLF で保存されている。Issue のチェックリストは、検証済みのシナリオだけを反映する。

## 検証の証跡

各フェーズの後、実行したコマンド、構成、件数、スキップ、生成したリリースのバージョン、asset のハッシュ、スクリーンショットのパスを `doc/task.md` と `doc/implementation.md` に記録する。必須の確認が実施できなかった、またはスキップしたフェーズは完了扱いにしない。
