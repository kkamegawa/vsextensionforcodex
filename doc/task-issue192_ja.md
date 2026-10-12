# Issue #192: 対話カードのテーマ色と継続する Plan モード

[English work record](task.md) · [設計・計画](interaction-theme-plan_ja.md)

追跡: [#192](https://github.com/kkamegawa/vsextensionforcodex/issues/192)、[#193](https://github.com/kkamegawa/vsextensionforcodex/issues/193)、[#194](https://github.com/kkamegawa/vsextensionforcodex/issues/194)。2026-10-12 承認の計画を実装した。

## 実装

- 対話カードと行コンテナーの文字・背景をVSテーマの組で指定した。RadioButton／CheckBoxは公式VSスタイルを使い、通常時の前景色も指定した。コマンド出力の折りたたみ見出しはStyle Setterで通常色を設定し、ホバー・押下時のトリガーを妨げない形にした。
- 入力欄の折り返し可能な設定列に、双方向バインドとアクセシビリティ名を持つPlan modeチェックを追加した。`/plan`と`/plan <prompt>`は同じ状態を有効にし、ターン成功・失敗・完了・中断では解除しない。チェック解除後の送信は`default`を明示し、実行中のターンは変更しない。
- 会話ごとの状態を同じ所有者の再接続でも保持し、所有者変更時は破棄する。非選択会話の待機コマンドも対象会話だけを更新する。ターン開始前の非同期処理より先にモードを取得し、開始中の変更は次のターンへ適用する。
- 既存のモデル選択を維持し、必須モデルが空の場合は推測せず明示的に失敗させる。パッケージ、承認ポリシー、秘密情報の除去処理は変更しない。

## 検証

| 検証 | 結果 |
|---|---|
| Release Core | 421成功、5スキップ、0失敗 |
| Release UI | 419成功、1スキップ、0失敗 |
| Debug／Release全体ビルド | どちらも警告0、エラー0 |
| Release VSIX | ID、DLLハッシュ、Worker契約、生XAMLとソースのバイト一致に合格 |
| 補助WPF描画 | 代表的なDark／Light／High Contrast辞書、400／1100px、Planチェック有無の12通りに合格。文字色と折り返しを実行時に検査し、代表画像を目視確認 |
| Wiki | 英日計画とHomeを登録。公開44リンクに失敗なし。Wiki commit: `71ebb7c` |

TRXとビルド・パッケージ証跡は`artifacts/issue192`に保存した。6件のスキップは既存の環境依存シンボリックリンクテストであり、成功件数に含めない。

補助描画は本番XAMLの断片を使うが、VSのスタイルリソースを標準WPFのベーススタイルへ置き換えるため、VS本体のテンプレート検証ではない。本セッションではネイティブアプリの操作・撮影を使えず、Experimental InstanceでのDark／Light／Blue／High Contrast、実行中テーマ変更、ホバー・押下・無効・フォーカス、キーボード操作、狭い幅のRemote UI表示は未確認。最終表示合格とは記録せず、追跡Issueは開いたままにする。

## レビュー指摘の修正、Codex 形式の提案計画、1問ずつの質問カード

追跡: [#195](https://github.com/kkamegawa/vsextensionforcodex/issues/195)、[#196](https://github.com/kkamegawa/vsextensionforcodex/issues/196)。

- codex 0.161.0 で、実験的でない接続の `turn/start.collaborationMode` が "requires experimentalApi capability" で拒否されることを確認した。Plan モードは Experimental API 有効時のみ利用可能とし（チェック無効化と理由のツールチップ、`/plan` の案内）、Worker もその接続では `default` を省略し `plan` を送信前に拒否する。
- 必須の collaboration mode でモデルが未選択の場合、仮の送信行を追加した後に例外を投げる動作をやめ、送信前に状態メッセージで拒否して入力欄を保持する。必須でない場合は従来どおり app-server の既定モデルに委ねる。
- Plan モード単独では隔離下書きを作らない。`/plan` による状態変更は UI コンテキストで現在の所有者に対してのみ行う。会話未選択時の選択は作成した会話へ移す。会話ごとの状態は有効な会話だけを保持し、複製せず導出する。
- 提案計画（実際の Plan モード出力として確認した `item/plan/delta` と完了した `plan` 項目）を「Proposed plan」の Markdown ブロックとして表示する。Plan ターン完了後は「Implement this plan?」カードで、default モードへ切り替えて「Implement the plan.」を送るか、Plan モードを維持するかを選べる。会話変更や次のターンで失効する。
- 保留中の質問は1問ずつ回答する（「Question n of N」、Back／Next、最後の質問で Submit、全回答を一括送信）。保留カードはピクセル単位でスクロールし、高いカードでも操作ボタンへ到達できる。
- Plan の利用可否と送信モードは、保存済み設定ではなく現在の接続が交渉した capability（`ConnectionTargetSnapshot.ExperimentalApi`、契約バージョン 23）に従う。再接続前の設定変更では Plan は有効にならず、実験的接続での明示的な `default` 送信も止まらない。
- レビュー対応: 会話未選択時の引数なし `/plan` は会話未選択の選択を有効にする。実装確認カードは完了したターンの開始時のモードで判定する。再接続の試行ごとに所有者区画が変わるため、Plan の選択は接続断時に退避し、一時的な復旧で再接続できた場合のみ復元する。
- Plan モードを使うテストは、端末の設定ファイルに依存せず Experimental API を明示する。Release Core 424成功／5スキップ、Release UI 436成功／1スキップ、Debug／Release ビルドは警告0・エラー0。
- 本番テンプレートへ実際の ViewModel をバインドした WPF 描画（Dark／Light）で、質問の1問目と3問目、Submit までスクロールした高いカード、実装確認カード、実プローブ文の提案計画を確認し、全検査に合格、画像を目視確認した。証跡は `artifacts/issue192/live-render/output`。Visual Studio はクリック操作のみ許可され Experimental Instance でプロンプトを入力できなかったため、VS 本体テンプレートでの表示は未確認とする。
