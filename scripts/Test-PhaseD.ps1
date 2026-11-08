<#
.SYNOPSIS
  Phase D の評価と受入試験（詳細仕様書 22 章 AT-08・AT-09・AT-23、抽出の precision / recall、20 章 NFR-07）を、
  アプリと同じ記録の生成処理（RecordGenerator と同梱の llama-server）で行う。
.DESCRIPTION
  評価ツールが回ごとに同梱の llama-server を起動し、終わったら止める（docs/phase-d-engine-plan.md）。測る前に、
  エミュレーター・Docker・WSL・Gradle などが動いていないことを確かめる。結果は %LOCALAPPDATA%\VoiceJournal\verify\phase-d\ に書く。
  - Evaluate: 評価セット（tests\eval\phase-d\*.json のうち use が -Use のもの。既定は acceptance）を素材ごとに一時のセッションにして記録を作り、
              正解と照合する。候補の precision 0.90 以上・recall 0.80 以上、素材ごとの違反 0 件で合格（AT-08・09・23）
  - Meeting:  Phase C の読み上げ録音（約 25 分）の文字起こしから記録を作り、所要時間を測る（NFR-07: 30 分の音声に換算して 7 分以内。区間の数を併記）
.EXAMPLE
  .\scripts\Test-PhaseD.ps1 -Test Evaluate
  .\scripts\Test-PhaseD.ps1 -Test Evaluate -Use tuning   # プロンプトの調整用の素材
  .\scripts\Test-PhaseD.ps1 -Test Evaluate -Repeat 3     # 3 回測って合計で判定する
  .\scripts\Test-PhaseD.ps1 -Test Evaluate -Fixture negation,injection -PieceTokens 2000
  .\scripts\Test-PhaseD.ps1 -Test Meeting
#>
param(
    [Parameter(Mandatory)] [ValidateSet('Evaluate', 'Meeting')] [string]$Test,
    # Evaluate: 素材の組（acceptance: 受入評価用、tuning: プロンプトの調整用）と、使う素材の id（省略時はその組のすべて）
    [ValidateSet('acceptance', 'tuning')] [string]$Use = 'acceptance',
    [string[]]$Fixture,
    # Evaluate: 同じ条件で繰り返す回数（1 回ごとのばらつきが大きいので、判定は合計で行う。回ごとにエンジンを起動し直す）
    [ValidateRange(1, 10)] [int]$Repeat = 1,
    [string]$Model = 'gemma-4-e2b-it',
    [int]$Context = 16384,
    [int]$Reserve = 6144,
    [int]$PieceTokens = 3000,
    # 推論エンジンの並列の枠（区間を同時に処理する数）。2 以上では KV キャッシュを共有し、1 枠の上限を Context にする
    [ValidateRange(1, 4)] [int]$Parallel = 1,
    # Meeting: 文字起こしのあるセッション（manifest.json と transcripts\ だけを一時の場所に写して使う）
    [string]$Session = (Join-Path $env:LOCALAPPDATA 'VoiceJournal\verify\phase-d\d2\meeting'),
    [string]$Root = (Join-Path $env:LOCALAPPDATA 'VoiceJournal')
)
$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = $utf8
$repo = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $repo 'tools\VoiceJournal.AudioProbe\VoiceJournal.AudioProbe.csproj'
$exe = Join-Path $repo 'tools\VoiceJournal.AudioProbe\bin\Release\net10.0-windows\VoiceJournal.AudioProbe.exe'
$out = Join-Path $Root 'verify\phase-d'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
# 結果の名前は LM Studio をやめる前（2026-11-06）と同じ形にする（bundled-c16384-o6144-…）
$tag = "bundled-c$Context-o$Reserve-p$PieceTokens$(if ($Parallel -gt 1) { "-np$Parallel" })-$($Model -replace '[^A-Za-z0-9.-]', '_')"
New-Item -ItemType Directory -Force $out | Out-Null

Write-Host '== 評価ツールをビルド' -ForegroundColor Cyan
dotnet build $proj -c Release --nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw '評価ツールのビルドに失敗しました' }

function Show([string]$label, [bool]$ok, [string]$detail) {
    $mark = if ($ok) { '合格' } else { '不合格' }
    $color = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("   [{0}] {1}: {2}" -f $mark, $label, $detail) -ForegroundColor $color
}

function Read-Json([string]$path) { return [IO.File]::ReadAllText($path, $utf8) | ConvertFrom-Json }

$common = @('--model', $Model, '--context', $Context, '--reserve', $Reserve, '--piece-tokens', $PieceTokens, '--parallel', $Parallel)

