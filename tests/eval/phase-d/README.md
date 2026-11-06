# Phase D の評価セット

詳細仕様書 22 章・`docs/phase-d-plan.md` 5.1。各ファイルは文字起こし相当の区間と、記録・候補の正解を持つ。

| ファイル | 種別 | 主に確かめること |
|---|---|---|
| `negation.json` | 会議 | AT-09（やらない・未完了・かもしれない・担当不明・提案） |
| `injection.json` | 相談 | AT-23（命令・危険な URI・境界マーカー） |
| `monologue.json` | 独話 | AT-08。反省・アイデア・生活の予定が混ざる中のタスク、推量の期限 |
| `learning.json` | 学習 | AT-08。講義の要点、講師の一般論をタスクにしない、曖昧な期限 |
| `mixed.json` | 混在 | AT-08。会議と自分用のメモ、結果しだいの予定・先送り |
| `tuning-standup.json` | 会議（調整用） | 担当と期限の多い朝会、やらない決定、済んだこと |
| `tuning-lecture.json` | 学習（調整用） | 講座の要点、講師の勧めをタスクにしない |
| `tuning-moving.json` | 独話（調整用） | 期限つきの作業、済んだこと・やめたこと・迷っていること |
| `tuning-noise.json` | 会議（調整用） | 他人の返事を待つこと・説明の一般論・他社の紹介を候補にしない |
| `tuning-dates.json` | 会議（調整用） | 「〇日までに〜を出します」のように、期限の後ろに動作が続く言い方の期限 |
| `tuning-solo-meeting.json` | 会議（調整用） | 話しているのが進行役 1 人だけの打ち合わせの種別（meeting）、変えないと決めたことの決定 |

プロンプトは調整用（`use: tuning`）の素材で直し、受入用（`use: acceptance`）は調整の後に測るだけにする（`docs/phase-d-plan.md` 0 章）。

会議の実際の認識誤りを含む入力は、Phase C の読み上げ録音（`verify\phase-c\eval-workspace`）で別に確かめる。

## 形式

- `segments`: `t`（開始時刻 `HH:MM:SS`）・`source`（`mic` / `loopback`）・`text`。区間の終わりは次の区間の開始
- `started_at`・`time_zone`: 相対期日（「来週の月曜」）の基準
- `use`: `acceptance`（受入評価）か `tuning`（プロンプトの調整用）

## 正解（`expected`）の読み方

候補の照合は、生成された候補の題名・本文に `match_any` のどれかが含まれるかで行う（NFKC で正規化。空文字列はすべてに一致）。正解への割り当ては、題名で一致する生成候補を先に、本文だけで一致するものを後に選ぶ。`forbidden_candidates` だけは題名で見る（本文の説明に出てくる語で誤りにしない）。出してはいけない候補に当たった生成候補は、正解に割り当てない。

道具は `AudioProbe eval-record <素材>... --workspace <dir>`（素材ごとに一時のセッションを作って生成し、照合する）。作ったセッションを照合し直すときは `--score <セッション>`。

| キー | 意味 |
|---|---|
| `record_types` | 記録の種別がこのどれかを含めば合格 |
| `candidates` | 必ず出てほしい候補（recall の分母）。1 つの生成候補は 1 つの正解にだけ割り当てる（`candidates` を先に、`optional_candidates` を後に） |
| `optional_candidates` | 出ても出なくてもよい候補。出たら precision の分子に数え、付いている条件（期限・担当）は守る |
| `forbidden_candidates` | 出てはいけない候補。確度 stated で出たら違反。tentative / unclear（要確認）で出たら違反にはしないが、precision では外れに数える（AT-09「誤った確定候補を作らず、要確認として表示する」） |
| `kind` | `task` / `issue`、または許す値の配列。照合の条件にはせず、違えば `kind_mismatch` に別に数える（違反にしない） |
| `assignee` | 文字列: 担当にその名前が含まれる。`null`: 担当なし。キーが無ければ照合しない |
| `due_date` | `YYYY-MM-DD`・`null`、または許す値の配列。キーが無ければ照合しない |
| `status` | `null` は未完了（`open` など完了以外）であること |
| `forbidden_status` | 一致する候補がこの状態であってはいけない |
| `no_assignee` | 一致する候補の担当は null。`assignee` の配列があれば、その名前を担当にしてはいけない（null 以外も可） |
| `no_due_date` | 一致する候補の期限は null |
| `markdown_must_not_contain` | 描画した記録 Markdown に含まれてはいけない文字列 |
| `key_points` | 要約か章の本文のどこかに、`match_any` のどれかが含まれる（学習の要点） |
| `decisions` | 決定が記録に残り、根拠の区間に `evidence_t` の区間が含まれる |

precision = 正解（`candidates` か `optional_candidates`）に割り当てた生成候補の数 / 生成候補の数。recall = 割り当てられた `candidates` の数 / `candidates` の数。期限・担当・状態の条件を破った候補は割り当てたうえで、別に誤りとして数える。
