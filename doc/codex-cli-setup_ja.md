# Visual Studio 拡張機能で Codex CLI を設定して使う

[English](codex-cli-setup.md) · [README](../README_ja.md)

このガイドは、現行の拡張機能と、接続仕様の固定対象である Codex CLI **0.159.1** に対応します。最初はローカルで利用してください。**Connection target** は App Server の接続先を選ぶ画面です。CLI のインストールや実行ファイルの指定はここでは行いません。**Connected to: Local**、**No remote profiles** と表示されていれば、通常の利用にリモートプロファイルの追加は不要です。

## 目次

- [1. 接続方式を選ぶ](#1-接続方式を選ぶ)
- [2. Windows 用 CLI の完全パッケージを導入する](#2-windows-用-cli-の完全パッケージを導入する)
- [3. 同じ CLI で認証する](#3-同じ-cli-で認証する)
- [4. 3種類の設定を区別する](#4-3種類の設定を区別する)
- [5. ローカルで最初の会話を始める](#5-ローカルで最初の会話を始める)
- [6. Connection target 画面の読み方](#6-connection-target-画面の読み方)
- [7. 認証付きリモート App Server を準備する](#7-認証付きリモート-app-server-を準備する)
- [8. 再接続・トークン更新・ローカルへの復帰](#8-再接続トークン更新ローカルへの復帰)
- [9. 設定の問題を調べる](#9-設定の問題を調べる)
- [10. 参照先と検証の範囲](#10-参照先と検証の範囲)

## 1. 接続方式を選ぶ

| | Local（既定） | Remote（Preview） |
|---|---|---|
| Visual Studio | Windows PC | Windows PC |
| Codex の実行場所 | その PC の CLI 子プロセス | 別ホストなどで事前に起動した App Server |
| CLI の選択 | `CODEX_PATH` などによる実行ファイル解決 | サーバー管理者がサーバー側の CLI を指定 |
| CLI のログイン・設定 | Windows ユーザーの Codex ホーム | サーバープロセス実行ユーザーの Codex ホーム |
| 通信 | Worker が管理する標準入出力（stdio） | Worker が管理する WebSocket |
| 作業ツリー | 開いたローカルのソリューション／フォルダー | ローカル／サーバールートから同じ作業ツリーを参照 |
| サーバーの管理 | 拡張機能が子プロセスを起動・再起動 | 管理者が起動・管理し、拡張機能は接続・再接続 |
| 必要なプロファイル | なし | 保存済みで Enabled のリモートプロファイル |

拡張機能は、リモートへのインストール、サーバーの起動・停止・更新、ファイル同期を行いません。内容の異なる2つのチェックアウトにパスを設定しても同期されません。

## 2. Windows 用 CLI の完全パッケージを導入する

必要なものは、Windows x64 または Arm64、Visual Studio 2022 **17.14 以降**または Visual Studio 2026、Codex で利用できるアカウント／プロバイダー、拡張機能の VSIX です。

1. [README の必要環境](../README_ja.md)にある公式 `rust-v0.159.1` リリースへのリンクを開きます。
2. x64 なら `codex-package-x86_64-pc-windows-msvc.tar.gz`、Arm64 なら `codex-package-aarch64-pc-windows-msvc.tar.gz` をダウンロードします。
3. 完全パッケージを使用し、`bin\codex.exe`、`bin\codex-code-mode-host.exe`、`codex-resources\` を含む配置を維持します。単体の実行ファイルでは初期接続できても、ツール実行が `failed to spawn code-mode host` で失敗することがあります。
4. そのパッケージの SHA-256 を、リリースページにある同じアセットのダイジェストと照合します。`app-server-contract.json` の単体実行ファイルのハッシュは別アセットの値です。

以下は **PowerShell 7** で実行します。ダウンロードしたアーカイブのパスと、そのパッケージの正しいハッシュを入力します。一致しなければ展開せず停止し、一致すればバージョン専用のディレクトリへ展開します。

~~~powershell
$archive = (Resolve-Path -LiteralPath (Read-Host 'Downloaded .tar.gz path')).Path
$expectedHash = (Read-Host 'SHA-256 for this package from the release page').Trim()
$actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
if ($expectedHash -notmatch '^[0-9a-fA-F]{64}$' -or $actualHash -ine $expectedHash) {
    throw 'Package SHA-256 mismatch.'
}
$codexInstall = Join-Path $env:LOCALAPPDATA 'OpenAI\Codex\0.159.1'
New-Item -ItemType Directory -Force -Path $codexInstall | Out-Null
tar -xzf $archive -C $codexInstall
if ($LASTEXITCODE -ne 0) { throw 'Package extraction failed.' }
$codexExe = Join-Path $codexInstall 'bin\codex.exe'
if (-not (Test-Path -LiteralPath $codexExe -PathType Leaf)) {
    throw 'Locate bin\codex.exe in the extracted package and correct codexInstall.'
}
if (-not (Test-Path -LiteralPath (Join-Path $codexInstall 'bin\codex-code-mode-host.exe'))) {
    throw 'The complete CLI package is required.'
}
$version = & $codexExe --version
if ($LASTEXITCODE -ne 0 -or $version.Trim() -ne 'codex-cli 0.159.1') {
    throw 'Expected codex-cli 0.159.1.'
}
$env:CODEX_PATH = $codexExe
[Environment]::SetEnvironmentVariable('CODEX_PATH', $codexExe, 'User')
$version
~~~

アーカイブに追加の親ディレクトリがある場合は、展開先の実際の `bin\codex.exe` を探し、そのフルパスを `CODEX_PATH` に設定してください。隣接するリソースも一緒に維持します。新しい CLI の導入だけで、この拡張機能との動作検証が済んだことにはなりません。

### 使用する実行ファイルを指定する

`CODEX_PATH` に指定するのは実行ファイルそのもののパスです。ディレクトリ、引数付きコマンド、App Server の接続先ではありません。保存する値に引用符自体を含めないでください。

Worker はローカル CLI を次の順に探します。

1. `CODEX_PATH` が指す、実在するファイル。
2. 内部の Worker オプションで明示されたパス。現行 UI は `codex` を指定し、実行ファイル選択画面はありません。
3. `PATH` 上の `codex.exe`、続いて `codex.cmd`／`codex.bat`。WindowsApps の実行エイリアスは除外します。
4. `%LOCALAPPDATA%\OpenAI\Codex\bin` 以下の `codex.exe`。
5. 最後に、指定されたコマンド名での起動を試みます。

`CODEX_PATH` が無効だと別のインストール先へフォールバックする場合があります。アカウントやモデルを調べる前に、指定したファイルを確認します。

~~~powershell
$codexExe = [Environment]::GetEnvironmentVariable('CODEX_PATH', 'User')
if (-not $codexExe -or -not (Test-Path -LiteralPath $codexExe -PathType Leaf)) {
    throw 'Set CODEX_PATH to the intended codex.exe first.'
}
& $codexExe --version
where.exe codex
~~~

`where.exe codex` は `PATH` の検索結果です。Worker が優先する `CODEX_PATH` のファイルを示すものではありません。また `CODEX_PATH` を設定しても `PATH` には追加されません。単なる `codex` コマンドが使えない、または別バージョンを指す場合でも、以下の例の `& $codexExe` で目的の CLI を呼び出せます。

永続化した環境変数を変更したら、**すべての Visual Studio インスタンスを閉じて起動し直します**。起動済み IDE の環境は更新されません。ターミナルでの `$env:CODEX_PATH` だけの指定は、そのターミナルとそこから起動した子プロセスにのみ適用されます。

## 3. 同じ CLI で認証する

Visual Studio と同じ Windows アカウント、同じ Codex ホームで実行します。

~~~powershell
$codexExe = [Environment]::GetEnvironmentVariable('CODEX_PATH', 'User')
& $codexExe login
& $codexExe login status
~~~

ブラウザーでログインを完了し、status コマンドが成功することを確認します。ブラウザーからのコールバック通信が使えず、アカウント／ワークスペースでデバイスログインが許可されている場合は、次を使用できます。

~~~powershell
& $codexExe login --device-auth
& $codexExe login status
~~~

資格情報は Codex CLI が管理します。拡張機能の設定に記入したり、チャットに貼り付けたりしないでください。App Server に接続できていても、プロバイダーのアカウントが未ログインの場合があります。

Windows の既定の Codex ホームは `%USERPROFILE%\.codex` です。意図して `CODEX_HOME` を使う場合は、ログイン処理と Visual Studio の両方に同じ値を渡し、IDE を再起動します。WSL の Linux ホームにある認証・設定は、Windows ネイティブ CLI の状態とは別です。

API キー認証を使う場合は、既存のシークレット管理手段から標準入力に渡す CLI の `login --with-api-key` を使用します。キーをコマンド引数やリモートプロファイルに書かないでください。ローカル子プロセスには `OPENAI_API_KEY` を渡せますが、任意のプロバイダー固有環境変数がすべて転送されるわけではありません。ターミナルで動くプロバイダーでも、この子プロセスで利用できる認証設定への調整が必要な場合があります。

リモート接続では、**サーバーホストでサーバープロセス実行ユーザーを認証**します。ローカル Windows CLI のログインでは、リモート側のプロバイダー認証は完了しません。

## 4. 3種類の設定を区別する

| 設定 | 保存先・管理主体 | 変更方法 |
|---|---|---|
| CLI 実行ファイル | Windows の `CODEX_PATH`、続いて実行ファイル解決の候補 | 環境変数を設定し Visual Studio を再起動 |
| CLI／プロバイダーの既定値 | `%USERPROFILE%\.codex\config.toml` または `CODEX_HOME\config.toml`、信頼済みプロジェクトの設定層 | 対象 TOML を編集し、起動時設定はローカル App Server を再起動して反映 |
| 拡張機能の選択値・リモートプロファイル | `%APPDATA%\Kkamegawa.CodexForVisualStudio\settings.json` | 拡張機能の操作と Connection target 画面 |
| リモートのプロバイダー・設定 | サーバーユーザーの Codex ホームと起動環境 | サーバー管理者が変更し、必要に応じ外部管理サーバーを再初期化／再起動 |

この拡張機能には汎用的な `config.toml` エディター、任意の App Server 起動引数、CLI の `--profile` 選択画面はありません。**リモート接続プロファイル**は接続先、トークンファイルのパス、ルートを保存するもので、Codex CLI の設定プロファイルとは異なります。

通常のローカル利用では TOML の手動編集は不要です。既定値を明示する場合は、既存設定に必要なキーだけを取り込み、ファイル全体を置き換えないでください。

~~~toml
approval_policy = "on-request"
sandbox_mode = "workspace-write"
model_reasoning_effort = "medium"
~~~

ユーザー設定、信頼済みプロジェクトの `.codex\config.toml`、実行時の上書き、組織が強制する要件が結果に影響します。信頼していないプロジェクトではプロジェクト設定を読み込みません。設定と指示を確認してから信頼するか判断してください。組織ポリシーによって選べる値が制限される場合もあります。

拡張機能のモデル・推論・速度・権限の選択によって、ターンの既定値を上書きできます。設定ファイルだけで判断せず、`/status` で選択値と実効状態を確認してください。汎用の CLI 設定プロファイルと権限プロファイルも別の機能です。

拡張機能の JSON には `ExperimentalApiEnabled`、`ApprovalModeId`、`ReasoningEffortId`、`ServiceTierId`、`RemoteProfiles`、`SelectedRemoteProfileName` を保存します。トークン本体は保存しません。拡張機能の実行中は手動編集を避けてください。リセットする場合は IDE を閉じてバックアップしてから削除します。保存済みプロファイルと選択値もリセットされます。

## 5. ローカルで最初の会話を始める

1. **Extensions > Manage Extensions** から VSIX を導入するか、[README](../README_ja.md)のリポジトリリリースリンクからダウンロードして導入し、Visual Studio を再起動します。
2. Codex に作業させたいソリューションまたはフォルダーを開きます。
3. **View > Codex** を開きます。
4. **Connection target** は **Local** のままにします。リモート一覧が空でも正常です。
5. 読み取り中心なら **Chat**、コマンド実行やファイル変更を伴う作業なら **Agent** を選びます。
6. 既定のモデル、またはサーバーが提示したモデルを選び、「ファイルを変更せず、このソリューションのプロジェクト構成を説明して」などの短い依頼を送ります。
7. 接続状態、表示される CLI バージョン、応答のストリーミングを確認します。必要に応じ `/status` を確認します。

ローカル Worker が `codex app-server` を標準入出力で起動し、管理します。リスナーの手動起動、トークンファイルの作成、ターミナル UI の事前起動は不要です。

主な操作は次のとおりです。

| 操作 | 意味 |
|---|---|
| `/model` | サーバーが返したモデルを表示・選択。利用可否はアカウントにも依存 |
| `/reasoning` | 対応する推論強度を選択。ピッカーの既定値は永続化できる |
| `/fast` | 対応する速度／サービスティアを変更 |
| `/permissions` | 権限モードを表示・選択。`/approve` は別名 |
| `/status` | 現在のセッションと次ターン／実効設定を確認 |
| 入力先頭の `/` メニュー | 組み込みコマンドと利用可能なスキル |
| `#` のファイル候補・添付操作 | ワークスペースのコンテキストを範囲内で追加 |
| Stop | 実行中の作業を停止。未確定状態を確認してから次の依頼を送る |

Agent の権限には **Ask for approval**、**Approve on my behalf**、**Full access**、**Custom (config.toml)**、対応する権限プロファイルがあります。Chat は読み取り専用に固定されます。Full access は Codex のサンドボックスと通常の承認要求を無効化するため確認が必要です。Custom では、そのスレッドにすでに適用した上書きを解除できません。そのようなスレッドを Custom に戻す場合は新しいスレッドを開始します。

**Experimental API** は既定でオフで、次回の初期化／再接続から反映されます。対応する実験 API 機能に必要な場合に有効にしてください。CLI の導入や認証の設定ではありません。明示的な `/shell` コマンドは CLI 0.159.1 でもサンドボックス外で Full access 実行されます。使う前に確認画面と[コマンドガイド](slash-commands_ja.md)を確認してください。

ローカル Windows のサンドボックス設定は、表示された **Check readiness** と **Start setup** を使います。ローカル専用で、リモートホストの設定は行いません。

## 6. Connection target 画面の読み方

| 表示・操作 | 意味 |
|---|---|
| **Connected to: Local**／プロファイル名 | 現在の接続先の表示。実際の稼働状態は別途確認 |
| **Remote profiles (Preview)** | オプションの WebSocket 接続用に保存したメタデータ |
| **No remote profiles. Codex runs on the local app-server.** | 保存済みプロファイルがない状態。ローカル利用は可能 |
| **Add** | 新規プロファイルの編集を開始。接続はしない |
| **Remove** | 選択した保存済み情報を削除。外部サーバーは停止しない |
| **Save profile** | メタデータを検証・保存。トークン読取や接続テストはしない |
| **Check health** | 選択した保存済み・有効なプロファイルのリスナーを診断 |
| **Connect with this profile** | 保存済み・有効なプロファイルを適用し RPC 接続 |
| **Use local app-server** | ローカルの子プロセスへ切り替え |
| **Close** | この画面を閉じる |

「Save the profile before connecting with it」は、保存・有効化を済ませ、未保存の変更がない状態にする必要があるという意味です。プロファイルがない、必須項目が不足・不正、編集が未保存の場合、接続ボタンが無効なのは正常です。一覧で選択しただけ、または保存しただけでは接続先は切り替わりません。ただし保存済みの行を選択すると、次回の Worker 接続／起動に使うプロファイルとして選択が永続化されます。未保存の行は保存済みリモート接続先にはなりません。IDE 再起動後も意図した接続先を確認してください。

添付画面の Preview 文言には、アカウント別・接続先別の状態分離が未提供という注意が含まれます。現行コードには所有者変更時の状態クリアや復旧処理もありますが、この表示は状態分離の全シナリオがリリース検証済みであるという保証ではありません。リモートは Preview として、意図したサーバーとワークスペースに限定して使用してください。

## 7. 認証付きリモート App Server を準備する

Visual Studio のプロファイル入力より**先に**、サーバー管理者が次を用意します。

1. 対応プラットフォームの CLI 0.159.1 完全パッケージと、動作する実行ヘルパー。
2. サーバープロセス実行ユーザーのプロバイダー認証。サーバー上で `login status` を確認。
3. サーバールートから参照できる同じ作業ツリー。既存のストレージ／作業環境の運用で内容を一致させる。
4. 十分なランダム性を持つ Bearer トークンと認証付き WebSocket リスナー。
5. ループバック以外では、WebSocket Upgrade に対応し Authorization を App Server へ渡す TLS 接続先。拡張機能は信頼できる TLS を要求し、証明書検証を無効にできない。
6. Windows PC のローカルドライブ上に、**同じクライアント用 Bearer トークン**を保存した非公開ファイル。

認証は2種類あります。Bearer トークンは App Server への接続を認証し、サーバーの Codex ログインはモデル／プロバイダーの利用を認証します。トークンファイルは `auth.json`、OpenAI API キー、JWT の署名シークレットではありません。

### リスナーを起動する

例では環境固有の値を入力します。この文書の作成作業でサーバーを起動することはありません。ローカル確認、SSH 転送、または同一ホスト上の TLS リバースプロキシではループバックで待ち受けます。直接のリモートプロファイル接続先は `wss://<SERVER_HOST>:<TLS_PORT>/<OPTIONAL_ROUTE>` です。平文の `ws://<LOOPBACK_HOST>:<PORT>` はループバックのみ使用できます。

**Windows サーバーの PowerShell 7** では、非公開の親ディレクトリを先に作り、新しいトークンファイルパスを指定します。32バイトの乱数を64文字の16進数にし、画面に表示せず保存します。

~~~powershell
$serverCodex = (Resolve-Path -LiteralPath (Read-Host 'Server codex.exe path')).Path
$serverVersion = & $serverCodex --version
if ($LASTEXITCODE -ne 0 -or $serverVersion.Trim() -ne 'codex-cli 0.159.1') {
    throw 'Expected codex-cli 0.159.1 on the server.'
}
& $serverCodex login status
if ($LASTEXITCODE -ne 0) { throw 'Sign in as the server process user first.' }
$serverTokenFile = Read-Host 'New absolute local token-file path'
if (-not [IO.Path]::IsPathFullyQualified($serverTokenFile)) { throw 'Use an absolute path.' }
if (Test-Path -LiteralPath $serverTokenFile) { throw 'Choose a new file; do not overwrite a token.' }
$serverToken = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$tokenStream = [IO.File]::Open($serverTokenFile, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
try {
    $tokenBytes = [Text.Encoding]::UTF8.GetBytes($serverToken)
    $tokenStream.Write($tokenBytes)
} finally {
    $tokenStream.Dispose()
    $serverToken = $null
}
$listenUri = Read-Host 'App Server ws listener URI'
& $serverCodex app-server --listen $listenUri --ws-auth capability-token --ws-token-file $serverTokenFile
~~~

通常のアクセス制御の運用でファイルを非公開にしてください。このコマンドはフォアグラウンドで実行され、停止するまで継続します。

**macOS/Linux サーバーの bash** では、公式リリースアセットからそのホスト用の 0.159.1 完全パッケージを先に導入します。次のトークン生成例には OpenSSL が必要です。

~~~bash
read -r -p 'Server codex executable path: ' server_codex
server_version=$("$server_codex" --version) || exit 1
[ "$server_version" = "codex-cli 0.159.1" ] || { printf 'Expected codex-cli 0.159.1.\n' >&2; exit 1; }
"$server_codex" login status || exit 1
read -r -p 'New absolute local token-file path: ' server_token_file
case "$server_token_file" in /*) ;; *) printf 'Use an absolute path.\n' >&2; exit 1 ;; esac
(umask 077; set -o noclobber; openssl rand -hex 32 > "$server_token_file") || exit 1
read -r -p 'App Server ws listener URI: ' listen_uri
"$server_codex" app-server --listen "$listen_uri" \
  --ws-auth capability-token --ws-token-file "$server_token_file"
~~~

既存の承認された安全な転送手段で、このトークンを Windows のローカルドライブ上の非公開ファイルへコピーします。クライアント側で別のトークンを生成してはいけません。signed-bearer-token 方式を使う環境では、クライアントファイルには有効な発行済み Bearer トークンを入れ、サーバーの共有署名シークレットは入れません。

リスナーの `--listen` と、App Server の実行バックエンドを選ぶ `--code-mode-host` は別の設定です。この拡張機能からバックエンドは設定しません。CLI 自身の待受 URI に `wss` を指定できるとは限りません。固定版のヘルプは TCP 待受に `ws` を挙げており、TLS 終端とルーティングは環境側で準備します。

### プロファイルに入力する

**Connection target > Add** を開いて入力します。

| 項目 | 入力する値 |
|---|---|
| **Name** | たとえば `Development server`。空でない一意の名前。大文字小文字を区別せず重複を検査 |
| **Endpoint (wss://, or ws:// for loopback)** | クライアントから接続する絶対 WebSocket 接続先。ルーティングパスは可。URL 内の資格情報、クエリ、フラグメントは不可 |
| **Token file path** | Windows のローカルドライブ上の絶対パス。例：`C:\CodexSecrets\development.token`。UNC／ネットワークドライブは不可 |
| **Local root (this PC)** | ローカルのソリューション／フォルダーを含む絶対ルート。例：`C:\src\sample` |
| **Server root (remote app-server)** | サーバーから見た同じ作業ツリーの絶対パス。例：`/srv/work/sample` または `D:\work\sample` |
| **Enabled** | 新規プロファイルは無効で作成される。接続を許可するためチェックする |

トークンファイルは UTF-8（BOM はあってもなくても可）、最大 **16 KiB**、**32文字以上**の ASCII Bearer トークン1個を格納します。前後の空白は除去しますが、途中の空白・制御文字、不正な文字コードは拒否します。シンボリックリンクやジャンクションでも最終参照先はローカルファイルである必要があります。保存時はパスの書式を確認し、接続時に Worker が読取可否と内容を検査します。

**Save profile**、必要に応じ **Check health**、そしてソリューション／フォルダーを開いた状態で **Connect with this profile** を選びます。ツールバーのプロファイル名、接続状態、`/status` を確認します。まず読み取り専用の依頼を送り、サーバーの作業ディレクトリが意図どおりか確認してください。

### ルート対応付けとヘルスチェックを理解する

ローカルルートが `C:\src\sample`、サーバールートが `/srv/work/sample` の場合：

| ローカルパス | サーバーパス |
|---|---|
| `C:\src\sample` | `/srv/work/sample` |
| `C:\src\sample\src\app.cs` | `/srv/work/sample/src/app.cs` |
| `C:\src\sample-other\app.cs` | ローカルルート外のため拒否 |
| リンク先がワークスペース境界を逸脱するパス | 適用される物理パス包含検査で拒否 |

ルートにはディレクトリを指定し、ソリューションファイルや接続 URL は指定しません。相対成分を維持してサーバーのパス規則で変換しますが、ディレクトリ作成、ファイルコピー、ローカルドライブからサーバー共有への変換は行いません。サーバーの権限とサンドボックスはサーバー側で適用します。

ヘルスチェックは Bearer トークンを送らず、接続先の authority 直下の `/healthz` と `/readyz` を要求します。接続先が `wss://<HOST>:<PORT>/codex` でもルート直下への要求です。プロキシには別途ヘルスルートが必要な場合があります。HTTP 成功だけでは WebSocket Upgrade、トークン、初期化、設定したルートの動作を確認できず、ヘルスチェック失敗でも RPC 接続できる場合があります。

## 8. 再接続・トークン更新・ローカルへの復帰

ローカルの管理対象子プロセスには **Restart local app-server**、リモート接続には **Reconnect remote app-server** を使用します。リモートの再接続ではサーバーを再起動しません。

現行実装は一時的な接続／Worker の消失に対して、最大5回、1回45秒、全体5分の上限で自動復旧を試みることがあります。認証、証明書、プロファイル／ルート、所有者の変更では自動復旧を停止します。読み取り専用 RPC の過負荷リトライは別の上限付き処理です。いずれも到達が不明なユーザー入力、承認、シェルコマンド、添付変更を再送する許可にはなりません。

下書きが復元されたら、復旧した履歴と内容を確認してから明示的に送信します。プロファイル・アカウント・ルートの変更では以前の所有者の会話や入力状態がクリアされる場合があるため、接続先の切り替えで下書きが維持されるとは考えないでください。

トークンを更新するときは、サーバーの認証情報更新と合わせて、クライアントファイルを同じ新トークンで置き換えてから明示的に再接続します。Worker は各ハンドシェイクで再読取しますが、内容の変更だけでは再接続しません。プロファイルのメタデータを編集したら、再度 **Save profile > Connect with this profile** を選びます。適用済み情報が古い、または未保存の場合は Reconnect が拒否されます。

ローカルへ戻るには **Use local app-server** を選びます。Remove は保存済み情報を削除し、残るプロファイルがあれば隣接する行を次回接続用の選択として永続化します。現在の接続は停止・切り替えしません。適用中のプロファイルを削除すると再接続／復旧時に検証できなくなるため、別の保存済みプロファイルを明示的に適用するかローカルを選択してください。

## 9. 設定の問題を調べる

| 症状 | 確認・対処 |
|---|---|
| View > Codex がない | 対応 Visual Studio、拡張機能の有効化、導入後の IDE 再起動を確認 |
| CLI が見つからない／別バージョン | `CODEX_PATH` の実ファイルと `& $codexExe --version` を確認し、全 IDE を再起動 |
| 送信しても応答がない | ソリューション／フォルダーを開き、同じユーザー／ホームでログイン確認。接続状態と診断を確認 |
| code-mode host の起動で全ツールが失敗 | 完全パッケージを導入し、ヘルパー・リソースを維持 |
| WSL や別ターミナルだけログイン済み | ネイティブ環境のユーザー、ホーム、`CODEX_HOME`、実行ファイル、継承環境を一致させる |
| モデルがない／拒否される | サーバーのカタログとアカウント権限を確認。リモートならサーバー CLI のバージョンも確認 |
| config.toml の変更が反映されない | プロジェクトの信頼、設定層、拡張機能の上書きを確認。対象サーバーを再起動し `/status` を確認 |
| Connect／Check health が無効 | 保存済み・有効・必須項目入力済みで未保存変更のないプロファイルを選択。Connect 前にワークスペースを開く |
| トークンファイルがない／読めない／不正 | ローカルの絶対パス、実行ユーザーの読取可否、UTF-8・トークン書式・サイズを確認 |
| 認証が拒否される | クライアント Bearer と接続先の検証情報を一致させ、別途サーバーのプロバイダーログインを確認 |
| 証明書が拒否される | 接続先の信頼、ホスト名、証明書チェーンを修正。検証の回避は不可 |
| Healthy なのに接続できない | WebSocket Upgrade／ルーティング、トークン、RPC 初期化を別々に確認 |
| 添付／作業ディレクトリが拒否される | 両ルートが対象ワークスペースを含むか、相対対応、逸脱するリンクを確認 |
| 編集後に Reconnect が拒否される | 保存して更新後のプロファイルを明示的に適用。古い設定で再試行しない |
| 社内ネットワークで失敗 | 継承したプロキシ・CA 環境変数と Windows プロキシ設定を確認。TLS は無効化しない |

診断は **View > Output** の **Codex Diagnostics**、および `%TEMP%\Kkamegawa.CodexForVisualStudio\diagnostics.log` を確認します。拡張機能／Worker の記録にはタグが付き、機密値は伏せられます。共有前にも内容を確認し、CLI／拡張機能のバージョン、Local／Remote、失敗した手順、分類済みエラーを提示します。トークンファイル、`auth.json`、認証済みの生通信は共有しないでください。

ローカル CLI 子プロセスへの環境変数は固定の許可リストで、`PATH`、`PATHEXT`、`CODEX_PATH`、`CODEX_HOME`、`OPENAI_API_KEY`、プロキシ変数（`HTTP_PROXY`、`HTTPS_PROXY`、`ALL_PROXY`、`NO_PROXY`）、対応する CA 変数などを渡します。すべてのシェル変数が CLI へ渡るという意味ではありません。永続環境変数を変更したら Visual Studio を再起動してください。

## 10. 参照先と検証の範囲

- [README](../README_ja.md)：公式リリースへのリンク、必要環境、簡単な導入手順。
- [スラッシュコマンド](slash-commands_ja.md)：コマンドの書式と権限の意味。
- [リモート接続設計](secure-remote-connection-design_ja.md)、[パス／所有者分離](path-state-isolation-design_ja.md)、[接続・履歴復旧](connection-history-recovery-design_ja.md)：実装の境界。
- [固定接続仕様](../app-server-contract.json)：`targetVersion` と回帰比較用の版。
- OpenAI 公式ドキュメントの項目名：**Codex CLI**、**Authentication**、**Config basics**、**Codex App Server**。環境固有の接続先はプレースホルダーです。管理者が提示した実際の値に置き換えます。スクリプトは実際の値を対話入力します。

このガイドはソース、CLI 0.159.1 の `app-server --help`、公式手順と照合しました。2026-10-12 の同一 PC のループバック試験では、資格情報の読み込み後に所有者パーティションだけが変わり世代が進まないため、UI が **Connecting / Checking account** のままになる製品不具合を確認しました。修正版は所有者世代を進め、初期化イベントより前に確定所有者の Connecting を通知します。SDK で配置した拡張機能 0.2.1.0、完全版 Windows パッケージ 0.159.1、既存の ChatGPT ログインで再検証し、UI のヘルスチェック、Local→Remote の接続（**Ready / Signed in**）、実際のモデル応答、試験用ワークスペース内のマーカーファイル読取、拒否されたトークンを修正した後の手動再接続が成功しました。外部管理のサーバーはその間同じプロセスで稼働しました。リモートプロファイルを選択済みの状態で別途新規に IDE を起動した場合も、**Ready / Signed in**、CLI 0.159.1、**Connected to: Loopback test** が表示され、Worker は外部リスナーに接続し、ローカル CLI 子プロセスは起動しませんでした。別 PC、TLS、異なる OS 間のマッピング、添付、サーバー側のトークンローテーション、新規プロバイダーログインは今回の検証に含みません。
