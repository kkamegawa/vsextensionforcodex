# 設計書の第17節（Issue #155）

参照先：[設計書](design.md) / [日常利用の App Server 機能設計](daily-use-app-server-design_ja.md)

## 17. 日常利用の App Server 機能

[日常利用の App Server 機能](daily-use-app-server-design_ja.md) は、承認済み Issue #155 の設計である。CLI 0.159.1 と 0.155.1 の回帰 fixture を対象にし、Issue #152–#154 の path/owner、復旧、対話の基盤を再利用する。CLI、SDK、runtime、package の版は変更せず、Worker contract は統合時に次の利用可能版を割り当てる。

対象は、任意の experimental plan delta と上限付き状態通知、実行時カタログに基づく入力判定、確認とローカル policy を通す明示的な unsandboxed shell 実行、型付き上限結果と操作時の path 再検証、クライアント所有の保存済み添付 metadata と明示的操作、ローカル Windows のみの sandbox 設定と正確な結果状態である。daemon/worktree lifecycle、Realtime、dynamic tools、ExternalMessage、plugin import/editor、native verification の成功経路、attestation は対象外。

実装と検証は [Issue #155](https://github.com/kkamegawa/vsextensionforcodex/issues/155) で追跡し、package ごとの計画は [daily-use-app-server-plan_ja.md](daily-use-app-server-plan_ja.md) に記録する。Issue #156 を統合 release gate とし、スクリーンショットがない場合は visual acceptance を未完了とする。
