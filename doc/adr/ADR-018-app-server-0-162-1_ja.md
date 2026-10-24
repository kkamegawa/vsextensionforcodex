# ADR-018: Codex App Server 0.162.1 契約と MXC バックエンド選択

[English](ADR-018-app-server-0-162-1.md)

- 日付: 2026-10-12
- 状態: 承認済み
- 追跡: [Issue #198](https://github.com/kkamegawa/vsextensionforcodex/issues/198) と子 Issue [#199](https://github.com/kkamegawa/vsextensionforcodex/issues/199)〜[#203](https://github.com/kkamegawa/vsextensionforcodex/issues/203)

## 背景

Codex CLI 0.162.1 では、goal 変更の操作元、MCP OAuth の試行識別子、途中回答を表すメッセージ phase、絶対パスとは限らない skill パス型、拡張可能な `CodexErrorInfo`、turn の系譜が追加されました。また、MXC の選択が管理ポリシーにより厳密に従うようになりました。MXC は 0.159.1 にも存在し、拡張側でインストーラーや helper を用意する必要はありません。Codex は自身の実行ファイルを MXC の helper として起動します。リモート App Server が 0.159.1 のまま動作している場合があります。

## 決定

- 0.162.1 を対象とし、0.159.1 を回帰比較の基準として維持します。新しい request フィールドは 0.159.1 が無視しても安全でなければならず、新しい response フィールドは任意項目として読み取ります。
- goal のすべての変更はユーザーの明示操作に由来するため、`origin: "user"` を付けて送ります。`automatic` は送りません。
- MCP OAuth は `loginId` が一致する場合だけ完了させます。応答より先に届いた完了通知は保持し、`loginId` がない場合だけサーバー名による照合にフォールバックします。
- skill パスは、ホストの絶対パスではなく、カタログの不透明な識別子として扱います。
- メッセージ phase と turn の系譜は Worker 契約に追加しません。拡張はすべての agent message item を 1 回ずつ表示し、turn の終了は `turn/completed` だけで判断します。また、代理・委譲の経路を持ちません。
- 管理要件による速度制限は拡張では適用しません。`supportsIndependentSpeedModes` は記録しますが使用しません。
- ローカル stdio 起動に限り、「Codex 設定に従う」「MXC 優先」（`features.prefer_mxc=true`）「MXC 必須」（`windows.sandbox=mxc`）を提供し、許可リストの `-c` override として次回のローカル起動時に渡します。診断には `config/read` と `configRequirements/read` を使い、不明な値は不明と表示します。管理ポリシーを緩めたり、リモートサーバーを再構成したり、失敗したコマンドを別のバックエンドで再実行したりはしません。`windowsSandbox/setupStart` は legacy の elevated／unelevated バックエンド専用として残します。

## 結果

- `WorkerOptions` と readiness の結果にバックエンド関連の項目が加わるため、Worker 契約のバージョンを 24 にします。
- 管理ポリシーが禁止している状態で「MXC 必須」を選択すると、ローカル App Server は初期化前に停止します。接続エラーで設定名を示し、ローカル起動の失敗後も sandbox パネルを表示し続けるため、ユーザーは設定を変更できます。
- readiness と設定の読み取りでは、あるコマンドがどのバックエンドで実行されたかを証明できません。UI は推測せず、その旨を表示します。
- 承認済みの設計と計画は [app-server-0-162-1-plan_ja.md](../app-server-0-162-1-plan_ja.md) にあります。

## 改定（2026-10-25、0.159.1 のサポート終了）

- 状態: 承認済み
- 追跡: [Issue #226](https://github.com/kkamegawa/vsextensionforcodex/issues/226)
- 2026-10-25 付の ADR-011 改定により、最低対応バージョンは 0.161.0 になります。これに伴い、0.159.1 の回帰 fixture と、0.159.1 専用のフォールバックを削除します。
  - `loginId` がない場合にサーバー名で照合する処理はなくなります。対応するすべてのバージョンが `loginId` を返すためです。
  - goal の `origin` と、skill パスを不透明な識別子として扱う方針は変わりません。
- 詳細は [ADR-019](ADR-019-app-server-0-164_ja.md) を参照してください。
