<#
.SYNOPSIS
  Phase D の評価と受入試験（詳細仕様書 22 章 AT-08・AT-09・AT-23、抽出の precision / recall、20 章 NFR-07）を、
  アプリと同じ記録の生成処理（RecordGenerator と LM Studio）で行う。
.DESCRIPTION
  LM Studio のサーバーを起動しておく（モデルは要求で自動的に読み込まれる）。結果は %LOCALAPPDATA%\VoiceJournal\verify\phase-d\ に書く。
  - Evaluate: 評価セット（tests\eval\phase-d\*.json のうち use が acceptance のもの）を素材ごとに一時のセッションにして記録を作り、
              正解と照合する。候補の precision 0.90 以上・recall 0.80 以上、素材ごとの違反 0 件で合格（AT-08・09・23）
  - Meeting:  Phase C の読み上げ録音（約 25 分）の文字起こしから記録を作り、所要時間を測る（NFR-07: 5 分以内。区間の数を併記）
.EXAMPLE
  .\scripts\Test-PhaseD.ps1 -Test Evaluate
  .\scripts\Test-PhaseD.ps1 -Test Evaluate -Fixture negation,injection -PieceTokens 2000
  .\scripts\Test-PhaseD.ps1 -Test Meeting
#>
param(
    [Parameter(Mandatory)] [ValidateSet('Evaluate', 'Meeting')] [string]$Test,
    # Evaluate: 使う素材の id（省略時は acceptance のすべて）
    [string[]]$Fixture,
    [string]$Model = 'gemma-3-4b-it',
    [int]$Context = 16384,
    [int]$Reserve = 3072,
    [int]$PieceTokens = 3000,
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
$tag = "c$Context-o$Reserve-p$PieceTokens-$($Model -replace '[^A-Za-z0-9.-]', '_')"
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

$common = @('--model', $Model, '--context', $Context, '--reserve', $Reserve, '--piece-tokens', $PieceTokens)

if ($Test -eq 'Evaluate') {
    $files = Get-ChildItem (Join-Path $repo 'tests\eval\phase-d') -Filter '*.json' | Where-Object {
        $j = Read-Json $_.FullName
        $j.use -eq 'acceptance' -and (-not $Fixture -or $Fixture -contains $j.id)
    }
    if (-not $files) { throw '評価素材が見つかりません' }
    $workspace = Join-Path $out "eval\$stamp-$tag"
    $result = Join-Path $out "eval-$stamp-$tag.json"
    Write-Host "== 記録を作って照合（$($files.Count) 本、$tag）" -ForegroundColor Cyan
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
        Show $f.id ($f.violations.Count -eq 0) ("違反 {0} 件" -f $f.violations.Count)
    }
    $t = $r.total
    Write-Host '== 判定' -ForegroundColor Cyan
    Show 'precision' ($null -ne $t.precision -and $t.precision -ge 0.90) ("{0}（{1} / {2}。0.90 以上）" -f $t.precision, $t.matched_produced, $t.produced)
    Show 'recall' ($null -ne $t.recall -and $t.recall -ge 0.80) ("{0}（{1} / {2}。0.80 以上）" -f $t.recall, $t.recalled, $t.expected)
    Show '違反' ($t.violations -eq 0) ("{0} 件（期限・担当・状態・出してはいけない候補・要点・決定・Markdown の無害化）" -f $t.violations)
    Write-Host "   結果: $result"
    Write-Host "   記録: $workspace"
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
    Show 'NFR-07' ($r.outcome -eq 'Generated' -and $r.elapsed_seconds -le 300) ("{0} 秒（300 秒以内）" -f $r.elapsed_seconds)
    Write-Host "   結果: $result"
    Write-Host "   記録: $copy"
}
