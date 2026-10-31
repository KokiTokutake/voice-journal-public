<#
.SYNOPSIS
  アプリアイコン（マイク＋記録の行）を GDI+ で描画し、マルチサイズ ICO とプレビュー PNG を生成する。
.DESCRIPTION
  各サイズを個別に描画する（縮小ではなく再描画）。24px 以下は行を省略しマイクだけを中央に置く。
  ICO の 256px は PNG 圧縮、それ未満は 32bpp DIB（古い System.Drawing でも読める形式）。
  Windows PowerShell 5.1（System.Drawing 同梱）で実行する。
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\New-AppIcon.ps1
#>
param(
    [string]$OutDir = (Join-Path $PSScriptRoot '..\src\VoiceJournal.App\Assets'),
    [int[]]$Sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# 配色（濃紺 → ティール）
$bgFrom = [System.Drawing.ColorTranslator]::FromHtml('#1E3A8A')
$bgTo   = [System.Drawing.ColorTranslator]::FromHtml('#0D9488')
$fg     = [System.Drawing.Color]::White
$fgSoft = [System.Drawing.Color]::FromArgb(220, 255, 255, 255)

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Min($r * 2, [Math]::Min($w, $h))
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)
        # 256 単位の座標系で描く
        $s = $size / 256.0
        $g.ScaleTransform($s, $s)

        $small = $size -le 24
        $inset = if ($small) { 4 } else { 8 }
        $radius = if ($small) { 48 } else { 56 }

        # 背景
        $bgPath = New-RoundedPath $inset $inset (256 - 2 * $inset) (256 - 2 * $inset) $radius
        $rect = New-Object System.Drawing.RectangleF(0, 0, 256, 256)
        $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $bgFrom, $bgTo, 45.0)
        $g.FillPath($bgBrush, $bgPath)

        # マイク（小サイズは中央、通常は左寄せ）
        $cx = if ($small) { 128 } else { 92 }
        $capW = if ($small) { 76 } else { 62 }
        $capH = if ($small) { 118 } else { 104 }
        $capTop = if ($small) { 30 } else { 44 }
        $stroke = if ($small) { 22 } else { 14 }

        $fgBrush = New-Object System.Drawing.SolidBrush($fg)
        $capPath = New-RoundedPath ($cx - $capW / 2) $capTop $capW $capH ($capW / 2)
        $g.FillPath($fgBrush, $capPath)

        $pen = New-Object System.Drawing.Pen($fg, $stroke)
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

        # U 字のホルダー
        $armW = $capW + 2 * $stroke + 12
        $armTop = $capTop + $capH * 0.35
        $armH = $capH * 0.95
        $g.DrawArc($pen, ($cx - $armW / 2), $armTop, $armW, $armH, 0, 180)

        # スタンド
        $stemTop = $armTop + $armH
        $stemBottom = if ($small) { 222 } else { 208 }
        $g.DrawLine($pen, $cx, $stemTop, $cx, $stemBottom)
        $baseHalf = if ($small) { 30 } else { 26 }
        $g.DrawLine($pen, ($cx - $baseHalf), $stemBottom, ($cx + $baseHalf), $stemBottom)

        # 記録の行（32px 以上のみ）
        if (-not $small) {
            $linePen = New-Object System.Drawing.Pen($fgSoft, 16)
            $linePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
            $linePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
            $g.DrawLine($linePen, 160, 84, 206, 84)
            $g.DrawLine($linePen, 160, 124, 196, 124)
            $g.DrawLine($linePen, 160, 164, 206, 164)
            $linePen.Dispose()
        }

        $pen.Dispose(); $fgBrush.Dispose(); $bgBrush.Dispose(); $bgPath.Dispose(); $capPath.Dispose()
    } finally {
        $g.Dispose()
    }
    return $bmp
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

function ConvertTo-IconDib([System.Drawing.Bitmap]$bmp) {
    # 32bpp BGRA の DIB（BITMAPINFOHEADER + 下から上の画素 + 1bpp AND マスク）。互換性が最も高い形式
    $size = $bmp.Width
    $rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $raw = New-Object byte[] ($stride * $size)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $raw, 0, $raw.Length)
    $bmp.UnlockBits($data)

    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter($ms)
    $w.Write([uint32]40); $w.Write([int32]$size); $w.Write([int32]($size * 2))
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]0)
    $w.Write([uint32]($size * $size * 4)); $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)
    for ($y = $size - 1; $y -ge 0; $y--) { $w.Write($raw, $y * $stride, $size * 4) }
    $maskRow = [int]([Math]::Ceiling($size / 32.0) * 4)
    $w.Write((New-Object byte[] ($maskRow * $size)))
    $w.Flush()
    # 先頭のカンマで配列の展開（パイプラインへの unroll）を防ぐ
    return , $ms.ToArray()
}

# 256px は PNG、それ未満は DIB にする
$entries = foreach ($size in ($Sizes | Sort-Object)) {
    $bmp = New-IconBitmap $size
    if ($size -ge 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Save((Join-Path $OutDir 'VoiceJournal-256.png'), [System.Drawing.Imaging.ImageFormat]::Png)
        $bytes = $ms.ToArray()
    } else {
        $bytes = ConvertTo-IconDib $bmp
    }
    $bmp.Dispose()
    [pscustomobject]@{ Size = $size; Bytes = [byte[]]$bytes }
}

# ICO を組み立てる（ICONDIR + ICONDIRENTRY×n + PNG データ）
$icoPath = Join-Path $OutDir 'VoiceJournal.ico'
$fs = [System.IO.File]::Create($icoPath)
$w = New-Object System.IO.BinaryWriter($fs)
try {
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$entries.Count)
    $offset = 6 + 16 * $entries.Count
    foreach ($e in $entries) {
        $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32)
        $w.Write([uint32]$e.Bytes.Length); $w.Write([uint32]$offset)
        $offset += $e.Bytes.Length
    }
    foreach ($e in $entries) { $w.Write($e.Bytes) }
} finally {
    $w.Dispose()
}

Get-Item $icoPath, (Join-Path $OutDir 'VoiceJournal-256.png') | Select-Object FullName, Length
