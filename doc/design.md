# design.md — アーキテクチャ設計決定記録

Session 1・2 での実装・修正作業から得た設計決定と教訓をまとめる。
次のセッション（Codex 等）へ引き継ぐための参照資料。

---

## 1. プロジェクト構成

| プロジェクト | TFM | 役割 |
|---|---|---|
| `Codex.VisualStudio.Extension` | net8.0-windows10.0.22621.0 | OOP 拡張本体（コマンド・ツールウィンドウ・ビジネスロジック） |
| `Codex.VisualStudio.Package` | net472 | in-proc プレースホルダ（将来の差分ビュー等 VSSDK 依存機能用） |
| `Codex.VisualStudio.Worker` | net8.0 | 将来の app-server 仲介役候補（現在は Extension 内 WorkerBridge が直接 spawn） |
| `Codex.VisualStudio.Contracts` | netstandard2.0 | Extension↔Worker 間 RPC 契約 |
| `Codex.AppServer.Protocol` | net8.0 | Codex app-server JSON-RPC 型定義 |
| `Codex.AppServer.Fake` | net8.0 | テスト用フェイク app-server |

---

## 2. OOP 拡張の設計決定

### 2.1 Microsoft.VisualStudio.Extensibility SDK を採用した理由

- Visual Studio 2022 の推奨拡張モデル。クラッシュ時に VS 本体を道連れにしない。
- `RemoteUserControl` / Remote UI でツールウィンドウを VS の WPF プロセスにレンダリングできる。
- `Command`、`ToolWindow` 等を `[VisualStudioContribution]` 属性で宣言的に登録できる。

### 2.2 ExtensionConfiguration.Metadata は必須

OOP モード（`RequiresInProcessHosting = false`）では `Metadata` が `null` だと
CEE0028（コンパイル時評価エラー）で失敗する。最低限の設定：

```csharp
public override ExtensionConfiguration ExtensionConfiguration => new()
{
    Metadata = new(
        id: "Kkamegawa.CodexForVisualStudio",
        version: ExtensionAssemblyVersion,   // 基底クラスのプロパティ
        publisherName: "kazushikamegawa",
        displayName: "Codex for Visual Studio",
        description: "AI coding assistant powered by OpenAI Codex."),
};
```

`RequiresInProcessHosting = true`（in-proc hosted 拡張）にした場合は逆に `Metadata = null` でなければならない。

### 2.3 アセンブリ名と SDK 基底クラス名の衝突

`RootNamespace = "Codex.VisualStudio.Extension"` のとき、
`Extension`（SDK の基底クラス `Microsoft.VisualStudio.Extensibility.Extension`）が
アセンブリと同じ名前空間で解決されず CS0118 になる。

**対処**: エイリアスを使う。

```csharp
using VSX = Microsoft.VisualStudio.Extensibility;

[VSX.VisualStudioContribution]
internal sealed class CodexExtension : VSX.Extension { ... }
```

### 2.4 CA1416（プラットフォーム互換性）の抑制方法

`Microsoft.VisualStudio.Extensibility` SDK の型は「Windows 8.0 以降」とマークされている。
OOP プロセスが `net8.0-windows10.0.22621.0` をターゲットにしているのに
`TreatWarningsAsErrors=true` のため CA1416 がエラーになる。

**対処**: Extension エントリポイントファイルにアセンブリ属性を一度だけ宣言する。

```csharp
// CodexExtension.cs
[assembly: SupportedOSPlatform("windows10.0.22621")]
```

これでプロジェクト全体が Windows 10 以降限定と宣言され、CA1416 が解消する。

### 2.5 コマンド表示名のローカライズ

SDK は `CommandConfiguration` のコンストラクタに生文字列リテラルを渡すと
CEE0027 でエラーにする。

**対処**: `%キー%` 形式 + `string-resources.json` を使う。

```csharp
// Commands/ShowCodexWindowCommand.cs
public override CommandConfiguration CommandConfiguration
    => new("%ShowCodexWindowCommand.DisplayName%")
    {
        Placements = [CommandPlacement.KnownPlacements.ToolsMenu],
    };
```

```json
// string-resources.json（プロジェクトルートに配置）
{
  "ShowCodexWindowCommand.DisplayName": "Codex"
}
```

### 2.6 XAML は EmbeddedResource として埋め込む

`UseWPF=true` を設定すると SDK は XAML ファイルを自動的に `<Page>`（BAML コンパイル）として扱う。
ただし `EnvironmentColors`（`Microsoft.VisualStudio.Shell.15.0` 由来）等 VS 固有型は
Extension プロジェクトで参照できないため、BAML コンパイル時に MC3050 が出る。

**対処**: BAML コンパイル対象から除外して生 XML として埋め込む。

```xml
<Page Remove="ToolWindows\ChatToolWindowContent.xaml" />
<EmbeddedResource Include="ToolWindows\ChatToolWindowContent.xaml">
  <LogicalName>Codex.VisualStudio.Extension.ToolWindows.ChatToolWindowContent.xaml</LogicalName>
</EmbeddedResource>
```

