# Voice Journal — Windowsローカル音声記録システム
## 環境構築仕様書・手順書（初版）

- 作成日: 2026-10-31
- 対象: Windows 11 / AMD Ryzen AI 9 HX 375 / Radeon 890M / RAM 32GB
- 目的: 録音 → オフライン文字起こし → ローカルLLMによる要約・Markdown構造化 → 課題・タスク更新
- 状態: **構築計画書**。動作確認済みの実測値や完成アプリを意味しない。

## 1. 基本方針

1. 音声・テキストは原則としてPC内で処理・保存する。インストーラとモデルの初回取得にはネット接続が必要。
2. WSLは使わずWindowsネイティブで構築する。
3. 録音中は録音に専念し、終了後にWhisperとGemmaを順次実行する。
4. 音声認識と文章整理は別プロセスとし、交換可能にする。
5. 原文・音声・整理後Markdownを別々に保存し、AIが推測した事項を事実として確定させない。
6. 課題・タスクは内部DBでIDと状態を管理し、Markdownへ書き出す。更新は差分確認後に確定する。

## 2. 構成・バージョン方針

| 領域 | 採用候補 | 用途・留意点 |
|---|---|---|
| OS | Windows 11 x64 | 標準ユーザーでの実行を原則とする |
| アプリ | .NET 10 SDK / WPF | 開発時はSDK、利用時は自己完結配布も可能 |
| IDE | Visual Studio 2022以降の対応版、またはVS Code | .NET 10をサポートする更新版を選ぶ |
| 録音 | NAudio | マイク入力、WASAPIループバック |
| 音声変換 | FFmpeg（任意） | WAV正規化・サンプルレート変換。配布ライセンスを確認 |
| 文字起こし | whisper.cpp | Windowsネイティブの実行ファイルを利用 |
| 初期音声モデル | multilingual small / medium | 日本語に対応する非 `.en` モデル |
| LLMホスト | LM Studio | ローカルAPIサーバーを有効化 |
| LLM | Gemma 3 4B Instruct GGUF（量子化版） | まずQ4系で評価。モデルの利用条件を確認 |
| DB | SQLite | 記録・課題・タスク・出典の内部管理 |
| 出力 | UTF-8 Markdown | ObsidianやVS Codeで閲覧可能 |

**バージョン固定:** 実際に導入した各ツールのバージョン、モデルファイル名、ハッシュを `environment-lock.md` に記録する。モデルやランタイムの最新バージョン・GPU対応状況は導入時に公式配布元で確認する。

## 3. 推奨ハードウェア設定

- RAM 32GB。GPUは共有メモリを利用するため、WhisperとGemmaを同時高負荷実行しない。
- 初回はWhisperをCPU実行、GemmaをLM Studioで実行する。安定動作確認後にVulkan等のGPUバックエンドを試す。
- NPUは初期必須要件にしない。対応ランタイム・モデル・ドライバーの組み合わせを別途検証する。
- 文字起こし所要時間は録音時間、モデルサイズ、バックエンドに依存する。実測前に「リアルタイム以上」などの保証を置かない。
- 保存容量は録音形式で大きく変わる。16kHz/16bit/mono PCM WAVは概算約115MB/時。ステレオ48kHz/16bit PCMなら約691MB/時。

## 4. ディレクトリ構成

```text
C:\VoiceJournal\
├── app\                    # ビルド済みアプリ
├── tools\
│   ├── whisper\           # whisper.cpp 実行ファイル
│   └── ffmpeg\            # 任意
├── models\
│   └── whisper\           # ggml-small.bin 等
├── data\
│   ├── recordings\        # 録音データ
│   ├── transcripts\       # 原文・タイムスタンプ
│   ├── notes\             # 整理後Markdown
│   ├── management\        # 課題管理.md / タスク管理.md
│   ├── db\                # SQLite
│   └── backups\
├── logs\                   # APIキー・全文音声は原則記録しない
└── config\
    ├── appsettings.json
    └── environment-lock.md
```

`C:\VoiceJournal` に書き込み権限がない場合は `%LOCALAPPDATA%\VoiceJournal` を既定保存先にする。実運用ではアプリ本体とユーザーデータを分離し、データをユーザープロファイル配下に置くことを推奨。

## 5. 構築手順

### 5.1 事前準備

