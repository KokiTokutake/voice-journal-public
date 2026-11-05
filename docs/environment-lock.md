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
| Microsoft.Data.Sqlite | 10.0.14 | SQLitePCLRaw 2.1.13 / エンジン **3.53.3**。FTS5 trigram で日本語部分一致を確認済み（`SqliteCapabilityTests`）。WAL まわりは `SqliteWalTests` で確認済み（下記） |
| CommunityToolkit.Mvvm | 8.4.2 | ViewModel の通知プロパティ・コマンドの生成（MIT、Phase B5 で追加）。部分プロパティ（`[ObservableProperty] public partial ...`）で使う |
| Microsoft.Extensions.DependencyInjection | 10.0.14 | App でのサービスの組み立て（MIT、Phase B5 で追加）。`Microsoft.Data.Sqlite` と同じ 10.0.14 の保守リリースにそろえた。破棄時に `IAsyncDisposable` だけのサービスがあると同期の `Dispose` が例外になるため、App のサービスは `IDisposable` を実装する |

SQLite の WAL まわりの確認結果（詳細仕様書 14.2 / 15、`SqliteWalTests`、%TEMP% 上のローカル NTFS のファイル DB）:

| 項目 | 結果 |
|---|---|
| `journal_mode=WAL` | 設定でき、DB ファイルに保存される（接続し直しても WAL のまま）。`-wal` ファイルが作られる |
| 反映用接続の設定 | `foreign_keys=ON`、`synchronous=FULL`（2）、`busy_timeout=5000` を設定・読み戻しできる |
| 読み取りと書き込みの並行 | 書き込みトランザクション中でも、別接続の読み取りは止まらず、確定済みの値を読める |
| 書き込み同士の競合 | 先の接続が書き込みロック（`BEGIN IMMEDIATE`）を持つ間、後の書き込みは `SQLITE_BUSY`（5）になり、解放後は成功する |
| バックアップ | Online Backup API（`SqliteConnection.BackupDatabase`）は、チェックポイント前の `-wal` の内容も含めて整合したコピーを作る（`integrity_check` ok）。**`.db` だけを単純コピーすると、`-wal` にある表もデータも失われる**ことを確認 |
| 既定値の注意 | Microsoft.Data.Sqlite の接続は `busy_timeout` が既定で **0**。SQLite 側では待たずに、ライブラリが `CommandTimeout`（既定 30 秒）まで自前で再試行する。仕様の 5000ms にそろえるには、`PRAGMA busy_timeout=5000` と接続文字列の `Default Timeout` を合わせて設定する |
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
| 比較候補 | `ggml-medium.bin`（Phase C で CER・所要時間を比較）。導入済み（12 章） |

## 6. LM Studio / LLM

