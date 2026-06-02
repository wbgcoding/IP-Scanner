# Generates a polished "network scanner" radar icon as a multi-size .ico.
# Drawn with GDI+ (System.Drawing), then packed as PNG-compressed ICO frames.
# Output: src/IpScanner/Resources/network.ico
Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'
$outDir = Join-Path $PSScriptRoot '..\src\IpScanner\Resources'
$outIco = Join-Path $outDir 'network.ico'

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    $s = [double]$size
    # Rounded background with vertical gradient.
    $radius = $s * 0.20
    $rect = New-Object System.Drawing.RectangleF(0, 0, $s, $s)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($s - $d, 0, $d, $d, 270, 90)
    $path.AddArc($s - $d, $s - $d, $d, $d, 0, 90)
    $path.AddArc(0, $s - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $top = [System.Drawing.Color]::FromArgb(255, 38, 38, 59)
    $bot = [System.Drawing.Color]::FromArgb(255, 21, 21, 31)
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $top, $bot, 90)
    $g.FillPath($bg, $path)

    # Radar centre slightly below middle.
    $cx = $s * 0.5
    $cy = $s * 0.54
    $rings = @(0.34, 0.23, 0.12)
    $alphas = @(70, 110, 160)
    for ($i = 0; $i -lt $rings.Count; $i++) {
        $r = $s * $rings[$i]
        $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb($alphas[$i], 203, 166, 247), [single]($s * 0.012))
        $g.DrawEllipse($pen, [single]($cx - $r), [single]($cy - $r), [single]($r * 2), [single]($r * 2))
        $pen.Dispose()
    }

    # Sweep wedge (green, translucent).
    $rOuter = $s * 0.34
    $sweep = New-Object System.Drawing.Drawing2D.GraphicsPath
    $sweep.AddPie([single]($cx - $rOuter), [single]($cy - $rOuter), [single]($rOuter * 2), [single]($rOuter * 2), -90, 55)
    $sweepBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(90, 166, 227, 161))
    $g.FillPath($sweepBrush, $sweep)

    # Connection lines + nodes.
    $nodes = @(
        @{ ax = -0.22; ay = -0.18; col = @(166, 227, 161) },  # green
        @{ ax =  0.24; ay = -0.10; col = @(137, 180, 250) },  # blue
        @{ ax =  0.12; ay =  0.24; col = @(250, 179, 135) },  # peach
        @{ ax = -0.18; ay =  0.20; col = @(203, 166, 247) }   # mauve
    )
    $linePen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(150, 88, 91, 112), [single]($s * 0.010))
    foreach ($n in $nodes) {
        $nx = $cx + $s * $n.ax
        $ny = $cy + $s * $n.ay
        $g.DrawLine($linePen, [single]$cx, [single]$cy, [single]$nx, [single]$ny)
    }
    foreach ($n in $nodes) {
        $nx = $cx + $s * $n.ax
        $ny = $cy + $s * $n.ay
        $nr = $s * 0.045
        $c = $n.col
        $nb = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, $c[0], $c[1], $c[2]))
        $g.FillEllipse($nb, [single]($nx - $nr), [single]($ny - $nr), [single]($nr * 2), [single]($nr * 2))
        $nb.Dispose()
    }
    # Centre node (mauve, larger).
    $cr = $s * 0.06
    $cb = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 203, 166, 247))
    $g.FillEllipse($cb, [single]($cx - $cr), [single]($cy - $cr), [single]($cr * 2), [single]($cr * 2))

    $g.Dispose()
    return $bmp
}

# Render the largest frame, downscale the rest for crispness.
$sizes = @(16, 32, 48, 64, 128, 256)
$pngs = @()
foreach ($sz in $sizes) {
    $bmp = New-IconBitmap $sz
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += ,($ms.ToArray())
    $ms.Dispose()
    $bmp.Dispose()
}

# Assemble the ICO container.
$fs = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0)            # reserved
$bw.Write([UInt16]1)            # type = icon
$bw.Write([UInt16]$sizes.Count) # image count

$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]
    $len = $pngs[$i].Length
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $bw.Write([Byte]$dim)       # width
    $bw.Write([Byte]$dim)       # height
    $bw.Write([Byte]0)          # palette
    $bw.Write([Byte]0)          # reserved
    $bw.Write([UInt16]1)        # planes
    $bw.Write([UInt16]32)       # bpp
    $bw.Write([UInt32]$len)     # bytes
    $bw.Write([UInt32]$offset)  # offset
    $offset += $len
}
foreach ($png in $pngs) { $bw.Write($png) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($outIco, $fs.ToArray())
$bw.Dispose(); $fs.Dispose()
Write-Output "Icon written: $outIco ($($sizes.Count) sizes)"
