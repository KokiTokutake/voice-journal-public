<#
.SYNOPSIS
  Windows 標準の日本語 TTS（Haruka）で whisper.cpp 動作確認用の 16kHz/16bit/mono WAV を生成する。
.EXAMPLE
  .\scripts\New-TestSpeech.ps1 -OutDir "$env:LOCALAPPDATA\VoiceJournal\verify\audio"
#>
param(
    [string]$OutDir = (Join-Path $env:LOCALAPPDATA 'VoiceJournal\verify\audio'),
    [string]$Name = 'test_ja_tts'
)
$ErrorActionPreference = 'Stop'

# 会議の短い一場面を想定した原稿。決定・タスク・未確定事項・否定を含める
$script = @(
    '本日の定例会議を始めます。',
    'ログ収集方式については、来週の金曜日までに田中さんが調査することになりました。',
    '既存のパソコンで性能が足りるかどうかは、まだ課題として残っています。',
    'なお、サーバーの追加購入は今回は行いません。'
) -join ''

Add-Type -AssemblyName System.Speech
New-Item -ItemType Directory -Force $OutDir | Out-Null
$wav = Join-Path $OutDir "$Name.wav"
$txt = Join-Path $OutDir "$Name.reference.txt"

$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
try {
    $synth.SelectVoice('Microsoft Haruka Desktop')
    $format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
    $synth.SetOutputToWaveFile($wav, $format)
    $synth.Speak($script)
} finally {
    $synth.Dispose()
}

# 正解原稿は BOM なし UTF-8 で保存
[System.IO.File]::WriteAllText($txt, $script, (New-Object System.Text.UTF8Encoding($false)))
Get-Item $wav, $txt | Select-Object FullName, Length
