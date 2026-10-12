# Issue #156 統合リリース検証 — 作業記録

追跡：[Issue #156](https://github.com/kkamegawa/vsextensionforcodex/issues/156)、親 [Issue #149](https://github.com/kkamegawa/vsextensionforcodex/issues/149)。[英語の設計](release-validation-design.md)／[日本語の設計](release-validation-design_ja.md)、[英語の実装計画](release-validation-plan.md)／[日本語の実装計画](release-validation-plan_ja.md)。

基準は main の b44e856、Worker contract v21、CLI 0.159.1、回帰 fixture 0.155.1。

- [x] 実装と PR #170 の記録を照合し、過去の件数／hash を後続 release candidate の結果と区別する。
- [x] Local-required／External の区分、状態、External blocked の明示承認、retry／skip、秘密除去済み証跡、Windows 受入条件を設計に定義する。
- [x] Issue #155 は close 済みだが、未完了の Experimental Instance 表示／アクセシビリティ受入を #156 の Local-required として引き継ぐ。
- [x] 英日設計・計画、Issue #156、既存文書、Wiki と Home 索引を同期する。
- [ ] 統合 script、CI／Release workflow、不足 test、External 検証、Experimental Instance 受入は後続作業。今回実装・実行していない。

この記録は設計・文書更新のみを対象とし、runtime 検証結果を主張しない。