if ($Test -eq 'Evaluate') {
    $files = Get-ChildItem (Join-Path $repo 'tests\eval\phase-d') -Filter '*.json' | Where-Object {
        $j = Read-Json $_.FullName
        $j.use -eq $Use -and (-not $Fixture -or $Fixture -contains $j.id)
    }
    if (-not $files) { throw '評価素材が見つかりません' }
    $runs = @()
    for ($n = 1; $n -le $Repeat; $n++) {
        # Each run is its own eval-record process, which starts the bundled server and stops it at the end.
        $suffix = if ($Repeat -gt 1) { "-r$n" } else { '' }
        $workspace = Join-Path $out "eval\$stamp-$Use-$tag$suffix"
        $result = Join-Path $out "eval-$stamp-$Use-$tag$suffix.json"
        Write-Host "== 記録を作って照合（$($files.Count) 本、$tag、$n / $Repeat 回目）" -ForegroundColor Cyan
        & $exe eval-record @($files.FullName) --workspace $workspace @common --out $result | Out-Null
        if (-not (Test-Path $result)) { throw "照合の結果がありません（終了コード $LASTEXITCODE）" }
        $r = Read-Json $result
        foreach ($f in $r.fixtures) {
            $c = $f.candidates
            $g = $f.generation
            Write-Host ("-- {0}: 候補 {1}（正解に当たった {2}）、正解 {3} 中 {4}、区間 {5}、{6} 秒" -f $f.id, $c.produced, $c.matched_produced, $c.expected, $c.recalled, $g.pieces, $g.elapsed_seconds)
            foreach ($x in $c.list) {
                Write-Host ("     {0,-10} {1} [{2}] 担当={3} 期限={4} 状態={5} 確度={6} 目印={7}" -f $x.match, $x.title, $x.kind, $x.assignee, $x.due_date, $x.status, $x.certainty, ($x.flags -join ','))
            }
            foreach ($v in $f.violations) { Write-Host "     違反: $v" -ForegroundColor Yellow }
        }
        $t = $r.total
        Write-Host ("   {0} 回目: precision {1}（{2} / {3}）、recall {4}（{5} / {6}）、違反 {7} 件" -f $n, $t.precision, $t.matched_produced, $t.produced, $t.recall, $t.recalled, $t.expected, $t.violations)
        Write-Host "   結果: $result"
        $runs += $t
    }
    # Pooled over the runs (the counts are added up), with the spread of the single runs.
    $produced = ($runs | Measure-Object produced -Sum).Sum
    $matched = ($runs | Measure-Object matched_produced -Sum).Sum
    $expected = ($runs | Measure-Object expected -Sum).Sum
    $recalled = ($runs | Measure-Object recalled -Sum).Sum
    $violations = ($runs | Measure-Object violations -Sum).Sum
    $precision = if ($produced) { [math]::Round($matched / $produced, 3) } else { $null }
    $recall = if ($expected) { [math]::Round($recalled / $expected, 3) } else { $null }
    $spread = { param($key) $v = $runs | ForEach-Object { $_.$key }; '{0}〜{1}' -f ($v | Measure-Object -Minimum).Minimum, ($v | Measure-Object -Maximum).Maximum }
    Write-Host "== 判定（$Repeat 回の合計）" -ForegroundColor Cyan
    Show 'precision' ($null -ne $precision -and $precision -ge 0.90) ("{0}（{1} / {2}。0.90 以上。各回 {3}）" -f $precision, $matched, $produced, (& $spread 'precision'))
    Show 'recall' ($null -ne $recall -and $recall -ge 0.80) ("{0}（{1} / {2}。0.80 以上。各回 {3}）" -f $recall, $recalled, $expected, (& $spread 'recall'))
    Show '違反' ($violations -eq 0) ("{0} 件（{1} 回の合計。期限・担当・状態・出してはいけない候補・要点・決定・Markdown の無害化）" -f $violations, $Repeat)
}
else {
    if (-not (Test-Path (Join-Path $Session 'manifest.json'))) { throw "セッションが見つかりません: $Session" }
    $copy = Join-Path $out "meeting\$stamp-$tag"
    New-Item -ItemType Directory -Force $copy | Out-Null
    Copy-Item (Join-Path $Session 'manifest.json') $copy
    Copy-Item (Join-Path $Session 'transcripts') $copy -Recurse
    $result = Join-Path $out "meeting-$stamp-$tag.json"
    Write-Host "== 会議の文字起こしから記録を作る（$tag）" -ForegroundColor Cyan
    & $exe organize $copy @common --out $result | Out-Null
    if (-not (Test-Path $result)) { throw "生成の結果がありません（終了コード $LASTEXITCODE）" }
    $r = Read-Json $result
    Write-Host ("   文字起こし: {0} 区間・{1} 字、生成: {2}、区間 {3}（失敗 {4}）、要求 {5} 回、入力 {6}・出力 {7} トークン" -f $r.segments, $r.characters, $r.outcome, $r.PieceCount, $r.failed.Count, $r.requests.Count, $r.prompt_tokens, $r.completion_tokens)
    # NFR-07（詳細仕様書 1.1）: 30 分の音声を 420 秒以内。音声の長さ（録音の開始〜終了）で 30 分に換算して判定する
    $m = Read-Json (Join-Path $copy 'manifest.json')
    $audioMinutes = ([DateTimeOffset]$m.ended_at_utc - [DateTimeOffset]$m.started_at_utc).TotalMinutes
    $per30 = [math]::Round($r.elapsed_seconds * 30 / $audioMinutes, 1)
    Show 'NFR-07' ($r.outcome -eq 'Generated' -and $per30 -le 420) ("{0} 秒（音声 {1:N1} 分。30 分換算 {2} 秒、420 秒以内）" -f $r.elapsed_seconds, $audioMinutes, $per30)
    Write-Host "   結果: $result"
    Write-Host "   記録: $copy"
}