| 項目 | 採用値 |
|---|---|
| LM Studio | 0.4.27+2（winget `ElementLabs.LMStudio`） |
| API | `http://127.0.0.1:1234/v1`（ループバックのみ） |
| CLI | `lms`（`%USERPROFILE%\.lmstudio\bin\lms.exe`、CLI commit `43d4b26`） |
| モデル | **Gemma 4 E2B Instruct Q4_0**（`ggml-org/gemma-4-E2B-it-GGUF`、Apache-2.0）。2026-11-05 に利用者と合意して Gemma 3 4B から切り替えた（下の「Phase D5 モデルの比較」） |
| 配布元 | https://huggingface.co/ggml-org/gemma-4-E2B-it-GGUF/resolve/main/gemma-4-E2B-it-Q4_0.gguf |
| モデル ID | `gemma-4-e2b-it`（`lms load --identifier` で固定） |
| GGUF ファイル | `%USERPROFILE%\.lmstudio\models\ggml-org\gemma-4-E2B-it-GGUF\gemma-4-E2B-it-Q4_0.gguf`（2,841,481,184 bytes） |
| GGUF SHA-256 | `8e30dff3ac4c8434c49a7036fa15564bdbb6044e42bf04550bf1a096ad7e6a52`（HF 公開値と一致） |
| 取得方法 | `lms get` は ggml-org のリポジトリで MTP 用のファイルだけを選んで失敗したため、`curl -L -C - --retry 5` で直接取得。MTP 用の `mtp-gemma-4-E2B-it-Q4_0.gguf`（SHA-256 `718d3a44…5ef4`）も取得したが、LM Studio は別ファイルの MTP を読まない（「MTP の頭部が同梱された GGUF が必要」）ため削除した。比較に使った E4B の 2 つ（Q4_K_M・Q4_0）も 2026-11-05 に削除。Gemma 3 4B は比べ直し・戻すときのために残している。mmproj（画像入力用）は不要のため未取得 |
| 思考 | Gemma 4 は LM Studio で既定で思考し、出力の約 4 分の 3 を思考に使う。要求に `reasoning_effort: "none"` を付けると切れる（`chat_template_kwargs`・`reasoning`・`enable_thinking` は効かない）。アプリは常に付けて送る |
| 以前の採用値 | Gemma 3 4B Instruct Q4_K_M（`lmstudio-community/gemma-3-4b-it-GGUF`、2,489,757,856 bytes、SHA-256 `be49949e48422e4547b00af14179a193d3777eea7fbbd7d6e1b0861304628a01`）。Phase A〜D4 で使用。抽出の recall が D5 の基準に届かなかった |
| コンテキスト長 C / 出力予約 O | **C=16384 / O=4096**（D2 で C を 8192 から変更、D5 で O を 3072 から変更。O=3072 では Gemma 4 の出力が切れ、区間を半分にしたやり直しが起きた）。区間の大きさの上限は 3,000 トークン |
| Structured Output（json_schema） | **対応を確認**。`response_format: json_schema`（strict）で日本語要約・タスク・課題を取得し、解析成功 |
| 推論エンジン | `llama.cpp-win-x86_64-vulkan-avx2@2.67.0`（LM Studio 既定選択）、AMD Radeon 890M（内蔵 GPU）。**GPU オフロードは `--gpu max`（100%）**。既定の自動ではモデルの大半が GPU に載らなかった（GPU 共有メモリ 841 MB） |
| 並列数 | **1**（`--parallel 1`）。既定の 4 は枠ごとに文脈を確保してメモリを約 1.3 GB 多く使い、並列 2 でも速さは約 1 割しか上がらなかった（出力はメモリ帯域で頭打ち、約 18〜20 トークン/秒） |
| mmap | **オフ**（LM Studio のモデル設定「Try mmap()」、2026-11-02 に利用者が変更）。オンでは GGUF の mmap（約 2.3 GB）と GPU 用のコピーが二重になり、llama-server の作業セットが 5〜8 GB になった。オフで読み込み直後の作業セット約 1.1〜1.3 GB、読み込み 2.8 秒 |
| 読み込みのコマンド | `lms load gemma-4-e2b-it -c 16384 --parallel 1 --gpu max --ttl 600 --identifier gemma-4-e2b-it -y`（推論後の llama-server 約 4.6 GB）。Gemma 3 のときは `gemma-3-4b-it`（読み込み直後: 作業セット 1.2 GB・プライベート 3.7 GB・GPU 共有 約 3.4 GB） |
| 推論によるメモリの増加 | 推論のたびに llama-server のプライベートが増え、待機しても減らない（25 分の会議 1 回で並列 1 は +1.6 GB、並列 2 は +3.5 GB）。読み込み直すと戻る。上限は未確認（プロンプトキャッシュの可能性）。対策の候補は `--ttl`（使わない間に解放） |
| 認証 | **なし**（トークンなし・でたらめなトークンのどちらでも HTTP 200）。詳細仕様書 9 章のとおり任意。使う場合はトークンを Windows 資格情報ストアから読み、ログに書かない |
| 待ち受け | `127.0.0.1:1234`（API）、`127.0.0.1:41343`・`127.0.0.1:61142`（`lms` CLI 等の内部用）。**LAN 向けの待ち受けなし**（`networkInterface: 127.0.0.1`、`cors: false`） |
| 遠隔機能 | LM Link は未ログインのため動いていない（`lms link status`）。MCP サーバーの登録なし（`mcp.json` が空）。NFR-01 を満たす |
| 自動起動 | `autoStartOnLaunch: false`。アプリ起動時に LM Studio のサーバーが動いていない場合の案内が必要（Phase D） |
| JIT ロード | `justInTimeModelLoading: true`。API でモデル ID を指定すると未ロードでも読み込まれる。詳細仕様書 24.2「モデル設定を無断変更しない」に合わせ、アプリは `GET /v1/models` でロード済みか確認してから使う |
| ログ | 当初 `logSensitiveData: true` で、サーバーログ `%USERPROFILE%\.lmstudio\server-logs\` にプロンプト本文（文字起こし）が残っていた。詳細仕様書 21 章の「全文を含むデバッグ記録は明示 ON・期間限定」に合わせ、2026-11-01 に **`false` へ変更**（`.internal\http-server-config.json`、変更前の控えは `.bak-20261101`）。合言葉入りの試験リクエストで、本文がログに出ず `[Sensitive]` と伏せられることを確認した |
| API 予測履歴 | `developer.apiPredictionHistoryEviction`: 30 日で削除 |

### Phase D5 モデルの比較（2026-11-04〜05）

評価セット（`tests/eval/phase-d/`、調整用 3 本・受入用 5 本）と、Phase C の 25 分の会議（D2 と同じ入力）で比べた。C=16384・並列 1・GPU 最大。速さとメモリは同じ要求（会議 10 区間、出力 500〜1,100 トークン）を 2 回送った値。

| モデル | 毎秒のトークン | llama-server | 精度（precision / recall） | 25 分の会議 |
|---|---|---|---|---|
| Gemma 3 4B Q4_K_M | 19 | 1.3〜3.6 GB | 調整用 3 回 0.85 / 0.633、受入用 1 回 0.80 / 0.667 | 未測定（今のプロンプト） |
| Gemma 4 E4B Q4_K_M（lmstudio-community） | 15〜16 | 9.3〜11.3 GB | 調整用 3 回 0.917 / 0.867、受入用 3 回 0.963 / 0.944 | 1,675 秒（O=3072、やり直しあり）→ 997 秒（O=4096、プロンプト 2026-11-05.6） |
| Gemma 4 E4B Q4_0（ggml-org） | 16.7 | 7.9 GB | 未測定 | 未測定 |
| **Gemma 4 E2B Q4_0（ggml-org）** | **29** | **4.6 GB** | 調整用 1 回 0.917 / 0.70 | 未測定 |

- 時間を決めるのは出力の量。E4B の 25 分の会議は出力 13,179 トークンで、NFR-07（300 秒）には届かない
- E4B は録音・whisper・ほかのアプリと並べると空きメモリが 1〜4 GB まで減り、PC の発熱も大きかった（2026-11-05、エミュレーター・Docker・Gradle と同時）。利用者の判断で、軽い E2B を採用した。E2B の recall（0.70）は基準 0.80 に届いていないので、プロンプトの調整を続ける
- 文脈長を下げても、メモリは 0.4〜0.7 GB しか減らない（E4B の見積もり: 16384 で 6.96 GiB、4096 で 6.29 GiB）。メモリの大部分は重み

### Phase D2 実測（2026-11-02、記録の生成）

入力: Phase C の読み上げ録音（会議風の原稿 3 本、24 分 55 秒、178 区間・7,524 字、Rev 13）。プロンプト `2026-11-02.2`。

| 設定 | 区間 | 統合の要求 | 入力 / 出力トークン | 所要時間 | 記録の中身 |
|---|---|---|---|---|---|
| C=8192・O=3072・区間 2,000・並列 1 | 6 | 1 | 12,418 / 約 9,400 | 約 10 分 | 話題が細かく残る。候補 12 件 |
| C=16384・並列 2（C=8192 相当の要求） | 6 | 3（2 段） | 17,101 / 11,478 | 10.4 分 | 同上 |
| C=16384・O=3072・区間 4,000・並列 1 | 3 | 1 | 11,683 / 5,231 | 5.3 分 | 要約と章は保つが細部が落ちる（ヘルプデスク・データ移行など）。候補 5 件 |

- LM Studio は `response_format` のスキーマをプロンプトに入れない（文法で制約）。実際の入力は、スキーマを数えた見積もりの約 1/3 だった。見積もりの係数は実測で約 0.74
- 時間を決めるのは出力の量（約 18〜20 トークン/秒）。区間を大きくすると出力の総量が減る代わりに、1 区間の出力の上限に収まらない話題が落ちる。NFR-07（5 分）と抽出の recall（22.1、0.80 以上）の兼ね合いは、D5 の評価セットで決める
- サーバーログ（`logSensitiveData: false`）に本文は残っていない（会議の語で検索して 0 件）

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
- `device_position_jumps` はマイクでほぼ全パケットに出ており、デバイス位置の単位がフレーム数と一致していない可能性がある。時刻合わせには使わず、Phase B で単位を調べる（→ 11 章で解決。単位はフレーム数で、飛びに見えたのは計測ツールが空の通知も数えていたため）

## 10. 詳細仕様書 26 章との照合（Phase A の採用値）

| 項目 | 詳細仕様書の前提 | 採用値（本書の該当章） | 状態 |
|---|---|---|---|
| OS | Windows 11 x64 | Windows 11 Home 10.0.26300 x64（1 章） | 照合済み |
| CPU / RAM | Ryzen AI 9 HX 375 / 32GB | 同 CPU、31.3 GiB 認識。GPU ドライバーは 2025-01 版のまま（1 章） | 照合済み（ドライバー更新は Phase H の前に判断） |
| SDK / TFM | .NET 10 / net10.0-windows | SDK 10.0.400、Core のみ net10.0（2 章） | 照合済み |
| NAudio | WASAPI 共有、固定版 | 3.3.0。3.x の `WasapiRecorder` を使う（3 章・9 章） | 照合済み。仕様書の 2.x 資料との差分あり |
| whisper.cpp | 固定ビルド、CPU 必須 | b5454（v1.9.5）CPU 版、zip と exe の SHA-256 を記録（4 章） | 照合済み |
| STT モデル | 多言語 small / medium 比較 | `ggml-small.bin`、SHA-256 を記録（5 章）。medium 比較は Phase C | 照合済み |
| LM Studio | loopback API | 0.4.27+2、認証なし、待ち受けは 127.0.0.1 のみ、LM Link・MCP 未使用、本文ログ OFF（6 章） | 照合済み |
| Gemma | 3 4B 量子化 | Gemma 3 4B Instruct Q4_K_M、GGUF の SHA-256 を記録（6 章） | 照合済み |
| API | 127.0.0.1:1234/v1 | `http://127.0.0.1:1234/v1`、json_schema の構造化出力に対応（6 章） | 照合済み |
| context / GPU | 実測後固定 | C=8192 / O=4096、推論エンジンは Vulkan 版を既定選択、オフロード量は未調整（6 章） | 初期値として固定。Phase D・H で再測定 |
| SQLite | Microsoft.Data.Sqlite / WAL | 10.0.14、エンジン 3.53.3、FTS5 trigram・WAL・Online Backup を確認（3 章） | 照合済み |
| JSON Schema 検証 | Draft 2020-12 | Corvus.Json.Validator 5.7.9（3 章） | 照合済み |
| 保存ルート | ローカル NTFS / 同期対象外 | `%LOCALAPPDATA%\VoiceJournal`（7 章）。OneDrive 配下には置かない | 照合済み |
| 管理ファイル | 初回解析＋追記専用が既定 | **既存ファイルなし**（2026-11-01 に利用者へ確認）。アプリが `Workspace\management\issues.md` と `tasks.md` を UTF-8 で新規作成する。既存ファイルの取込み（11.3）は機能として残すが、初期運用では使わない | 確定 |

