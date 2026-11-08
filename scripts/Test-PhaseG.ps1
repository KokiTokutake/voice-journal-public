<#
.SYNOPSIS
  Phase G（運用・配布）の試験。
.DESCRIPTION
  - Search: AT-21。1,000 セッション・1 万項目のワークスペースを作り、ライブラリと管理一覧の検索（短い語・部分一致・日付・状態）の時間を測る。
  結果は %LOCALAPPDATA%\VoiceJournal\verify\phase-g\ に出る（試験用のワークスペースもそこに作る）。
.EXAMPLE
  .\scripts\Test-PhaseG.ps1 -Test Search
#>
param(
    [Parameter(Mandatory)] [ValidateSet('Search')] [string]$Test,
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
