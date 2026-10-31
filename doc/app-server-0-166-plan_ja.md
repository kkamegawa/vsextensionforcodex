# Codex App Server 0.166.0 実装計画

[English](app-server-0-166-plan.md)

追跡: [Issue #240](https://github.com/kkamegawa/vsextensionforcodex/issues/240)。2026-10-31 承認。

## 成果

この計画は、[ADR-020](adr/ADR-020-app-server-0-166_ja.md)と2026-10-31のADR-011追補のもとで、[app-server-0-166-design_ja.md](app-server-0-166-design_ja.md)を実現します。

- 検証対象を0.166.0とし、最低対応を0.161.0、回帰対象を0.165.0、0.164.0、0.163.0、0.162.1、0.161.0とします。
- 各機能階層を、それぞれの導入版と比較します。
- Experimental APIは明示的に有効化する方式のままとします。
- すべての段階を単独でリリース可能にします。

## 作業順序

| 段階 | 作業 | 追跡 |
| --- | --- | --- |
| 1. 互換対応 | マニフェストをschema v4へ上げ、完全パッケージの資産とハッシュを追加します。0.162.1、0.163.0、0.165.0のパッケージを確認します。インストーラーと実機検証を完全パッケージへ移します。6版のstableとexperimentalのスキーマを比較します。0.165と0.166の階層を追加し、`GetFeatureTier` を修正します。Worker契約をv27へ上げ、`ReviewDelivery` を `ReviewPlacement` に置き換えて `Detached` を削除します。項目時刻、構造化プロバイダーエラー、`policyCapacityUnavailable` を投影します。プロバイダー拒否時に読み取り専用の履歴を開きます。60秒アンロードを検証します。 | [#241](https://github.com/kkamegawa/vsextensionforcodex/issues/241) |
| 2. Stable機能 | `/review --new-thread`、履歴範囲、質問Markdown、読み取り専用の会話テキスト、USD利用額、ネットワーク診断を追加します。 | [#242](https://github.com/kkamegawa/vsextensionforcodex/issues/242) |
| 3. Experimental機能 | 親子間を移動できる関連分岐、記憶の参照、SSOと確認ダイアログ、指紋変更を含むワークスペース切替を追加します。 | [#243](https://github.com/kkamegawa/vsextensionforcodex/issues/243) |
| 4. 統合と記録 | Fakeと実App Serverで全版を検証し、Experimental Instanceのスクリーンショットを取得し、VSIXを検査し、実装記録と作業記録を証跡付きで更新します。 | [#244](https://github.com/kkamegawa/vsextensionforcodex/issues/244) |

代表的なファイル:

- `app-server-contract.json`、`scripts/install-codex.ps1`、`scripts/validate-contract-manifest.ps1`、`scripts/verify-release.ps1`、`doc/codex-cli-setup*.md`
- `src/Codex.VisualStudio.Contracts/CodexVersionContracts.cs`、`WorkerContracts.cs`
- `src/Codex.AppServer.Protocol/TransportPolicies.cs`
- `src/Codex.VisualStudio.Worker/CodexSessionService*.cs`、`WorkerRpcService.cs`
- `src/Codex.VisualStudio.Extension/ChatViewModel.cs`、`SlashCommands.cs`、`WorkerBridge.cs`、Remote UI XAML
- `src/Codex.AppServer.Fake/Program.cs`

再利用する既存コード:

- `CodexCliVersion` のバージョン解析と順序付け。
- `JsonRpcMessages.cs` の `ErrorData` と16 KiB制限。
- 所有者スナップショットの照合: `CaptureOwnerSnapshot`、`IsCurrentOwner`。
- `ExecuteForkAsync` の分岐の流れ。選択では読み取り専用の履歴を読み込み、参加は明示操作のままです。
- 質問Markdown用の `ToSafeTextAndBlocks`。
- `ArtifactPreviewCache` の所有者分離と、`ArtifactFileActions.cs` の `OpenDocumentAsync` 経路。
- ログインURL用の既存の安全なブラウザー経路。

`StartThreadAsync` は別会話レビューに再利用しません。`ActiveThreadId` をすぐに変更し、作業フォルダーしか送らないためです。

## 実機での確認事項

次の動作は、示した段階で実CLIにより確認します。設計と異なる場合は、実装を続ける前に設計を更新します。

| 段階 | 確認事項 |
| --- | --- |
| 1 | 0.162.1、0.163.0、0.165.0に完全パッケージがあるか |
| 2 | 別会話レビューが履歴一覧でどう表示されるか(タイトルを含む) |
| 2 | `review_model` が引き継いだモデルより優先されるか |
| 2 | 0.161.0の新しい空の会話で `review/start` が動作するか |
| 3 | `thread/list.attachedToThreadId` と `sourceKinds` を併用できるか |

## 受入確認

- **契約:**
  - スクリプトが全6版の両surfaceを検証し、回帰差分をバージョンごとに報告します。
  - パッケージ資産またはハッシュがない場合、マニフェスト検証が失敗します。
  - Workerの定数がマニフェストと一致します。
- **バージョン階層:**
  - 0.164.0は0.164階層のままで、0.165.0は0.165階層になります。
  - `0.166.0-alpha` は0.165階層になります。
  - 検証対象より新しいバージョンは0.166階層になります。
  - Experimentalが無効のとき、Experimental要求を送りません。
- **互換性:**
  - `detached` を送れるコード経路がありません。
  - 項目時刻、プロバイダーエラー、`policyCapacityUnavailable` が、ライブ通知と履歴再生の両方で投影されます。
  - プロバイダー拒否時に読み取り専用の履歴が開き、割り込みとgoalの一時停止・クリアが使えます。
  - 60秒アンロード後の `thread/closed` のあとも会話を再開できます。
- **レビュー:**
  - 引き継いだ設定で権限が広がりません。
  - 引き継げない設定があると、会話を作る前に止まります。
  - 失敗、切断、結果不明が設計どおりに動作し、自動再送しません。
  - レビューが開始するまで元の会話が選択されたままです。
- **履歴:**
  - worktree範囲が、複数worktree、サブフォルダー、Gitなし、パス正規化、遅延応答、ページングに対応します。
  - リモート接続では表示されません。
- **質問とテキスト:**
  - Markdownが安全に描画され、送信値は `OptionId` のままです。
  - 会話テキストで選択、コピー、検索、日本語、長大な履歴が扱えます。
  - 更新で別ファイルが作られ、閉じると削除され、起動時の清掃で古いファイルが削除されます。
- **使用量と診断:**
  - 実額USD、推定USD、推定クレジット、ゼロ、欠損が正しく表示されます。
  - 診断に `ISecretRedactor` とタイムアウトが適用されます。
- **関連分岐と記憶:**
  - 未対応の保存方式は理由付きで拒否されます。
  - 子は通常の一覧に表示されず、subagentと関連分岐は区別されます。
  - `summary` が `null` の場合、記憶なしと表示されます。
- **ワークスペース切替:**
  - 同じメールとプランのワークスペース間の切替で所有者境界が更新されます。
  - 確認ダイアログ、SSOと取り消し、失敗、結果不明が設計どおりに動作します。
  - 遅延応答と古いキューは破棄されます。
- **品質:**
  - Core、UI、Fakeの各テストと、DebugとReleaseのビルドが警告ゼロで完了します。
  - VSIX検査に合格します。
  - Experimental Instanceのスクリーンショットで、各テーマ、狭い幅、日本語、キーボード操作、スクリーンリーダー出力を確認します。
- **記録:** 英語と日本語の設計、計画、ADR、実装記録、作業記録はBOM付きUTF-8です。Issueのチェックリストには検証済みの項目だけを反映します。

## 検証の証跡

各段階の後で、実行したコマンド、構成、合計、スキップ、バージョン、資産ハッシュ、スクリーンショットのパスを `doc/task.md` と `doc/implementation.md` に記録します。段階1または段階2の確認がブロックまたはスキップされている間は、その段階を完了としません。設計の「実機環境の線引き」に挙げた段階3の環境は、リリース記録に未検証と明記すれば、Fakeの検証で完了できます。
