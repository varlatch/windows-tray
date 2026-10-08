<#
.SYNOPSIS
Makes the app's icons in src/Varlatch/Assets from the marks in assets/.

.DESCRIPTION
Varlatch.ico (the executable, Start, Settings, and the taskbar while a
window is open): the Varlatch mark on a light rounded plate, as the macOS
app's icon has it, so it reads on light and dark backgrounds alike, at the
sizes Windows asks for. VarlatchLogo.png: the same at 256 pixels, for
notifications. The tray icon is drawn by the app itself, from
varlatch-mark-symbolic.png, at the size and in the color the taskbar needs.

Runs in Windows PowerShell 5.1; the results are committed.
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'src\Varlatch\Assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null
$mark = [System.Drawing.Image]::FromFile((Join-Path $root 'assets\varlatch-mark.png'))

function New-RoundedRectangle([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-Icon([int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.CompositingQuality = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # The plate: nearly the whole square at small sizes, where every pixel counts.
    $inset = if ($size -le 24) { 0 } else { [Math]::Round($size * 0.04) }
    $side = $size - 2 * $inset
    $plate = New-RoundedRectangle $inset $inset $side $side ($side * 0.22)
    $top = [System.Drawing.Color]::FromArgb(255, 250, 250, 250)
    $bottom = [System.Drawing.Color]::FromArgb(255, 232, 232, 234)
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.RectangleF 0, $inset, $size, $side), $top, $bottom, 90
    $g.FillPath($fill, $plate)
    if ($size -ge 32) {
        $edge = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(40, 0, 0, 0)), ([Math]::Max(1, $size / 128))
        $g.DrawPath($edge, $plate)
    }

    # The mark, centered, a little above the middle as the macOS icon has it.
    $scale = if ($size -le 24) { 0.80 } else { 0.66 }
    $w = $side * $scale
    $h = $w * $mark.Height / $mark.Width
    $x = ($size - $w) / 2
    $y = ($size - $h) / 2 - $size * 0.01
    $g.DrawImage($mark, (New-Object System.Drawing.RectangleF $x, $y, $w, $h))
    $g.Dispose()
    return $bitmap
}

function Get-PngBytes([System.Drawing.Bitmap]$bitmap) {
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    return ,$stream.ToArray()
}

# An .ico of PNG frames, which every Windows since Vista reads.
$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 256
$frames = foreach ($size in $sizes) { , (Get-PngBytes (New-Icon $size)) }
$ico = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $ico
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dimension = if ($s -ge 256) { 0 } else { $s }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
$writer.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets 'Varlatch.ico'), $ico.ToArray())

[System.IO.File]::WriteAllBytes((Join-Path $assets 'VarlatchLogo.png'), (Get-PngBytes (New-Icon 256)))
Copy-Item (Join-Path $root 'assets\varlatch-mark-symbolic.png') (Join-Path $assets 'TrayMark.png') -Force
$mark.Dispose()
Write-Host "Wrote Varlatch.ico ($($sizes -join ', ')), VarlatchLogo.png, and TrayMark.png to $assets"
