<#
.SYNOPSIS
  Phase G（運用・配布）の試験。
.DESCRIPTION
  - Search: AT-21。1,000 セッション・1 万項目のワークスペースを作り、ライブラリと管理一覧の検索（短い語・部分一致・日付・状態）の時間を測る。
  - Offline: AT-22。Wi-Fi と有線 LAN を切ってから実行する。ネットワークが切れていることを確かめ、試験用の TTS の録音の写しで
    文字起こしと記録の作成を通し、その間の接続（ループバック以外が 0 件か）を記録する。最後にアプリでの管理の反映も手で確かめる。
  - MicStart: NFR-03。マイクで 5 秒の録音を 3 回して、開始から「録音中」までの時間を出す（録ったものはすぐ消す。話さなくてよい）。
  - Restore: 復元訓練。アプリを閉じてから実行する。ワークスペースのスナップショットを作り、隣の新しいフォルダーに復元して検査する
    （今のワークスペースは読むだけ。スナップショットは backups\ に増える）。
  結果は %LOCALAPPDATA%\VoiceJournal\verify\phase-g\ に出る（試験用のワークスペースもそこに作る）。
.EXAMPLE
  .\scripts\Test-PhaseG.ps1 -Test Search
#>
param(
    [Parameter(Mandatory)] [ValidateSet('Search', 'Offline', 'MicStart', 'Restore')] [string]$Test,
    # Restore: 復元訓練をするワークスペース（既定は利用者のワークスペース）
    [string]$Workspace = (Join-Path $env:LOCALAPPDATA 'VoiceJournal\Workspace'),
    # Offline: ネットワークの確認を飛ばす（スクリプトの動作確認用。AT-22 の判定には使わない）
    [switch]$SkipNetworkCheck,
    [string]$Root = (Join-Path $env:LOCALAPPDATA 'VoiceJournal')
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$out = Join-Path $Root 'verify\phase-g'
New-Item -ItemType Directory -Force $out | Out-Null

Write-Host '== 評価ツールをビルド' -ForegroundColor Cyan
dotnet build (Join-Path $repo 'tools\VoiceJournal.AudioProbe\VoiceJournal.AudioProbe.csproj') -nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
$exe = Join-Path $repo 'tools\VoiceJournal.AudioProbe\bin\Debug\net10.0-windows\VoiceJournal.AudioProbe.exe'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

if ($Test -eq 'Search') {
    $result = Join-Path $out "search-$stamp.json"
    Write-Host '== 1,000 セッション・1 万項目を作って検索の時間を測る（各 5 回の中央値）' -ForegroundColor Cyan
    & $exe search-bench (Join-Path $out 'search') | Out-File -FilePath $result -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw 'search-bench failed' }
    $json = Get-Content $result -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($c in $json.cases) { Write-Host ("   {0,8:0.0} ms  {1,6} 件  {2}" -f $c.median_ms, $c.results, $c.name) }
    $slow = @($json.cases | Where-Object { $_.median_ms -gt 200 })
    if ($slow.Count -eq 0) { Write-Host '   [合格] AT-21: どの検索も 200 ms 以内' -ForegroundColor Green }
    else { Write-Host "   [不合格] AT-21: 200 ms を超えた検索が $($slow.Count) 件" -ForegroundColor Red }
    Write-Host "   結果: $result"
}

