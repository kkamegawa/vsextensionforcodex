# Issue #156: 統合リリース検証 — 実装記録

承認済みの[設計](release-validation-design_ja.md)と[実装計画](release-validation-plan_ja.md)を基準に、英日文書、Issue #156、Wiki の同期を行った。追跡：[Issue #156](https://github.com/kkamegawa/vsextensionforcodex/issues/156)、親 [Issue #149](https://github.com/kkamegawa/vsextensionforcodex/issues/149)。基準は main の b44e856、Worker contract v21、CLI 0.159.1、回帰 fixture 0.155.1。

この変更では統合検証 script、CI／Release workflow、不足 test、External シナリオ、Experimental Instance 受入を実装・実行していない。新しい runtime 検証合格は主張しない。

PR #170 の過去証跡は Debug／Release build 警告・エラー 0、Core 399 合格／5 skip、UI 370 合格／1 skip、schema／cache／method と package／XAML／hash 検査である。後続 candidate の合格を示すものではない。Issue #155 は close 済みだが、未完了の Experimental Instance 表示／アクセシビリティ受入は #156 の Local-required として残る。
