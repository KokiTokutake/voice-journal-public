<#
.SYNOPSIS
  Phase C の評価と受入試験（詳細仕様書 22 章 CER・AT-06・AT-26、20 章 NFR-06）を、アプリと同じ文字起こし処理で行う。
.DESCRIPTION
  録音は利用者のライブラリではなく %LOCALAPPDATA%\VoiceJournal\verify\phase-c\eval-workspace に保存し、
  結果を verify\phase-c\ に書く。
  - Record:   既定のマイクで録音しながら、会議風の原稿 3 本（scripts\stt-eval\meeting-*.txt、各約 10 分）を順に表示する。
              利用者が読み上げ、1 本読み終えるごとに Enter を押す（その時刻を原稿の区切りとして記録する）
  - Evaluate: 録音したセッションを設定を変えて文字起こしし（small / medium、VAD あり / なし、ゲインあり / なし）、
              原稿ごとの CER と所要時間を出して、CER・NFR-06・AT-06・AT-26 を判定する。操作不要（30 分の録音で約 30 分）
  - Check:    セッションの Revision の並びを表示し、Evaluate の時点から既存の Revision が変わっていないこと、
              修正 Revision の参照（親の Revision と segment_id）がたどれることを確かめる（AT-07・AT-26）
.EXAMPLE
  .\scripts\Test-PhaseC.ps1 -Test Record
  .\scripts\Test-PhaseC.ps1 -Test Evaluate            # 直前に Record したセッション
  .\scripts\Test-PhaseC.ps1 -Test Evaluate -Quick     # small と medium の既定の設定だけ
  .\scripts\Test-PhaseC.ps1 -Test Check
#>
param(
    [Parameter(Mandatory)] [ValidateSet('Record', 'Evaluate', 'Check')] [string]$Test,
    # Evaluate / Check: セッションのフォルダー（省略時は直前に Record したもの）
    [string]$Session,
    [switch]$Quick,
    [int]$MaxMinutes = 60,
    [string]$Root = (Join-Path $env:LOCALAPPDATA 'VoiceJournal')
)
$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = $utf8
$repo = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $repo 'tools\VoiceJournal.AudioProbe\VoiceJournal.AudioProbe.csproj'
$exe = Join-Path $repo 'tools\VoiceJournal.AudioProbe\bin\Release\net10.0-windows\VoiceJournal.AudioProbe.exe'
$out = Join-Path $Root 'verify\phase-c'
$workspace = Join-Path $out 'eval-workspace'
$recordJson = Join-Path $out 'record.json'
$snapshotJson = Join-Path $out 'revisions-snapshot.json'
$scripts = @(1..3 | ForEach-Object { Join-Path $PSScriptRoot "stt-eval\meeting-$_.txt" })
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

function Resolve-Session {
    if ($Session) { return (Resolve-Path $Session).Path }
    if (-not (Test-Path $recordJson)) { throw '先に -Test Record で録音するか、-Session でセッションのフォルダーを指定してください' }
    return (Read-Json $recordJson).session_directory
}

