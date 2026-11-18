# 生成 assets/icon.ico：深色圆角底 + 地球经纬线 + “A / 文”两个对话气泡，表达翻译。
# 全部用矢量绘制再缩小，每个尺寸都清晰。16~24 像素时经纬线会糊成一片，只画气泡并放大。
# 图标有改动时手动运行一次，结果提交到仓库。-PreviewDir 会额外输出各尺寸的 png 方便检查。
param(
    [string]$PreviewDir,
    [string]$OutFile = (Join-Path $PSScriptRoot 'icon.ico')
)
Add-Type -AssemblyName System.Drawing

$Supersample = 4
$BackTop = [System.Drawing.ColorTranslator]::FromHtml('#2E2E31')
$BackBottom = [System.Drawing.ColorTranslator]::FromHtml('#151517')
$Globe = [System.Drawing.ColorTranslator]::FromHtml('#7FAFBF')
$Accent = [System.Drawing.ColorTranslator]::FromHtml('#4FA3C0')
$Ink = [System.Drawing.ColorTranslator]::FromHtml('#1C1C1E')

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

# 圆角矩形气泡，尾巴在左下或右下，和主体合成一个轮廓，描边时才不会有接缝。
function New-Bubble([float]$x, [float]$y, [float]$w, [float]$h, [float]$r, [bool]$tailLeft) {
    $d = $r * 2
    $t = $h * 0.28
    $bottom = $y + $h
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.StartFigure()
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    if ($tailLeft) {
        $path.AddArc($x + $w - $d, $bottom - $d, $d, $d, 0, 90)
        $path.AddLine(($x + $w - $r), $bottom, ($x + $w * 0.32), $bottom)
        $path.AddLine(($x + $w * 0.32), $bottom, ($x + $w * 0.07), ($bottom + $t))
        $path.AddLine(($x + $w * 0.07), ($bottom + $t), $x, ($bottom - $r * 0.6))
    }
    else {
        $path.AddLine(($x + $w), ($y + $r), ($x + $w), ($bottom - $r * 0.6))
        $path.AddLine(($x + $w), ($bottom - $r * 0.6), ($x + $w * 0.93), ($bottom + $t))
        $path.AddLine(($x + $w * 0.93), ($bottom + $t), ($x + $w * 0.68), $bottom)
        $path.AddLine(($x + $w * 0.68), $bottom, ($x + $r), $bottom)
        $path.AddArc($x, $bottom - $d, $d, $d, 90, 90)
    }
    $path.CloseFigure()
    return $path
}

function Add-Glyph($g, [string]$text, [string]$fontName, [System.Drawing.FontStyle]$style, [float]$em, [float]$cx, [float]$cy, $brush) {
    $family = New-Object System.Drawing.FontFamily $fontName
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddString($text, $family, [int]$style, $em, (New-Object System.Drawing.PointF 0, 0), [System.Drawing.StringFormat]::GenericTypographic)
    $bounds = $path.GetBounds()
    $matrix = New-Object System.Drawing.Drawing2D.Matrix
    $matrix.Translate($cx - $bounds.X - $bounds.Width / 2, $cy - $bounds.Y - $bounds.Height / 2)
    $path.Transform($matrix)
    $g.FillPath($brush, $path)
}

# 地球：外圈、两条经线（椭圆）和中轴、三条纬线，纬线裁剪在圆内。
function Draw-Globe($g, [float]$cx, [float]$cy, [float]$r, [float]$stroke) {
    $pen = New-Object System.Drawing.Pen $Globe, $stroke
    $pen.LineJoin = 'Round'
    $circle = New-Object System.Drawing.Drawing2D.GraphicsPath
    $circle.AddEllipse($cx - $r, $cy - $r, 2 * $r, 2 * $r)
    $state = $g.Save()
    $g.SetClip($circle)
    foreach ($k in 0.52) { $g.DrawEllipse($pen, $cx - $r * $k, $cy - $r, 2 * $r * $k, 2 * $r) }
    $g.DrawLine($pen, $cx, $cy - $r, $cx, $cy + $r)
    foreach ($dy in -0.5, 0, 0.5) {
        # 纬线略向下弯，像从斜上方看地球。
        $y = $cy + $r * $dy
        $half = [Math]::Sqrt(1 - $dy * $dy) * $r
        $bend = $r * 0.10 * [Math]::Sqrt(1 - $dy * $dy)
        $g.DrawBezier($pen, ($cx - $half), $y, ($cx - $half * 0.4), ($y + $bend), ($cx + $half * 0.4), ($y + $bend), ($cx + $half), $y)
    }
    $g.Restore($state)
    $g.DrawEllipse($pen, $cx - $r, $cy - $r, 2 * $r, 2 * $r)
}

function New-Frame([int]$size) {
    $big = $size * $Supersample
    $bitmap = New-Object System.Drawing.Bitmap $big, $big
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    $rect = New-Object System.Drawing.Rectangle 0, 0, $big, $big
    $gradient = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $BackTop, $BackBottom, 90
    $g.FillPath($gradient, (New-Squircle $big))

    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $ink = New-Object System.Drawing.SolidBrush $Ink
    $accent = New-Object System.Drawing.SolidBrush $Accent
    # 气泡外圈描一道底色，把压在下面的经纬线隔开。
    $gap = New-Object System.Drawing.Pen $BackBottom, ($big * 0.045)
    $gap.LineJoin = 'Round'

    if ($size -ge 32) {
        Draw-Globe $g ($big * 0.53) ($big * 0.50) ($big * 0.33) ($big * 0.022)
        $a = New-Bubble ($big * 0.08) ($big * 0.19) ($big * 0.40) ($big * 0.29) ($big * 0.075) $true
        $b = New-Bubble ($big * 0.52) ($big * 0.55) ($big * 0.40) ($big * 0.27) ($big * 0.075) $false
        $em = $big * 0.25
        $aCenter = 0.335; $bCenter = 0.685
    }
    else {
        # 小尺寸：去掉地球，气泡放大、加粗字形。
        $a = New-Bubble ($big * 0.06) ($big * 0.10) ($big * 0.54) ($big * 0.40) ($big * 0.10) $true
        $b = New-Bubble ($big * 0.40) ($big * 0.46) ($big * 0.54) ($big * 0.38) ($big * 0.10) $false
        $em = $big * 0.31
        $aCenter = 0.30; $bCenter = 0.65
    }
    $g.DrawPath($gap, $a)
    $g.FillPath($white, $a)
    $g.DrawPath($gap, $b)
    $g.FillPath($accent, $b)
    $style = if ($size -ge 32) { [System.Drawing.FontStyle]::Regular } else { [System.Drawing.FontStyle]::Bold }
    $aBounds = $a.GetBounds(); $bBounds = $b.GetBounds()
    Add-Glyph $g 'A' 'Segoe UI' $style $em ($aBounds.X + $aBounds.Width / 2) ($big * $aCenter) $ink
    Add-Glyph $g '文' 'Microsoft YaHei' $style $em ($bBounds.X + $bBounds.Width / 2) ($big * $bCenter) $white
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
[System.IO.File]::WriteAllBytes($OutFile, $out.ToArray())