### Phase A の完了条件（詳細仕様書 23 章）

- **ローカルサンプル 1 本で音声 → STT → 検証済み JSON**: TTS 音声 → whisper（CER 0）→ Gemma の構造化出力 → Draft 2020-12 のスキーマ検証に合格（8 章）。実音声（マイク・ループバック）の録音と STT も確認（9 章）
- **26 章の採用値確定**: 上表のとおり
- 持ち越し: GPU ドライバー更新の判断（Phase H の前）、medium モデルとの比較（Phase C）。`WasapiRecorder` のデバイス位置の単位調査は Phase B3 で完了（11 章）

## 11. Phase B3 実機確認（2026-11-01）

`tools/VoiceJournal.AudioProbe` の `positions`（デバイス位置の計測）と `session`（本番と同じ `RecordingSession` ＋ `WasapiCaptureSource` で録音）、利用者が実行する `scripts/Test-Recording.ps1` で確認した。デバイスは USB ヘッドセット（マイク 48kHz / float / 1ch、スピーカー 48kHz / float / 2ch）。

### デバイス位置と取得時刻

`WasapiRecorder.DataAvailable` が渡す 2 つの位置を、パケットごとに記録して比べた。

| 音源 | デバイス位置 | 取得時刻（QPC 位置） | デバイスの時計（QPC 比） |
|---|---|---|---|
| ループバック | **フレーム数**。ただし**再生していない間は進まない**（38.05 秒の録音で 34.36 秒分しか進まなかった） | 100ns 単位。`Stopwatch` と 1ms 以内で一致 | −156 ppm（37 秒の連続区間） |
| マイク（raw） | フレーム数。10 秒間で飛びなし | 同上 | −31 ppm（10 秒、精度は ±100 ppm 程度） |
| マイク（通常） | フレーム数。10 秒間で飛びなし | 同上 | +47 ppm（同上） |

