# Draws the launcher's icon in the style of Void Crew's main menu: a charcoal panel with cut corners and a steel edge,
# grey meander bands, and the game's pointed button shape in the logo's gold around a PLAY arrow.
# Writes src\VoidCrewModLauncher\Assets\launcher.png (256 x 256) and launcher.ico (16 to 256 px).
# Usage: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
param(
    [string]$OutDir = (Join-Path $PSScriptRoot "..\src\VoidCrewModLauncher\Assets")
)

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force $OutDir | Out-Null

function C([string]$hex, [int]$alpha = 255) {
    $c = [System.Drawing.ColorTranslator]::FromHtml($hex)
    return [System.Drawing.Color]::FromArgb($alpha, $c.R, $c.G, $c.B)
}

function Poly([float[]]$xy) {
    $pts = New-Object 'System.Drawing.PointF[]' ($xy.Length / 2)
    for ($i = 0; $i -lt $pts.Length; $i++) { $pts[$i] = New-Object System.Drawing.PointF $xy[2 * $i], $xy[2 * $i + 1] }
    return $pts
}

function Draw([int]$size, [bool]$detail) {
    $scale = 4
    $big = New-Object System.Drawing.Bitmap ($size * $scale), ($size * $scale)
    $g = [System.Drawing.Graphics]::FromImage($big)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.ScaleTransform($scale * $size / 256.0, $scale * $size / 256.0)

    # Panel: both bottom corners cut, like the main menu's panel and news cards.
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tile.AddPolygon((Poly @(6, 6, 250, 6, 250, 214, 214, 250, 42, 250, 6, 214)))
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.RectangleF 0, 0, 256, 256), (C "#2E3136"), (C "#121418"), 90
    $g.FillPath($fill, $tile)
    $glow = New-Object System.Drawing.Drawing2D.GraphicsPath
    $glow.AddEllipse(28, 40, 200, 176)
    $glowBrush = New-Object System.Drawing.Drawing2D.PathGradientBrush $glow
    $glowBrush.CenterColor = C "#2A5C8C" 150
    $glowBrush.SurroundColors = @(C "#2A5C8C" 0)
    $g.FillPath($glowBrush, $glow)
    $g.DrawPath((New-Object System.Drawing.Pen (C "#9AA6B0"), 7), $tile)

    if ($detail) {
        $pen = New-Object System.Drawing.Pen (C "#8A9097" 170), 3.2
        foreach ($y in 26, 214) {
            $g.DrawLine($pen, 30, $y, 226, $y)
            $g.DrawLine($pen, 30, ($y + 16), 226, ($y + 16))
            for ($x = 34.0; $x + 20 -le 226; $x += 22) {
                $g.DrawLines($pen, (Poly @($x, $y, $x, ($y + 12), ($x + 12), ($y + 12), ($x + 12), ($y + 5), ($x + 6), ($y + 5))))
            }
        }
    }

    # The game's pointed button, in orange, around a PLAY arrow.
    $hex = Poly @(72, 78, 184, 78, 230, 128, 184, 178, 72, 178, 26, 128)
    $g.FillPolygon((New-Object System.Drawing.SolidBrush (C "#000000" 90)), $hex)
    $gold = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.RectangleF 0, 70, 256, 116), (C "#FFE7A8"), (C "#C8892C"), 90
    $g.DrawPolygon((New-Object System.Drawing.Pen $gold, 12), $hex)
    $g.FillPolygon((New-Object System.Drawing.SolidBrush (C "#FFFFFF")), (Poly @(106, 100, 162, 128, 106, 156)))

    $g.Dispose()
    $small = New-Object System.Drawing.Bitmap $size, $size
    $s = [System.Drawing.Graphics]::FromImage($small)
    $s.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $s.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $s.DrawImage($big, 0, 0, $size, $size)
    $s.Dispose()
    $big.Dispose()
    return $small
}

$png = Draw 256 $true
$png.Save((Join-Path $OutDir "launcher.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$png.Dispose()

# An .ico of PNG images (Windows Vista and later read these).
$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($size in $sizes) {
    $bitmap = Draw $size ($size -ge 48)
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    , $stream.ToArray()
}
$ico = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $ico
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $writer.Write($image) }
$writer.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutDir "launcher.ico"), $ico.ToArray())
Write-Output "Wrote launcher.png and launcher.ico to $OutDir"
