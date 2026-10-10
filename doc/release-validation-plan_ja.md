# 統合リリース検証計画

状態：2026-10-09 承認済み。後続実装の計画。基準：main commit b44e856、Worker contract v21、CLI 0.159.1、回帰 fixture 0.155.1。[統合リリース検証設計](release-validation-design_ja.md) の設計を実装する。今回の文書作業では runtime／API を変更しない。

## 作業パッケージ

| Package | 作業 | gate |
|---|---|---|
| P0 — 証跡対応表 | Issue #156 の各条件を既存 test、script、package 検査、手動シナリオ、不足 test に対応付ける。PR #170 は過去の証跡として保持する。 | 全項目に担当、区分、証跡元がある。 |
| P1 — Windows 統合実行 | 固定 CLI の取得／hash、4種 schema、比較／cache／method 検査、Debug／Release build、Core／UI、VSIX 検査、結果 manifest を PowerShell 7 でまとめる。 | Windows のクリーン実行で証跡が揃い、必須失敗で非0終了する。 |
| P2 — CI／Release 結果処理 | 両 workflow へ試行ごとの TRX、失敗 test のみ1回再試行、flaky の不合格化、`if: always()` による秘密除去済み成果物保存を追加する。test 前に link capability を確認する。 | 初回と再試行の結果が両方保存され、suite 再実行で失敗が隠れない。 |
| P3 — 不足 test | P0 で特定した競合、protocol、path、分離、拒否動作の不足分だけを追加する。能力確認済み Windows ID で symlink／junction test を動かし、全 skip を分類する。 | Local-required の自動条件が合格し、skip 理由が明確。 |
| P4 — External 受入 | 固定 CLI 通信、実 MCP OAuth lifecycle、実 remote TLS／証明書／token rotation を公開 CI の外で手動検証する。 | 合格、または明示承認済みで証跡付きの External 制約。 |
| P5 — Experimental Instance | theme、狭幅、keyboard／focus、UI Automation／Narrator、各 workflow state、同一マシン上の2 instance を確認し証跡を保存する。 | 必須画面・アクセシビリティ・分離シナリオすべて合格。 |
| P6 — 最終準備記録 | release candidate の commit で全 matrix を実行し、証跡と承認済み制約を確認して `doc/implementation.md`／`doc/task.md` と Issue #156 を更新する。 | `failed`／`flaky`／`not-run` がなく、Local-required が全合格、External は合格または明示受け入れ済み blocked。 |

## 結果形式

manifest に commit、host／OS、PowerShell／.NET／Visual Studio 版、CLI 版／hash、scenario ID／区分／状態、command、終了コード、pass／fail／skip 件数と理由、retry 結果、artifact path／hash、承認済み External 制約を記録する。秘密除去済み TRX と report を保存し、認証済み通信の生ログを upload しない。

## コマンドとプラットフォーム

全 solution build、UI suite、固定 CLI schema 生成、VSIX 検査、link capability test、Experimental Instance 受入は Windows／PowerShell 7 で実施する。他 OS の Core-only run は任意診断であり、platform skip は Windows 受入にならない。

実装後の Windows PowerShell 7 実行例：

```powershell
pwsh -NoProfile -File scripts/verify-release.ps1 -Configuration Both -OutputDirectory <EVIDENCE_DIRECTORY>
```

Bash 例は Core-only check、または生成済み schema の比較に限る。Windows 対応 Bash 環境で full gate を例示する場合は同じ PowerShell entry point を呼び出す。Linux で Windows CLI、WPF UI suite、Visual Studio が動くかのように示さない。

## 実施順序

1. `app-server-contract.json`、既存 tests／CI、PR #170 の記録をもとに証跡対応表を作成する。
2. 統合実行スクリプトと manifest を実装し、Windows で失敗・skip・retry の記録を確認する。
3. CI と Release workflow に同じ結果判定を適用し、成功・失敗時の成果物保存を確認する。
4. Local-required の不足 test を解消し、実行時入力と秘密除去済み出力で External を検証する。
5. Experimental Instance 受入を完了し、各状態を記録する。
6. release candidate で全 matrix を実行し、最終準備記録と Issue #156 を更新する。

設計書、test 定義、オフスクリーン描画、過去の PR 結果を、最終 candidate の合格証跡として扱わない。
