# ADR-022: Visual Studio 2026 18.12 以降と .NET 10 だけをサポートする

[English](ADR-022-visual-studio-2026-dotnet-10.md)

- 日付: 2026-11-16
- 状態: 承認済み
- 追跡: [Issue #268](https://github.com/kkamegawa/vsextensionforcodex/issues/268)([#273](https://github.com/kkamegawa/vsextensionforcodex/issues/273)、[#274](https://github.com/kkamegawa/vsextensionforcodex/issues/274))。実装は [Issue #269](https://github.com/kkamegawa/vsextensionforcodex/issues/269) と [Issue #271](https://github.com/kkamegawa/vsextensionforcodex/issues/271)

## 背景

拡張機能は Visual Studio 2022 17.14 以降と .NET 8 に対応していました。Visual Studio 2026 は、アウトプロセスの VisualStudio.Extensibility 拡張機能を .NET 10 で実行します。両方に対応すると、全プロジェクトが .NET 8 のまま、`Codex.VisualStudio.Contracts` も netstandard2.0 のままになり、.NET 10 と C# 14 の機能を使えません。net472 のプロジェクト `Codex.VisualStudio.Package` は、どの機能も使っていない空のプレースホルダーです。

## 決定

- **Visual Studio:** サポートするのは Visual Studio 2026 のバージョン 18.12 以降(amd64 と arm64)だけです。Visual Studio 2022 はサポートしません。
- **ランタイム:** 全プロジェクトが .NET 10 をターゲットにします。Extension と UI テストは `net10.0-windows10.0.22621.0`、Worker、Protocol、Contracts、Fake app-server、PoC、Core テストは `net10.0` です。.NET 8 はサポートしません。
- **プレースホルダー:** `Codex.VisualStudio.Package` を削除します。インプロセスのコンポーネントはありません。
- **Contracts:** `Codex.VisualStudio.Contracts` を netstandard2.0 から `net10.0` に移します。
- **プロジェクト形式:** プロジェクトは `Microsoft.NET.Sdk` と明示的なパッケージ参照のままです。Visual Studio SDK のパッケージは 18.12 のバージョンにします。VSIX のインストール対象と .NET ターゲットは、これらのパッケージとターゲットフレームワークから決まります。生成されたマニフェストが `[18.12,)` と `net10.0` を含まない場合に限り、明示的に指定します。
- **Worker のホスト:** Worker はフレームワーク依存のまま、Extension のランタイムをホストする `dotnet.exe` で起動します。`RollForward` は既定の `Minor` のままにするため、最新の .NET 10 パッチで動作し、他のメジャーバージョンでは動作しません。
- **言語:** コードベースは、適用できる箇所で .NET 10 と C# 14 の機能を使います。

## 代替案

- **Visual Studio 2022 と 2026 の両方をサポートする。** 不採用です。共通のランタイムが .NET 8 のままになり、.NET 10 と C# 14 を使えず、受け入れ確認の組み合わせも 2 倍になります。
- **将来のインプロセス機能のために net472 のプレースホルダーを残す。** 不採用です。すべてのビルドに .NET Framework のビルドが加わる一方、使う機能がありません。将来インプロセスの機能が必要になれば、新しい決定として扱います。
- **Worker を自己完結型にする。** 不採用です。VSIX にランタイム全体が加わりますが、Visual Studio はすでに対応するランタイムを提供しています。
- **Worker を `RollForward=Major` にする。** 不採用です。テストしていないメジャーバージョンで動作すると非互換が隠れます。新しいメジャーバージョンには、いずれにしても Extension のターゲット変更が必要です。

## 結果

- Visual Studio 2022 の利用者は、新しいリリースをインストールできません。
- Contracts は .NET Framework や netstandard の呼び出し側から使えなくなります。
- CI、スクリプト、VSIX の内容検査、文書は .NET 10 のパスを使い、.NET 8 互換のためのパッケージ固定を行いません。
- 設計は [design.md](../design.md) と [plan.md](../plan.md)、このプラットフォーム上のプロトコル対応は [app-server-0-169-design_ja.md](../app-server-0-169-design_ja.md) にあります。

## 決定の履歴

- 2026-11-16: 承認。メンテナーが 2026-11-15 の実装計画でプラットフォームの変更を承認しました。
