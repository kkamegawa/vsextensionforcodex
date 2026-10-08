# 計画書の第13節（Issue #155）

参照先：[計画書](plan.md) / [詳細計画](daily-use-app-server-plan_ja.md)

## 13. Issue #155 日常利用の App Server 機能

承認済みの詳細設計は [daily-use-app-server-design_ja.md](daily-use-app-server-design_ja.md)、英語版は [daily-use-app-server-design.md](daily-use-app-server-design.md) に記録する。実装順序と検証ゲートは [daily-use-app-server-plan_ja.md](daily-use-app-server-plan_ja.md) に従う。

- 基準は CLI 0.159.1、回帰比較は 0.155.1。CLI、SDK、runtime、package は更新せず、Worker contract は統合時の次の利用可能版を使う。
- P0 で contract/schema/DTO/payload registry を整備し、P1 で plan/status/catalog admission、P2 で typed results と mapped actions、P3 で保存済み添付操作、P4 で明示 shell、P5 でローカル Windows sandbox 状態を実装する。P6 で統合 review、全検証、画面証跡を完成させる。
- 完了条件は詳細設計に定める protocol/owner/path/policy 上限、テスト、warning-free Debug/Release、VSIX integrity、Experimental Instance のテーマ・狭幅・keyboard・accessibility 証跡を含む。証跡が未記録の項目は完了としない。Issue #156 の release gate を維持する。
