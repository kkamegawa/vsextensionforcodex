# Goal 完了後の強調された確認質問カード

[English work record](task.md#2026-10-16-show-emphasized-goal-confirmation-questions)

対象: [Issue #214](https://github.com/kkamegawa/vsextensionforcodex/issues/214)。依存パッケージ更新の PR #213 のブランチ `chore/2026-10-pkgupdate` を親として、修正ブランチ `codex/214-goal-question-card` を積み重ねます。子 PR のマージ先は親ブランチです。Wiki は作成していません。

## 修正内容

- 既存の `doc/interaction-design.md` の質問カード仕様と承認済みの修正計画に従いました。
- 単独確認質問の判定で、末尾の疑問符を調べる前に Markdown の強調を除去します。実際のログにあった太字の日本語質問も、装飾のない質問と同じ Yes／No カードになります。
- 正規化によってコードフェンス内の単独確認質問を新たに検出しないようにしました。既存の選択肢リストと過去メッセージの扱いを維持します。
- Goal が blocked、接続状態が Ready、実行中ターンがない状態になった後で TurnCompleted が届く順序を再現しました。カード表示や選択だけでは送信せず、Submit で一度だけ回答します。所有者変更前のカードからは送信できません。

## 検証

- パーサーと Goal の重点テスト 28 件が成功しました。実ログの太字質問、英語・日本語の強調形式、コードフェンス内の単独質問、Goal の状態遷移と明示的な送信を含みます。
- Debug／Release のソリューションビルドはいずれも警告 0、エラー 0 です。
- 各構成で Core 451 件成功／5 件スキップ／失敗 0、UI 472 件成功／1 件スキップ／失敗 0 でした。基になるテストプロセスは両方とも終了コード 0 です。
- 既存のシンボリックリンク権限制約による 6 件のスキップが残るため、厳格な証跡ハーネスは blocked を記録しています。全テスト成功のリリース条件は未達です。証跡は `artifacts/issue214` のビルドログ、`debug/test-results.json`、`release/test-results.json`、サニタイズ済みログと TRX にあります。
- 本番質問カード XAML と実際の Goal ViewModel を使った WPF 描画で、未選択状態と Yes 選択後・Submit 前の画像を確認しました。日本語質問全文、Yes／No、Submit／Cancel が表示され、選択だけでは送信せず Submit で Yes を一度送信します。証跡は `artifacts/issue214/visual/bin/Release/net8.0-windows10.0.22621.0` の `screenshots` と `visual-result.txt` です。Visual Studio のスタイルを標準 WPF スタイルに置き換えた検証であり、Experimental Instance や実際の VS ホストテンプレートの合格は主張しません。
- Release VSIX の DLL ペイロード、本番 Remote UI XAML、契約バージョン 24 の検査が成功しました。証跡は `artifacts/issue214/vsix.json` です。
