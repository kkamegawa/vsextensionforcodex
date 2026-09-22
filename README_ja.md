# Codex for Visual Studio

ローカルの Codex CLI app server を Visual Studio 内から起動し、チャットツールウィンドウとして利用できるようにする拡張機能です。ストリーミング応答、承認を伴うコマンド実行とファイル変更、スラッシュコマンド、カスタムスキルの呼び出し、ワークスペースコンテキストの受け渡しに対応します。

本拡張はアウトプロセスの `Microsoft.VisualStudio.Extensibility` 拡張 (`net8.0`) で、`net8.0` のワーカープロセスを起動します。ワーカーが `codex app-server` の子プロセスを保持し、改行区切りの JSON-RPC を stdio 経由でやり取りします。資格情報を Visual Studio 側で扱うことはありません。
サインインは Codex CLI で行います。

## 使い方

[![Codex for Visual Studio の使い方](https://img.youtube.com/vi/J5mvALbV8Mk/0.jpg)](https://youtu.be/J5mvALbV8Mk)

## 動作要件

- Windows (x64 または Arm64)
- Visual Studio 2022 17.14 以降、または Visual Studio 2026 (Community / Professional / Enterprise)
- [最新の安定版リリース](https://github.com/openai/codex/releases/latest) に含まれる公式 Windows x64 Codex CLI 実行ファイル
- `codex login` でサインインできる ChatGPT アカウント

## セットアップ

1. [最新の安定版 Codex リリース](https://github.com/openai/codex/releases/latest) から Windows x64 MSVC 実行ファイルをローカルディレクトリへダウンロードします。拡張機能はこの standalone 実行ファイルを直接起動するため、winget は不要です。

   ```powershell
   $codexDirectory = Join-Path $env:LOCALAPPDATA 'OpenAI\Codex\bin'
   New-Item -ItemType Directory -Force -Path $codexDirectory | Out-Null
   Invoke-WebRequest `
     -Uri 'https://github.com/openai/codex/releases/latest/download/codex-x86_64-pc-windows-msvc.exe' `
     -OutFile (Join-Path $codexDirectory 'codex.exe')
   [Environment]::SetEnvironmentVariable('CODEX_PATH', (Join-Path $codexDirectory 'codex.exe'), 'User')
   ```

2. Visual Studio を再起動し、実行ファイルを確認します。現在の latest release は 0.155.1 で、本拡張が使用する app-server 契約のバージョンです。

   ```powershell
   codex --version
   ```

3. ターミナルから一度サインインします。Visual Studio 側が資格情報に触れることはありません。

   ```powershell
   codex login
   ```

4. 本リポジトリの最新リリースから `Codex.VisualStudio.Extension.vsix` をダウンロードし、
   ダブルクリック、または **拡張機能 > 拡張機能の管理** からインストールします。

5. Visual Studio を再起動し、**表示 > Codex** を開きます。

6. ソリューションまたはフォルダーを開き、プロンプトを送信します。最初のターンでワーカーと
   `codex app-server` の子プロセスが起動します。反応がない場合は [FAQ](#faq) を参照してください。

## 制限事項

- **他の Codex CLI バージョンは対象契約ではありません。** 動作確認済みバージョンは 0.155.1 です。0.154.0 は schema 回帰比較の基準としてのみ保持します。その他のビルドは app-server のプロトコル形状が異なるため、`initialize` や `turn/start` が失敗したり、イベントが欠落したりします。
- **codex が複数インストールされていると、意図しないバージョンが起動することがあります。**
  バージョン管理ツール (mise)、winget、npm、Codex デスクトップアプリはそれぞれ別の場所に `codex`実行ファイルを配置し、`PATH` 上で先に見つかるものが最新とは限りません。ワーカーは次の順序で実行ファイルを解決します。
  1. 環境変数 `CODEX_PATH`
  2. ワーカーオプションで指定された明示パス
  3. `PATH` 上の `codex.exe` (`WindowsApps` の実行エイリアスは除外)
  4. `%LOCALAPPDATA%\OpenAI\Codex\bin`

  `where.exe codex` ですべての候補を確認できます。複数表示される場合は使用したい実行ファイルを`CODEX_PATH` に設定し、Visual Studio を再起動してください。
- **npm 経由のインストールは推奨しません。** `@openai/codex` npm パッケージはこの構成で問題が出ることが分かっています。Node.js の更新後にシムが解決できなくなり、app server が起動直後に終了します。公式 standalone リリース実行ファイルを使用してください。
- **スキル機能は Codex CLI に依存します。** スキルは app server の `skills/list` から取得します。CLI が
  未実装の場合、`Skills` グループにはカタログが利用できない旨が表示され、そのセッションの間は組み込み
  コマンドのみでスラッシュメニューが動作します。`interface.iconSmall` で宣言されたスキルアイコンは
  描画されず、すべての行が固定グリフを使用します。スキル固有の承認要求は許可せず拒否します。
- app-server 契約は 0.155.1 を基準に検証しています。CI は最新安定版を runner のローカル一時ディレクトリへ取得して実行 smoke を行いますが、schema 生成は再現性のため manifest の固定バージョンを使用します。ローカル実行ファイルは必ず `codex --version` で確認してください。
- 本拡張は Windows 上の Visual Studio 専用です。Visual Studio Code 版やクロスプラットフォーム版はありません。

## FAQ

**表示メニューに Codex ツールウィンドウが出てきません。**
Visual Studio が 17.14 以降であること、**拡張機能 > 拡張機能の管理** に本拡張が表示され有効に
なっていることを確認し、VSIX インストール後に Visual Studio を一度再起動してください。

**チャットが応答しない、またはワーカーがすぐ終了します。**
ほとんどの場合は拡張ではなく Codex CLI 側の問題です。ターミナルで `codex --version` (0.155.1)
と `codex login` を確認してください。ターミナルでは成功するのに Visual Studio では失敗する場合は、
別の `codex` が起動しています。[制限事項](#制限事項)のとおり `CODEX_PATH` で固定してください。

**特定の Codex CLI を固定するには？**
環境変数を設定し、変更を引き継ぐために Visual Studio を再起動します。

```powershell
[Environment]::SetEnvironmentVariable('CODEX_PATH', 'C:\path\to\codex.exe', 'User')
```

**ログはどこにありますか？**
`%TEMP%\Kkamegawa.CodexForVisualStudio\diagnostics.log` です。拡張とワーカーが同じファイルに書き込み、それぞれ `[EXTENSION]` と `[WORKER]` のタグが付きます。URL や資格情報らしき値は書き込み前にマスクされます。

**毎回の承認プロンプトを止められますか？**
チャット入力での `/permissions` (別名 `/approve`)、またはツールウィンドウの承認モードピッカーを使用します。組み込みモードは `ask`、`auto`、`full`、`custom` です。`full` は Codex のサンドボックスと通常の承認プロンプトを無効化するため、明示的な確認を求めます。`/model`、`/reasoning`、`/review`などを含むコマンド一覧は [doc/slash-commands_ja.md](doc/slash-commands_ja.md) を参照してください。

**自分の Codex スキルを実行するには？**
チャット入力で `/` を入力します。組み込みコマンドが先に表示され、続いて現在のワークスペースに対して
Codex CLI が返す `Skills` グループが表示されます。スキルを選択するとコンポーザーの上に削除可能なチップが
追加され、コンポーザーは編集可能なまま残るため、プロンプトを追記することも、スキルだけを送信することも
できます。1 ターンに付けられるスキルは 1 つで、別のスキルを選ぶと置き換わります。無効化されたスキルは
表示されますが選択できません。スキルはステアリング用テキストではなく `turn/start` の構造化入力として
渡されるため、ターン実行中は送信が無効になります。完了を待つか、チップを削除してください。

**設定はどこに保存されますか？**
`%APPDATA%\Kkamegawa.CodexForVisualStudio\settings.json` です。承認モード、reasoning effort、サービスティア、実験 API の有効/無効を保持します。ファイルを削除すると既定値に戻ります。破損している場合は無視され、ツールウィンドウがブロックされることはありません。

スキルカタログはこれとは別に `%LOCALAPPDATA%\Kkamegawa.CodexForVisualStudio\skill-catalog\v1` へワークスペース単位でキャッシュされ、CLI の応答を待たずにメニューを開けるようにします。キャッシュ由来の行は古い状態として表示され、最新のカタログを取得するまで選択できません。ターン開始時には必ず最新のカタログで再検証されます。フォルダーを削除しても、次回に 1 回再取得されるだけです。

**プロキシやファイアウォールの設定は必要ですか？**
拡張自体は stdio によるローカル子プロセスとローカル名前付きパイプしか使いません。外部への通信はすべて Codex CLI が行うため、プロキシやファイアウォールの設定は CLI 側の構成で行ってください。

## ビルドとテスト

開発時の前提条件:

- Visual Studio 2022 17.14 以降 (Visual Studio 拡張機能開発ワークロード)
- .NET 8 SDK
- 公式 Codex CLI 0.155.1 実行ファイル (ビルド時の対象プロトコル schema 生成に使用)

復元とビルド:

```powershell
dotnet restore CodexForVisualStudio.slnx
dotnet build CodexForVisualStudio.slnx -c Release --no-restore
```

`schemas/` は Apache-2.0 ライセンスの Codex CLI が生成する出力であり、MIT ライセンスの本リポジトリ
からは意図的に除外しています。cache は `schemas/<version>/<stable|experimental>/` に分離し、CLI version、surface、generator arguments、metadata、schema sentinel がすべて一致する場合だけ再利用します。`schemas/0.155.1/stable/codex_app_server_protocol.schemas.json` が存在しないか古い場合、Windows 上での `Codex.AppServer.Protocol` のビルドが次と同等の処理を自動実行します。

```powershell
pwsh -NoProfile -File scripts/generate-schemas.ps1 -OutputDirectory schemas -Version 0.155.1 -Surface stable -CodexPath $env:CODEX_PATH
```

generator は `CODEX_PATH` を優先し、未設定時は `PATH` 上の `codex` を参照します。選択した stable manifest entry と異なる version は拒否します。CI は固定した Windows x64 の 0.154.0 / 0.155.1 公式 release asset を取得し、`app-server-contract.json` の SHA-256 を検証して stable / experimental の 4 組を生成し、正規化した構造差分を確認します。build と release job はさらに最新安定版の Windows x64 実行ファイルを runner のローカル一時ディレクトリへ取得し、そのパスを `CODEX_PATH` として渡しますが、固定 schema generator は置き換えません。ローカル schema ビルドでは公式 0.155.1 実行ファイルを `CODEX_PATH` に設定してください。

```powershell
$env:CODEX_PATH = "C:\path\to\codex.exe"
dotnet build CodexForVisualStudio.slnx --no-restore
```

単体テストの実行:

```powershell
dotnet test tests/Codex.VisualStudio.Core.Tests/Codex.VisualStudio.Core.Tests.csproj
dotnet test tests/Codex.VisualStudio.Ui.Tests/Codex.VisualStudio.Ui.Tests.csproj
```

app-server の PoC 実行とスキーマの手動生成:

```powershell
dotnet run --project src/Codex.AppServer.Poc/Codex.AppServer.Poc.csproj -- --schema-out schemas --cwd .
```

WindowsApps 経由でインストールされた Codex は、実行エイリアスが子プロセスからブロックされることがあります。その環境では `--codex <standalone-codex.exe のパス>` を指定してください。

VSIX はアウトプロセス拡張プロジェクトが生成します。

```text
src/Codex.VisualStudio.Extension/bin/Release/net8.0-windows10.0.22621.0/Codex.VisualStudio.Extension.vsix
```

`src/Codex.VisualStudio.Package` は将来のインプロセス機能向けの `net472` プレースホルダーで、VSIX は
生成しません。

実装済みの境界と残作業は [doc/implementation.md](doc/implementation.md) を参照してください。

## Visual Studio でのデバッグ

Debug ビルドは既定では配置を行わないため、開発ビルドがインストール済みの Visual Studio を暗黙に書き換えることはありません。

1. Visual Studio で `CodexForVisualStudio.slnx` を開きます。
2. `Codex.VisualStudio.Extension` をスタートアッププロジェクトに設定します。
3. `Debug` 構成を選択し `F5` を押します。ビルド、配置、実験用インスタンスの起動が行われます。
4. 実験用インスタンスで **表示 > Codex** を開きます。

ワーカーは `Codex.VisualStudio.Worker.exe` という子プロセスです。ワーカーのコードをデバッグする場合は **デバッグ > プロセスにアタッチ** から `Codex.VisualStudio.Worker.exe` を選び、マネージド(.NET Core) のコードの種類を指定してください。

## リリース

リリースはタグ駆動です。

1. リリースコミットを `main` にマージします。
2. `vX.Y.Z` タグを push します (ホットフィックスの再公開向けに `vX.Y.Z.W` も使用できます)。
3. リリースワークフローがタグの `main` 所属を検証し、VSIX の `Identity Version` にタグを書き込み
   (`vX.Y.Z` は `X.Y.Z.0` になります)、ビルドとテストを実行して、VSIX を添付した**ドラフトの**
   GitHub リリースを作成します。
4. ドラフトを確認し、準備ができたら GitHub の Releases ページから手動で公開します。タグ push の
   副作用として自動公開されることはありません。

プルリクエストは CI ワークフローで検証され、ソリューションのビルド、両テストプロジェクトの実行、VSIX のビルドアーティファクトへのアップロードが行われます。

## エージェント資産のセットアップ

エージェント資産は Microsoft APM で管理します。`winget` で CLI をインストールします。

```powershell
winget install microsoft.apm
```
```powershell
apm marketplace add github/awesome-copilot
apm install
apm audit --ci --policy apm-policy.yml
```

APM は `.codex/agents/`、`.github/agents/`、`.claude/agents/` を配置します。`apm_modules/` の
依存キャッシュは Git 管理外です。`apm.yml` と `apm.lock.yaml` から `apm install` で再作成できます。

## アーキテクチャ

- UI 層: チャットツールウィンドウ、コンポーザー、承認プロンプト、差分表示
- プレゼンテーション層: チャット ViewModel、ストリーミングバッファー、スラッシュコマンドとスキルを
  統合した候補リスト
- アプリケーション層: セッションライフサイクル、スラッシュコマンドのルーティング、スキルカタログの
  キャッシュと同一性検証、承認ワークフロー、ワークスペースコンテキスト収集
- セキュリティ層: 承認ポリシー、パスアクセス検査、シークレットの秘匿、監査ログ
- プロトコル層: `codex app-server` のプロセスホスト、JSON-RPC ディスパッチ、スキーマとバージョンの
  ガード、通知処理

トランスポートは stdio です。WebSocket や Unix ソケットは将来の選択肢であり、採用する場合もローカルかつ認証付きに限定します。

設計・計画ドキュメント: [doc/design.md](doc/design.md)、[doc/plan.md](doc/plan.md)、
[doc/task.md](doc/task.md)、[doc/implementation.md](doc/implementation.md)、
[doc/adr.md](doc/adr.md)。

## セキュリティ

脆弱性の報告方法は [SECURITY.md](SECURITY.md) を参照してください。`codex app-server` の出力はすべて信頼できない入力として扱い、安全な Markdown パイプラインを通して表示し、ログ出力前に秘匿処理を行います。

## ライセンス

本プロジェクトは MIT ライセンスです。[LICENSE](LICENSE) を参照してください。

英語版: [README.md](README.md)。