`RemoteUserControl` の SDK がランタイムに VS の WPF プロセスで XAML をロードするため、
`EnvironmentColors` 等は正常に解決される。

**XAML のルート要素は `DataTemplate`**（`x:Class` なし、コードビハインドなし）。
`DataContext` は SDK が `RemoteUserControl` のコンストラクタ引数から自動バインドする。

### 2.7 sealed を付ける

`Extension`・`Command`・`ToolWindow` サブクラスは、外部から継承されないなら `sealed` にする。
`TreatWarningsAsErrors=true` のもとで CA1852 がエラーになるため。

---

## 3. ビルド設定の決定

### 3.1 experimental instance への自動デプロイ

```xml
<!-- Codex.VisualStudio.Extension.csproj -->
<DeployExtension Condition="!('$(BuildingInsideVisualStudio)' == 'true'
                              and '$(Configuration)' == 'Debug')">false</DeployExtension>
<VSSDKTargetPlatformRegRootSuffix>Exp</VSSDKTargetPlatformRegRootSuffix>
<StartArguments>/RootSuffix Exp /log "$(VisualStudioActivityLogPath)"</StartArguments>
```

- VS 内 Debug ビルド → `DeployExtension = true`（VSSDK 既定）→ Exp インスタンスへ自動配置。
- コマンドライン / CI / Release ビルド → `false` → 配置なし。
- `Directory.Build.props` に `DeployToExperimentalInstance` を書いてはいけない（全プロジェクトに漏れる）。

### 3.2 Central Package Management

`Directory.Packages.props` でバージョンを一元管理。各 `.csproj` では `Version=` を省略する。

```xml
<PackageVersion Include="Microsoft.VisualStudio.Extensibility" Version="17.14.2098" />
<PackageVersion Include="Microsoft.VisualStudio.Extensibility.Sdk" Version="17.14.40608" />
<PackageVersion Include="Microsoft.VisualStudio.Extensibility.Build" Version="17.14.40608" />
```

- `Sdk` と `Build` は `<PrivateAssets>all</PrivateAssets>` を付ける（ビルド専用ツール）。
- テストプロジェクトが Extension を ProjectReference で参照すると NU1603 が出る（安全な警告）→ `<NoWarn>` で抑制。
- `Microsoft.Extensions.DependencyInjection.Abstractions` のバージョン競合（MSB3277）は
  `<MSBuildWarningsAsMessages>` で抑制できる。

### 3.3 VSIX マニフェスト（source.extension.vsixmanifest）

- `InstallationTarget` に amd64・arm64 の両 `<ProductArchitecture>` を追加する。
- バージョン上限は `[17.9,)` に開放する（将来の VS をブロックしない）。
- Preview 段階は `<Preview>true</Preview>` を追加する。

---

## 4. ワーカーの埋め込み

Extension プロジェクトの MSBuild ターゲットで Worker を VSIX に含める。

```xml
<Target Name="BuildCodexWorker" BeforeTargets="GetVsixSourceItems">
  <MSBuild Projects="../Codex.VisualStudio.Worker/Codex.VisualStudio.Worker.csproj"
           Targets="Build" Properties="Configuration=$(Configuration)" />
  <ItemGroup>
    <VSIXSourceItem Include="../Codex.VisualStudio.Worker/bin/$(Configuration)/net8.0/**/*.*"
                    Exclude=".../**/*.pdb">
      <VSIXSubPath>Worker/%(RecursiveDir)</VSIXSubPath>
    </VSIXSourceItem>
  </ItemGroup>
</Target>
```

`WorkerBridge` は Extension 起動時に `Worker/Codex.VisualStudio.Worker.exe` を spawn し、
名前付きパイプ + StreamJsonRpc で通信する。

---

## 5. テストプロジェクト構成

| プロジェクト | TFM | 備考 |
|---|---|---|
| `Codex.VisualStudio.Core.Tests` | net8.0 | プロトコル・ロジックのユニットテスト |
| `Codex.VisualStudio.Ui.Tests` | net8.0-windows10.0.22621.0 | ViewModel のユニットテスト |

UI テストは Extension への ProjectReference を持つため `UseWPF=true` が必要。
NU1603・MSB3277 を `<NoWarn>` / `<MSBuildWarningsAsMessages>` で抑制する。

---

## 6. 既知の問題・今後の検討事項

| 項目 | 状態 | 優先度 |
|---|---|---|
| `CommandPlacement.KnownPlacements.ToolsMenu` を使用中 → View メニューへ移動したい場合は `CommandGroupConfiguration` + `GroupPlacement.VsctParent(...)` で実装 | 暫定 | 低 |
| `WorkerBridge` が Extension 内にある → 将来 Worker プロセスに移動して責務を分離 | 暫定 | 中 |
| `ChatViewModel.OnUiAsync` の `Application.Current?.Dispatcher` → OOP プロセスでは null になるため直接実行（意図的） | 正常動作 | — |
| `Codex.VisualStudio.Package` は空プレースホルダ → 差分ビュー等が必要になったときに実装 | 予定 | 低 |
| DI コンテナへの `AppServerClient` / `CodexSessionService` 登録 → Phase 1 未完了 | 未着手 | 高 |
