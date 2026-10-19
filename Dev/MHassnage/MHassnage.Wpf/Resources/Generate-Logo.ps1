# Génère mhassnage.png (256) et mhassnage.ico (16..256) à partir du dessin du logo (identique au SVG).
# Usage : pwsh -File Generate-Logo.ps1
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$out = $PSScriptRoot

function New-Logo([int]$size) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.ScaleTransform($size / 256.0, $size / 256.0)

    $bg = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $r = 96
    $bg.AddArc(8, 8, $r, $r, 180, 90); $bg.AddArc(248 - $r, 8, $r, $r, 270, 90)
    $bg.AddArc(248 - $r, 248 - $r, $r, $r, 0, 90); $bg.AddArc(8, 248 - $r, $r, $r, 90, 90)
    $bg.CloseFigure()
    $g.FillPath([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(0x1E, 0x22, 0x28)), $bg)

    $blue = [System.Drawing.Color]::FromArgb(0x41, 0xBD, 0xF5)
    $light = [System.Drawing.Color]::FromArgb(0xE6, 0xE9, 0xEE)

    $house = [System.Drawing.Pen]::new($blue, 16); $house.LineJoin = 'Round'
    $pts = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new(40, 120), [System.Drawing.PointF]::new(84, 64),
        [System.Drawing.PointF]::new(128, 112), [System.Drawing.PointF]::new(172, 64),
        [System.Drawing.PointF]::new(216, 120), [System.Drawing.PointF]::new(216, 212),
        [System.Drawing.PointF]::new(40, 212))
    $g.DrawPolygon($house, $pts)

    $solid = [System.Drawing.Pen]::new($light, 12); $solid.StartCap = 'Round'; $solid.EndCap = 'Round'
    $g.DrawArc($solid, 96, 140, 64, 64, 180, 180)
    $g.FillPolygon([System.Drawing.SolidBrush]::new($light), [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new(160, 154), [System.Drawing.PointF]::new(172, 178), [System.Drawing.PointF]::new(148, 178)))

    $dashed = [System.Drawing.Pen]::new($blue, 12)
    if ($size -ge 32) { $dashed.DashPattern = [single[]]@(0.67, 0.67) }
    $g.DrawArc($dashed, 96, 140, 64, 64, 0, 180)

    $g.Dispose()
    return $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-Logo $s
    $ms = [System.IO.MemoryStream]::new()
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($s -eq 256) { $bmp.Save((Join-Path $out 'mhassnage.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    , $ms.ToArray()
}

# ICO à entrées PNG
$fs = [System.IO.File]::Create((Join-Path $out 'mhassnage.ico'))
$w = [System.IO.BinaryWriter]::new($fs)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $d = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$d); $w.Write([byte]$d); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Dispose()
Write-Host "Logo généré dans $out"
