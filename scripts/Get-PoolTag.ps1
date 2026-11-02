<#
.SYNOPSIS
  カーネルのプール（非ページ・ページ）の使用量をタグ別に記録し、前回の記録と比べる。
.DESCRIPTION
  NtQuerySystemInformation(SystemPoolTagInformation = 22) で全タグの確保・解放の回数と使用量を読み、
  %LOCALAPPDATA%\VoiceJournal\verify\pool\<日時>-<ラベル>.json に保存する。
  -Compare に前の記録を渡すと、増えたタグを上から表示する（未解放の件数 = 確保 - 解放 の差も出す）。
  Phase C の評価で非ページプールが約 10 GB まで増えた件（docs/phase-c-report.md 5 章）の切り分けと、
  長時間録音（Phase B の残りのリスク）の前後の確認に使う。管理者権限は要らない。
.EXAMPLE
  .\scripts\Get-PoolTag.ps1 -Label boot
  .\scripts\Get-PoolTag.ps1 -Label idle -Compare latest     # 直前の記録と比べる
  .\scripts\Get-PoolTag.ps1 -Label rec -Compare <path.json> -Top 20
#>
param(
    [string]$Label = 'snapshot',
    # 比べる記録のパス。'latest' は保存先の最新の記録
    [string]$Compare,
    [int]$Top = 15,
    # 表示するタグ（増え方にかかわらず必ず出す）
    [string[]]$Watch = @('amda', 'File', 'IoWI', 'SQSF'),
    [string]$Root = (Join-Path $env:LOCALAPPDATA 'VoiceJournal')
)
$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = $utf8
$outDir = Join-Path $Root 'verify\pool'
New-Item -ItemType Directory -Force $outDir | Out-Null

if (-not ('VoiceJournal.PoolTag' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
namespace VoiceJournal {
public sealed class PoolTagEntry {
    public string Tag; public long PagedAllocs; public long PagedFrees; public long PagedUsed;
    public long NonPagedAllocs; public long NonPagedFrees; public long NonPagedUsed;
}
public static class PoolTag {
    [DllImport("ntdll.dll")]
    static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int ret);
    public static List<PoolTagEntry> Read() {
        int len = 1 << 20;
        for (int attempt = 0; attempt < 8; attempt++) {
            IntPtr buf = Marshal.AllocHGlobal(len);
            try {
                int ret;
                int st = NtQuerySystemInformation(22, buf, len, out ret);
                if (st == unchecked((int)0xC0000004)) { len = Math.Max(len * 2, ret + 65536); continue; }
                if (st != 0) throw new InvalidOperationException("NtQuerySystemInformation: 0x" + st.ToString("X8"));
                // SYSTEM_POOLTAG_INFORMATION (x64): ULONG Count; (pad 4); SYSTEM_POOLTAG[Count] (40 バイトずつ)
                int count = Marshal.ReadInt32(buf);
                var list = new List<PoolTagEntry>(count);
                for (int i = 0; i < count; i++) {
                    IntPtr p = IntPtr.Add(buf, 8 + i * 40);
                    byte[] t = new byte[4]; Marshal.Copy(p, t, 0, 4);
                    char[] c = new char[4];
                    for (int k = 0; k < 4; k++) c[k] = (t[k] >= 0x20 && t[k] < 0x7F) ? (char)t[k] : '.';
                    list.Add(new PoolTagEntry {
                        Tag = new string(c),
                        PagedAllocs = (uint)Marshal.ReadInt32(p, 4),
                        PagedFrees = (uint)Marshal.ReadInt32(p, 8),
                        PagedUsed = Marshal.ReadInt64(p, 16),
                        NonPagedAllocs = (uint)Marshal.ReadInt32(p, 24),
                        NonPagedFrees = (uint)Marshal.ReadInt32(p, 28),
                        NonPagedUsed = Marshal.ReadInt64(p, 32),
                    });
                }
                return list;
            } finally { Marshal.FreeHGlobal(buf); }
        }
        throw new InvalidOperationException("buffer too small");
    }
}
}
'@
}

$entries = [VoiceJournal.PoolTag]::Read()
$os = Get-CimInstance Win32_OperatingSystem
$now = Get-Date
$snapshot = [ordered]@{
    label = $Label
    time = $now.ToString('o')
    last_boot = $os.LastBootUpTime.ToString('o')
    uptime_min = [math]::Round(($now - $os.LastBootUpTime).TotalMinutes, 1)
    free_physical_mb = [math]::Round($os.FreePhysicalMemory / 1KB)
    nonpaged_total = ($entries | Measure-Object NonPagedUsed -Sum).Sum
    paged_total = ($entries | Measure-Object PagedUsed -Sum).Sum
    tags = $entries
}
$path = Join-Path $outDir ('{0}-{1}.json' -f $now.ToString('yyyyMMdd-HHmmss'), $Label)

