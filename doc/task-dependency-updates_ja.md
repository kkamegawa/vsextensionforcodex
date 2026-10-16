# 依存パッケージのセキュリティ更新と系列内更新

作業日: 2026-10-16。追跡: [#212](https://github.com/kkamegawa/vsextensionforcodex/issues/212)。[English](task.md#2026-10-16-dependency-security-updates-with-preserved-major-versions)。

## 承認済みの範囲と実装

既存ブランチ `chore/2026-10-pkgupdate` を使用し、Wiki は作成しない。対象フレームワークと .NET SDK の選択を維持し、直接・推移依存を既存メジャー系列の公開中かつ互換性のある最新版へ更新した。

MessagePack 2.5.302 の脆弱性 GHSA-qhrr-8q5h-9q3h は、Contracts への明示参照と中央バージョン指定2.5.307で解消した。MessagePack.Annotations も各参照先で2.5.307に解決される。

追加依頼では57種類のパッケージ、プロジェクトごとの依存として116件を更新した。`Directory.Packages.props` で中央推移依存の固定機能を有効化し、プロジェクトごとの条件で異なるメジャー系列を維持した。親パッケージの更新によって最小依存バージョンが下がった Memory 4.6.3 と Unsafe 6.1.2 も、既存の最新版を保持する。

| 対象 | System.Text.Json | System.Text.Encodings.Web | System.IO.Pipelines |
| --- | --- | --- | --- |
| Contracts | 8.0.6 を維持 | 8.0.0 を維持 | 8.0.0 を維持 |
| Extension | 9.0.21 | 9.0.21 | 9.0.21 |
| Core / UI テスト | 10.0.13 | 10.0.13 | 10.0.13 |

Contracts の各8系列は確認時点の最新安定版。Extension とテストの .NET 8 ターゲットは維持し、既存の SDK・テスト基盤が要求する9・10系列を更新した。全依存グラフの比較でも、承認していないメジャー変更と対象フレームワークの変更がないことを確認した。

代表的な更新先は、Test SDK 18.11.0、MSTest 4.5.1、Testing Platform 2.5.1、Drawing.Common 8.0.32、既存9系列の DependencyInjection / Composition / Immutable 9.0.21、DependencyModel 10.0.13、Nerdbank.MessagePack 1.4.82、Nerdbank.Streams 2.14.405、PolyType 1.4.1、Newtonsoft.Json 13.0.4。

## 明示承認された例外

- Nerdbank.Streams の更新に必要な Threading ファミリー17系列から18.12.1への移行。
- 非推奨の5系列を使っていた Core/UIテストの DiagnosticSource を、非推奨ではない8.0.1へ移行。Contracts の8.0.1、Extension の9.0.21は維持する。
- DLLを提供しない互換性情報用の NETCore.Platforms 5.0.0を5.0.4へ更新。非推奨であることを明示し、ユーザーが規則の例外を承認した。
- 公開から365日以上経過していても、安全性を確認できた最新版を使用する。

既存ランタイムの制約に合わせ、次の3件は公開中の最新互換版を選択した。

| パッケージ | 選択した版 | より大きい番号を選択しない理由 |
| --- | --- | --- |
| Microsoft.NET.StringTools | 18.4.0 を維持 | 18.6.3以降の安定版は .NET 10/11対応を要求し、.NET 8で未サポート警告を出す。警告の抑制や対象フレームワークの変更はしていない。 |
| Microsoft.VisualStudio.Utilities | 17.13.40008 | 17.14.40264は Drawing.Common 9系列を要求するため、既存8系列と両立しない。 |
| Microsoft.VisualStudio.SDK.Analyzers | 17.7.113 | より大きい17.7.120/122は非公開化されている。 |

## 検証結果

- `nuget-validate` スキルと現在のNuGetメタデータで、選択した版の公開状態と既知の脆弱性がないことを確認した。承認済みの NETCore.Platforms 5.0.4以外は非推奨ではない。ローカルの検証ツールの公開状態参照を `catalogEntry.listed` に修正し、非公開化された Analyzer を選ばないことを確認した。テスト基盤が追加する Mono.Cecil 0.11.6も安全性を確認し、0系列の最新安定版だった。
- 全9プロジェクトの直接・推移依存の再監査は既知の脆弱性0件。証跡は `artifacts/minor-checked-*.json`、`minor-update-nuget-versions.json`、`minor-update-completion-audit.json`、`minor-packages-vulnerable.json`。
- Debug / Release の全ソリューションビルドは、それぞれ警告0・エラー0。`artifacts/minor-build-debug.log` と `artifacts/minor-build-release.log` に保存した。
- Debug / Release の各構成で、Coreは451件成功・5件スキップ・失敗0件、UIは463件成功・1件スキップ・失敗0件。元の `dotnet test` の終了コードはいずれも0。
- シンボリックリンクの権限が必要な既存6件は、各構成で実行できなかった。厳格な `Invoke-TestSuitesWithEvidence.ps1` は終了コード1・`blocked` を記録しており、全テスト必須のリリースゲートを完全に通過したとは扱わない。`artifacts/minor-update-debug/test-results.json`、`artifacts/minor-update-release/test-results.json` と各TRX・秘匿済みログに記録した。
- Release VSIX の Worker 同梱ファイル・埋め込み Remote UI XAML の検査は成功。ルートの39 DLLもビルド出力とハッシュが一致した。`artifacts/minor-update-vsix.json` と `artifacts/minor-update-root-payload.json` に保存した。SDKによるホスト共有DLLの除外は維持される。Extension 本体は Threading / Nerdbank.Streams を直接アセンブリ参照せず、Worker側は Threading 18.12.1（アセンブリ18.7.0.0）と対応する Streams DLL・依存マニフェストを同梱する。新たな Experimental Instance や最小サポート版VS2022実機での受け入れ確認は実施していない。
- 開いているPRの確認では、クローズ対象のDependabot PRはなかった。UIのレイアウトや動作を変更する実装は行っていない。