1. Windows Updateを適用し再起動する。
2. AMDのGPUドライバーを更新する（メーカー提供版との互換性を優先）。
3. PowerShellを開き、OS/CPU/メモリを確認する。

```powershell
Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, OSArchitecture
Get-CimInstance Win32_Processor | Select-Object Name
Get-CimInstance Win32_ComputerSystem | Select-Object TotalPhysicalMemory
```

4. Microsoft公式サイトから.NET 10 SDKをインストールする。
5. 必要ならVisual Studioをインストールし「.NET デスクトップ開発」ワークロードを選ぶ。Visual Studioのインストール版が.NET 10に対応することを確認する。

```powershell
dotnet --info
dotnet --list-sdks
```

### 5.2 LM Studio + Gemma

1. 公式サイト https://lmstudio.ai/ からWindows版を入手してインストールする。
2. アプリのモデル検索画面で `Gemma 3 4B Instruct` のGGUF量子化版（Q4系）を探す。
3. 配布者・ライセンス・モデル形式を確認してダウンロードする。
4. モデルを読み込み、最初は小さめのコンテキスト長（例: 8192トークン）で動作を確認する。必要に応じて増やす。
5. Developer / Local Server画面からローカルAPIサーバーを開始する。ポートは原則1234、ホストは `127.0.0.1` に限定する。
6. モデルIDをAPIで確認する。

```powershell
Invoke-RestMethod http://127.0.0.1:1234/v1/models | ConvertTo-Json -Depth 6
```

7. 返却された `id` をコピーして以下の `$modelId` に設定する。

```powershell
$modelId = 'ここに/v1/modelsで返されたidを入力'
$body = @{
  model = $modelId
  messages = @(
    @{ role = 'system'; content = 'あなたは日本語の記録整理アシスタントです。' },
    @{ role = 'user'; content = '次を一文で要約してください: 来週までにログ収集方式を調査する。' }
  )
  temperature = 0.1
} | ConvertTo-Json -Depth 8
Invoke-RestMethod -Uri 'http://127.0.0.1:1234/v1/chat/completions' `
  -Method Post -ContentType 'application/json; charset=utf-8' -Body ([System.Text.Encoding]::UTF8.GetBytes($body))
```

**合格条件:** 日本語の応答が返り、通信先がlocalhostである。LM Studioのバージョンによって画面名は異なる可能性がある。

### 5.3 whisper.cpp

1. 公式リポジトリ https://github.com/ggml-org/whisper.cpp を開く。
2. Windows向け公式リリースバイナリが利用できれば取得する。利用できない場合はリポジトリのWindowsビルド手順に従ってCMake/Visual Studioでビルドする。
3. 実行ファイル（通常 `whisper-cli.exe`）を `tools\whisper` に配置する。
4. リポジトリで案内されるモデル配布元から多言語版 `ggml-small.bin` を取得し `models\whisper` に配置する。英語専用の `.en` モデルは選ばない。
5. まず公式サンプルWAVまたは自分の短い日本語WAVで実行する。入力は16kHz、16bit、mono PCM WAVに変換しておくと扱いやすい。

```powershell
cd C:\VoiceJournal
.\tools\whisper\whisper-cli.exe `
  -m .\models\whisper\ggml-small.bin `
  -f .\data\recordings\test_16k_mono.wav `
  -l ja -otxt -of .\data\transcripts\test
```

**合格条件:** `test.txt` 相当の出力が生成され、日本語の発話内容を確認できる。CLIオプションは導入したバージョンの `--help` で確認する。

#### FFmpegを使う場合

公式 https://ffmpeg.org/download.html の案内するWindowsビルドを取得し、ライセンスを確認する。例:

```powershell
.\tools\ffmpeg\ffmpeg.exe -i .\data\recordings\test.wav `
  -ar 16000 -ac 1 -c:a pcm_s16le .\data\recordings\test_16k_mono.wav
