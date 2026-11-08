# 第三者の部品とライセンス

- 状態: **草案（2026-11-08）**。配布の前に、各部品の配布物に含まれるライセンスの原文で確かめ、原文をこのファイルの末尾か配布物に同梱する（Phase G）
- 版は `Directory.Packages.props` と `docs/environment-lock.md` を正とする

## アプリに組み込む部品（NuGet）

| 部品 | 版 | ライセンス | 根拠 |
|---|---|---|---|
| CommunityToolkit.Mvvm | 8.4.2 | MIT | environment-lock.md 2 章 |
| Microsoft.Extensions.DependencyInjection | 10.0.14 | MIT | environment-lock.md 2 章 |
| Microsoft.Data.Sqlite | 10.0.14 | MIT | 要確認（原文） |
| SQLitePCLRaw（Microsoft.Data.Sqlite が使う） | 2.1.13 | Apache-2.0 | 要確認（原文） |
| SQLite（エンジン 3.53.3） | 3.53.3 | パブリックドメイン | 要確認（原文） |
| NAudio | 3.3.0 | MIT | 要確認（原文） |
| Corvus.Json.Validator | 5.7.9 | Apache-2.0 | environment-lock.md 2 章。実行時に Roslyn でコード生成する（依存 DLL 約 11.8 MB） |
| .NET ランタイム | 10 | MIT | 要確認（原文） |

## 同梱する実行ファイルとモデル

| 部品 | 版 | ライセンス | 根拠 |
|---|---|---|---|
| whisper.cpp（`whisper-cli.exe` ほか） | b5454（v1.9.5） | MIT | 要確認（原文） |
| whisper のモデル `ggml-small.bin` / `ggml-medium.bin` | — | MIT（OpenAI Whisper の重みを変換したもの） | 要確認（原文） |
| llama.cpp（`llama-server.exe` ほか） | b12121 | MIT | environment-lock.md 6 章 |
| Gemma 4 E2B Instruct Q4_0（GGUF）と MTP | — | Apache-2.0 | environment-lock.md 6 章（`ggml-org/gemma-4-E2B-it-GGUF`）。配布の前にモデルの利用条件の原文を確かめる |

## 署名

- 同梱の実行ファイル（whisper.cpp・llama.cpp）には署名がない。アプリは SHA-256 で登録したものだけを起動する（`config\stt-tools.json`・`config\llm-tools.json`）
- アプリ本体の署名（Authenticode）は、証明書の用意が要る。利用者の判断待ち（Phase G）

## オフライン（AT-22）

- Corvus.Json.Validator は外部の `$ref` を取りに行く部品（`HttpClientDocumentResolver`）を含むが、アプリは埋め込みのスキーマだけを使う。2026-11-08 にアプリで文字起こしと記録の作成を通したとき、アプリ・推論エンジン・whisper の接続はループバックだけだった（`verify\phase-g\offline-connections.csv`）
