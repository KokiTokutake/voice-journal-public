# 次のセッションでやること（2026-11-02 時点）

Phase C（文字起こし）の **C5：評価と受入試験** の途中です。C1〜C4 はコミット済み（f361b26 / 46c8232 / 70b7338 / 821a94d）。計画は `docs/phase-c-plan.md`、正本は `docs/VoiceJournal_アプリ詳細仕様書.md`。

## 現在の状態

C5 の評価用の道具は作り終え、試しに動かして確かめました。**まだコミットしていません。**

| ファイル | 状態 | 内容 |
|---|---|---|
| `scripts/stt-eval/meeting-1.txt` | 新規 | 原稿 1：受発注システムの週次定例（約 2700 字、約 10 分） |
| `scripts/stt-eval/meeting-2.txt` | 新規 | 原稿 2：みなと製作所との見積もりの打ち合わせ（約 2600 字） |
| `scripts/stt-eval/meeting-3.txt` | 新規 | 原稿 3：部門会議（予算・採用・勉強会、約 2570 字） |
| `tools/VoiceJournal.AudioProbe/EvaluateCommands.cs` | 新規 | `transcribe`（アプリと同じ処理で 1 回文字起こし）と `cer`（原稿ごとの CER） |
| `tools/VoiceJournal.AudioProbe/SessionCommand.cs` | 変更 | `--prompt-files`（録音中に原稿を表示し、Enter で区切り時刻を記録）と `--out` |
| `tools/VoiceJournal.AudioProbe/Program.cs` | 変更 | `transcribe` / `cer` の登録 |
| `scripts/Test-PhaseC.ps1` | 新規 | `-Test Record` / `Evaluate` / `Check`（BOM 付き UTF-8、CRLF） |

これまでに確かめたこと：

- AT-01 の録音のコピー（`%LOCALAPPDATA%\VoiceJournal\verify\phase-c\probe-test\`）で `transcribe` と `cer` を実行した。small の RTF は 0.06〜0.08、medium は 0.22、CER は 0.99%
- 内部録音（無音）で `--prompt-files` を試し、Enter による区切りが記録された
- 仮のルートを使って `Evaluate -Quick` と `Check` を最後まで動かした。AT-06 と AT-26 は合格。NFR-06 と CER は、音声が 1 分で原稿とも合わないため、想定どおり不合格になった

## 手順

### 1. 利用者に読み上げ録音を依頼する（利用者が実行、約 30〜35 分）

```powershell
.\scripts\Test-PhaseC.ps1 -Test Record
```

- 既定のマイク（USB Microphone）で録音する。原稿は 1 本ずつ表示され、1 本読み終えるごとに Enter を押す。3 本目の後の Enter で録音が終わる
- 結果は `%LOCALAPPDATA%\VoiceJournal\verify\phase-c\record.json` に保存される。3 つの区切り、20 分以上の長さ、`verified` を確かめる
- **録音中は、私が画面を操作したり CPU を多く使う作業をしたりしない。** Enter の入力が別のウィンドウに取られるおそれがある

### 2. 評価を実行する（私が実行、約 30 分、バックグラウンド）

```powershell
.\scripts\Test-PhaseC.ps1 -Test Evaluate
```

- 次の 5 通りの設定で文字起こしする：small-vad-gain（既定）、medium-vad-gain、small-novad-gain、small-vad-nogain、small-novad-nogain
- 結果は `verify\phase-c\eval-<日時>\` に保存される（`evaluation.md`、`*.transcribe.json`、`*.cer.json`、`*.hyp.txt`）
- 次の 4 つを判定する
  - NFR-06：処理時間が音声の長さ以下で、音声が 25 分以上ある
  - CER：既定の small で、原稿ごとに 15% 以下
  - AT-06：small と medium のどちらでも文字起こしが終わり、モデルの SHA-256 が `stt.json` と一致する
  - AT-26：入力は同じで、Revision が別々に作られ、それまでの Revision が変わっていない
- `*.hyp.txt` を読み、誤りの傾向を分析する（数字、人名、否定の言い方、VAD による抜け、2 つのピースのつなぎ目など）
- 約 30 分の録音は 2 つのピース（30 分ごと、重なり 2 秒）に分かれるので、実際のデータで統合の処理を確かめられる

### 3. AT-07 を実機で確かめる（区間の修正と区間の再認識）

- 読み上げ録音とは別に進めてよい。ただし、利用者の録音中は避ける
- AT-01 のコピーのセッションをアプリで開き、区間を 1 つ修正する。続けて、範囲を指定して再認識する
- 画面の操作は、前回の scratchpad にあった `Shoot-Transcript.ps1` / `WindowCapture.ps1` と同じ方法で行う。これらは一時ファイルなので、無ければ作り直す
- 最後に `.\scripts\Test-PhaseC.ps1 -Test Check` を実行し、修正後の Revision の parent / raw / segment_ids をたどれること、元の Revision が変わっていないことを確かめる

### 4. 報告書と文書を更新する

- `docs/phase-c-report.md` を新しく書く。`phase-b-report.md` と同じ形にし、受入試験の結果、CER の表、設定ごとの比較、リスク、残っている課題を載せる
- 報告書に書いておくこと
  - AT-26 の「承認の無効化」は Phase D/E で扱う。Phase C には承認がないため対象外
  - 長い無音にまたがる VAD の区間をどう扱うか
  - Phase B から引き継いだ、まだ確かめていないリスク（実機 8 時間、実機スリープ）
- `CLAUDE.md` を更新する
  - コマンドの節に `Test-PhaseC.ps1` を加える
  - 現在のフェーズの記述を、Phase C 合格・次は Phase D に改める
- 必要なら `docs/environment-lock.md` も更新する（モデルのハッシュ、評価の結果）

### 5. コミット（利用者の指示があってから）

- メッセージの例：`add: Phase C5 評価用の原稿・AudioProbe の transcribe/cer・Test-PhaseC.ps1 を追加`
- 報告書は別のコミットにしてよい：`docs: Phase C の受入試験の結果を追加`
- `verify\phase-c\probe-test` は検証用の一時データなので、最後に消してよい（リポジトリの外、`%LOCALAPPDATA%` の下にある）

## 守ること

- チャットは必ず日本語にする。ツール呼び出しの合間の短い文も同じ
- 利用者のマイクで録音するのは、利用者の了承を得てから。スピーカーから不要な音を出さない
- 音声、モデル、DB、`settings.local.json` はコミットしない。ログに文字起こしの本文を残さない
- コミットとプッシュは指示があってから行う。メッセージは日本語で、接頭辞を付ける
- PowerShell 5.1 の注意：`.ps1` は BOM 付き UTF-8 にする。`Measure-Object -Property {スクリプトブロック}` は使わない。プローブを対話モードで起動するときは `Start-Process -NoNewWindow -Wait -PassThru` を使う（パイプでつなぐと、キー入力を読めない）
