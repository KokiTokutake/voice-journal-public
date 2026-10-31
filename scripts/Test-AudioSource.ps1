<#
.SYNOPSIS
  Phase A の音源単体試験。NAudio（WasapiRecorder）でマイク／システム音声を録音し、whisper.cpp で文字起こしして CER を測る。
.DESCRIPTION
  - loopback: 検証用の TTS 音声（New-TestSpeech.ps1 で生成）をスピーカーで再生しながら、システム音声を録音する。操作は不要。
  - mic:      画面に表示する原稿を、利用者がマイクに向かって読み上げる。Enter で録音を開始する。
  録音ごとに原音（.orig.wav）、16kHz モノラル（.wav）、計測値（.probe.json）、文字起こし結果（audio-<source>-result.json）を
  %LOCALAPPDATA%\VoiceJournal\verify 以下に保存する。
.EXAMPLE
  .\scripts\Test-AudioSource.ps1                 # loopback → mic の順に実行
  .\scripts\Test-AudioSource.ps1 -Source loopback
  .\scripts\Test-AudioSource.ps1 -Source mic -Seconds 30
#>
param(
    [ValidateSet('both', 'loopback', 'mic')]
    [string]$Source = 'both',
    [int]$Seconds = 30,
    [string]$Root = (Join-Path $env:LOCALAPPDATA 'VoiceJournal'),
    [switch]$NoPrompt,
    # マイクをシステムの音声処理（ノイズ抑制・自動音量調整）を通さない raw モードで録音する
    [switch]$Raw
)
$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$repo = Split-Path $PSScriptRoot -Parent
$audioDir = Join-Path $Root 'verify\audio'
$ttsWav = Join-Path $audioDir 'test_ja_tts.wav'
$ttsRef = Join-Path $audioDir 'test_ja_tts.reference.txt'
if (-not (Test-Path $ttsWav) -or -not (Test-Path $ttsRef)) { throw "先に .\scripts\New-TestSpeech.ps1 を実行してください（$ttsWav がありません）" }
$reference = [IO.File]::ReadAllText($ttsRef, $utf8).Trim()

# --- 録音ツールのビルド ------------------------------------------------------
$probeProj = Join-Path $repo 'tools\VoiceJournal.AudioProbe\VoiceJournal.AudioProbe.csproj'
$probeExe = Join-Path $repo 'tools\VoiceJournal.AudioProbe\bin\Release\net10.0-windows\VoiceJournal.AudioProbe.exe'
Write-Host '== 録音ツールをビルド' -ForegroundColor Cyan
dotnet build $probeProj -c Release --nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw '録音ツールのビルドに失敗しました' }

function Invoke-Probe([string]$src, [double]$sec, [string]$outBase, [scriptblock]$during, [switch]$RawMode) {
    $log = "$outBase.probe.log"
    $probeArgs = @('record', $src, $sec.ToString([Globalization.CultureInfo]::InvariantCulture), "`"$outBase`"")
    if ($RawMode) { $probeArgs += '--raw' }
    $p = Start-Process -FilePath $probeExe -ArgumentList $probeArgs `
        -NoNewWindow -PassThru -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    $null = $p.Handle   # PowerShell 5.1: ハンドルを先に取得しないと終了後に ExitCode が空になる
    if ($during) { & $during }
    $p.WaitForExit()
    $err = if (Test-Path "$log.err") { [IO.File]::ReadAllText("$log.err").Trim() } else { '' }
    switch ($p.ExitCode) {
        0 { }
        3 { Write-Warning "$src : 録音レベルが -60 dBFS 未満です（ほぼ無音）。デバイスや音量を確認してください" }
        default { throw "録音ツールが終了コード $($p.ExitCode) で失敗しました: $err" }
    }
    return [IO.File]::ReadAllText("$outBase.probe.json", $utf8) | ConvertFrom-Json
}

function Invoke-Stt([string]$name) {
    $wav = Join-Path $audioDir "$name.wav"
    [IO.File]::WriteAllText((Join-Path $audioDir "$name.reference.txt"), $reference, $utf8)
    & (Join-Path $PSScriptRoot 'Test-Environment.ps1') -Root $Root -Wav $wav -SkipLlm -ResultName "audio-$name-result.json" | Out-Null
    return [IO.File]::ReadAllText((Join-Path $Root "verify\audio-$name-result.json"), $utf8) | ConvertFrom-Json
}

$summary = @()

# --- loopback ---------------------------------------------------------------
if ($Source -in 'both', 'loopback') {
    Write-Host '== システム音声（loopback）: TTS 音声をスピーカーで再生しながら録音します' -ForegroundColor Cyan
    $len = (Get-Item $ttsWav).Length
    $ttsSeconds = [Math]::Ceiling(($len - 44) / 32000.0)   # 16kHz/16bit/mono = 32000 B/s
    $name = 'loopback_tts'
    $probe = Invoke-Probe 'loopback' ($ttsSeconds + 4) (Join-Path $audioDir $name) {
        Start-Sleep -Milliseconds 1500
        $player = New-Object System.Media.SoundPlayer $ttsWav
        try { $player.PlaySync() } finally { $player.Dispose() }
    }
    $stt = Invoke-Stt $name
    $summary += [pscustomobject]@{ 音源 = 'loopback'; デバイス = $probe.device; 'ピーク dBFS' = $probe.peak_dbfs; 'RMS dBFS' = $probe.rms_dbfs; '補完した無音 秒' = $probe.loopback_padded_seconds; 'STT 秒' = $stt.stt_seconds; CER = $stt.cer; 文字起こし = $stt.transcript }
}

# --- mic --------------------------------------------------------------------
if ($Source -in 'both', 'mic') {
    Write-Host "== マイク: $Seconds 秒間録音します。次の原稿を、普段の会議と同じ声の大きさで読み上げてください" -ForegroundColor Cyan
    Write-Host ''
    Write-Host $reference -ForegroundColor Yellow
    Write-Host ''
    if (-not $NoPrompt) { [void](Read-Host '準備ができたら Enter を押してください（押すと 3 秒後に録音を開始します）') }
    foreach ($i in 3, 2, 1) { Write-Host "  $i ..."; Start-Sleep -Seconds 1 }
    Write-Host '  録音中です。読み上げてください（読み終わったら、そのまま終了を待ってください）' -ForegroundColor Green
    $name = if ($Raw) { 'mic_real_raw' } else { 'mic_real' }
    $probe = Invoke-Probe 'mic' $Seconds (Join-Path $audioDir $name) $null -RawMode:$Raw
    Write-Host '  録音を終了しました' -ForegroundColor Green
    $stt = Invoke-Stt $name
    $summary += [pscustomobject]@{ 音源 = $name; デバイス = $probe.device; 'ピーク dBFS' = $probe.peak_dbfs; 'RMS dBFS' = $probe.rms_dbfs; '補完した無音 秒' = $probe.loopback_padded_seconds; 'STT 秒' = $stt.stt_seconds; CER = $stt.cer; 文字起こし = $stt.transcript }
}

Write-Host '== 結果' -ForegroundColor Cyan
$summary | Format-List
[IO.File]::WriteAllText((Join-Path $Root 'verify\audio-source-summary.json'), ($summary | ConvertTo-Json -Depth 5), $utf8)
