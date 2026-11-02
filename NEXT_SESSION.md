# 次のセッションでやること（2026-11-02 時点）

**Phase C（文字起こし）は受入試験に合格**しました（`docs/phase-c-report.md`）。次は Phase D（要約・構造化）です。

## まだコミットしていない変更（コミットは利用者の指示があってから）

| ファイル | 内容 |
|---|---|
| `src/VoiceJournal.Core/Transcription/TranscriptStore.cs` | 使用中の Revision（`transcripts/current.json`）。`CurrentRevision()` / `LoadCurrent()` / `Adopt()`。`Save()` は使用中になったかを返す |
| `src/VoiceJournal.Core/Transcription/SttJobProcessor.cs` | 区間の再認識は使用中の版の上に置く。保存の競合は `conflict` |
| `src/VoiceJournal.App/ViewModels/TranscriptViewModel.cs`、`Views/TranscriptView.xaml` | `IsLatest` を `IsCurrent` に変更。「この Revision を使う(U)」と「［使用中］」の表示。修正後に全体ジョブが終わったときの案内 |
| `tests/...`（3 ファイル） | 上の動作のテスト（全 272 件成功） |
| `docs/phase-c-report.md`（新規）、`CLAUDE.md`、`NEXT_SESSION.md` | 報告書、コマンドとフェーズの更新 |

コミットの分け方の案:

1. `fix: 修正後に終わった全体ジョブの原文が利用者の修正を隠さないよう、使用中の Revision を記録`（src と tests）
2. `docs: Phase C の受入試験の結果を追加`（報告書、CLAUDE.md、NEXT_SESSION.md）

## 手順

### 1. PC を再起動したあと、カーネルのメモリ増加を切り分ける

評価の時点で、非ページプールが約 10 GB ありました（通常は 0.5 GB 前後）。持ち主のドライバーは分かっていません。

- 主な内訳：`amda` 3.4 GB、`File` 2.5 GB、`IoWI` 1.1 GB、`SQSF` 0.5 GB
- 詳細：報告書の 5 章

切り分けの手順：

1. 再起動の直後に、プールのタグ別の量を記録する。読み取りには `NtQuerySystemInformation(22)` を使う（前回の PowerShell は一時ファイルなので、作り直す）
2. 次の 3 つを同じ時間ずつ行い、それぞれの前後で `amda` / `File` / `IoWI` / `SQSF` の増え方を比べる
   - 何もしない
   - マイクで録音する（`AudioProbe session mic`。**マイクで録音する前に利用者の了承を得る**）
   - LM Studio で推論する（Vulkan）
3. 全部のドライバーを調べ直し、`amda` の持ち主を探す。前回は `amdacp...` という名前に当たっただけで、持ち主の根拠にはならなかった

### 2. Phase D の計画を立てる

- 詳細仕様書の Phase D の章と、23 章の完了条件を読み、`docs/phase-d-plan.md` を作る。進め方は Phase C と同じで、計画を作って利用者の承認を得てから実装する
- Phase C から引き継ぐこと
  - 要約は**使用中の Revision**（`TranscriptStore.LoadCurrent()`）を使う
  - 根拠の参照は「Revision ＋ segment_id」の組にする
  - DB の索引に使用中の版を加えるかを決める（加えるなら、DB の版 3 にする）
  - 数字・日時・人名の取り違えがある（午前→午後、月額→合計 など。報告書の 3 章）。抽出した期限・担当・金額には根拠の区間を示し、承認制で確かめる
  - AT-26 の後半（承認の失効）を Phase D/E で試す

### 3. 残っている確認（急がない）

- 30 分を超える音声の実測（ピースの統合）。長い会議を録ったときに、`Test-PhaseC.ps1 -Test Evaluate` を実行する
- Phase B から引き継いだもの：実機での 8 時間録音、実機でのスリープ
- 検証用の一時データ `%LOCALAPPDATA%\VoiceJournal\verify\phase-c\probe-test` は消してよい。`eval-workspace` は評価の記録なので残す

## 守ること

- チャットは必ず日本語にする。ツール呼び出しの合間の短い文も同じ
- 利用者のマイクで録音するのは、利用者の了承を得てから。スピーカーから不要な音を出さない
- 音声、モデル、DB、`settings.local.json` はコミットしない。ログに文字起こしの本文を残さない
- コミットとプッシュは指示があってから行う。メッセージは日本語で、接頭辞を付ける。リモートは `origin`（https://github.com/KokiTokutake/voice-journal 、非公開）
- Python の文字列に Windows のパスを書くと、`\v` などが制御文字に化ける。Edit を使うか、raw 文字列にする
