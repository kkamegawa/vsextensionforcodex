# ADR-020: Codex App Server 0.166.0 対応、版ごとの機能階層、段階的な提供

[English](ADR-020-app-server-0-166.md)

- 日付: 2026-10-31
- 状態: 承認済み
- 追跡: [Issue #240](https://github.com/kkamegawa/vsextensionforcodex/issues/240) と子Issue [#241](https://github.com/kkamegawa/vsextensionforcodex/issues/241)、[#242](https://github.com/kkamegawa/vsextensionforcodex/issues/242)、[#243](https://github.com/kkamegawa/vsextensionforcodex/issues/243)、[#244](https://github.com/kkamegawa/vsextensionforcodex/issues/244)

## 背景

Codex CLIは1週間のうちに0.164、0.165、0.166をリリースしました。0.166には拡張機能に影響する変更がいくつかあります。

- `detached` レビュー配信が削除されました(openai/codex#57667)。
- スレッドのアンロード既定が60秒に短縮され(openai/codex#57242)、購読者のない会話は早く `thread/closed` を受けます。
- プロバイダーの拒否は、構造化されたJSON-RPCエラーの `data` で返ります。`thread/read` は使える一方で、`thread/resume` は拒否されることがあります。
- `accountSession/switch` は読み込み済みの会話を止めます。`account/updated` にワークスペースの識別情報はありません。
- 実機検証に必要な単位は、単一の実行ファイルではなく完全なWindowsパッケージになります。

製品のUIから `detached` に到達する経路はありません。`ExecuteReviewAsync` が配信方法を設定しないためです。既存の `GetFeatureTier` は検証対象バージョンと比較するため、検証対象を上げるだけでは0.164と0.165の接続が0.163階層に下がります。

## 決定

- **対応バージョン:** 最低対応は0.161.0のままで、検証対象を0.166.0、回帰対象を0.165.0、0.164.0、0.163.0、0.162.1、0.161.0とします。同日のADR-011追補に記録しています。
- **バージョン階層:** 0.165と0.166の階層を追加し、各階層は自身の導入版と比較します。Experimental機能には交渉済みcapabilityも必須とします。
- **提供方法:** 互換対応、Stable機能、Experimental機能、統合の段階に分け、それぞれ単独で提供できるようにします。互換対応が機能追加を待つことはありません。
- **レビュー:** `ReviewDelivery` を `ReviewPlacement` に置き換えます。別会話レビューは、`thread/start` と `inline` の `review/start` を1つのWorker操作とする新機能で、すべての対応版で利用できます。履歴は引き継がず、その用途は `/fork` で対応します。
- **関連分岐:** 導入版を0.166とします。子の履歴から親のターンが除かれるのは0.166以降のためです(openai/codex#57256)。子の取得元は `attachedToThreadId` のみとします。
- **worktree範囲:** ローカル接続のみとし、上流の `repository_cwd_filter` に合わせます。拡張機能にはリモートサーバーでGitを読み取り専用で実行する経路がありません。
- **アカウント指紋:** `account/read.workspaceRouting.chatgptAccountId` のワークスペースIDを加えます。Experimentalが無効の間、外部での切替は検知対象外です。
- **採用しないもの:** 認証ウィザード、`account/gcp/setup`、および `turn/moderationMetadata` の書き換え候補です。このメタデータは型がなく不安定です。
- **Worker契約:** v27とします。

## 影響

- 別会話レビューの会話は通常のスレッドなので、通常の履歴一覧に増えます。
- 共有サーバーではワークスペース切替が他のクライアントを止めるため、確認ダイアログでその旨を示します。
- 会話テキストは、安全化した内容を所有者分離のもとでディスクへ書き込みます。閉じたときに削除し、24時間で清掃します。
- 次の4つの実環境は、リリース記録に未検証と記せばFakeの検証のみで完了できます。SSO必須のワークスペース、同じメールとプランの複数ワークスペース、ネイティブUSDのプラン、Vertex AIまたはBedrockのみの環境です。
- 承認済みの設計と計画は[app-server-0-166-design_ja.md](../app-server-0-166-design_ja.md)と[app-server-0-166-plan_ja.md](../app-server-0-166-plan_ja.md)にあります。