- `WasapiRecorder` は中身が空の通知（0 フレーム）もほぼ同数出す。Phase A の計測ツールはこれも数えていたため、マイクの位置が毎回飛んで見えた
- **採用した方式**: デバイス位置は時刻合わせに使わない。パケットの取得時刻（QPC 位置を `Stopwatch` の単位に変換）を `IAudioCaptureSource` から渡し、ループバックの無音補完とトラックの開始位置はこれで決める。到着時刻はバッファ長（約 100ms）ぶれるので、取得時刻がない音源のときだけ使う。到着時刻と 2 秒以上ずれた取得時刻は捨てる
- **時計のずれは原音を補正せず記録する**: 各チャンクに、単調時計で測った開始・終了時刻（`clock_start_ms` / `clock_end_ms`）を保存する。サンプル数で数えた `start_ms` との差がドリフトで、マイクとループバックを合わせる時刻写像になる（詳細仕様書 6.3、AT-03）。−156 ppm なら 60 分で約 0.56 秒、8 時間で約 4.5 秒ずれる計算
- `StartRecording()` はオーディオクライアントを同期的に初期化する（呼ぶ前の `LatencyMilliseconds` は 0、呼んだ後は 100）。権限なし・raw 非対応などの失敗は開始時の例外として受け取れる

