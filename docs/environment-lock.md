# environment-lock — 導入済み環境の採用値

詳細仕様書 26 章「環境構築仕様書との照合・採用値記録表」の実値。版・ハッシュを変えたらこのファイルを更新する。

- 記録日: 2026-10-31
- 記録者: Claude Code（Stage A セッション）

## 1. ハードウェア・OS

| 項目 | 採用値 | 確認方法 |
|---|---|---|
| OS | Windows 11 Home 10.0.26300 x64 | `Get-CimInstance Win32_OperatingSystem` |
| CPU | AMD Ryzen AI 9 HX 375 w/ Radeon 890M | `Win32_Processor` |
| RAM | 31.3 GiB（OS 認識値） | `Win32_ComputerSystem` |
| GPU ドライバー | AMD Radeon 890M 32.0.12010.10001（2025-01-06） | `Win32_VideoController`。**手順書 5.1 では更新推奨。未更新** |
| 保存先ボリューム | C:（NTFS、空き約 500GB） | `Get-PSDrive C` |

## 2. 開発ツール

| 項目 | 採用値 |
|---|---|
| .NET SDK | 10.0.400 |
| TFM | App / Infrastructure / Tests: `net10.0-windows`、Core: `net10.0` |
| ソリューション形式 | `VoiceJournal.slnx` |
| IDE | Visual Studio Community 2026（.NET デスクトップ開発・C++ ツールあり）、VS Code |
| git | 2.56.0.windows.4 |
| FFmpeg（任意） | 9.0.2 full_build（winget `Gyan.FFmpeg`） |

## 3. NuGet（`Directory.Packages.props` が正本）

| パッケージ | 版 | 備考 |
|---|---|---|
| NAudio | 3.3.0 | 詳細仕様書は 2.x のドキュメントを参照。WASAPI API の差分は Phase B で確認 |
| Microsoft.Data.Sqlite | 10.0.14 | SQLitePCLRaw 2.1.13 / エンジン **3.53.3**。FTS5 trigram で日本語部分一致を確認済み（`SqliteCapabilityTests`） |
| xunit / runner | 2.9.3 / 3.1.4 | |
| Microsoft.NET.Test.Sdk | 17.14.1 | |
| JSON Schema 検証器 | **未選定** | 詳細仕様書 3 章「Draft 2020-12 検証能力を確認して固定」。Phase A の残課題 |

## 4. whisper.cpp

| 項目 | 採用値 |
|---|---|
| リリース | `b5454`（v1.9.5 と同時公開、2026-10-06）`whisper-bin-x64.zip`（CPU 版） |
| 配布元 | https://github.com/ggml-org/whisper.cpp/releases/download/b5454/whisper-bin-x64.zip |
| zip SHA-256 | `6ba69e3482d7826214f90a6a9c84ca07782aec1e1d0c6a7c30c994fd5d816ccb` |
| 配置先 | `%LOCALAPPDATA%\VoiceJournal\tools\whisper\b5454\Release\whisper-cli.exe` |
| `--version` | whisper.cpp version: 1.9.5 |
| whisper-cli.exe SHA-256 | `331dba46d6427105d2b802cdbc7eae916ea1c5abf9b0d3b5cfe460d8db8e4366` |
| 選択された CPU バックエンド | `ggml-cpu-cascadelake.dll`（AVX512 / AVX512_VNNI 有効） |
| 確認済み引数 | `-m -f -l ja -t -oj -otxt -of` が存在（`--help`） |

## 5. 音声認識モデル

| 項目 | 採用値 |
|---|---|
| モデル | `ggml-small.bin`（多言語、非 `.en`） |
| 配布元 | https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin |
| サイズ | 487,601,967 bytes |
| SHA-256 | `1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b` |
| 比較候補 | `ggml-medium.bin`（Phase C で CER・所要時間を比較） |

## 6. LM Studio / LLM

| 項目 | 採用値 |
|---|---|
| LM Studio | 0.4.27+2（winget `ElementLabs.LMStudio`） |
| API | `http://127.0.0.1:1234/v1`（ループバックのみ） |
| モデル | （未導入: Gemma 3 4B Instruct GGUF Q4 系） |
| モデル ID / GGUF ファイル / SHA-256 | （未記入） |
| コンテキスト長 C / 出力予約 O | （未記入。初期案 C=8192 / O=4096） |
| Structured Output（json_schema） | （未確認） |
| GPU オフロード | （未記入） |

## 7. 保存先

| 項目 | 採用値 |
|---|---|
| ルート | `%LOCALAPPDATA%\VoiceJournal`（= `C:\Users\USERNAME\AppData\Local\VoiceJournal`） |
| サブフォルダー | `tools\` `models\` `Workspace\` `config\` `logs\` `verify\` |
| 手順書との差分 | 手順書は `C:\VoiceJournal`。詳細仕様書 15.1 の既定値を採用した |

## 8. Stage A 検証結果

| 項目 | 結果 |
|---|---|
| テスト音声 | Windows TTS（Microsoft Haruka Desktop）生成、16kHz/16bit/mono、約 20 秒 |
| whisper small CPU 所要時間 / CER | 3.4 秒（20 秒音声、12 スレッド）/ CER 0（句読点除外の簡易計算）。**TTS 音声のため実音声より楽観的**。実音声での再計測が必要 |
| Gemma 応答時間 / JSON 解析 | （未記入） |