if ($Test -eq 'Offline') {
    Write-Host '== ネットワークが切れているか' -ForegroundColor Cyan
    $up = @(Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object { $_.Status -eq 'Up' -and -not $_.Virtual })
    $internet = @(Get-NetConnectionProfile -ErrorAction SilentlyContinue | Where-Object { $_.IPv4Connectivity -eq 'Internet' -or $_.IPv6Connectivity -eq 'Internet' })
    if (($up.Count -gt 0 -or $internet.Count -gt 0) -and -not $SkipNetworkCheck) {
        Write-Host ("   まだつながっています: " + (($up | ForEach-Object Name) -join ', ')) -ForegroundColor Red
        Write-Host '   Wi-Fi と有線 LAN を切ってから、もう一度実行してください。'
        exit 2
    }
    if ($SkipNetworkCheck) { Write-Host '   （確認を飛ばした: 動作確認用。AT-22 の判定にはならない）' -ForegroundColor Yellow }
    else { Write-Host '   [確認] 物理アダプターはすべて切断、インターネット接続なし' -ForegroundColor Green }
    $src = Join-Path $Root 'verify\phase-e\workspace\sessions\2026\11\01a194d8-6606-7353-a29d-6b15970e2ce0'
    $work = Join-Path $out "offline-$stamp"
    $session = Join-Path $work 'session'
    New-Item -ItemType Directory -Force $session | Out-Null
    Copy-Item (Join-Path $src 'manifest.json') $session
    Copy-Item (Join-Path $src 'audio') $session -Recurse
    $connections = Join-Path $work 'connections.txt'
    $stop = Join-Path $work 'stop'
    $poll = Start-Job -ArgumentList $connections, $stop -ScriptBlock {
        param($file, $stop)
        $names = 'VoiceJournal.AudioProbe', 'VoiceJournal.App', 'whisper-cli', 'llama-server'
        $seen = @{}
        while (-not (Test-Path $stop)) {
            $pids = @{}; Get-Process -Name $names -ErrorAction SilentlyContinue | ForEach-Object { $pids[$_.Id] = $_.ProcessName }
            Get-NetTCPConnection -ErrorAction SilentlyContinue | Where-Object { $pids.ContainsKey([int]$_.OwningProcess) } |
                ForEach-Object { $seen["$($pids[[int]$_.OwningProcess]) $($_.State) $($_.RemoteAddress):$($_.RemotePort)"] = 1 }
            Start-Sleep -Milliseconds 200
        }
        $seen.Keys | Set-Content $file -Encoding UTF8
    }
    Write-Host '== 文字起こし（small）' -ForegroundColor Cyan
    & $exe transcribe $session --model small --out (Join-Path $work 'transcribe.json') | Where-Object { $_ -match '^transcribe:' } | ForEach-Object { Write-Host "   $_" }
    Write-Host '== 記録の作成（同梱の推論エンジン）' -ForegroundColor Cyan
    & $exe organize $session --out (Join-Path $work 'organize.json') | Out-Null
    New-Item $stop -ItemType File | Out-Null
    $null = Wait-Job $poll -Timeout 30; Remove-Job $poll -Force
    $organize = Get-Content (Join-Path $work 'organize.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $lines = @(Get-Content $connections -Encoding UTF8 -ErrorAction SilentlyContinue)
    $outside = @($lines | Where-Object { $_ -and $_ -notmatch ' (127\.0\.0\.1|::1|0\.0\.0\.0|::):' })
    Write-Host ("   記録の作成: {0}（区間 {1}、要求 {2} 回）" -f $organize.outcome, $organize.PieceCount, $organize.requests.Count)
    Write-Host ("   観測した接続: {0} 種類（ループバック以外 {1}）" -f $lines.Count, $outside.Count)
    $outside | ForEach-Object { Write-Host "     $_" -ForegroundColor Red }
    if ($SkipNetworkCheck) { Write-Host '   （動作確認: ネットワークを切っていないので AT-22 の判定はしない）' -ForegroundColor Yellow }
    elseif ($organize.outcome -eq 'Generated' -and $outside.Count -eq 0) { Write-Host '   [合格] AT-22（文字起こし・記録の作成）: オフラインで完了し、ループバック以外の接続 0 件' -ForegroundColor Green }
    else { Write-Host '   [不合格] AT-22' -ForegroundColor Red }
    Write-Host '   続けて、LAN を切ったままアプリで録音 → 文字起こし → 記録の作成 → 「管理」タブの反映を通してください（AT-22 の全フロー）。'
    Write-Host "   結果: $work"
}

if ($Test -eq 'MicStart') {
    Write-Host '== マイクで 5 秒の録音を 3 回（録ったものはすぐ消す）' -ForegroundColor Cyan
    $tmp = Join-Path $env:TEMP ("vj-micstart-" + [guid]::NewGuid().ToString('N'))
    $times = @()
    try {
        foreach ($i in 1..3) {
            $lines = & $exe session mic 5 --workspace $tmp 2>&1
            $rec = $lines | Where-Object { $_ -match '\[(\d\d):(\d\d\.\d)\] state: Recording' } | Select-Object -First 1
            if ($rec -match '\[(\d\d):(\d\d\.\d)\]') { $sec = [int]$Matches[1] * 60 + [double]$Matches[2]; $times += $sec; Write-Host ("   {0} 回目: 録音中まで {1:0.0} 秒" -f $i, $sec) }
            else { Write-Host "   $i 回目: 録音中になりませんでした" -ForegroundColor Red; $lines | Select-Object -Last 5 | ForEach-Object { Write-Host "     $_" } }
        }
    }
    finally { if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp } }
    if ($times.Count -eq 3 -and ($times | Measure-Object -Maximum).Maximum -le 3) { Write-Host '   [合格] NFR-03: 3 回とも 3 秒以内' -ForegroundColor Green }
    else { Write-Host '   [不合格] NFR-03' -ForegroundColor Red }
    "$stamp mic start seconds: $($times -join ', ')" | Add-Content (Join-Path $out 'mic-start.txt') -Encoding UTF8
}

if ($Test -eq 'Restore') {
    if (Get-Process VoiceJournal.App -ErrorAction SilentlyContinue) { Write-Host 'Voice Journal を閉じてから実行してください。' -ForegroundColor Red; exit 2 }
    Write-Host "== 復元訓練: $Workspace" -ForegroundColor Cyan
    $result = Join-Path $out "restore-$stamp.json"
    & $exe restore-drill $Workspace | Out-File -FilePath $result -Encoding utf8
    $ok = $LASTEXITCODE -eq 0
    $json = Get-Content $result -Raw -Encoding UTF8 | ConvertFrom-Json
    Write-Host ("   スナップショット: {0}（{1} ファイル、{2:N1} MB）" -f $json.snapshot, $json.files, ($json.bytes / 1MB))
    Write-Host ("   復元先: {0}" -f $json.restored_to)
    if ($ok) { Write-Host '   [合格] 復元して検査し、問題 0 件' -ForegroundColor Green } else { $json.problems | ForEach-Object { Write-Host "   問題: $_" -ForegroundColor Red } }
    Write-Host "   アプリで中身を確かめるには: VoiceJournal.App.exe --workspace `"$($json.restored_to)`"（確かめたら、そのフォルダーは消してかまいません）"
}