# SHA-256 of every file of every revision folder, keyed "rev-NNNN/<file>".
function Get-RevisionHashes([string]$dir) {
    $map = [ordered]@{}
    $root = Join-Path $dir 'transcripts'
    if (Test-Path $root) {
        foreach ($rev in (Get-ChildItem $root -Directory | Where-Object Name -match '^rev-\d{4}$' | Sort-Object Name)) {
            foreach ($f in (Get-ChildItem $rev.FullName -File | Sort-Object Name)) {
                $map["$($rev.Name)/$($f.Name)"] = (Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
    }
    return $map
}

function Format-Minutes([double]$ms) { return ('{0:0.0} 分' -f ($ms / 60000)) }

switch ($Test) {
    'Record' {
        Write-Host '== Phase C 評価用の読み上げ録音' -ForegroundColor Cyan
        Write-Host @'
   既定のマイクで録音しながら、会議風の原稿を 3 本、順に表示します（各約 10 分、合計約 30 分）。
   - 静かな部屋で、会議で話すときのふつうの速さと声で読み上げてください
   - 「──」で始まる行は読まないでください
   - 1 本読み終えたら Enter を押してください。次の原稿が表示されます。原稿の間で休憩してもかまいません
   - 言い間違えても止めずに、そのまま続けてください
   - 3 本目を読み終えて Enter を押すと録音が終わります（最長 60 分で自動的に終わります）
'@
        Read-Host '   準備ができたら Enter を押してください（録音が始まり、1 本目の原稿が表示されます）' | Out-Null
        if (Test-Path $recordJson) { Remove-Item $recordJson }
        # Started with a shared console (not piped) so that the tool reads Enter directly from the keyboard.
        $argList = @('session', 'mic', ($MaxMinutes * 60), '--workspace', "`"$workspace`"", '--title', '"Phase C 評価（読み上げ 3 本）"',
            '--level-every', '30', '--prompt-files', "`"$($scripts -join ';')`"", '--out', "`"$recordJson`"")
        $p = Start-Process -FilePath $exe -ArgumentList $argList -NoNewWindow -Wait -PassThru
        if (-not (Test-Path $recordJson)) { throw "録音ツールが結果を出力しませんでした（終了コード $($p.ExitCode)）" }
        $r = Read-Json $recordJson
        $t = $r.tracks[0]
        $marks = @($r.prompt_marks_ms)
        Write-Host '== 結果' -ForegroundColor Cyan
        Show '原稿の区切り' ($marks.Count -eq 3) ((($marks | ForEach-Object { Format-Minutes $_ }) -join ' / ') + ' の時点で Enter')
        Show '録音の長さ' ($t.recorded_seconds -ge 1200) ("{0}（NFR-06 には約 30 分が必要）" -f (Format-Minutes ($t.recorded_seconds * 1000)))
        Show '保存の照合' ([bool]$r.verified) 'フレーム数と SHA-256 が manifest と一致'
        Write-Host "   セッション: $($r.session_directory)"
        Write-Host '   次は .\scripts\Test-PhaseC.ps1 -Test Evaluate で評価します（操作不要）。'
    }
    'Evaluate' {
        $dir = Resolve-Session
        $marks = @()
        if (Test-Path $recordJson) {
            $r = Read-Json $recordJson
            if ($r.session_directory -eq $dir) { $marks = @($r.prompt_marks_ms) }
        }
        if ($marks.Count -lt 2) { throw '原稿の区切り（record.json の prompt_marks_ms）が見つかりません' }
        $runs = @(
            @{ Name = 'small-vad-gain'; Label = 'small・VAD・ゲイン（既定）'; Args = @('--model', 'small') },
            @{ Name = 'medium-vad-gain'; Label = 'medium・VAD・ゲイン'; Args = @('--model', 'medium') },
            @{ Name = 'small-novad-gain'; Label = 'small・VAD なし・ゲイン'; Args = @('--model', 'small', '--no-vad') },
            @{ Name = 'small-vad-nogain'; Label = 'small・VAD・ゲインなし'; Args = @('--model', 'small', '--no-gain') },
            @{ Name = 'small-novad-nogain'; Label = 'small・VAD なし・ゲインなし'; Args = @('--model', 'small', '--no-vad', '--no-gain') }
        )
        if ($Quick) { $runs = $runs[0..1] }
        $evalDir = Join-Path $out ('eval-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
        New-Item -ItemType Directory -Force $evalDir | Out-Null
        $before = Get-RevisionHashes $dir
        $results = New-Object System.Collections.Generic.List[object]
        foreach ($run in $runs) {
            Write-Host "== $($run.Label) で文字起こし" -ForegroundColor Cyan
            $tj = Join-Path $evalDir "$($run.Name).transcribe.json"
            & $exe transcribe $dir @($run.Args) --out $tj | Where-Object { $_ -match '^transcribe:|Transcribing .* (0|50|100)%$' } | ForEach-Object { Write-Host "   $_" }
            $t = Read-Json $tj
            $c = $null
            if ($t.state -eq 'Transcribed') {
                $cj = Join-Path $evalDir "$($run.Name).cer.json"
                & $exe cer $dir --revision $t.result.revision --refs ($scripts -join ';') --marks ($marks -join ';') --hyp-out (Join-Path $evalDir "$($run.Name).hyp.txt") --out $cj | Out-Null
                $c = Read-Json $cj
                Write-Host ("   Rev {0}: CER {1}、全体 {2:P1}、所要 {3}（音声 {4}）" -f $t.result.revision,
                    (($c.scripts | ForEach-Object { '{0:P1}' -f $_.cer.rate }) -join ' / '), $c.all.rate, (Format-Minutes $t.wall_ms), (Format-Minutes $t.result.audio_ms))
            }
            else {
                Write-Host "   失敗: $($t.state) $($t.error) $($t.error_detail)" -ForegroundColor Red
            }
            $results.Add([pscustomobject]@{ Run = $run; T = $t; C = $c })
        }
        $after = Get-RevisionHashes $dir
        [IO.File]::WriteAllText($snapshotJson, (@{ session_directory = $dir; revisions = $after } | ConvertTo-Json -Depth 4), $utf8)

        # Markdown table for the report.
        $md = New-Object System.Text.StringBuilder
        [void]$md.AppendLine('| 設定 | Rev | 原稿 1 | 原稿 2 | 原稿 3 | 全体（S/D/I） | 所要時間 | RTF | 区間 | 目印付き |')
        [void]$md.AppendLine('|---|---|---|---|---|---|---|---|---|---|')
        foreach ($x in $results) {
            if (-not $x.C) { [void]$md.AppendLine("| $($x.Run.Label) | 失敗 | | | | | | | | |"); continue }
            $s = @($x.C.scripts | ForEach-Object { '{0:0.0}%' -f ($_.cer.rate * 100) })
            [void]$md.AppendLine(('| {0} | {1} | {2} | {3} | {4} | {5:0.0}%（{6}/{7}/{8}） | {9} | {10:0.00} | {11} | {12} |' -f $x.Run.Label, $x.T.result.revision,
                    $s[0], $s[1], $s[2], ($x.C.all.rate * 100), $x.C.all.substitutions, $x.C.all.deletions, $x.C.all.insertions,
                    (Format-Minutes $x.T.wall_ms), $x.T.result.rtf_wall, $x.T.result.segments, $x.T.result.flagged_segments))
        }
        [IO.File]::WriteAllText((Join-Path $evalDir 'evaluation.md'), $md.ToString(), $utf8)

        Write-Host '== 結果' -ForegroundColor Cyan
        Write-Host $md.ToString()
        $byName = @{}
        foreach ($x in $results) { $byName[$x.Run.Name] = $x }
        $default = $byName['small-vad-gain']
        $medium = $byName['medium-vad-gain']
        if ($default.C) {
            $audio = $default.T.result.audio_ms
            Show 'NFR-06（small・CPU で 30 分の音声を 30 分以内）' ($default.T.wall_ms -le $audio -and $audio -ge 1500000) ("音声 {0} を {1} で処理（RTF {2}）" -f (Format-Minutes $audio), (Format-Minutes $default.T.wall_ms), $default.T.result.rtf_wall)
            $worst = ($default.C.scripts | ForEach-Object { $_.cer.rate } | Measure-Object -Maximum).Maximum
            Show 'CER（静かな日本語 10 分×3 本で 15% 以下）' ($worst -le 0.15) ((($default.C.scripts | ForEach-Object { '{0:P1}' -f $_.cer.rate }) -join ' / ') + "（small の既定の設定）")
        }
        else { Show 'NFR-06 / CER' $false 'small の既定の設定で文字起こしできませんでした' }

        $stt = Read-Json (Join-Path $Root 'config\stt.json')
        $at06 = $true
        $detail = @()
        foreach ($x in @($default, $medium)) {
            if (-not $x -or -not $x.C) { $at06 = $false; continue }
            $prov = $x.T.result.provenance
            $registered = $stt.models.($prov.ModelName).sha256
            $revDir = Join-Path $dir ('transcripts\rev-{0:0000}' -f $x.T.result.revision)
            $files = (Test-Path (Join-Path $revDir 'transcript.raw.json')) -and (Test-Path (Join-Path $revDir 'transcript.raw.txt'))
            if ($prov.ModelSha256 -ne $registered -or -not $files -or $x.T.result.segments -eq 0) { $at06 = $false }
            $detail += ('{0}: Rev {1}、model {2}…（登録値と{3}）、区間 {4}、全体 CER {5:P1}、{6}' -f $prov.ModelName, $x.T.result.revision, $prov.ModelSha256.Substring(0, 12),
                $(if ($prov.ModelSha256 -eq $registered) { '一致' } else { '不一致' }), $x.T.result.segments, $x.C.all.rate, (Format-Minutes $x.T.wall_ms))
        }
        Show 'AT-06（small と medium で処理し、原文・区間・モデルの hash・CER・所要時間を残す）' $at06 ($detail -join ' ／ ')

        # AT-26: same original audio (chunk hashes), different settings, all revisions kept and unchanged.
        $ok = $true
        $notes = @()
        $chunkSets = @()
        foreach ($x in ($results | Where-Object { $_.C })) {
            $info = Read-Json (Join-Path $dir ('derived\stt-input\{0}\input.json' -f $x.T.input_hash.Substring(0, 16)))
            $chunkSets += ((@($info.sources) | ForEach-Object { @($_.chunks) | ForEach-Object { $_.sha256 } }) -join ',')
        }
        if (@($chunkSets | Select-Object -Unique).Count -ne 1) { $ok = $false; $notes += '原音チャンクが実行ごとに違う' }
        $revs = @($results | Where-Object { $_.C } | ForEach-Object { $_.T.result.revision })
        if (@($revs | Select-Object -Unique).Count -ne $revs.Count) { $ok = $false; $notes += 'Revision 番号が重複' }
        $configs = @($results | Where-Object { $_.C } | ForEach-Object { $_.T.configuration.config_hash })
        if (@($configs | Select-Object -Unique).Count -ne $configs.Count) { $ok = $false; $notes += '設定 hash が重複' }
        $changed = @($before.Keys | Where-Object { $after[$_] -ne $before[$_] })
        if ($changed.Count -gt 0) { $ok = $false; $notes += "既存の Revision が変わった: $($changed -join ', ')" }
        $inputs = @($results | Where-Object { $_.C } | ForEach-Object { $_.T.input_hash } | Select-Object -Unique).Count
        Show 'AT-26（同じ原音から設定を変えて再処理し、入力・モデル・設定・成果物を追跡できる）' $ok ((@("Rev $($revs -join ', ')（$($revs.Count) 件、すべて原文）", "原音チャンクは共通", "入力 hash $inputs 種類（ゲインの有無で変わる）", "設定 hash $($configs.Count) 種類", "既存 Revision $($before.Count) ファイルは不変") + $notes) -join '、')
        Write-Host "   詳細: $evalDir"
    }
    'Check' {
        $dir = Resolve-Session
        Write-Host "== Revision の並び: $dir" -ForegroundColor Cyan
        $root = Join-Path $dir 'transcripts'
        $docs = @{}
        foreach ($rev in (Get-ChildItem $root -Directory | Where-Object Name -match '^rev-\d{4}$' | Sort-Object Name)) {
            $file = Get-ChildItem $rev.FullName -File | Where-Object Name -in 'transcript.raw.json', 'transcript.edited.json' | Select-Object -First 1
            $doc = Read-Json $file.FullName
            $docs[[int]$doc.revision] = $doc
            $what = if ($doc.kind -eq 'raw') { "原文 model=$($doc.provenance.model_name) 設定 $($doc.provenance.config_hash.Substring(0, 12))…" }
            else { "修正 親=Rev $($doc.parent_revision) 原文=Rev $($doc.raw_revision) 変更 $(@($doc.changes).Count) 件（$((@($doc.changes) | ForEach-Object { $_.kind }) -join ', ')）" }
            Write-Host ("   Rev {0}: {1}、区間 {2}" -f $doc.revision, $what, @($doc.segments).Count)
        }
        $ok = $true
        $notes = @()
        foreach ($doc in $docs.Values) {
            if ($doc.kind -eq 'raw') { continue }
            $parent = $docs[[int]$doc.parent_revision]
            if (-not $parent) { $ok = $false; $notes += "Rev $($doc.revision) の親 Rev $($doc.parent_revision) がない"; continue }
            $parentIds = @($parent.segments | ForEach-Object { $_.segment_id })
            $ids = @($doc.segments | ForEach-Object { $_.segment_id })
            foreach ($change in @($doc.changes)) {
                foreach ($id in @($change.segment_ids)) { if ($parentIds -notcontains $id) { $ok = $false; $notes += "Rev $($doc.revision): 親にない segment $id" } }
                foreach ($id in @($change.new_segment_ids)) { if ($ids -notcontains $id) { $ok = $false; $notes += "Rev $($doc.revision): 自身にない segment $id" } }
            }
        }
        Show '修正 Revision の参照（親の Revision と segment_id）がたどれる' $ok $(if ($notes) { $notes -join '、' } else { "Revision $($docs.Count) 件" })
        if (Test-Path $snapshotJson) {
            $snap = Read-Json $snapshotJson
            if ($snap.session_directory -eq $dir) {
                $now = Get-RevisionHashes $dir
                $names = @($snap.revisions.PSObject.Properties.Name)
                $changed = @($names | Where-Object { $now[$_] -ne $snap.revisions.$_ })
                $added = @($now.Keys | Where-Object { $names -notcontains $_ })
                Show '既存の Revision は変わらない（Evaluate の時点と比べる）' ($changed.Count -eq 0) ("{0} ファイルを照合、変わったもの {1} 件、新しく増えたファイル {2} 件" -f $names.Count, $changed.Count, $added.Count)
            }
        }
    }
}
