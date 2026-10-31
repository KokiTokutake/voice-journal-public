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
| NAudio | 3.3.0 | **3.x では `WasapiCapture` / `WasapiLoopbackCapture` が非推奨**。後継は `WasapiRecorderBuilder` → `WasapiRecorder`（パケットごとに無音・不連続フラグとデバイス位置を受け取れる、`WithRawMode()` でシステムの音声処理を回避できる）。`ISampleProvider.Read` は `Span<float>` 版。詳細仕様書は 2.x の資料を参照しているので、Phase B は 3.x の API で設計する |
| Microsoft.Data.Sqlite | 10.0.14 | SQLitePCLRaw 2.1.13 / エンジン **3.53.3**。FTS5 trigram で日本語部分一致を確認済み（`SqliteCapabilityTests`） |
| xunit / runner | 2.9.3 / 3.1.4 | |
| Microsoft.NET.Test.Sdk | 17.14.1 | |
| Corvus.Json.Validator | 5.7.9 | JSON Schema 検証器（Apache-2.0）。Draft 2020-12 の `$defs`/`$ref`・`prefixItems`・`unevaluatedProperties` と不正出力の拒否を確認済み（`JsonSchemaValidatorTests`）。実行時に Roslyn でコード生成するため依存 DLL が約 11.8MB 増える。`HttpClientDocumentResolver` を含むので、**外部 `$ref` を使わない埋め込みスキーマに限定**し、オフライン動作は Phase G で確認する |

JSON Schema 検証器の比較（2026-10-31）:

| 候補 | 版 | 判断 |
|---|---|---|
| Corvus.Json.Validator | 5.7.9 | **採用**。Apache-2.0、Draft 2020-12 対応 |
| JsonSchema.Net | 9.6.0 | 不採用。ソースは MIT だが、NuGet のバイナリは Open Source Maintenance Fee 契約（収益を伴う利用に料金）の対象 |
| NJsonSchema | 11.6.1 | 不採用。MIT だが Draft 4/6/7 が中心で 2020-12 の検証に向かない |

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
| CLI | `lms`（`%USERPROFILE%\.lmstudio\bin\lms.exe`、CLI commit `43d4b26`） |
| モデル | Gemma 3 4B Instruct（`lmstudio-community/gemma-3-4b-it-GGUF`、Gemma ライセンス、gated なし） |
| 配布元 | https://huggingface.co/lmstudio-community/gemma-3-4b-it-GGUF/resolve/main/gemma-3-4b-it-Q4_K_M.gguf |
| モデル ID | `gemma-3-4b-it`（`lms load --identifier` で固定） |
| GGUF ファイル | `%USERPROFILE%\.lmstudio\models\lmstudio-community\gemma-3-4b-it-GGUF\gemma-3-4b-it-Q4_K_M.gguf`（2,489,757,856 bytes） |
| GGUF SHA-256 | `be49949e48422e4547b00af14179a193d3777eea7fbbd7d6e1b0861304628a01`（HF 公開値と一致） |
| 取得方法 | `lms get` が低速で中断したため `curl -C - --retry 5` で取得。mmproj（画像入力用）は不要のため未取得 |
| コンテキスト長 C / 出力予約 O | C=8192（`lms load --context-length 8192`、モデル上限 131072）/ O=4096（初期案のまま。Phase D で調整） |
| Structured Output（json_schema） | **対応を確認**。`response_format: json_schema`（strict）で日本語要約・タスク・課題を取得し、解析成功 |
| 推論エンジン | `llama.cpp-win-x86_64-vulkan-avx2@2.67.0`（LM Studio 既定選択）。GPU オフロード量は既定値のまま、未調整 |
| 並列数 | 4（LM Studio 既定） |

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
| whisper small CPU 所要時間 / CER | 3.4〜3.8 秒（20 秒音声、12 スレッド）/ CER 0（句読点除外の簡易計算）。**TTS 音声のため実音声より楽観的**。実音声での再計測が必要 |
| Gemma 応答時間 / JSON 解析 | 7.9 秒（prompt 108 / completion 135 tokens、`finish_reason: stop`）/ 解析成功。担当者「田中さん」・期限「来週の金曜日」・課題「既存のパソコンの性能不足」を正しく抽出 |
| JSON Schema 検証 | Gemma の実出力が Draft 2020-12 スキーマに合格（`JsonSchemaValidatorTests.GemmaOutput_IsValid`） |
| 結果ファイル | `%LOCALAPPDATA%\VoiceJournal\verify\stage-a-result.json` |

## 9. 音源単体試験（2026-10-31）

`scripts/Test-AudioSource.ps1` と `tools/VoiceJournal.AudioProbe`（NAudio 3.3.0 の `WasapiRecorder`、共有モード）で録音し、whisper small（CPU）で文字起こしした。原稿は TTS 試験と同じ文。デバイスはいずれも 48kHz / 32bit 浮動小数点 / 2ch。

| 音源 | 条件 | ピーク / 平均 dBFS | STT 秒 | CER |
|---|---|---|---|---|
| ループバック | Speaker (Realtek(R) Audio)、TTS 音声をスピーカー再生 | −1.8 / −14.4 | 3.9 | 0.020 |
| マイク | Microphone Array (AMD Audio Device)、通常モード、実音声 | −4.2 / −28.1 | 3.3 | 0.158 |
| マイク | 同、**raw モード**（`WithRawMode()`） | −26.1 / −53.3 | 3.1 | 0.079 |
| マイク | raw モードの録音をピーク −3 dBFS に正規化（+27 dB） | — | — | 0.069 |

わかったこと:

- **通常モードのマイクは、話していない区間が完全なデジタル無音になる**。Windows または AMD の音声処理（ノイズ抑制・ゲート）が働いており、話し始めの音も削られて CER が悪化したと考えられる。raw モードでは自然な雑音（−65〜−78 dBFS）が残り、CER が半分になった
- raw モードは通常モードより約 25 dB 小さい（通常モードは自動で音量を上げている）。STT 用の 16kHz 版を作るときにソフトウェアで正規化すると、さらに少し改善した
- Phase B の方針案: マイクは raw モードで録音して原音として保存し、STT 用の派生ファイルだけ正規化する。通常モードとの切り替えを設定で選べるようにする
- ループバックは再生していない間パケットが届かない。経過時間に合わせて無音を補うことで、録音長と経過時間が一致した（4.99 秒を補完）
- 録音開始の直後に話し始めると冒頭（「本日」）が欠けた。開始前の待ち時間か録音中表示が必要。通常モードでは開始要求から最初のパケットまで約 1.1 秒の差があった
- 人名などの固有名詞（「田中さん」→「田舎さん」「花子さん」「パナコさん」）は small モデルで誤りやすい。Phase C で medium と比較する
- `device_position_jumps` はマイクでほぼ全パケットに出ており、デバイス位置の単位がフレーム数と一致していない可能性がある。時刻合わせには使わず、Phase B で単位を調べる
