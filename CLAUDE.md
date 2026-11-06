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
- LLM はアプリが起動・終了する同梱の `llama-server`（ループバック・起動ごとの空きポートとランダムな API キー・使わなければ終了、`docs/phase-d-engine-plan.md`）。実行体・DLL・GGUF は `config\llm-tools.json` に SHA-256 で登録する。移行期間は設定で LM Studio（`http://127.0.0.1:1234/v1`、`logSensitiveData: false`）も選べる
- LLM の試験の前に、エミュレーター・Docker・WSL・Gradle・LM Studio が動いていないか確かめ、動いていれば閉じてよいか利用者に聞く（メモリ不足と PC の発熱のため）
- 管理ファイルは既存なし。アプリが `Workspace\management\issues.md` / `tasks.md` を新規作成する
- 録音の時刻はデバイス位置ではなく、パケットの取得時刻（QPC）で決める。デバイスの時計のずれは原音を補正せず、チャンクの `clock_start_ms` / `clock_end_ms` に時刻写像として残す（`docs/environment-lock.md` 11 章）
- 録音は NAudio 3.x の `WasapiRecorder` を使う（`WasapiCapture` は非推奨）。マイクは raw モードで原音を保存し、STT 用の派生ファイルだけ正規化する方針（`docs/environment-lock.md` 9 章）

## コマンド

```powershell
dotnet build VoiceJournal.slnx
dotnet test VoiceJournal.slnx
.\scripts\New-TestSpeech.ps1          # 日本語 TTS で検証用 WAV を生成
.\scripts\Test-Environment.ps1        # Stage A の疎通確認（whisper → LM Studio）
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
- 現在のフェーズ: Phase A（環境固定・技術検証）は完了（`docs/environment-lock.md` 10 章）。**Phase B（録音・保存）は受入試験に合格**（`docs/phase-b-report.md`。実機 8 時間と実機スリープは未確認のリスクとして残す）。**Phase C（文字起こし）は受入試験に合格**（`docs/phase-c-report.md`。30 分を超える音声の実測と、カーネルのメモリ増加の切り分けが残る）。**Phase D（記録生成）は受入試験の途中**（`docs/phase-d-report.md`。AT-08 が不合格、NFR-07・NFR-05・AT-26 後半は保留。残りの作業は報告書 6 章）。Phase D を終えてから **Phase E（管理レビュー）** に進む。受入の判定では、懸念が残る項目を合格にしない。詳細仕様書 23 章の順に進める
- 受入試験の道具: `.\scripts\Test-PhaseB.ps1 -Test AT01|AT02|AT03`（結果は `%LOCALAPPDATA%\VoiceJournal\verify\phase-b\`）、`.\scripts\Test-PhaseC.ps1`（結果は `verify\phase-c\`）、`.\scripts\Test-PhaseD.ps1 -Test Evaluate|Meeting -Engine bundled`（結果は `verify\phase-d\`）
