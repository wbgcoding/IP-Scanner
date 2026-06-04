# Generates a polished "network radar" icon as a multi-size .ico.
# A single 1024px master is drawn with GDI+ (glow, gradient, radar sweep),
# then downscaled to every frame for maximum crispness.
# Output: src/IpScanner/Resources/network.ico
Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'
$outDir = Join-Path $PSScriptRoot '..\src\IpScanner\Resources'
$outIco = Join-Path $outDir 'network.ico'

function New-MasterBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    $s = [double]$size

    # ── Rounded background, deep vertical gradient ──
    $radius = $s * 0.21
    $rect = New-Object System.Drawing.RectangleF(0, 0, $s, $s)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($s - $d, 0, $d, $d, 270, 90)
    $path.AddArc($s - $d, $s - $d, $d, $d, 0, 90)
    $path.AddArc(0, $s - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $top = [System.Drawing.Color]::FromArgb(255, 49, 50, 79)
    $bot = [System.Drawing.Color]::FromArgb(255, 17, 17, 27)
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $top, $bot, 90)
    $g.FillPath($bg, $path)
    $bg.Dispose()

    # Subtle inner border highlight.
    $hl = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(46, 205, 214, 244), [single]($s * 0.008))
    $g.DrawPath($hl, $path)
    $hl.Dispose()

    $cx = $s * 0.5
    $cy = $s * 0.53

    # ── Radar rings (mauve) with soft glow ──
    $rings  = @(0.36, 0.25, 0.14)
    $alphas = @(80, 120, 170)
    for ($i = 0; $i -lt $rings.Count; $i++) {
        $r = $s * $rings[$i]
        $glow = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(28, 203, 166, 247), [single]($s * 0.030))
        $g.DrawEllipse($glow, [single]($cx - $r), [single]($cy - $r), [single]($r * 2), [single]($r * 2))
        $glow.Dispose()
        $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb($alphas[$i], 203, 166, 247), [single]($s * 0.011))
        $g.DrawEllipse($pen, [single]($cx - $r), [single]($cy - $r), [single]($r * 2), [single]($r * 2))
        $pen.Dispose()
    }

    # ── Radar sweep (green, layered for a soft falloff) ──
    $rOuter = $s * 0.36
    foreach ($layer in @(@(40, 70), @(70, 38))) {
        $alpha = $layer[0]; $sweepAngle = $layer[1]
        $sw = New-Object System.Drawing.Drawing2D.GraphicsPath
        $sw.AddPie([single]($cx - $rOuter), [single]($cy - $rOuter), [single]($rOuter * 2), [single]($rOuter * 2), -90, $sweepAngle)
        $sb = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb($alpha, 166, 227, 161))
        $g.FillPath($sb, $sw)
        $sb.Dispose(); $sw.Dispose()
    }
    # Sweep leading edge.
    $edge = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(190, 166, 227, 161), [single]($s * 0.012))
    $rad = (-90 + 38) * [Math]::PI / 180
    $g.DrawLine($edge, [single]$cx, [single]$cy,
        [single]($cx + $rOuter * [Math]::Cos($rad)), [single]($cy + $rOuter * [Math]::Sin($rad)))
    $edge.Dispose()

    # ── Connection lines + glowing nodes (Catppuccin accents) ──
    $nodes = @(
        @{ ax = -0.24; ay = -0.17; col = @(166, 227, 161) },  # green
        @{ ax =  0.26; ay = -0.11; col = @(137, 180, 250) },  # blue
        @{ ax =  0.13; ay =  0.26; col = @(250, 179, 135) },  # peach
        @{ ax = -0.20; ay =  0.22; col = @(243, 139, 168) }   # red/pink
    )
    $linePen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(160, 88, 91, 112), [single]($s * 0.010))
    foreach ($n in $nodes) {
        $g.DrawLine($linePen, [single]$cx, [single]$cy,
            [single]($cx + $s * $n.ax), [single]($cy + $s * $n.ay))
    }
    $linePen.Dispose()
    foreach ($n in $nodes) {
        $nx = $cx + $s * $n.ax
        $ny = $cy + $s * $n.ay
        $c = $n.col
        # halo
        $hr = $s * 0.075
        $halo = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(55, $c[0], $c[1], $c[2]))
        $g.FillEllipse($halo, [single]($nx - $hr), [single]($ny - $hr), [single]($hr * 2), [single]($hr * 2))
        $halo.Dispose()
        # core
        $nr = $s * 0.043
        $nb = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, $c[0], $c[1], $c[2]))
        $g.FillEllipse($nb, [single]($nx - $nr), [single]($ny - $nr), [single]($nr * 2), [single]($nr * 2))
        $nb.Dispose()
    }

    # ── Centre node (mauve) with glow + specular dot ──
    $chr = $s * 0.105
    $chalo = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(70, 203, 166, 247))
    $g.FillEllipse($chalo, [single]($cx - $chr), [single]($cy - $chr), [single]($chr * 2), [single]($chr * 2))
    $chalo.Dispose()
    $cr = $s * 0.062
    $cb = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 203, 166, 247))
    $g.FillEllipse($cb, [single]($cx - $cr), [single]($cy - $cr), [single]($cr * 2), [single]($cr * 2))
    $cb.Dispose()
    $sp = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(170, 245, 240, 255))
    $sr = $s * 0.020
    $g.FillEllipse($sp, [single]($cx - $sr * 1.6), [single]($cy - $sr * 2.2), [single]($sr * 2), [single]($sr * 2))
    $sp.Dispose()

    $g.Dispose()
    return $bmp
}

# Draw one hi-res master, downscale to every frame (crisper than per-size drawing).
$master = New-MasterBitmap 1024
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngs = @()
foreach ($sz in $sizes) {
    $frame = New-Object System.Drawing.Bitmap($sz, $sz, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $fg = [System.Drawing.Graphics]::FromImage($frame)
    $fg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $fg.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $fg.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $fg.DrawImage($master, 0, 0, $sz, $sz)
    $fg.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $frame.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += ,($ms.ToArray())
    $ms.Dispose()
    $frame.Dispose()
}
$master.Dispose()

# Assemble the ICO container (PNG-compressed frames).
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
