<#
.SYNOPSIS
  Phase B3 の実機確認。本番と同じ録音パイプライン（RecordingSession と WASAPI アダプター）で録音し、
  マイクのデバイス位置の単位・時計のずれ・USB の抜き差しを確かめる。
.DESCRIPTION
  1. マイクのデバイス位置と時計の速度を計測する（raw モードと通常モードで各 10 秒。音声は保存しない）
  2. 両方モードで録音する。途中で USB マイクを抜き、数秒後に挿し直す（-SkipUnplug で省略）
  結果は %LOCALAPPDATA%\VoiceJournal\verify\b3-*.json に保存する。録音した音声は verify\b3-workspace に残る。
.EXAMPLE
  .\scripts\Test-Recording.ps1
  .\scripts\Test-Recording.ps1 -SkipUnplug -Seconds 30
#>
param(
    [int]$Seconds = 60,
    [switch]$SkipUnplug,
    [string]$Root = (Join-Path $env:LOCALAPPDATA 'VoiceJournal')
)
$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = $utf8
$repo = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $repo 'tools\VoiceJournal.AudioProbe\VoiceJournal.AudioProbe.csproj'
$exe = Join-Path $repo 'tools\VoiceJournal.AudioProbe\bin\Release\net10.0-windows\VoiceJournal.AudioProbe.exe'
$verify = Join-Path $Root 'verify'
New-Item -ItemType Directory -Force $verify | Out-Null

Write-Host '== 録音ツールをビルド' -ForegroundColor Cyan
dotnet build $proj -c Release --nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw '録音ツールのビルドに失敗しました' }

# --- 1. マイクのデバイス位置 ---------------------------------------------------
Write-Host '== 1/2 マイクのデバイス位置と時計の速度を計測します（各 10 秒。話さなくて構いません。音声は保存しません）' -ForegroundColor Cyan
foreach ($mode in 'raw', 'normal') {
    Write-Host "   マイク（$mode モード）を計測中..."
    $probeArgs = @('positions', 'mic', '10')
    if ($mode -eq 'raw') { $probeArgs += '--raw' }
    $out = & $exe @probeArgs
    if ($LASTEXITCODE -ne 0) { throw "マイクの計測に失敗しました（$mode モード）" }
    $json = $out -join "`n"
    [IO.File]::WriteAllText((Join-Path $verify "b3-positions-mic-$mode.json"), $json, $utf8)
    $r = $json | ConvertFrom-Json
    Write-Host ("   位置が連続: {0}、飛び: {1}、デバイスの時計: {2} ppm" -f $r.contiguous_positions, $r.position_jumps, $r.device_clock_ppm_vs_qpc)
}

# --- 2. 両方モードの録音と USB の抜き差し ---------------------------------------
Write-Host "== 2/2 両方モード（マイクとシステム音声）で $Seconds 秒録音します" -ForegroundColor Cyan
if (-not $SkipUnplug) {
    Write-Host '   録音が始まって 20 秒ほどしたら、USB マイク（ヘッドセット）を抜いてください。' -ForegroundColor Yellow
    Write-Host '   5 秒ほど待ってから挿し直してください。同じ機器が戻ると自動で再接続し、新しいチャンクから録音を続けます。' -ForegroundColor Yellow
    Write-Host '   スピーカーも同じ USB 機器なので、マイクとシステム音声の両方がいったん止まるのが正常です。' -ForegroundColor Yellow
}
Read-Host '   準備ができたら Enter を押してください' | Out-Null

$sessionArgs = @('session', 'both', "$Seconds", '--chunk-seconds', '20')
if (-not $SkipUnplug) { $sessionArgs += '--reconnect' }
$lines = New-Object System.Collections.Generic.List[string]
& $exe @sessionArgs | ForEach-Object {
    $lines.Add($_)
    if ($_ -match '^\[') { Write-Host "   $_" }
}
$exitCode = $LASTEXITCODE
$start = $lines.FindIndex([Predicate[string]] { param($l) $l -eq '{' })
if ($start -lt 0) { throw "録音ツールが結果を出力しませんでした（終了コード $exitCode）" }
$json = ($lines.GetRange($start, $lines.Count - $start)) -join "`n"
$resultPath = Join-Path $verify 'b3-session-both.json'
[IO.File]::WriteAllText($resultPath, $json, $utf8)
$result = $json | ConvertFrom-Json

Write-Host '== 結果' -ForegroundColor Cyan
foreach ($t in $result.tracks) {
    $kinds = ($t.events | ForEach-Object { $_.Kind } | Group-Object | ForEach-Object { "$($_.Name)×$($_.Count)" }) -join ', '
    Write-Host ("   {0}: {1} 秒、チャンク {2} 個、状態 {3}、イベント: {4}" -f $t.Source, $t.recorded_seconds, @($t.chunks).Count, $t.State, $kinds)
}
if ($result.verified) {
    Write-Host '   すべてのチャンクで、フレーム数と SHA-256 が manifest と一致しました' -ForegroundColor Green
} else {
    Write-Warning '   照合に失敗したチャンクがあります'
}
Write-Host "   詳細: $resultPath"
Write-Host '   終わったら、チャットで「終わった」と伝えてください。'