### 実録音

| 試験 | 結果 |
|---|---|
| ループバック 30 秒（TTS を再生 → 4 秒休止 → 再生）、10 秒チャンク | 録音長と経過時間の差 +10ms。無音補完は再生前（0〜1.8 秒）と休止中（22.2〜25.9 秒）で、再生の時刻と一致。全チャンクのフレーム数と SHA-256 が manifest と一致 |
| 両方モード 60 秒（raw マイク）、20 秒チャンク、途中で USB ヘッドセットを抜き差し | 抜いた時点（約 20.7 秒）でマイクとループバックの両方が「切断」（`DeviceInvalidated`）になり、セッションは「一部音源停止」で継続。挿し直すと同じデバイス ID で再接続し、新しいチャンクから再開（ループバック 40.3 秒、マイク 41.1 秒）。終了時は「確定」、5 チャンクすべて照合一致。録音長と経過時間の差はマイク +4ms、ループバック +16ms |

- マイクは開始要求から最初のパケットまで 0.87 秒かかった（Phase A の通常モードでは約 1.1 秒）。UI は最初のパケットが届くまで「準備中」と表示する（B5）
- この試験で見つけて直した点: 再接続直後の最初のパケットに付く不連続フラグを欠損として記録していた。ループバックが最後まで無音のとき時刻写像が空になっていた（無音補完は単調時計が基準なので、位置＝時計の写像を記録するようにした）