```

### 5.4 WPFアプリの雛形

```powershell
mkdir VoiceJournal.Source
cd VoiceJournal.Source
dotnet new wpf -n VoiceJournal -f net10.0
dotnet new sln -n VoiceJournal
dotnet sln add .\VoiceJournal\VoiceJournal.csproj
cd .\VoiceJournal
dotnet add package NAudio
dotnet add package Microsoft.Data.Sqlite
dotnet build
```

**注意:** 上記は開発環境の雛形作成であり、録音・LLM連携などの実装はまだ含まれない。NuGetの依存パッケージのバージョンは初回ビルド成功後に固定する。

### 5.5 録音の実装方針

- マイク: NAudioのWASAPI Capture等を利用。
- PC内部音声: WASAPI Loopbackを利用。対象の再生デバイスを選べるようにする。
- 両方を録音: 入力を別ストリームで収録し、録音開始時刻・サンプルレート・タイムスタンプを基に同期してミックスする。異なるクロックのドリフトや音量差に注意する。
- 一時停止・再開・デバイス抜き差し・無音区間・ディスク容量不足に対応する。
- マイク利用権限とWeb会議参加者への録音通知・同意を確認する。
- 録音途中の異常終了に備えて、分割保存または定期フラッシュを行う。

## 6. アプリの処理パイプライン

```text
録音開始
  ↓
マイク/PC音声を安全に保存
  ↓
録音終了 → WAV確定
  ↓
音声の16kHz/mono変換
  ↓
whisper.cppで日本語文字起こし
  ↓
原文テキストとタイムスタンプを保存
  ↓
Gemmaで内容分類・要約・構造化
  ↓
JSONスキーマ検証（不正なら再生成または保留）
  ↓
Markdown生成・プレビュー
  ↓
課題/タスク候補を既存DBと照合
  ↓
追加・更新候補の差分を表示
  ↓
ユーザー承認後、DBトランザクションで確定
  ↓
Markdownを一時ファイルに出力し原子的に置換
```

### 6.1 長時間音声

- 文字起こし結果がLLMのコンテキスト上限を超える場合は、時刻・話題単位で分割する。
- 各分割の要約と抽出事項を保持し、最後に統合する。
- 統合時も元の時刻範囲と記録IDを保持し、出典を追えるようにする。
- 固有名詞、担当者、日付は推測で補完せず、未確認として明示する。

### 6.2 LLMの出力契約（例）

```json
{
  "record_type": "meeting",
  "title": "ログ収集方式の検討",
  "summary": "ログ収集方式を調査することになった。",
  "sections": [
    { "heading": "議論内容", "body": "既存PCの性能が未確認。" }
  ],
  "decisions": [],
  "issues": [
    { "title": "既存PCの性能検証", "status": "open", "source_quote": "既存PCで性能が足りるかはまだ課題" }
  ],
  "tasks": [
    { "title": "ログ収集方式の調査", "assignee": "田中", "due_date": null, "due_text": "来週まで", "source_quote": "ログ収集方式は来週までに調査する" }
  ]
}
```

このJSONは概念例。実装時には必須フィールド・文字列長・許容値・スキーマバージョンを定義する。Markdown自体はアプリ側で生成する。

## 7. Markdown仕様

### 7.1 記録ファイル

```markdown
---
record_id: REC-20261031-0001
recorded_at: 2026-10-31T14:00:00+09:00
record_type: meeting
source_audio: ../recordings/REC-20261031-0001.wav
---

# ログ収集方式の検討

## 要約
ログ収集方式の調査を進めることになった。

## 議論内容
...

## 決定事項
...

## 未解決課題
- [ISS-0001](../management/課題管理.md): 既存PCの性能検証

