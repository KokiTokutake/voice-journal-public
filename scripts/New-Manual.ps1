<#
.SYNOPSIS
  使い方の説明書（docs\manual\manual.html）を PDF にする。
.DESCRIPTION
  Microsoft Edge のヘッドレス印刷で A4 の PDF を作る（ページ番号は原稿の CSS の @page で付ける）。
  出力は %LOCALAPPDATA%\VoiceJournal\dist\<版>\VoiceJournal-使い方の説明書-<版>.pdf（インストーラーと同じ場所）。
  画面の写真（docs\manual\img）は試験用のワークスペースで撮ったもの。画面を変えたら撮り直す。
.EXAMPLE
  .\scripts\New-Manual.ps1
#>
param([string]$Root = (Join-Path $env:LOCALAPPDATA 'VoiceJournal'))
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$html = Join-Path $repo 'docs\manual\manual.html'
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'Microsoft Edge が見つからない' }

$version = (dotnet msbuild (Join-Path $repo 'src\VoiceJournal.App\VoiceJournal.App.csproj') -nologo -getProperty:Version).Trim()
if ($LASTEXITCODE -ne 0 -or -not $version) { throw '版を読めなかった' }
$dist = Join-Path $Root "dist\$version"
New-Item -ItemType Directory -Force $dist | Out-Null
$pdf = Join-Path $dist "VoiceJournal-使い方の説明書-$version.pdf"
if (Test-Path $pdf) { Remove-Item $pdf -Force }

# 利用者の Edge のプロファイルを使わない（開いている Edge にも影響しない）
$profileDir = Join-Path $env:TEMP "vj-manual-edge-$PID"
$uri = ([Uri](Resolve-Path $html).Path).AbsoluteUri
$p = Start-Process $edge -ArgumentList @('--headless=new', '--disable-gpu', '--no-first-run', "--user-data-dir=`"$profileDir`"",
    '--no-pdf-header-footer', "--print-to-pdf=`"$pdf`"", $uri) -PassThru -Wait -WindowStyle Hidden
Remove-Item $profileDir -Recurse -Force -ErrorAction SilentlyContinue
if (-not (Test-Path $pdf)) { throw "PDF ができなかった（終了コード $($p.ExitCode)）" }
Write-Host ("{0}（{1:N1} MB）" -f $pdf, ((Get-Item $pdf).Length / 1MB))
