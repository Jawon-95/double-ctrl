# 生成 assets/icon.ico：iOS 风格蓝色圆角底 + “A / 文”两个对话气泡，表达翻译。
# 托盘等小尺寸放不下两个气泡，改用单气泡的简化版。图标有改动时手动运行一次，结果提交到仓库。
param([string]$PreviewDir)
Add-Type -AssemblyName System.Drawing

$Supersample = 4

function New-Squircle([float]$size) {
    # 超椭圆比普通圆角矩形更接近 iOS 图标的连续曲率。
    $n = 5.0
    $half = $size / 2
    $points = New-Object 'System.Collections.Generic.List[System.Drawing.PointF]'
    for ($i = 0; $i -lt 360; $i++) {
        $t = $i * [Math]::PI / 180
        $c = [Math]::Cos($t)
        $s = [Math]::Sin($t)
        $x = [Math]::Sign($c) * [Math]::Pow([Math]::Abs($c), 2 / $n) * $half + $half
        $y = [Math]::Sign($s) * [Math]::Pow([Math]::Abs($s), 2 / $n) * $half + $half
        $points.Add((New-Object System.Drawing.PointF ([float]$x), ([float]$y)))
    }
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddPolygon($points.ToArray())
    return $path
}

function New-Bubble([float]$x, [float]$y, [float]$w, [float]$h, [float]$r, [bool]$tailLeft) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $t = $h * 0.26
    if ($tailLeft) { $tipX = $x + $w * 0.20; $baseA = $x + $w * 0.20; $baseB = $x + $w * 0.20 + $t }
    else { $tipX = $x + $w * 0.80; $baseA = $x + $w * 0.80 - $t; $baseB = $x + $w * 0.80 }
    $path.AddPolygon([System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF $baseA, ($y + $h - 1)),
        (New-Object System.Drawing.PointF $baseB, ($y + $h - 1)),
        (New-Object System.Drawing.PointF $tipX, ($y + $h + $t * 0.9))
    ))
    return $path
}

function Add-Glyph($g, [string]$text, [string]$fontName, [float]$em, [float]$cx, [float]$cy, $brush) {
    $family = New-Object System.Drawing.FontFamily $fontName
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddString($text, $family, [int][System.Drawing.FontStyle]::Bold, $em, (New-Object System.Drawing.PointF 0, 0), [System.Drawing.StringFormat]::GenericTypographic)
    $bounds = $path.GetBounds()
    $matrix = New-Object System.Drawing.Drawing2D.Matrix
    $matrix.Translate($cx - $bounds.X - $bounds.Width / 2, $cy - $bounds.Y - $bounds.Height / 2)
    $path.Transform($matrix)
    $g.FillPath($brush, $path)
}

function New-Frame([int]$size) {
    $big = $size * $Supersample
    $bitmap = New-Object System.Drawing.Bitmap $big, $big
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    $rect = New-Object System.Drawing.Rectangle 0, 0, $big, $big
    $top = [System.Drawing.Color]::FromArgb(255, 64, 170, 255)
    $bottom = [System.Drawing.Color]::FromArgb(255, 0, 88, 230)
    $gradient = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $top, $bottom, 90
    $g.FillPath($gradient, (New-Squircle $big))

    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $blue = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0, 100, 240))
    if ($size -le 24) {
        $g.FillPath($white, (New-Bubble ($big * 0.14) ($big * 0.15) ($big * 0.72) ($big * 0.58) ($big * 0.16) $true))
        Add-Glyph $g '文' 'Microsoft YaHei' ($big * 0.52) ($big * 0.50) ($big * 0.44) $blue
    } else {
        $soft = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(115, 255, 255, 255))
        $g.FillPath($soft, (New-Bubble ($big * 0.11) ($big * 0.13) ($big * 0.50) ($big * 0.42) ($big * 0.11) $true))
        Add-Glyph $g 'A' 'Segoe UI' ($big * 0.33) ($big * 0.36) ($big * 0.335) $white
        $g.FillPath($white, (New-Bubble ($big * 0.37) ($big * 0.38) ($big * 0.52) ($big * 0.42) ($big * 0.11) $false))
        Add-Glyph $g '文' 'Microsoft YaHei' ($big * 0.33) ($big * 0.63) ($big * 0.59) $blue
    }
    $g.Dispose()

    $frame = New-Object System.Drawing.Bitmap $size, $size
    $down = [System.Drawing.Graphics]::FromImage($frame)
    $down.InterpolationMode = 'HighQualityBicubic'
    $down.PixelOffsetMode = 'HighQuality'
    $down.DrawImage($bitmap, 0, 0, $size, $size)
    $down.Dispose()
    $bitmap.Dispose()
    return $frame
}

$sizes = 16, 20, 24, 32, 48, 64, 256
$frames = foreach ($size in $sizes) {
    $bitmap = New-Frame $size
    if ($PreviewDir) { $bitmap.Save((Join-Path $PreviewDir "icon-$size.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    , $stream.ToArray()
}

$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $out
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dimension)
    $writer.Write([byte]$dimension)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$frames[$i].Length)
    $writer.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'icon.ico'), $out.ToArray())
