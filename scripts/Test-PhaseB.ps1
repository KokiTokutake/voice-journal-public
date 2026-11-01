<#
.SYNOPSIS
  Phase B の受入試験（詳細仕様書 22 章 AT-01・AT-02・AT-03）を、本番と同じ録音パイプラインで行う。
.DESCRIPTION
  録音は利用者のライブラリではなく %LOCALAPPDATA%\VoiceJournal\verify\phase-b\workspace に保存し、
  結果を verify\phase-b\*.json に書く。
  - AT01: マイクで 60 秒録音する（画面の原稿を読み上げる）。長さと経過時間の差・モードの記録・再生できることを確かめる
  - AT02: 既定の出力先で TTS を再生しながら、その出力先と別の出力先を同時に内部録音する（操作不要）
  - AT03: 校正音をスピーカーから一定間隔で鳴らし、マイクと内部音声を両方モードで録音する（既定 60 分、放置でよい）。
          両トラックの時刻のずれと、メモリの推移を測る
.EXAMPLE
  .\scripts\Test-PhaseB.ps1 -Test AT01
  .\scripts\Test-PhaseB.ps1 -Test AT03                       # 60 分
  .\scripts\Test-PhaseB.ps1 -Test AT03 -Minutes 5            # 試しに短く
#>
param(
    [Parameter(Mandatory)] [ValidateSet('AT01', 'AT02', 'AT03')] [string]$Test,
    [int]$Minutes = 60,
    # 校正音を鳴らす出力先（名前の一部）。マイクに聞こえるスピーカーを選ぶ
    [string]$BeepDevice = 'Realtek',
    [string]$Root = (Join-Path $env:LOCALAPPDATA 'VoiceJournal')
)
$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = $utf8
$repo = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $repo 'tools\VoiceJournal.AudioProbe\VoiceJournal.AudioProbe.csproj'
$exe = Join-Path $repo 'tools\VoiceJournal.AudioProbe\bin\Release\net10.0-windows\VoiceJournal.AudioProbe.exe'
$out = Join-Path $Root 'verify\phase-b'
$workspace = Join-Path $out 'workspace'
New-Item -ItemType Directory -Force $out | Out-Null

Write-Host '== 録音ツールをビルド' -ForegroundColor Cyan
dotnet build $proj -c Release --nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw '録音ツールのビルドに失敗しました' }

# Runs the probe, shows its progress lines, returns the trailing JSON as an object (and saves it).
function Invoke-Probe([string[]]$probeArgs, [string]$saveAs) {
    $lines = New-Object System.Collections.Generic.List[string]
    & $exe @probeArgs | ForEach-Object {
        $lines.Add($_)
        if ($_ -match '^\[|^beeping|^(Mic|Loopback):') { Write-Host "   $_" }
    }
    $start = $lines.FindIndex([Predicate[string]] { param($l) $l -eq '{' })
    if ($start -lt 0) { throw "録音ツールが結果を出力しませんでした（$($probeArgs[0])）" }
    $json = ($lines.GetRange($start, $lines.Count - $start)) -join "`n"
    if ($saveAs) { [IO.File]::WriteAllText((Join-Path $out $saveAs), $json, $utf8) }
    return $json | ConvertFrom-Json
}

function Show([string]$label, [bool]$ok, [string]$detail) {
    $mark = if ($ok) { '合格' } else { '不合格' }
    $color = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("   [{0}] {1}: {2}" -f $mark, $label, $detail) -ForegroundColor $color
}

