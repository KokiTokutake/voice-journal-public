<#
.SYNOPSIS
  Phase F（安全反映・復旧）の試験。
.DESCRIPTION
  - Crash: AT-17。反映（詳細仕様書 14.2）の 12 か所で、反映している子プロセスを実際に強制終了（TerminateProcess）し、
    再起動時の判定（14.3）で前進・やり直し・DB だけの確定ができ、二重確定がないことを確かめる。
    結果は %LOCALAPPDATA%\VoiceJournal\verify\phase-f\ に出る（試験用のワークスペースもそこに作る）。
.EXAMPLE
  .\scripts\Test-PhaseF.ps1 -Test Crash
#>
param(
    [Parameter(Mandatory)] [ValidateSet('Crash')] [string]$Test,
    [string]$Root = (Join-Path $env:LOCALAPPDATA 'VoiceJournal')
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$out = Join-Path $Root 'verify\phase-f'
New-Item -ItemType Directory -Force $out | Out-Null

Write-Host '== 評価ツールをビルド' -ForegroundColor Cyan
dotnet build (Join-Path $repo 'tools\VoiceJournal.AudioProbe\VoiceJournal.AudioProbe.csproj') -nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
$exe = Join-Path $repo 'tools\VoiceJournal.AudioProbe\bin\Debug\net10.0-windows\VoiceJournal.AudioProbe.exe'

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$work = Join-Path $out "crash-$stamp"
$result = Join-Path $out "crash-$stamp.json"
Write-Host "== 12 か所で強制終了して復旧（$work）" -ForegroundColor Cyan
& $exe apply-crash $work | Out-File -FilePath $result -Encoding utf8
$code = $LASTEXITCODE
$json = Get-Content $result -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($r in $json.results) {
    $mark = if ($r.ok) { '[合格]' } else { '[不合格]' }
    Write-Host ("   {0} {1}/{2}  {3}" -f $mark, $r.step, $r.sequence, $r.detail)
}
if ($code -eq 0) { Write-Host "   [合格] AT-17: $($json.points) か所すべてで、承認した内容に 1 回だけ確定" -ForegroundColor Green }
else { Write-Host "   [不合格] AT-17: $($json.failed) か所で失敗" -ForegroundColor Red }
Write-Host "   結果: $result"
exit $code
