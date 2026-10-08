# Issue #156 リリース検証の証拠マップ

このマップは、承認済みのリリース条件を実行可能な検査または明示的な受入証拠へ結び付けます。証拠の保存先を示すものであり、未実行シナリオの合格を示すものではありません。最終結果はリリース候補コミットで記録します。

| シナリオ | 区分 | 証拠の参照先 | 完了条件 |
|---|---|---|---|
| 固定CLI 0.155.1／0.159.1の成果物整合性 | Local-required | [`install-codex.ps1`](../scripts/install-codex.ps1)、`app-server-contract.json` | ダウンロードした成果物のSHA-256が固定値と一致し、報告バージョンも一致する。 |
| 両CLI版のstable／experimental schema生成 | Local-required | [`generate-schemas.ps1`](../scripts/generate-schemas.ps1)、[`compare-schemas.ps1`](../scripts/compare-schemas.ps1)、[`test-schema-cache.ps1`](../scripts/test-schema-cache.ps1) | 4種類のschemaが生成され、記録済み差分との比較とcache検証に合格する。 |
| 使用methodとcache契約 | Local-required | [`verify-contract-surface.ps1`](../scripts/verify-contract-surface.ps1)、[`validate-schema-cache.ps1`](../scripts/validate-schema-cache.ps1) | 対象の両surfaceに必要なmethodがあり、cache metadataが有効である。 |
| Fake App Serverのprotocol／interaction | Local-required | `FakeAppServerIntegrationTests`、`FakeInteractionIntegrationTests`、`CodexSessionServiceTests`、`WorkerRpcServiceTests` | 未分類skipなしでCore suiteに合格する。ネイティブ本人確認は安全に拒否するケースとして扱う。 |
| ローカルRPC、transport、TLS、tokenファイル更新の模擬試験 | Local-required | `JsonLineRpcConnectionTests`、`WebSocketJsonRpcConnectionTests`、`RemoteConnectionSecurityTests`、`TransportPoliciesTests`、`LoopbackTestServer` | 信頼済み／未信頼証明書とtokenファイル更新を含めてCore suiteに合格する。 |
| パス種別とファイルシステム境界 | Local-required | `LocalPathBoundaryTests`、`RemotePathMapperTests`、`ProtectedDirectoryPolicyTests`、`CodexSessionServiceTests`、`ArtifactFileActionsTests.OpenRejectsArtifactWhoseDirectoryLinkWasRetargetedOutsideCurrentRoot` | 必須のsymlink／junctionケースが実行される。必須skipはblockedになる。 |
| ビルドとVSIX payload整合性 | Local-required | [`verify-release.ps1`](../scripts/verify-release.ps1)、[`Test-VsixContents.ps1`](../scripts/Test-VsixContents.ps1) | Debug／Releaseビルドが成功し、VSIX manifest、Worker／contract／protocol assembly、Worker実行payload、埋め込みXAMLが対応する入力と一致する。 |
| CoreとWPF UIの自動テスト | Local-required | [`Invoke-TestSuitesWithEvidence.ps1`](../scripts/Invoke-TestSuitesWithEvidence.ps1)、[`Test-TestSuiteEvidence.ps1`](../scripts/Test-TestSuiteEvidence.ps1)、`tests/Codex.VisualStudio.Core.Tests`、`tests/Codex.VisualStudio.Ui.Tests` | 初回結果に合格し、skip理由が分類される。再試行の成功も`flaky`として失敗扱いにする。実行テスト数が0件の実行は失敗とする。policy scriptはpass、flaky、必須skip、skipと再試行対象の失敗の併存、0件実行、分離不能data row、異常hostを検証する。 |
| ネイティブ本人確認の拒否 | Local-required | `InteractionValidationTests.McpFormParser_RefusesVerificationAndExtensionSchemasWithoutProjectingPayload`、`FakeInteractionIntegrationTests`、`CodexSessionService`の初期化capabilityテスト | capabilityを公開せず、要求を拒否し、challenge/proofを画面へ出さず、拒否後の遅延eventを無視する。成功フローのテストは不要。 |
| 複数の稼働中インスタンス間の状態分離 | Local-required | `ConnectionStatePartitionTests`、`SkillCatalogStoreTests`、`DailyUseArtifactStoreTests`、`WorkerRpcOwnerBoundaryTests.SimultaneousWorkerInstancesInOneWorkspaceKeepMutationsAndNotificationsPartitioned`、下記の同一マシン2インスタンス受入 | owner／partitionの自動テストに合格し、Experimental Instance 2つで会話、下書き、catalog、認証、承認が分離される。 |
| Experimental Instanceの画面・アクセシビリティ受入 | Local-required | Windowsでの手動受入記録とスクリーンショット | Light／Dark／High Contrast、狭幅、各workflow、キーボード／focus、accessible name、Narrator／automation通知、長い履歴の操作を記録する。画面外描画は補助証拠とする。 |
| 認証済み固定CLI通信、実MCP OAuth失効／再認証、実remote TLS／証明書／token更新 | External | Public CI外で実施する手動シナリオ記録。保存するのは秘密除去済み要約のみ | 各シナリオが合格するか、リスク、代替証拠、承認者、日付、承認元を記録した明示承認済みblocked制約とする。 |

build／workflowスクリプトは結果manifest、秘密除去済みの試行別TRX／log、skip理由、再試行結果、成果物hashを保存します。結果が無い場合は`not-run`であり、合格とは扱いません。Public CIではExternalシナリオを実行せず、認証通信の生ログもアップロードしません。

PR #170の件数とhashは過去の証拠として維持し、リリース候補の検証結果の代わりにはしません。過去のWindows実行で発生した6件のskipは、元のテスト結果で原因が確認できない限り推定しません。