switch ($Test) {
    'AT01' {
        $ref = Join-Path $Root 'verify\audio\test_ja_tts.reference.txt'
        Write-Host '== AT-01 マイクで 60 秒録音します' -ForegroundColor Cyan
        Write-Host '   録音が始まったら、次の原稿をふつうの声で読み上げてください（読み終えたら黙って待ってください）。' -ForegroundColor Yellow
        if (Test-Path $ref) { Write-Host ''; Write-Host ([IO.File]::ReadAllText($ref, $utf8)); Write-Host '' }
        Read-Host '   準備ができたら Enter を押してください' | Out-Null
        $r = Invoke-Probe @('session', 'mic', '60', '--workspace', $workspace, '--title', 'AT-01', '--level-every', '5') 'at01-session.json'
        $t = $r.tracks[0]
        $wav = Join-Path $r.session_directory ($t.chunks[0].File -replace '/', '\')
        Write-Host '== 録音した音声を再生します（既定の出力先から 60 秒流れます）' -ForegroundColor Cyan
        $p = Invoke-Probe @('play', $wav) 'at01-play.json'
        Write-Host '== 結果' -ForegroundColor Cyan
        Show '長さと経過時間の差' ([Math]::Abs($t.timeline_end_minus_wall_ms) -le 100) "$($t.timeline_end_minus_wall_ms) ms（目標 ±100 ms）"
        Show 'モードの記録' ($r.CaptureMode -eq 'Mic' -and $r.MicMode -eq 'Raw') "CaptureMode=$($r.CaptureMode) MicMode=$($r.MicMode)"
        Show '保存の照合' ([bool]$r.verified) 'フレーム数と SHA-256 が manifest と一致'
        Show '再生できる' ($p.loopback_audible_packets -gt 0) "出力先 $($p.output_device)、ピーク $($p.loopback_peak_dbfs) dBFS"
    }
    'AT02' {
        Write-Host '== AT-02 既定の出力先で TTS を再生しながら、2 つの出力先を内部録音します' -ForegroundColor Cyan
        $tts = Join-Path $Root 'verify\audio\test_ja_tts.wav'
        $dev = (& $exe devices) -join "`n" | ConvertFrom-Json
        $default = (@($dev.loopback) | Where-Object is_default | Select-Object -First 1).name
        $other = (@($dev.loopback) | Where-Object { -not $_.is_default } | Select-Object -First 1).name
        if (-not $default -or -not $other) { throw 'AT-02 には出力先が 2 つ以上必要です' }
        Write-Host "   再生と録音: $default / 無音のはずの出力先: $other"
        $jobA = Start-Job { param($e, $w, $d) & $e session loopback 25 --workspace $w --loopback-device $d --title 'AT-02 再生した出力先' --level-every 5 } -ArgumentList $exe, $workspace, $default
        $jobB = Start-Job { param($e, $w, $d) & $e session loopback 25 --workspace $w --loopback-device $d --title 'AT-02 別の出力先' --level-every 5 } -ArgumentList $exe, $workspace, $other
        Start-Sleep -Seconds 3
        $player = New-Object System.Media.SoundPlayer $tts
        $player.PlaySync(); $player.Dispose()
        $a = (Receive-Job $jobA -Wait -AutoRemoveJob) ; $b = (Receive-Job $jobB -Wait -AutoRemoveJob)
        $ja = ($a[([array]::IndexOf($a, '{'))..($a.Count - 1)]) -join "`n"; [IO.File]::WriteAllText((Join-Path $out 'at02-played.json'), $ja, $utf8)
        $jb = ($b[([array]::IndexOf($b, '{'))..($b.Count - 1)]) -join "`n"; [IO.File]::WriteAllText((Join-Path $out 'at02-other.json'), $jb, $utf8)
        $ra = $ja | ConvertFrom-Json; $rb = $jb | ConvertFrom-Json
        $ta = $ra.tracks[0]; $tb = $rb.tracks[0]
        $silA = 0; foreach ($ev in @($ta.events)) { if ($ev.Kind -eq 'LoopbackSilence') { $silA += $ev.EndMs - $ev.StartMs } }
        $silB = 0; foreach ($ev in @($tb.events)) { if ($ev.Kind -eq 'LoopbackSilence') { $silB += $ev.EndMs - $ev.StartMs } }
        Write-Host '== 結果' -ForegroundColor Cyan
        Show '再生した出力先の音が保存される' ($ta.recorded_seconds -gt 20 -and $silA -lt 15000) "$($ta.device): $($ta.recorded_seconds) 秒、うち無音補完 $([Math]::Round($silA / 1000, 1)) 秒"
        Show '別の出力先は無音として正常に保存' ($rb.CaptureState -eq 'Finalized' -and $silB -gt 20000 -and -not ($tb.events | Where-Object Kind -in 'QueueOverflow', 'DeviceDisconnected', 'WriteFailed')) "$($tb.device): 状態 $($rb.CaptureState)、無音補完 $([Math]::Round($silB / 1000, 1)) 秒、欠損イベントなし"
        Show '保存の照合' ([bool]$ra.verified -and [bool]$rb.verified) 'フレーム数と SHA-256 が manifest と一致'
    }
    'AT03' {
        $seconds = $Minutes * 60
        Write-Host "== AT-03 両方モードで $Minutes 分録音します（校正音つき）" -ForegroundColor Cyan
        Write-Host "   ・「$BeepDevice」の出力先から 20 秒おきに短い「ピッ」という音が鳴ります。" -ForegroundColor Yellow
        Write-Host '   ・その音がマイクに届くよう、ヘッドセットのマイクをスピーカーに向けて置いてください（音量は中くらい）。' -ForegroundColor Yellow
        Write-Host '   ・録音中は PC をスリープさせません（画面は消えても構いません）。席を外して大丈夫です。' -ForegroundColor Yellow
        Read-Host '   準備ができたら Enter を押してください' | Out-Null
        $beeper = Start-Process -FilePath $exe -ArgumentList @('beeps', "$($seconds + 5)", '--device', "`"$BeepDevice`"", '--interval', '20') -NoNewWindow -PassThru
        $r = Invoke-Probe @('session', 'both', "$seconds", '--workspace', $workspace, '--loopback-device', $BeepDevice, '--title', "AT-03 $Minutes 分", '--level-every', '60') 'at03-session.json'
        $beeper.WaitForExit()
        Write-Host '== 時刻のずれを解析しています' -ForegroundColor Cyan
        $s = Invoke-Probe @('analyze-sync', $r.session_directory) 'at03-sync.json'
        $mem = @($r.memory_per_minute)
        Write-Host '== 結果' -ForegroundColor Cyan
        Show '校正音の検出' ($s.matched_in_mic -ge [Math]::Max(3, $s.loopback_bursts * 0.8)) "内部音声 $($s.loopback_bursts) 回、うちマイクでも検出 $($s.matched_in_mic) 回"
        Write-Host ("   補正前（サンプル数で数えた時刻）: ずれの幅 {0} ms、1 時間あたり {1} ms" -f $s.mic_minus_loopback_uncorrected.spread_ms, $s.mic_minus_loopback_uncorrected.drift_ms_per_hour)
        Show '時刻写像で補正したずれ' ([bool]$s.pass) ("幅 {0} ms（目標 100 ms 以内）、1 時間あたり {1} ms" -f $s.mic_minus_loopback_time_map.spread_ms, $s.mic_minus_loopback_time_map.drift_ms_per_hour)
        Show '別々の原音として保存' ([bool]$r.verified -and @($r.tracks).Count -eq 2) "トラック $(@($r.tracks).Count) 本、チャンク $((@($r.tracks) | ForEach-Object { @($_.chunks).Count } | Measure-Object -Sum).Sum) 個すべて照合一致"
        if ($mem.Count -ge 2) {
            Show 'メモリ（AT-05 の代替）' ($mem[-1].working_set_mb -le 500) ("1 分後 {0} MB → 終了時 {1} MB（最大 {2} MB、目標 500 MB 以内）" -f $mem[0].working_set_mb, $mem[-1].working_set_mb, ($mem | Measure-Object working_set_mb -Maximum).Maximum)
        }
    }
}
Write-Host "   詳細: $out"
Write-Host '   終わったら、チャットで「終わった」と伝えてください。'
