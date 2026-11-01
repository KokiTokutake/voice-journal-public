# Voice Journal

Windows ローカル完結の音声記録アプリ。録音 → whisper.cpp で文字起こし → LM Studio＋Gemma で要約・構造化 → 課題・タスク候補を承認制で管理ファイルへ反映する。

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
- LM Studio API は `http://127.0.0.1:1234/v1` のループバックのみ。LM Studio のログにプロンプト本文を残さない（`logSensitiveData: false`）
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
```

## 注意

- `.ps1` は日本語を含むため **BOM 付き UTF-8** で保存する（PowerShell 5.1 対策）
- 音声・モデル・DB（`*.wav` `*.bin` `*.gguf` `*.db`）はコミットしない
- DB（`Workspace\db\voicejournal.db`）は索引にすぎない。正本は各セッションの `manifest.json` と音声ファイルで、`SessionIndex.Rebuild()` で作り直せる。スキーマ変更は `Infrastructure/Storage/Migrations.cs` に新しい版を追加し、公開済みの SQL は書き換えない（ハッシュで検査している）
- Windows では、開かれているファイルへの置き換え（`File.Move` の上書き）は相手が削除共有を指定していても失敗する。録音中の `manifest.json` やサイドカーを同じプロセスから読まない（`RecordingSession.SnapshotManifestJson()` を使う）。どうしても読むときは `AtomicFile.ReadAllBytes` で短時間だけ開く
- 現在のフェーズ: Phase A（環境固定・技術検証）は完了（`docs/environment-lock.md` 10 章）。**Phase B（録音・保存）は受入試験に合格**（`docs/phase-b-report.md`。実機 8 時間と実機スリープは未確認のリスクとして残す）。次は **Phase C（文字起こし）**。詳細仕様書 23 章の順に進める
- 受入試験の道具: `.\scripts\Test-PhaseB.ps1 -Test AT01|AT02|AT03`（結果は `%LOCALAPPDATA%\VoiceJournal\verify\phase-b\`）
