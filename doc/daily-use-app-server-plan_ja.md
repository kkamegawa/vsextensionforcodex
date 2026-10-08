# 日常利用の App Server 機能 — 実装計画

日付: 2026-10-08
トラッキング: [Issue #155](https://github.com/kkamegawa/vsextensionforcodex/issues/155)、[Issue #149](https://github.com/kkamegawa/vsextensionforcodex/issues/149) の配下。
設計: [承認済み設計](daily-use-app-server-design_ja.md) と[英語版](daily-use-app-server-design.md)。
状態: 計画承認済み。実装と検証証跡は `task.md` と `implementation.md` で追跡する。

## 基準と実行ルール

CLI 0.159.1 stable を対象とし、0.155.1 回帰 fixture を保持する。基準 commit は `69deda1`（PR #169 マージ済み）、Worker contract は v20 とし、統合時に次の利用可能版を割り当てる。CLI、SDK、runtime、package は更新しない。#152、#153、#154 の基盤を再利用する。

依存順に package を実装する。各 checkpoint で承認済み設計の上限と trust boundary を守る。並行する source 変更を統合し、他の変更を取り消さない。実装証跡を記録するまで Issue #155 を open とし、Issue #156 を release gate とする。

## パッケージ順序

| パッケージ | 実装範囲 | 依存 | checkpoint |
|---|---|---|---|
| P0 | 使用 method の stable／experimental-described 登録、contract 版更新、固定 0.159.1／0.155.1 schema fixture、型付き DTO と payload registry | — | contract/schema test で wire boundary を確立し、experimental plan delta が任意であることを確認する。 |
| P1 | plan delta/final 統合、上限付き thread/config/model/MCP notice、composer と Worker の catalog/modality 検証、0.159.1 追加 field | P0 | 完了状態の優先、notice 上限、owner race、未対応 turn input の拒否を fixture test で確認する。 |
| P2 | 型付き上限 content、切り詰め情報の分離、PNG/JPEG preview lifecycle、操作時検証付き mapped changed-file Open/Reveal | P0、P1 envelope | 混在結果、不正／過大画像、cleanup、表示後操作前の path 置換／escape を検証する。 |
| P3 | `relaycodex.file.v1` payload reader/writer、既存の上限付き store を使った明示的添付 add/remove、送達結果記録と read-only reconcile | P2 | created/existing、未存在の削除、不正／未知 payload、古い page、missing target、不確実な送達時に再送しないことを検証する。 |
| P4 | 正確な `/shell` parsing、対象／コマンド／cwd の確認、ローカル承認 policy、`thread/shellCommand` dispatch、thread ごとの pending lock と独立 timeout | P0、P1 envelope | 文法、timeout 範囲、拒否、dispatch 前 cancel、他 client の turn、応答不確実性、Stop なし、再送なしを検証する。 |
| P5 | ローカル Windows sandbox 設定 state machine、mode/cwd 確認、generation 単位の完了処理と上限付き秘匿エラー | P0 | 応答／完了の順序、`started` の意味、未対応 method、cwd containment、旧 generation、remote 拒否を検証する。 |
| P6 | package 統合、サブエージェント review、全検証、VSIX 検査、スクリーンショットによる Experimental Instance の画面／accessibility review | P1–P5 | 自動、build、package、画面の全証跡を記録する。未取得のスクリーンショットは未達のままとする。 |

依存が満たされた後は独立 package を並列化できる。P1 は P2 と P4 が共有する envelope であり、owner/generation、catalog、上限付き notice の規則を重複実装しない。

## 検証順序

1. 承認済み設計に列挙した edge case を含め、固定 0.159.1／0.155.1 fixture を使う Fake App Server package-focused test を実行する。
2. 対象 test の成功後、Core と UI の全 test suite を実行する。
3. `TreatWarningsAsErrors=true` のもと Debug と Release を build する。両方とも warning/error を0件にする。
4. 固定 schema 比較、contract used-method 登録、DTO/XAML integrity、VSIX 内容を検査する。
5. Visual Studio Experimental Instance で拡張を起動する。Light、Dark、High Contrast、狭幅、keyboard/focus、accessibility の画面を撮影・確認する。pass/fail と画像証跡を記録する。instance や画像がない場合は visual acceptance を保留のままにする。
6. 実際の件数、コマンド、結果、受容した制限、証跡参照を `implementation.md` と `task.md` に記録する。他 package の結果や source inspection から pass を推測しない。

## 完了条件

P0–P6 の全 checkpoint を完了し、承認済み設計の入力上限、owner、generation、policy、path、再送禁止の条件を満たす。自動、build、contract、package の検査に合格し、必要な画面証跡を記録する。取得できない画面証跡は未完了とする。Issue #155 は Issue #156 の release gate を解除しない。
