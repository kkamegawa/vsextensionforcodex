# Codex App Server 0.169.0 対応設計

[English](app-server-0-169-design.md)

追跡: [Issue #268](https://github.com/kkamegawa/vsextensionforcodex/issues/268) と子 Issue [#273](https://github.com/kkamegawa/vsextensionforcodex/issues/273)、[#274](https://github.com/kkamegawa/vsextensionforcodex/issues/274)、[#275](https://github.com/kkamegawa/vsextensionforcodex/issues/275)、[#276](https://github.com/kkamegawa/vsextensionforcodex/issues/276)。実装: [#269](https://github.com/kkamegawa/vsextensionforcodex/issues/269)(プラットフォーム)、[#270](https://github.com/kkamegawa/vsextensionforcodex/issues/270)(契約)、[#271](https://github.com/kkamegawa/vsextensionforcodex/issues/271)(.NET 10 と C# 14)、[#272](https://github.com/kkamegawa/vsextensionforcodex/issues/272)(機能)。2026-11-16 承認。

> これは 2026-11-16 に承認された #272 の Phase 4 当時の履歴設計です。以下のバージョン方針と機能ティアは当時の目標を記録したもので、現行 CLI の対応方針ではありません。現行方針は [ADR-024](adr/ADR-024-codex-cli-0-169-minimum.md) と [0.171 対応設計](app-server-0-171-design_ja.md) を参照してください。機能要件は #272 と #302～#305 で追跡します。

## 目的

Visual Studio 2026 と .NET 10 のプラットフォーム上で Codex App Server 0.167.0 から 0.169.0 に対応し、これらのリリースから 4 群の機能を取り込みます。モデルプロバイダー切り替え、承認 UI の強化、スレッド管理、診断・状態表示です。

決定事項は [ADR-022](adr/ADR-022-visual-studio-2026-dotnet-10_ja.md)、[ADR-023](adr/ADR-023-codex-cli-0-167-minimum_ja.md)、および 2026-11-16 付の ADR-011 追補に記録します。一次情報は上流リリース `rust-v0.167.0` から `rust-v0.169.0` と、固定した実行ファイルから生成した stable と experimental のスキーマです。

この文書に書く app-server のメソッドとフィールドは、すべて生成した 0.169.0 のスキーマに存在します。型のない `_meta` オブジェクト内のキー、設定キー、プロバイダーのエラーコードなど、スキーマが型を定めていない名前には、それを定義した上流の変更を併記します。

## 1. バージョン方針

| 役割 | バージョン |
| --- | --- |
| 最低(ローカルとリモート) | 0.167.0 |
| ターゲット・推奨 | 0.169.0 |
| 回帰確認 | 0.168.0、0.167.2 |
| 非対応 | 0.167.0 未満 |

固定して検証するのは各マイナー系列の最新パッチだけです。方針として 0.167.x はすべて接続できます。0.167.0、0.167.1、0.167.2 から生成した stable と experimental のスキーマは生成メタデータを除いて同一で、3 つのリリースのプロトコル面は同じです。実行時の動作を検証するのは 0.167.2 だけです。0.167.0 と 0.167.1 は固定せず、実行時の動作は未検証です。

| バージョン | リリースタグ | `codex-x86_64-pc-windows-msvc.exe` の SHA-256 | `codex-package-x86_64-pc-windows-msvc.tar.gz` の SHA-256 |
| --- | --- | --- | --- |
| 0.167.2 | `rust-v0.167.2` | `6f65c42e3769209976cce097a400720b5d877e1d24ec8bb6272bf0f02d5e0fd7` | `20b0f94105f40b617bf209b1679b8674ab6d3d8f303e767ae43cf1860a8705da` |
| 0.168.0 | `rust-v0.168.0` | `ad8296f7d95eea56bc4bab33cef025eb000340a7252ddb83dd41041ebcc65d0b` | `f6757eb2a4daaad569b01b3ea519c9009d38d0bac5fc4082ff02517e00537797` |
| 0.169.0 | `rust-v0.169.0` | `ce1945eb440b5481ed1233ebaf32b7056ee7e4e452fa62ffe1cbfb26103cdee0` | `456db280d16f4393cd99a398f11b4a14a3ede68b6bd31610ce41fb10d6b65737` |

### 契約マニフェスト

- `minimumVersion` は受け入れる最低バージョンです。`releases` には固定したバージョンだけを載せます。
- `scripts/validate-contract-manifest.ps1` は、`minimumVersion` が固定した最古のリリースと同じマイナー系列にあり、それを超えないことを要求します。`minimumVersion` が回帰確認バージョンや固定リリースであることは要求しません。
- `regressionVersions` は 0.167.2 と 0.168.0 です。`knownDifferences` には、`scripts/compare-schemas.ps1` が出力する、各回帰確認バージョンから 0.169.0 へのファイル単位の差分を両サーフェスについて記録します。
- `methodsByVersion` には、拡張機能が使うメソッドを導入ティアとともに記録します。

### 機能ティア

| ティア | 条件 | 機能 |
| --- | --- | --- |
| `Unsupported` | 不明、不正、`0.0.0`、または 0.167.0 未満 | `initialize` の後、`initialized` の前に接続を拒否します |
| `Baseline` | 0.167.0 以降 | 既存の全機能、スレッド管理、セクション表示、承認 UI の強化、MCP 診断、終端エラーのメッセージ |
| `Tier168` | 0.168.0 以降 | アシスタントのトークン数 |
| `Tier169` | 0.169.0 以降 | モデルプロバイダー切り替え、アカウントのセキュリティリマインダー、`inferenceHookDenied` |

`GetFeatureTier` は各ティアの導入バージョンと比較し、ターゲットバージョンとは比較しません。ターゲットより新しいバージョンは `Tier169` になります。比較は Semantic Versioning の優先順位に従います。ビルドメタデータは無視し、プレリリースはそのリリースより下に並ぶため、`0.167.0-alpha.1` は `Unsupported` です。experimental の機能には、ティアと、交渉済みの `experimentalApi` 機能の両方が必要です。experimental API は既定で無効のままです。

0.163、0.164、0.165、0.166 のゲートは削除します。これらが守っていた機能はすべて `Baseline` に含まれ、古いサーバー向けのフォールバックも合わせて削除します。

### Worker のランタイム

Worker はフレームワーク依存で `net10.0` をターゲットにします。`WorkerBridge` は、Extension 自身の .NET ランタイムをホストする `dotnet.exe` で `Worker/Codex.VisualStudio.Worker.dll` を起動するため、Worker は常に Visual Studio が提供するランタイムで動作します。Worker は既定の `RollForward` ポリシー `Minor` のままにします。インストールされている最新の .NET 10 パッチで動作し、異なるメジャーバージョンでは動作しません。将来の Visual Studio が新しいメジャーバージョンだけを提供する場合は、Extension と Worker を合わせてターゲットを変更します。そのホストがない場合は、同梱の apphost が同じポリシーでマシン全体の .NET 10 ランタイムを使います。

## 2. 0.166.0 から 0.169.0 へのプロトコル面の変化

### メソッド

| 導入 | 種別 | メソッド | サーフェス |
| --- | --- | --- | --- |
| 0.167 | サーバー通知 | `thread/section/changed` | Stable |
| 0.167 | クライアント要求 | `thread/guardianReview` | Experimental |
| 0.168 | クライアント要求 | `modelProvider/list` | Stable |
| 0.168 | クライアント要求 | `modelProvider/config/write` | Stable |
| 0.169 | クライアント要求 | `account/securitySetup/read` | Stable |
| 0.169 | クライアント要求 | `thread/prepareApprovedPlan` | Experimental |

削除されたメソッドはありません。サーバー要求とクライアント通知の追加・削除もありません。

### 形の変化

| 導入 | 型 | 変化 | サーフェス |
| --- | --- | --- | --- |
| 0.167 | `CommandExecutionApprovalKind` | `humanCommand` を追加 | Stable |
| 0.167 | `InitializeCapabilities` | `requiredHumanApproval` を追加 | Stable |
| 0.167 | `OtelRequirements` | `log_sse_deltas` を追加 | Stable |
| 0.167 | `ThreadSampleParams` | `interruptOnCancel` を追加 | Experimental |
| 0.168 | `ItemCompletedNotification` | `contentTokenCount` を追加 | Stable |
| 0.168 | `ThreadListParams`、`ThreadSearchParams` | `currentAccountOnly` を追加 | Stable、Experimental |
| 0.168 | `ThreadItemsListParams` | `deferOutput` を追加 | Stable |
| 0.168 | `ThreadItemEntry` | `outputDeferred` を追加 | Experimental |
| 0.168 | `McpResourceReadParams` | `_meta` を追加 | Stable |
| 0.168 | `Config` | `client_search_provider` を追加 | Stable |
| 0.169 | `CodexErrorInfo` | `inferenceHookDenied` を追加 | Stable |
| 0.169 | `ModelListParams` | `modelProvider` を追加 | Stable |
| 0.169 | `GetAccountResponse` | `userId` を追加 | Stable |
| 0.169 | `account/gatewayOAuth/read`、`account/gatewayOAuth/login` | パラメーターが `null` から、`modelProvider` を持つ省略可能な `GatewayOAuthReadParams` と `GatewayOAuthLoginParams` に変化 | Stable |
| 0.169 | `GatewayOAuthLoginResponse`、`GatewayOAuthReadResponse`、`GatewayOAuthChangedNotification` | `prompt`、`supportsDeviceAuth`、`loginId` を追加 | Stable |
| 0.169 | `ThreadItem` の `agentMessage` | `moderation` を追加 | Stable |
| 0.169 | `Thread` | `attachedToThreadId` を追加 | Experimental |
| 0.169 | `ThreadListParams`、`ThreadSearchParams` | `includeAttachedThreads` を追加 | Experimental |
| 0.169 | `ThreadSettings`、`ThreadSettingsUpdateParams`、`TurnSettingsUpdateParams` | `environments` を追加 | Experimental |
| 0.169 | `QueuedSubmission`、`ThreadQueueAddParams`、`ThreadQueueUpdateParams` | `responsesapiClientMetadata` を追加 | Experimental |
| 0.169 | `ThreadSampleParams` | `allowInProgressTurn` を追加 | Experimental |
| 0.169 | `UserInput` の `audio`、`ContentItem` と `FunctionCallOutputContentItem` の `input_audio` | URL フィールドの変化 | Stable |

`thread/start`、`thread/resume`、`thread/fork` の応答は、0.166.0 と 0.169.0 で同じ形です。

### スキーマとパーサーの対応

Worker はペイロードを JSON 要素として読み取るため、Worker が送受信する形の変化それぞれについて、パーサーでの扱いを明示します。各行は Fake app-server または実 CLI に対するテストで確認します。

| ワイヤー上の項目 | Worker のコード | 扱い |
| --- | --- | --- |
| `initialize` の `capabilities.requiredHumanApproval` | `CodexSessionService.CreateClientCapabilities` | `experimentalApi` を送るときだけ `true` で送ります |
| `item/commandExecution/requestApproval` の `kind` | `CodexSessionService.CreateApprovalRequest`、`OfferedCommandChoiceSet` | 読み取ります。`command` は既存のカードのままです。`writeStdin` は実行中のターミナルへの入力としてカードを表示します。`humanCommand` は 4 章の組織のチェックポイントを表示します。不明な値は永続的な許可やポリシー変更の選択肢を含めず、通常のカードを表示します |
| `mcpServer/elicitation/request` の `_meta` | `McpElicitationFormParser` | 4 章のとおり `tool_name` と `tool_annotations` を読み取ります |
| `item/completed` の `contentTokenCount` | `CodexSessionService.DispatchNotificationAsync` | `Tier168` で `agentMessage` 項目について読み取ります |
| ライブと再生されたターンの `codexErrorInfo` にある `inferenceHookDenied`、`sessionBudgetExceeded` | `CodexSessionService.ReadTurnErrorReason` とライブ／再生ターンリーダー | 認識し、ペイロードのテキストを秘匿化して安全な固定説明を表示します。ターンは自動で再送しません |
| `model/list` の `modelProvider` | `CodexSessionService.ListModelsAsync` | 既定以外のプロバイダーを選んだとき、`Tier169` で送ります |
| `thread/start` の `modelProvider` | `CodexSessionService.StartThreadAsync` | 新しい会話のプロバイダーを選んだとき、`Tier169` で送ります |
| `account/read` の `userId` | `CodexSessionService.ReadAccountStatus` | セキュリティリマインダーの照合のため、`Tier169` で読み取ります |
| `account/gatewayOAuth/read`、`account/gatewayOAuth/login` の `modelProvider` | ゲートウェイ OAuth の操作 | 選んだプロバイダーについて `Tier169` で送ります。`deviceAuth` は送らないため、`prompt` は `null` のままで、読み取りません |
| `account/gatewayOAuth/changed` の `loginId` | ゲートウェイ OAuth の通知ハンドラー | 読み取りません。完了は `providerId` で照合します |
| `thread/section/changed` | 新しい通知ハンドラー | 一覧にある会話のセクションを更新します |
| `ThreadItem` の `agentMessage.moderation` | 型付き項目のリーダー | 読み取りません。拡張機能は `AgentMessageDisplay` のアクションを適用しません |
| `thread/start`、`thread/resume`、`turn/start` の `personality` | `CodexSessionService` のスレッド／ターン要求ビルダー | シリアライズしません。`/personality` と下書きの状態は削除します |
| `thread/rollback` と、再生される巻き戻し済み履歴 | `ReadThread` とターン履歴リーダー | メソッドは要求せず、既存の履歴は引き続き読み込みます。`threadRollbackFailed` は通常のエラーとして残します |
| `thread/start`、`thread/resume`、`thread/settings/updated` の権限フィールド | `ReadThread`、`ReadEffectiveThreadPermissions`、設定通知ハンドラー | `activePermissionProfile` と `sandbox` を実効承認モードに反映します。スキーマで定義されたファイルシステムの `read`／`write` 形式は維持します |
| プリセットの `config/batchWrite` | `WritePresetAsync` | プリセットのキーだけを書き込み、`features.multi_agent` と `agents.enabled` は書き込みません |
| 形の変化の表にあるその他の行 | — | 送信も読み取りもしません |

## 3. 必須の追従項目

- **`personality`。** `Personality` 値はスレッド／ターン要求にシリアライズしません。`/personality` スラッシュコマンド、`StartTurnRequest.Personality`、下書き状態、`supportsPersonality` の読み取りを削除します。
- **`thread/rollback`。** 対応するどのスキーマにも存在しないため、メソッドを要求しません。巻き戻し済みターンを含む既存の履歴は引き続き読み込みます。`threadRollbackFailed` は通常のエラーとして扱います。
- **既定のスレッド権限。** 既定の権限で作成したスレッドは、権限を permission profile として保存します(openai/codex#60472)。Worker は `activePermissionProfile` と `sandbox` を読み取り、実効承認モードに反映します。スキーマに残るファイルシステム権限の `read`／`write` 形式も読み取ります。
- **`features.multi_agent`。** このフラグは `agents.enabled` に置き換えられて非推奨になり(openai/codex#59479)、設定時に CLI は stderr に警告を書きます。拡張機能はプリセットの `config/batchWrite` でプリセットキーだけを書き込み、どちらのキーも書きません。警告は接続失敗ではありません。
- **終端エラー。** プロバイダーの `budget_reached` は `sessionBudgetExceeded` として届きます(openai/codex#59228)。ライブ通知と再生履歴の `sessionBudgetExceeded`、`inferenceHookDenied` を認識し、ペイロードのテキストを表示せず安全な固定説明を表示します(openai/codex#60511)。サーバーのアクセス拒否も既存のエラー値と秘匿化したメッセージで扱います(openai/codex#60476、#60480)。ターンを自動で再送しません。

## 4. 機能

各群は、既存の要求ヘルパー、通知ディスパッチ、Worker 契約レコード、表示クラスを再利用します。動的な文字列は `SafeMarkdownService`、ログは `ISecretRedactor`、破壊的な操作は `IApprovalPolicyEngine` を通します。各群で、これらの経路を迂回できないことを示すテストを追加します。

### 4.1 モデルプロバイダー切り替え

**利用条件:** `Tier169`、stable サーフェス。

- **プロトコル:** `modelProvider/list` は `data`(`id`、`name`、`baseUrl`、`canEdit`、`settings` を持つ `ModelProviderSummary`)、`defaultProviderId`、`canChangeDefault` を返します。`modelProvider` を指定した `model/list` は、そのプロバイダーのモデルを返します。`modelProvider` を指定した `thread/start` は、そのプロバイダーで会話を開始します。
- **情報構造:** モデルピッカーの上にプロバイダー選択を置きます。各プロバイダーの `name` を表示し、既定のものに印を付け、`baseUrl` は `null` でないときだけ 2 行目に表示します。プロバイダーの種類は `settings.type` から求め、ラベル(OpenAI、カスタムゲートウェイ、Amazon Bedrock、Google Cloud)で表示します。プロバイダーを選ぶとそのモデル一覧を読み直し、入力欄は使える状態のままです。既存の会話のプロバイダーは `Thread.modelProvider` から読み取り専用で表示します。切り替えは新しい会話にだけ適用します。
- **既定のプロバイダー:** `canChangeDefault` が `true` のとき、選択欄に「新しい会話の既定にする」を表示します。`keyPath: "model_provider"`、`value` にプロバイダーの `id`、`mergeStrategy: "replace"` で `config/value/write` を送り、その後 `modelProvider/list` を読み直します。`canChangeDefault` が `false` のときは操作を表示せず、サーバーの設定で既定が固定されていることを説明します。書き込みに失敗した場合は以前の既定を保ち、エラーを表示します。書き込みは再試行しません。
- **ゲートウェイのサインイン:** プロバイダーを選んだ後、`modelProvider` を指定した `account/gatewayOAuth/read` で、ゲートウェイ OAuth が `required` かどうかと `status` を取得します。サインインが必要な場合は、既存のゲートウェイのサインイン経路で `modelProvider` を指定して `account/gatewayOAuth/login` を実行します。認可 URL は安全なブラウザー経路でだけ開きます。
- **エラー:** JSON-RPC エラーの `data.type` の値 `modelProviderNotConfigured`、`modelProviderPolicyConflict`、`modelProviderMismatch` を、`thread/start`、`thread/resume`、`model/list` で読み取ります。それぞれ、Codex の設定でプロバイダーを構成する、組織のポリシーが許可するプロバイダーを選ぶ、保存されたプロバイダーで会話を開く、という案内を表示します。不明な値は秘匿化したサーバーのメッセージを表示します。
- **取り込まないもの:** `modelProvider/config/write`。プロバイダーの作成・編集と、ゲートウェイ、Bedrock、Google Cloud の設定は、Codex CLI と設定ファイルで行います。

### 4.2 承認 UI の強化

**利用条件:** `Baseline`。組織のチェックポイントには `experimentalApi` が必要です。

- **MCP ツールの承認:** MCP ツール呼び出しの `mcpServer/elicitation/request` には `_meta.tool_name` と `_meta.tool_annotations` が含まれます(openai/codex#58161)。`tool_annotations` には真偽値 `readOnlyHint`、`destructiveHint`、`idempotentHint`、`openWorldHint` が含まれることがあり、含まれないヒントは不明として扱います。カードはサーバー名とツール名をタイトルにします。「読み取り専用」「破壊的」「冪等」のバッジは、対応するヒントが `true` の場合だけ表示し、`destructiveHint: true` は警告スタイルにします。`openWorldHint` は `true` のとき「外部システムにアクセス」、`false` のときサーバー申告として「閉じた操作範囲」を表示します。読み取り専用、破壊的、冪等のヒントが `false` または未指定の場合はバッジを表示しません。これらは MCP サーバーが申告する信頼できない補助情報です。選べる決定を変えず、自動承認を有効にせず、`IApprovalPolicyEngine` を迂回しません。`tool_name` がない、文字列でない、または大きすぎる場合は既存のカードを表示します。
- **組織のチェックポイント:** `experimentalApi` を交渉した場合、`initialize` で `requiredHumanApproval` を宣言します。`kind: "humanCommand"` のコマンド承認は、組織の管理ポリシーが要求するチェックポイントです。カードのタイトルは「組織ポリシーによる承認が必要です」で、コマンドと理由を表示し、サーバーが提示した「承認」「拒否」「キャンセル」のうち一回限りの決定だけを提示します。タイムアウト時に提示済みの `cancel` を返せるよう、`cancel` がない要求はカードを表示する前に拒否します。カードに表示できない空でない追加権限はカード表示前に拒否し、秘匿化した診断を記録します。空の権限コンテナーはチェックポイントを妨げません。チェックポイントには、セッション単位の許可、`acceptForSession`、実行ポリシーの修正、ローカルポリシーによる自動化、自動承認のいずれも使いません。タイムアウト時は `cancel` を送ります。`experimentalApi` がない場合は機能を宣言せず、サーバー側の動作に任せます。

### 4.3 スレッド管理

**利用条件:** `Baseline`。`thread/search` には `experimentalApi` が必要です。

- **アーカイブ:** 履歴一覧に「アーカイブ」を、アーカイブ済みの表示に「アーカイブ解除」を置き、`thread/archive` と `thread/unarchive` を使います。アーカイブ済みの表示は `archived: true` の `thread/list` を使います。`thread/archived` と `thread/unarchived` の通知で一覧を更新します。
- **削除:** 「削除」は `thread/delete` で会話を完全に削除します。確認ダイアログで会話名を示し、元に戻せないことを明記します。操作は `IApprovalPolicyEngine` を通し、実行中のターンがある会話では実行できません。リンクされたフォークの子が残っている親は削除できません。`thread/deleted` で一覧から取り除きます。
- **巻き戻し:** ページング履歴を持つ参加中の会話のターンに「このターンの前まで巻き戻す」を置きます。確認ダイアログで、そのターンと以降のすべてのターンが会話履歴から削除されること、ローカルのファイル変更は元に戻らないことを明記します。操作は `IApprovalPolicyEngine` を通し、`threadId` と `beforeTurnId` を指定して `thread/revert` を送ります。その後、返されたカーソルで `thread/turns/list` と `thread/items/list` からトランスクリプトを読み直します。実行中のターンがある間は使えません。
- **検索:** `experimentalApi` がある場合、履歴一覧の検索欄は `searchTerm` を指定して `thread/search` を呼び、各結果の会話と `snippet` を表示します。ない場合は、既存の `thread/list.searchTerm` によるタイトルの絞り込みのままです。スニペットは `SafeMarkdownService.ToSafeLiteralText` を通します。
- **セクション:** 履歴一覧に各会話のセクションを `Thread.section` から表示し、`thread/list.sectionId` でセクションごとに絞り込みます。絞り込みの選択肢は `threadSection/list` の全セクションで、各セクションの `name` を表示します。`thread/section/changed` で一覧の会話を更新します。拡張機能はセクションの作成、名前の変更、削除、移動を行いません。
- **取り込まないもの:** `currentAccountOnly`、`includeAttachedThreads`、セクションの管理。

### 4.4 診断・状態表示

- **MCP の起動**(`Baseline`): `mcpServer/startupStatus/updated` は `name`、`status`(`starting`、`ready`、`failed`、`cancelled`)、`error`、`failureReason` を持ちます。`failed` のサーバーは、秘匿化した `error` を 2,048 文字以内で MCP の詳細に表示し、`reauthenticationRequired` は既存のサインイン経路を使います。`mcpServerStatus/list` に含まれるのに起動状態が届いていないサーバーは「未起動」と表示します。起動を遅延したサーバーは最初の使用時に起動するためです。入力欄は、状態を報告していないサーバーを待ちません。
- **終端エラー**(`Baseline`、`inferenceHookDenied` は `Tier169`): `sessionBudgetExceeded` は、セッションの予算に達したこと、新しい会話かより大きな予算が必要なことを表示します。`tooManyDenials` は既存のメッセージのままです。`inferenceHookDenied` は、組織の推論フックが要求を拒否したことを表示します。いずれも秘匿化したサーバーのメッセージを含め、自動の再試行は提示しません。
- **アシスタントのトークン数**(`Tier168`): アシスタントのメッセージの項目詳細に、`contentTokenCount` があれば表示します。値がないか `null` の場合は表示せず、ゼロとしても表示しません。
- **アカウントのセキュリティリマインダー**(`Tier169`): サインイン後とアカウントの変更後に、Worker は `account/securitySetup/read` を呼びます。`notice` は、その `accountId` が現在のアカウントと一致し、`userId` が `account/read.userId` と一致するときだけ表示します。アカウント領域に `title` と `description` を `SafeMarkdownService` を通したプレーンテキストで表示し、`action.label` をボタンにします。ボタンは安全なブラウザー経路でだけ `action.url` を開き、その経路は `https` だけを受け付けます。リマインダーはセッション中だけ閉じられます。アカウントが変わると、取得中または表示中のリマインダーを破棄します。`notice` が `null` の場合は何も表示しません。

### 取り込まないもの

- Cloud 接続。
- 音声添付とリアルタイム音声。
- ターミナル専用の機能: ワークスペースのサイドチャット、エージェントのコマンドセンター、レポートエディター、ターミナル通知。
- Vertex AI と Bedrock のセットアップウィザード、`account/bedrock/*`、`account/gcp/setup`、`modelProvider/config/write`。
- message-board API。
- `userVerification/*`。
- アニメーション画像生成。
- `thread/guardianReview` と `thread/prepareApprovedPlan`。
- 履歴の出力の遅延取得(`deferOutput`、`outputDeferred`)。履歴の要求では `deferOutput` を送らないため、出力はすべて項目に含まれたままです。
- `agentMessage.moderation` の表示アクション。

## 5. Worker 契約

Worker 契約は次の未使用のバージョンにし、次を追加します。

- プロバイダー一覧、モデル一覧と新しい会話のプロバイダー指定、既定のプロバイダーの書き込み。
- コマンド承認の種類と、MCP のツール名・annotation のヒント。
- アーカイブ、アーカイブ解除、削除、巻き戻し、検索、セクション一覧、セクション変更のレコード。
- MCP の起動エラーのテキスト、トークン数、新しいエラー理由、セキュリティ通知。

`StartTurnRequest.Personality` は削除します。

### 再試行の方針

| メソッド | サーフェス | 再試行の方針 |
| --- | --- | --- |
| `modelProvider/list` | Stable | `ReadOnlyRequestAllowlist` に追加 |
| `account/securitySetup/read` | Stable | `ReadOnlyRequestAllowlist` に追加 |
| `thread/search` | Experimental | `ReadOnlyRequestAllowlist` に追加 |
| `threadSection/list` | Stable | `ReadOnlyRequestAllowlist` に追加 |
| `config/value/write` | Stable | 再試行しない |
| `thread/archive`、`thread/unarchive` | Stable | 再試行しない |
| `thread/delete` | Stable | 再試行しない |
| `thread/revert` | Stable | 再試行しない |

変更操作の途中で切断された場合は結果不明として扱います。拡張機能は対象の一覧を読み直し、変更操作を再送しません。

## 6. 検証

確認は [ADR-021](adr/ADR-021-validation-policy_ja.md) に従います。

- **契約:** 実 CLI 0.167.2 と 0.169.0 に対して `scripts/validate-contract-manifest.ps1`、`scripts/verify-contract-surface.ps1`、`scripts/smoke-app-server.ps1` を実行します。実 CLI 0.166.0 は拒否されます。バージョン境界のテストは、0.166.x、0.167.0、0.167.2、0.168.0、0.169.0、プレリリースとビルドメタデータ付きのバージョン、ターゲットより新しいバージョンを対象にします。
- **パーサー:** スキーマとパーサーの対応表の各行に、Fake app-server または実 CLI に対するテストを用意します。
- **セキュリティ:** MCP のヒントで自動承認が有効にならないこと、`humanCommand` がセッション単位の許可を使わないこと、削除と巻き戻しが `IApprovalPolicyEngine` を通ること、スニペット、通知、エラーが `SafeMarkdownService` と `ISecretRedactor` を通ること、セキュリティリマインダーの URL が安全なブラウザー経路でだけ開くことをテストで示します。
- **UI:** Visual Studio 2026 と実 CLI 0.169.0 で各機能群を操作し、ライトテーマとダークテーマのスクリーンショットと判定を記録します。
- **品質:** Release ビルドが警告ゼロで通り、Core と UI のテストがすべて成功すること。
