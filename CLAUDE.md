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
- LM Studio API は `http://127.0.0.1:1234/v1` のループバックのみ

## コマンド

```powershell
dotnet build VoiceJournal.slnx
dotnet test VoiceJournal.slnx
.\scripts\New-TestSpeech.ps1          # 日本語 TTS で検証用 WAV を生成
.\scripts\Test-Environment.ps1        # Stage A の疎通確認（whisper → LM Studio）
```

## 注意

- `.ps1` は日本語を含むため **BOM 付き UTF-8** で保存する（PowerShell 5.1 対策）
- 音声・モデル・DB（`*.wav` `*.bin` `*.gguf` `*.db`）はコミットしない
- 現在のフェーズ: **Phase A（環境固定・技術検証）**。詳細仕様書 23 章の順に進める
