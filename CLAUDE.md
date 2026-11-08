# Voice Journal

Windows ローカル完結の音声記録アプリ。録音 → whisper.cpp で文字起こし → 同梱の llama.cpp（`llama-server`）＋Gemma 4 E2B で要約・構造化 → 課題・タスク候補を承認制で管理ファイルへ反映する。

## 正となる文書

- `docs/VoiceJournal_アプリ詳細仕様書.md` — **設計・受入基準の正本**。手順書と食い違う場合はこちらを優先する
- `docs/VoiceJournal_環境構築仕様書_手順書.md` — 導入手順の参考
- `docs/environment-lock.md` — 実際に導入したツール・モデルの版とハッシュ（詳細仕様書 26 章の採用値）

## 決定事項

- 保存先は `%LOCALAPPDATA%\VoiceJournal`（tools / models / Workspace / config / logs / verify）。**OneDrive 配下にデータを置かない**（詳細仕様書 14.1）。ソースコードだけがこのリポジトリ（OneDrive 配下）にある
- 構成: `src/VoiceJournal.App`（WPF, net10.0-windows）/ `src/VoiceJournal.Core`（net10.0、Windows・NAudio・SQLite・LLM に依存しない）/ `src/VoiceJournal.Infrastructure`（net10.0-windows）/ `tests/VoiceJournal.Tests`（xUnit）
- NuGet の版は `Directory.Packages.props` で一元管理する。版を変えたら `docs/environment-lock.md` も更新する
- 警告はエラー扱い（`TreatWarningsAsErrors`）。テストのみ CA1707（メソッド名のアンダースコア）を許可
- STT は CPU 経路が必須。GPU（Vulkan）・NPU は Phase H の任意最適化
- LLM はアプリが起動・終了する同梱の `llama-server`（ループバック・起動ごとの空きポートとランダムな API キー・使わなければ終了、`docs/phase-d-engine-plan.md`）。実行体・DLL・GGUF は `config\llm-tools.json` に SHA-256 で登録する。LM Studio は 2026-11-06 にアンインストールし、アプリの選択肢も外した
- LLM の試験の前に、エミュレーター・Docker・WSL・Gradle が動いていないか確かめ、動いていれば閉じてよいか利用者に聞く（メモリ不足と PC の発熱のため）
- 管理ファイルは既存なし。アプリが `Workspace\management\issues.md` / `tasks.md` を新規作成する（11.5 の管理ブロック。反映は `ApplyEngine`、変更計画と backup は `Workspace\transactions\<batch-id>\`）。管理項目の表（`items` ほか、マイグレーション 4）は索引ではなく正本で、作り直さない
- 録音の時刻はデバイス位置ではなく、パケットの取得時刻（QPC）で決める。デバイスの時計のずれは原音を補正せず、チャンクの `clock_start_ms` / `clock_end_ms` に時刻写像として残す（`docs/environment-lock.md` 11 章）
- 録音は NAudio 3.x の `WasapiRecorder` を使う（`WasapiCapture` は非推奨）。マイクは raw モードで原音を保存し、STT 用の派生ファイルだけ正規化する方針（`docs/environment-lock.md` 9 章）

## コマンド

```powershell
dotnet build VoiceJournal.slnx
dotnet test VoiceJournal.slnx
.\scripts\New-TestSpeech.ps1          # 日本語 TTS で検証用 WAV を生成
.\scripts\Test-Environment.ps1        # Stage A の疎通確認（whisper まで。LM Studio は削除済みで LLM の段は飛ばす）
.\scripts\Test-AudioSource.ps1 -Source loopback      # システム音声の録音試験（TTS を再生しながら録音）
.\scripts\Test-AudioSource.ps1 -Source mic [-Raw]    # マイクの録音試験（原稿を読み上げる。利用者が自分の端末で実行）
.\scripts\Test-Recording.ps1                        # 本番の録音パイプラインでの実機確認（マイク計測・両方モード・USB 抜き差し。利用者が実行）
.\scripts\Test-PhaseC.ps1 -Test Record|Evaluate|Check  # 文字起こしの評価（原稿 3 本の読み上げ録音 → 5 設定の CER・速度・AT-06/26 → Revision の確認）
```

## 注意

- `.ps1` は日本語を含むため **BOM 付き UTF-8** で保存する（PowerShell 5.1 対策）
- 音声・モデル・DB（`*.wav` `*.bin` `*.gguf` `*.db`）はコミットしない
- DB（`Workspace\db\voicejournal.db`）は索引にすぎない。正本は各セッションの `manifest.json` と音声ファイルで、`SessionIndex.Rebuild()` で作り直せる。スキーマ変更は `Infrastructure/Storage/Migrations.cs` に新しい版を追加し、公開済みの SQL は書き換えない（ハッシュで検査している）
- 文字起こしの正本もファイル。ジョブの台帳は各セッションの `stt/native/<job-id>/job.json`、原文と修正は `transcripts/rev-NNNN/`（書き換え禁止、修正は次の番号）。**使用中の Revision** は `transcripts/current.json`（無ければ最新）。修正・再認識は使用中の版からだけ行い、修正後に終わった全体ジョブの原文は自動では使用中にならない（`TranscriptStore` の remarks）。最新ではなく `LoadCurrent()` を使う。DB の `jobs` / `transcript_revisions` / `segments` は `TranscriptIndex.Rebuild()` で作り直せる索引
- Windows では、開かれているファイルへの置き換え（`File.Move` の上書き）は相手が削除共有を指定していても失敗する。録音中の `manifest.json` やサイドカーを同じプロセスから読まない（`RecordingSession.SnapshotManifestJson()` を使う）。どうしても読むときは `AtomicFile.ReadAllBytes` で短時間だけ開く
- DB や JSON に保存した相対パス、取り込んだファイル名からパスを作るときは `SafePath.Under`（`..`・絶対パス・デバイスパス・代替データストリーム・デバイス名を拒否、詳細仕様書 21.1）を通す
- 現在のフェーズ: Phase A（環境固定・技術検証）は完了（`docs/environment-lock.md` 10 章）。**Phase B（録音・保存）は受入試験に合格**（`docs/phase-b-report.md`。実機 8 時間と実機スリープは未確認のリスクとして残す）。**Phase C（文字起こし）は受入試験に合格**（`docs/phase-c-report.md`。30 分を超える音声は 34.9 分の素材で実測済み、カーネルのメモリ増加の切り分けが残る）。**Phase D（記録生成）は完了**（`docs/phase-d-report.md`。NFR-07 は 2026-11-08 に利用者の判断で 8 分以内に改めて合格）。**Phase E（管理レビュー）と Phase F（安全反映・復旧）は完了**（`docs/phase-e-report.md`・`docs/phase-f-report.md`、計画書 0 章は利用者が承認）、**Phase G（運用・配布）は途中**（`docs/phase-g-report.md`）。2026-11-08 に利用者が「進められるだけ進め、完成を目指して」と指示した。受入の判定では、懸念が残る項目を合格にしない。詳細仕様書 23 章の順に進める
- 受入試験の道具: `.\scripts\Test-PhaseB.ps1 -Test AT01|AT02|AT03`（結果は `%LOCALAPPDATA%\VoiceJournal\verify\phase-b\`）、`.\scripts\Test-PhaseC.ps1`（結果は `verify\phase-c\`）、`.\scripts\Test-PhaseD.ps1 -Test Evaluate|Meeting [-Parallel 2]`（結果は `verify\phase-d\`）、`.\scripts\Test-PhaseF.ps1 -Test Crash`（AT-17。反映中のプロセスを 12 か所で実際に強制終了して復旧。結果は `verify\phase-f\`）。アプリは `--workspace <フォルダー>` で試験用のワークスペースを使える（利用者のワークスペースに試験のデータを残さない）