## 12. Phase C2 文字起こしの道具（2026-11-01）

### 追加したモデル

| 項目 | 採用値 |
|---|---|
| medium モデル | `ggml-medium.bin`、1,533,763,059 bytes、SHA-256 `6c14d5adee5f86394037b4e4e8b59f1673b6cee10e3cf0b11bbdbee79c156208`（配布元の LFS ハッシュと一致）。配布元 https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.bin 。配置先 `%LOCALAPPDATA%\VoiceJournal\models\whisper\` |
| VAD モデル | `ggml-silero-v6.2.0.bin`（Silero VAD v6.2.0 の ggml 版）、885,098 bytes、SHA-256 `2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987`（配布元と一致）。配布元 https://huggingface.co/ggml-org/whisper-vad （リポジトリ版 `9ffd54a1`）。配置先 `%LOCALAPPDATA%\VoiceJournal\models\vad\` |
| 登録 | アプリは `%LOCALAPPDATA%\VoiceJournal\config\stt.json` に登録した実行体・モデルだけを使い、毎回 SHA-256 を照合する（`SttToolSettings`。無ければこの章と 4・5 章の値で作る） |

### VAD と時刻（whisper.cpp b5454 の `--vad`）

`verify\phase-c\vad-offset-test.wav`（16kHz。無音 10 秒 → TTS 19.06 秒 → 無音 20 秒 → 同じ TTS → 無音 5 秒）を small で認識した。

| 条件 | 結果 |
|---|---|
| VAD なし | 最初の区間が 0 ms から始まる（実際の声は 10.0 秒から）。2 回しか読んでいない原稿を 3 回分出力した（無音からの幻覚） |
| VAD あり | 最初の区間は 9,990 ms（実際は 10,000 ms）。**時刻は元の音声の時刻で返る**。繰り返しなし |

- VAD ありでも、1 つの区間が無音をまたぐことがある（26.6〜52.2 秒の区間が、1 回目の終わりと 2 回目の始まりを含んでいた）。whisper は声の部分をつないで認識するため。区間の時刻は両端とも正しい
- 方針: VAD は両方の音源で既定オン（`SttConfiguration.UseVad`）。無音をまたぐ区間の扱いは C4 の画面と C5 の評価で判断する

### メモリと速度（Ryzen AI 9 HX 375、12 スレッド、VAD あり、73.1 秒の音声）

| モデル | 所要時間 | 実時間比 | プロセスの最大ワーキングセット |
|---|---|---|---|
| small | 8.3 秒 | 0.11 | 864 MB |
| medium | 24.5 秒 | 0.34 | 2,173 MB |

- 起動前の空きメモリ確認は small 1,024 MB、medium 2,560 MB とした（`RegisteredModel.RequiredMemoryMb`）
- 録音中の一時停止（`NtSuspendProcess`）では、このメモリを持ったまま止まる
- 30 分の音声の見込み: small 約 3.5 分、medium 約 10 分（NFR-06 の目標は small で 30 分以内）。実音声での計測は C5
