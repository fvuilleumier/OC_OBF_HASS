# Génère mhassnage.png (256) et mhassnage.ico (16..256) : roue dentée bleu Home Assistant + « M ».
# Usage : pwsh -File Generate-Logo.ps1
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$out = $PSScriptRoot
function C([string]$hex, [int]$a = 255) { $c = [System.Drawing.ColorTranslator]::FromHtml($hex); [System.Drawing.Color]::FromArgb($a, $c) }
function P([double]$x, [double]$y) { [System.Drawing.PointF]::new([float]$x, [float]$y) }

# Contour de la roue dentée : 10 dents trapézoïdales
function Gear-Points {
    $teeth = 10; $rOut = 116; $rIn = 94; $pts = @()
    for ($i = 0; $i -lt $teeth; $i++) {
        $a = 2 * [Math]::PI * $i / $teeth
        $step = 2 * [Math]::PI / $teeth
        foreach ($k in @(@(-0.50, $rIn), @(-0.28, $rOut), @(0.28, $rOut), @(0.50, $rIn))) {
            $t = $a + $k[0] * $step
            $pts += P (128 + $k[1] * [Math]::Cos($t)) (128 + $k[1] * [Math]::Sin($t))
        }
    }
    return [System.Drawing.PointF[]]$pts
}

function New-Logo([int]$size) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.ScaleTransform($size / 256.0, $size / 256.0)

    # Roue dentée en dégradé bleu HA
    $gear = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $gear.AddPolygon([System.Drawing.PointF[]]@(Gear-Points))
    $g.FillPath([System.Drawing.Drawing2D.LinearGradientBrush]::new((P 0 12), (P 0 244), (C '#5FD0FF'), (C '#1E8BC3')), $gear)

    # Disque central sombre
    $g.FillEllipse([System.Drawing.SolidBrush]::new((C '#1B2531')), 50, 50, 156, 156)
    # Fin anneau bleu intérieur
    $ring = [System.Drawing.Pen]::new((C '#41BDF5'), 4)
    if ($size -ge 48) { $g.DrawEllipse($ring, 58, 58, 140, 140) }

    # M
    $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, $(if ($size -le 24) { 26 } else { 20 }))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $g.DrawLines($pen, [System.Drawing.PointF[]]@((P 90 162), (P 90 98), (P 128 136), (P 166 98), (P 166 162)))

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
