# Codex CLI 0.171 リリースノート

[English](codex-cli-0-171-release-notes.md)

## Enterprise MCP サインイン

組織の ID プロバイダーが更新トークンを発行せず、OpenID Connect の ID トークンを発行する場合は、**Codex CLI 0.171.0 以降**を使用してください。同じ名前の登録を環境ごとに区別する場合は、先に **Experimental API** を有効にして app-server に再接続します。その後、Codex ツールウィンドウの MCP 詳細を開き、対象の登録を選んで **Sign in to MCP server** を選択してください。サインイン操作が保留になるため、続けて **Open authorization page** を選択し、ブラウザーでサインインを完了してください。CLI がトークン交換と ID トークンの検証を行います。トークンが期限切れまたは無効になったら、その登録に再度サインインしてください。

0.169 と 0.170 では、このサインインに更新トークンが必要です。リモート接続では、接続先の app-server を実行する CLI のバージョンで対応可否が決まります。

## OTLP HTTP ヘッダーヘルパー

| CLI | 設定 |
| --- | --- |
| 0.169 | 既存のエクスポーター設定を使用します。ヘッダーヘルパーは 0.170 以降で利用できます。 |
| 0.170 | ユーザー設定で OTLP HTTP エクスポーターを明示的に構成し、`otel.headers_helper` と、必要に応じて `otel.headers_helper_refresh_interval_ms` を設定します。 |
| 0.171 | システムの `requirements.toml` または MDM 要件の同一ポリシーレイヤーで、ヘルパー実行ファイルの絶対パスと、空でないエンドポイントを持つ OTLP HTTP エクスポーターを指定できます。管理対象ヘルパーは、この 2 種類のポリシーソースで利用できます。 |

管理対象ヘルパーの設定を有効にする前に、対象となるすべての CLI を 0.171.0 以降に更新してください。0.170 を含む古いクライアントは、管理要件に `otel.headers_helper` または `otel.headers_helper_refresh_interval_ms` が含まれていると、起動に失敗する場合があります。クライアントをダウングレードする前に、ポリシーから両方のキーを削除し、その変更が対象マシンに反映されるまで待ってください。

CLI がヘルパーを検証・実行し、更新したヘッダーを対象のエクスポーターに適用します。実行ファイルと設定は、CLI を実行するマシンで管理してください。拡張機能は MCP のサインイン状態を表示します。資格情報とテレメトリのヘッダー値は CLI 内で扱います。

**実装済み。Enterprise 環境では未検証です。** Enterprise 環境での受け入れ確認は免除されています。通常のビルドと回帰テストは対象です。

[Enterprise MCP サインイン](https://github.com/openai/codex/pull/62129) · [ユーザー設定の OTLP ヘッダーヘルパー](https://github.com/openai/codex/pull/61028) · [管理対象の OTLP ヘッダーヘルパー](https://github.com/openai/codex/pull/62110)
