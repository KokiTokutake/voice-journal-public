<#
.SYNOPSIS
  インストーラー（自分のユーザーだけに入れる MSI）を作る。
.DESCRIPTION
  1. アプリを x64 の self-contained で発行する（詳細仕様書 4 章の配布方式）
  2. 発行したアプリを --self-test で起動し、版と、実行時に行う分析スキーマのコンパイルを確かめる
  3. WiX（installer\VoiceJournal.Installer.wixproj）で MSI を作り、SHA-256 を出す
  版は Directory.Build.props の VersionPrefix / VersionSuffix。MSI の版は VersionPrefix（数字だけ）で、
  同じ版の新しいビルドは古いビルドを置き換える。
  インストール先は %LOCALAPPDATA%\Programs\VoiceJournal（管理者権限は要らない）、スタートメニューに「Voice Journal」。
  whisper・llama-server・モデル・データは %LOCALAPPDATA%\VoiceJournal のままで、MSI には入れず、アンインストールでも消さない。

  -Verify: MSI を作ったあと、静かにインストール → ショートカットとファイルを確かめる → インストールしたアプリで --self-test
  → アンインストール → アプリのフォルダーとショートカットが消え、%LOCALAPPDATA%\VoiceJournal の一覧が変わっていないことを確かめる。
  すでにインストールされているときは止まる（利用者が入れたものを消さない）。
  出力は %LOCALAPPDATA%\VoiceJournal\dist\<版>\（OneDrive の外）。
.EXAMPLE
  .\scripts\New-Installer.ps1
  .\scripts\New-Installer.ps1 -Verify
#>
param(
    [switch]$Verify,
    [string]$Root = (Join-Path $env:LOCALAPPDATA 'VoiceJournal')
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$app = Join-Path $repo 'src\VoiceJournal.App\VoiceJournal.App.csproj'
$wixproj = Join-Path $repo 'installer\VoiceJournal.Installer.wixproj'
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\VoiceJournal'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Voice Journal.lnk'

function Invoke-SelfTest([string]$exe, [string]$report) {
    if (Test-Path $report) { Remove-Item $report -Force }
    $p = Start-Process -FilePath $exe -ArgumentList @('--self-test', "`"$report`"") -PassThru -Wait
    if (-not (Test-Path $report)) { throw "自己診断の結果ができなかった（終了コード $($p.ExitCode)）: $exe" }
    $result = Get-Content $report -Raw -Encoding UTF8 | ConvertFrom-Json
    Write-Host ("   版 {0}、スキーマ {1}（{2} ms）、終了コード {3}" -f $result.app_version, $result.schema, $result.schema_ms, $p.ExitCode)
    if ($p.ExitCode -ne 0 -or -not $result.ok) { throw "自己診断に失敗した: $report" }
    return $result
}

$version = (dotnet msbuild $app -nologo -getProperty:Version).Trim()
if ($LASTEXITCODE -ne 0 -or -not $version) { throw '版を読めなかった' }
$dist = Join-Path $Root "dist\$version"
$publish = Join-Path $dist 'publish'
Write-Host "== Voice Journal $version" -ForegroundColor Cyan
Write-Host "   出力先: $dist"

Write-Host '== アプリを発行（win-x64、self-contained）' -ForegroundColor Cyan
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish $app -nologo -v q -c Release -r win-x64 --self-contained -o $publish
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

Write-Host '== 発行したアプリの自己診断' -ForegroundColor Cyan
Invoke-SelfTest (Join-Path $publish 'VoiceJournal.App.exe') (Join-Path $dist 'self-test-publish.json') | Out-Null

Write-Host '== MSI をビルド（WiX）' -ForegroundColor Cyan
dotnet build $wixproj -nologo -v q -c Release "-p:PublishDir=$publish\" "-p:OutputPath=$dist\"
if ($LASTEXITCODE -ne 0) { throw 'installer build failed' }
$msi = Get-ChildItem $dist -Filter '*.msi' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $msi) { throw "MSI が見つからない: $dist" }
$hash = (Get-FileHash $msi.FullName -Algorithm SHA256).Hash
Set-Content -Path "$($msi.FullName).sha256" -Value "$hash  $($msi.Name)" -Encoding ASCII
Write-Host ("   {0}（{1:N1} MB）" -f $msi.FullName, ($msi.Length / 1MB))
Write-Host "   SHA-256 $hash"

if (-not $Verify) { return }

Write-Host '== インストールとアンインストールの確認' -ForegroundColor Cyan
if (Test-Path (Join-Path $installDir 'VoiceJournal.App.exe')) {
    throw "すでにインストールされている（$installDir）。確認するときは先にアンインストールする"
}
$dataBefore = @(Get-ChildItem $Root -Force | Where-Object { $_.Name -ne 'dist' } | ForEach-Object Name) -join ','
$log = Join-Path $dist 'install.log'
$p = Start-Process msiexec.exe -ArgumentList @('/i', "`"$($msi.FullName)`"", '/qn', '/l*v', "`"$log`"") -PassThru -Wait
if ($p.ExitCode -ne 0) { throw "インストールに失敗した（終了コード $($p.ExitCode)、ログ $log）" }
$checks = [ordered]@{}
$checks['アプリのファイル'] = Test-Path (Join-Path $installDir 'VoiceJournal.App.exe')
$checks['分析スキーマ用の参照（refs）'] = Test-Path (Join-Path $installDir 'refs')
$checks['第三者のライセンス一覧'] = Test-Path (Join-Path $installDir 'third-party-notices.md')
$checks['スタートメニューのショートカット'] = Test-Path $shortcut
$published = @(Get-ChildItem $publish -Recurse -File).Count
$installed = @(Get-ChildItem $installDir -Recurse -File).Count
$checks["ファイル数（発行 $published + 1 = 入った $installed）"] = ($installed -eq $published + 1)
$installedTest = Invoke-SelfTest (Join-Path $installDir 'VoiceJournal.App.exe') (Join-Path $dist 'self-test-installed.json')
$checks['インストールしたアプリの自己診断'] = [bool]$installedTest.ok

$log = Join-Path $dist 'uninstall.log'
$p = Start-Process msiexec.exe -ArgumentList @('/x', "`"$($msi.FullName)`"", '/qn', '/l*v', "`"$log`"") -PassThru -Wait
$checks['アンインストール（終了コード 0）'] = ($p.ExitCode -eq 0)
$checks['アプリのフォルダーが消えた'] = -not (Test-Path $installDir)
$checks['ショートカットが消えた'] = -not (Test-Path $shortcut)
$dataAfter = @(Get-ChildItem $Root -Force | Where-Object { $_.Name -ne 'dist' } | ForEach-Object Name) -join ','
$checks["データ・ツール・モデルが残った（$dataAfter）"] = ($dataBefore -eq $dataAfter)

$failed = 0
foreach ($k in $checks.Keys) {
    if ($checks[$k]) { Write-Host "   OK  $k" -ForegroundColor Green } else { Write-Host "   NG  $k" -ForegroundColor Red; $failed++ }
}
$checks.GetEnumerator() | ForEach-Object { [pscustomobject]@{ check = $_.Key; ok = $_.Value } } |
    Export-Csv (Join-Path $dist 'verify.csv') -NoTypeInformation -Encoding UTF8
if ($failed -gt 0) { throw "確認で $failed 件が NG（$dist）" }
Write-Host '== すべて OK' -ForegroundColor Green