$previousPath = $null
if ($Compare -eq 'latest') {
    $previousPath = Get-ChildItem $outDir -Filter '*.json' | Sort-Object Name | Select-Object -Last 1 -ExpandProperty FullName
    if (-not $previousPath) { throw '比べる記録がありません' }
} elseif ($Compare) {
    $previousPath = (Resolve-Path $Compare).Path
}
[IO.File]::WriteAllText($path, ($snapshot | ConvertTo-Json -Depth 4), $utf8)

function Format-Mb([double]$bytes) { '{0,9:N1}' -f ($bytes / 1MB) }
Write-Host ('記録: {0}' -f $path)
Write-Host ('起動から {0} 分  非ページ {1} MB  ページ {2} MB  空き物理メモリ {3} MB' -f `
        $snapshot.uptime_min, (Format-Mb $snapshot.nonpaged_total).Trim(), (Format-Mb $snapshot.paged_total).Trim(), $snapshot.free_physical_mb)

if (-not $previousPath) {
    Write-Host "`n非ページの多いタグ（MB / 未解放の件数）"
    $entries | Sort-Object NonPagedUsed -Descending | Select-Object -First $Top | ForEach-Object {
        '  {0}  {1}  {2,12:N0}' -f $_.Tag, (Format-Mb $_.NonPagedUsed), ($_.NonPagedAllocs - $_.NonPagedFrees)
    }
    foreach ($w in $Watch) {
        $e = $entries | Where-Object Tag -CEQ $w
        if ($e) { '  [注視] {0}  非ページ {1} MB  ページ {2} MB' -f $w, (Format-Mb $e.NonPagedUsed).Trim(), (Format-Mb $e.PagedUsed).Trim() }
    }
    return
}

$prev = Get-Content $previousPath -Raw -Encoding UTF8 | ConvertFrom-Json
# プールのタグは大文字と小文字を区別する（EtwB と Etwb は別のタグ）
$prevByTag = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([StringComparer]::Ordinal)
foreach ($t in $prev.tags) { $prevByTag[$t.Tag] = $t }
$elapsed = ([datetime]$snapshot.time - [datetime]$prev.time).TotalMinutes
Write-Host ('比較: {0}（{1}、{2:N1} 分前）' -f (Split-Path $previousPath -Leaf), $prev.label, $elapsed)
Write-Host ('非ページの合計 {0} MB 増  ページの合計 {1} MB 増' -f `
        (Format-Mb ($snapshot.nonpaged_total - $prev.nonpaged_total)).Trim(), (Format-Mb ($snapshot.paged_total - $prev.paged_total)).Trim())

$rows = foreach ($e in $entries) {
    $p = $null
    [void]$prevByTag.TryGetValue($e.Tag, [ref]$p)
    $pn = if ($p) { [long]$p.NonPagedUsed } else { 0 }
    $pp = if ($p) { [long]$p.PagedUsed } else { 0 }
    $po = if ($p) { [long]$p.NonPagedAllocs - [long]$p.NonPagedFrees } else { 0 }
    [pscustomobject]@{
        Tag = $e.Tag
        NonPagedDelta = $e.NonPagedUsed - $pn
        PagedDelta = $e.PagedUsed - $pp
        OutstandingDelta = ($e.NonPagedAllocs - $e.NonPagedFrees) - $po
        NonPagedUsed = $e.NonPagedUsed
        PagedUsed = $e.PagedUsed
    }
}
Write-Host "`n非ページの増えたタグ（増加 MB / 現在 MB / 未解放の増加件数）"
$rows | Sort-Object NonPagedDelta -Descending | Select-Object -First $Top | ForEach-Object {
    '  {0}  {1}  {2}  {3,12:N0}' -f $_.Tag, (Format-Mb $_.NonPagedDelta), (Format-Mb $_.NonPagedUsed), $_.OutstandingDelta
}
Write-Host "`nページの増えたタグ（増加 MB / 現在 MB）"
$rows | Sort-Object PagedDelta -Descending | Select-Object -First ([math]::Min($Top, 8)) | ForEach-Object {
    '  {0}  {1}  {2}' -f $_.Tag, (Format-Mb $_.PagedDelta), (Format-Mb $_.PagedUsed)
}
Write-Host "`n注視するタグ（非ページ 増加 / 現在 MB、ページ 増加 / 現在 MB）"
foreach ($w in $Watch) {
    $r = $rows | Where-Object Tag -CEQ $w
    if ($r) {
        '  {0}  {1} / {2}   {3} / {4}' -f $w, (Format-Mb $r.NonPagedDelta), (Format-Mb $r.NonPagedUsed), (Format-Mb $r.PagedDelta), (Format-Mb $r.PagedUsed)
    } else { '  {0}  （なし）' -f $w }
}
