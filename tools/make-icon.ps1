# Generates the IP-Scanner artwork:
#  - network.ico  multi-size app icon (rounded square + network-circle motif)
#  - logo.png     512px transparent, rotationally symmetric network circle
#                 (used in the header; spins during a scan, so it must look
#                  identical under rotation)
# Everything is drawn once at high resolution and downscaled for crispness.
Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'
$outDir = Join-Path $PSScriptRoot '..\src\IpScanner\Resources'
$outIco = Join-Path $outDir 'network.ico'
$outPng = Join-Path $outDir 'logo.png'

# Draws the rotation-symmetric network circle: rings, six evenly spaced nodes
# (one color = identical at every 60° rotation), spokes and a glowing centre.
function Draw-Motif($g, [double]$s, [double]$cx, [double]$cy) {
    # Rings with soft glow (mauve).
    $rings  = @(0.40, 0.26)
    $alphas = @(150, 90)
    for ($i = 0; $i -lt $rings.Count; $i++) {
        $r = $s * $rings[$i]
        $glow = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(30, 203, 166, 247), [single]($s * 0.035))
        $g.DrawEllipse($glow, [single]($cx - $r), [single]($cy - $r), [single]($r * 2), [single]($r * 2))
        $glow.Dispose()
        $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb($alphas[$i], 203, 166, 247), [single]($s * 0.018))
        $g.DrawEllipse($pen, [single]($cx - $r), [single]($cy - $r), [single]($r * 2), [single]($r * 2))
        $pen.Dispose()
    }

    # Six nodes on the outer ring + spokes to the centre.
    $rNode = $s * 0.40
    $spoke = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(170, 116, 119, 141), [single]($s * 0.014))
    for ($k = 0; $k -lt 6; $k++) {
        $a = ($k * 60 - 90) * [Math]::PI / 180
        $nx = $cx + $rNode * [Math]::Cos($a)
        $ny = $cy + $rNode * [Math]::Sin($a)
        $g.DrawLine($spoke, [single]$cx, [single]$cy, [single]$nx, [single]$ny)
    }
    $spoke.Dispose()
    for ($k = 0; $k -lt 6; $k++) {
        $a = ($k * 60 - 90) * [Math]::PI / 180
        $nx = $cx + $rNode * [Math]::Cos($a)
        $ny = $cy + $rNode * [Math]::Sin($a)
        # halo
        $hr = $s * 0.085
        $halo = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(60, 203, 166, 247))
        $g.FillEllipse($halo, [single]($nx - $hr), [single]($ny - $hr), [single]($hr * 2), [single]($hr * 2))
        $halo.Dispose()
        # core
        $nr = $s * 0.052
        $nb = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 203, 166, 247))
        $g.FillEllipse($nb, [single]($nx - $nr), [single]($ny - $nr), [single]($nr * 2), [single]($nr * 2))
        $nb.Dispose()
    }

    # Centre node (green) with glow — rotation-invariant by definition.
    $chr = $s * 0.13
    $chalo = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(80, 166, 227, 161))
    $g.FillEllipse($chalo, [single]($cx - $chr), [single]($cy - $chr), [single]($chr * 2), [single]($chr * 2))
    $chalo.Dispose()
    $cr = $s * 0.075
    $cb = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 166, 227, 161))
    $g.FillEllipse($cb, [single]($cx - $cr), [single]($cy - $cr), [single]($cr * 2), [single]($cr * 2))
    $cb.Dispose()
}

function New-Graphics($bmp) {
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    return $g
}

# ── Transparent logo master (for the header image) ──
function New-LogoBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = New-Graphics $bmp
    Draw-Motif $g ([double]$size) ($size * 0.5) ($size * 0.5)
    $g.Dispose()
    return $bmp
}

# ── App-icon master (rounded square background + motif) ──
function New-IconMaster([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = New-Graphics $bmp
    $s = [double]$size

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
    $hl = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(46, 205, 214, 244), [single]($s * 0.008))
    $g.DrawPath($hl, $path)
    $hl.Dispose()

    # Motif slightly smaller so the halos stay inside the tile.
    Draw-Motif $g ($s * 0.92) ($s * 0.5) ($s * 0.5)
    $g.Dispose()
    return $bmp
}

function Resize($src, [int]$sz) {
    $frame = New-Object System.Drawing.Bitmap($sz, $sz, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $fg = New-Graphics $frame
    $fg.DrawImage($src, 0, 0, $sz, $sz)
    $fg.Dispose()
    return $frame
}

# ── logo.png (512px, transparent) ──
$logoMaster = New-LogoBitmap 1024
$logo = Resize $logoMaster 512
$logo.Save($outPng, [System.Drawing.Imaging.ImageFormat]::Png)
$logo.Dispose(); $logoMaster.Dispose()

# ── network.ico (multi-size) ──
$master = New-IconMaster 1024
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngs = @()
foreach ($sz in $sizes) {
    $frame = Resize $master $sz
    $ms = New-Object System.IO.MemoryStream
    $frame.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += ,($ms.ToArray())
    $ms.Dispose()
    $frame.Dispose()
}
$master.Dispose()

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
Write-Output "Written: $outIco ($($sizes.Count) sizes), $outPng (512px)"