## タスク
- [TASK-0001](../management/タスク管理.md): ログ収集方式の調査
```

「要約」を必ず先頭の本文セクションに置き、その後の章立ては記録の種類に応じて可変とする。原文への参照リンクも保持する。

### 7.2 課題・タスクの整合性

- DBを正本とし、Markdownは編集・閲覧用のエクスポートとして扱う（初期版）。
- Markdownを外部編集した場合は自動でDBへ逆同期しない。将来、インポート/差分確認機能を追加する。
- 課題・タスクには固定ID、タイトル、説明、状態、担当、期限、作成日時、更新日時、記録ID、出典箇所を持たせる。
- 同名課題でも別件の可能性があるため、自動統合せず候補提示する。
- 「来週」など相対日付は録音日時・文脈で解釈できない場合、日付確定を求める。
- 失敗時はDBとMarkdownが食い違わないよう、書き出し再試行・リカバリ処理を設ける。

## 8. 設定例

`config/appsettings.json` の例:

```json
{
  "storageRoot": "%LOCALAPPDATA%\\VoiceJournal\\data",
  "whisper": {
    "executable": "C:\\VoiceJournal\\tools\\whisper\\whisper-cli.exe",
    "model": "C:\\VoiceJournal\\models\\whisper\\ggml-small.bin",
    "language": "ja"
  },
  "llm": {
    "provider": "lmstudio",
    "baseUrl": "http://127.0.0.1:1234/v1",
    "modelId": "REPLACE_WITH_LOADED_MODEL_ID",
    "temperature": 0.1,
    "timeoutSeconds": 600
  },
  "output": {
    "markdownEncoding": "utf-8",
    "requireApprovalForIssueChanges": true
  }
}
```

実装では環境変数展開、パス検証、モデル存在確認、タイムアウト・再試行、ログの秘匿化を行う。

## 9. 動作確認チェックリスト

- [ ] .NET SDKが認識される
- [ ] LM StudioでGemmaが日本語応答する
- [ ] LM Studio APIにlocalhostから接続できる
- [ ] whisper.cppで短い日本語音声を文字起こしできる
- [ ] マイク単独録音ができる
- [ ] PC内部音声の録音ができる
- [ ] 両入力の同期・音量・無音時挙動を確認できる
- [ ] 録音終了後に自動処理が開始される
- [ ] 要約が先頭のMarkdownを生成できる
- [ ] 原文と要約を突き合わせられる
- [ ] 課題・タスクの新規登録、重複候補、更新候補を確認できる
- [ ] 更新拒否時に課題管理ファイルが変化しない
- [ ] LM Studio停止時・モデル未ロード時に安全に失敗する
- [ ] 長時間録音、アプリ異常終了、ディスク容量不足から復旧できる

## 10. 性能測定

5分・30分・60分の日本語音声で、次を計測する。

| 指標 | 記録内容 |
|---|---|
| 音声認識処理時間 | CPU版とGPU版を比較 |
| 認識精度 | 固有名詞、数字、専門用語の誤認識数 |
| Gemma処理時間 | 入力トークン数・生成時間 |
| 最大メモリ消費 | Whisper / LM Studioそれぞれ |
| 出力品質 | 要約の忠実性、決定事項の誤抽出、タスクの漏れ |
| 安定性 | 連続実行、途中停止、復旧可否 |

品質を確認してからWhisper medium、Gemma 12B等へ段階的に変更する。

## 11. セキュリティ・運用

- LM Studio APIはループバックのみで待ち受け、LANへ公開しない。
- 録音・議事録は機密情報として扱い、Windowsアカウント権限・BitLocker/デバイス暗号化・バックアップを検討する。
- 音声データの保持期間と削除方法を設定可能にする。
- 音声や全文をデバッグログへ出力しない。
- モデルの配布元・ライセンス・ハッシュを記録する。
- 録音の同意・社内規程・第三者のプライバシーを遵守する。
- クラウドAPIへの切替を追加する場合は、外部送信前に明示確認する。

## 12. 開発順序と完成条件

### Stage A: 環境検証

.NET、Whisper、LM Studioを導入し、短い日本語音声からテキストを得てGemmaで要約する。**完成条件:** 2ツールの単独動作とAPI疎通を確認。

### Stage B: 録音・記録MVP

WPF画面、録音、停止、文字起こし、Gemma連携、Markdownプレビュー、保存。**完成条件:** 録音終了からMarkdown保存まで一操作で完了。

### Stage C: 課題・タスク管理

SQLite、固定ID、重複候補、更新差分、承認、Markdown書き出し。**完成条件:** 既存課題を壊さず新規/更新/拒否を処理。

### Stage D: 品質・性能改善

GPU/NPU検証、長時間分割、復旧、ログ管理、インストーラ/単体EXE化。**完成条件:** 30〜60分の実データで安定性と品質を評価。

## 13. 公式参照先

- .NET: https://dotnet.microsoft.com/download/dotnet/10.0
- LM Studio: https://lmstudio.ai/
- LM Studio Docs: https://lmstudio.ai/docs
- whisper.cpp: https://github.com/ggml-org/whisper.cpp
- NAudio: https://github.com/naudio/NAudio
- FFmpeg: https://ffmpeg.org/
- SQLite: https://www.sqlite.org/
- Gemma: https://ai.google.dev/gemma

> 重要: 本書は構築・実装のための仕様書であり、Windowsアプリ本体の実装済み成果物ではない。配布物のライセンス・最新版の互換性・実測性能は構築時に確認する。
