# Renders the demo screenshot for the README (matches the real window layout).
# All data is synthetic: TEST-NET IPs (192.0.2.x, RFC 5737) and 12:34:56 MACs.
# Output: docs/screenshot.png
Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot '..\docs\screenshot.png'

# Non-ASCII via char codes — PowerShell 5.1 reads BOM-less files as ANSI.
$AVG  = [string][char]0x00D8     # O-slash
$AUML = [string][char]0x00E4     # a-umlaut
$UUML = [string][char]0x00FC     # u-umlaut
$STAR = [string][char]0x2605     # self marker
$PIN  = [string][char]0x25B8     # pinned marker
$PLAY = [string][char]0x25B6     # play
$GEAR = [string][char]0x2699     # gear
$BEST = [string][char]0x25BC     # best mark
$WORST = [string][char]0x25B2    # worst mark

$W = 1366; $H = 768
$SIDEBAR = 300
$HEADER = 96
$bmp = New-Object System.Drawing.Bitmap($W, $H)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$g.TextRenderingHint = 'ClearTypeGridFit'

function C([string]$hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }
function SB([string]$hex) { New-Object System.Drawing.SolidBrush (C $hex) }
function F([single]$size, [string]$style = 'Regular') {
    New-Object System.Drawing.Font('Segoe UI', $size, [System.Drawing.FontStyle]::$style)
}
function Draw([string]$t, [single]$x, [single]$y, $font, $brush) { $g.DrawString($t, $font, $brush, $x, $y) }
function DrawR([string]$t, [single]$xr, [single]$y, $font, $brush) {
    $sz = $g.MeasureString($t, $font); $g.DrawString($t, $font, $brush, [single]($xr - $sz.Width), $y)
}
function DrawC([string]$t, [single]$xc, [single]$y, $font, $brush) {
    $sz = $g.MeasureString($t, $font); $g.DrawString($t, $font, $brush, [single]($xc - $sz.Width / 2), $y)
}
function Round([single]$x, [single]$y, [single]$w, [single]$h, [single]$r, $brush) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); $g.FillPath($brush, $p); $p.Dispose()
}

# palette
$bg = SB '#1E1E2E'; $bgDark = SB '#181825'; $bgAlt = SB '#252535'
$surface = SB '#313244'; $text = SB '#CDD6F4'; $sub = SB '#6C7086'
$green = SB '#A6E3A1'; $red = SB '#F38BA8'; $mauve = SB '#CBA6F7'
$yellow = SB '#F9E2AF'; $peach = SB '#FAB387'; $gray = SB '#585B70'; $blue = SB '#89B4FA'
$dark = SB '#0B0B12'

$g.FillRectangle($bg, 0, 0, $W, $H)

# ══ Header ══
$g.FillRectangle($bgDark, 0, 0, $W, $HEADER)

# title + logo (logo centered under the title)
DrawC 'IP-Scanner' 70 12 (F 13 'Bold') $mauve
$pen = New-Object System.Drawing.Pen((C '#CBA6F7'), 2)
$g.DrawEllipse($pen, 53, 42, 34, 34)
for ($k = 0; $k -lt 6; $k++) {
    $a = ($k * 60 - 90) * [Math]::PI / 180
    $nx = 70 + 17 * [Math]::Cos($a); $ny = 59 + 17 * [Math]::Sin($a)
    $g.FillEllipse($mauve, [single]($nx - 3.5), [single]($ny - 3.5), 7, 7)
}
$g.FillEllipse($green, 65, 54, 10, 10)

# center: device bar + legend, ping bar + legend
$bx = 215; $bw = 660
DrawR "Ger$($AUML)te" ($bx - 8) 10 (F 9) $sub
Round $bx 11 $bw 13 6 $surface
Round $bx 11 14 13 6 $green
$g.FillRectangle($red, 229, 11, 8, 13)
Draw '12 / 254' ($bx + $bw + 8) 10 (F 8.5) $text
$g.FillRectangle($green, $bx, 32, 9, 9); Draw 'Online 6' ($bx + 13) 29 (F 8) $sub
$g.FillRectangle($red, ($bx + 70), 32, 9, 9); Draw 'Offline 6' ($bx + 83) 29 (F 8) $sub
DrawR 'Pings' ($bx - 8) 50 (F 9) $sub
Round $bx 51 $bw 13 6 $surface
Round $bx 51 200 13 6 $green
$g.FillRectangle($red, 410, 51, 6, 13)
Draw '512 / 25,4k' ($bx + $bw + 8) 50 (F 8.5) $text
$g.FillRectangle($green, $bx, 72, 9, 9); Draw 'Erfolg 498' ($bx + 13) 69 (F 8) $sub
$g.FillRectangle($red, ($bx + 80), 72, 9, 9); Draw 'Fehlschlag 14' ($bx + 93) 69 (F 8) $sub
$g.FillRectangle($gray, ($bx + 185), 72, 9, 9); Draw "$($UUML)bersprungen 0" ($bx + 198) 69 (F 8) $sub

# right controls: subnet row above ping row, right-aligned to the window edge
Round 1092 10 170 30 6 $bgAlt; Draw '192.0.2.0' 1104 16 (F 10) $text
Round 1270 10 80 30 6 $bgAlt;  Draw '/24' 1294 16 (F 10) $text
DrawR 'Pings:' 1128 56 (F 9) $sub
Round 1136 50 80 30 6 $bgAlt;  Draw '100' 1164 56 (F 10) $text
Round 1224 50 78 30 7 $green;  DrawC "$PLAY Scan" 1263 56 (F 10 'Bold') $dark
Round 1310 50 40 30 7 $surface; DrawC $GEAR 1330 54 (F 11) $text

# ══ Table ══
$tw = $W - $SIDEBAR
$cols = @(
    @{ t = 'IP-Adresse'; x = 22 },   @{ t = 'Status'; x = 175 },  @{ t = 'Gruppe'; x = 268 },
    @{ t = 'Hostname'; x = 345 },    @{ t = $AVG; x = 480 },      @{ t = 'Min'; x = 575 },
    @{ t = 'Max'; x = 665 },         @{ t = 'Letzter'; x = 755 }, @{ t = 'Fortschritt'; x = 855 },
    @{ t = 'MAC'; x = 955 }
)
$y = $HEADER + 12
foreach ($c in $cols) { Draw $c.t $c.x $y (F 10 'Bold') $sub }
$g.FillRectangle($surface, 0, ($y + 26), $tw, 1)

$rows = @(
    @{ ip = "192.0.2.1  $STAR"; ipB = $mauve; host = 'gateway';  grp = '#CBA6F7'; avg = '2,1 ms';  min = '1,4 ms';  max = '6,8 ms';   last = '2,0 ms';  mac = '12:34:56:78:9A:01'; aC = $green;  lC = $green;  bestA = $true }
    @{ ip = "$PIN 192.0.2.10";  ipB = $peach; host = 'nas';      grp = '#89B4FA'; avg = '0,8 ms';  min = '0,5 ms';  max = '2,1 ms';   last = '0,7 ms';  mac = '12:34:56:78:9A:0A'; aC = $green;  lC = $green;  bestA = $false }
    @{ ip = '192.0.2.21';       ipB = $text;  host = 'printer';  grp = '#F9E2AF'; avg = '12,4 ms'; min = '4,2 ms';  max = '48,9 ms';  last = '9,8 ms';  mac = '12:34:56:78:9A:15'; aC = $green;  lC = $green;  bestA = $false }
    @{ ip = '192.0.2.34';       ipB = $text;  host = 'desk-01';  grp = '#89B4FA'; avg = '64,2 ms'; min = '21,0 ms'; max = '180,3 ms'; last = '77,1 ms'; mac = '12:34:56:78:9A:22'; aC = $yellow; lC = $yellow; bestA = $false }
    @{ ip = '192.0.2.57';       ipB = $text;  host = 'cam-flur'; grp = '#F9E2AF'; avg = '5,5 ms';  min = '2,2 ms';  max = '14,0 ms';  last = '4,9 ms';  mac = '12:34:56:78:9A:39'; aC = $green;  lC = $green;  bestA = $false }
    @{ ip = '192.0.2.80';       ipB = $text;  host = 'tv-wohnen';grp = '#A6E3A1'; avg = '9,7 ms';  min = '3,8 ms';  max = '30,2 ms';  last = 'N/A';     mac = '12:34:56:78:9A:50'; aC = $green;  lC = $red;    bestA = $false }
)
$y = $HEADER + 44
$i = 0
foreach ($r in $rows) {
    if ($i % 2 -eq 1) { $g.FillRectangle($bgAlt, 0, ($y - 5), $tw, 30) }
    Draw $r.ip 22 $y (F 10 'Bold') $r.ipB
    Draw 'ONLINE' 175 $y (F 10 'Bold') $green
    Round 276 ($y + 1) 14 14 3 (SB $r.grp)
    Draw $r.host 345 $y (F 10) $text
    if ($r.bestA) { Draw $BEST 462 ($y + 3) (F 7) $green }
    if ($r.avg -eq '64,2 ms') { Draw $WORST 462 ($y + 3) (F 7) $red }
    Draw $r.avg 480 $y (F 10 'Bold') $r.aC
    Draw $r.min 575 $y (F 10) $green
    Draw $r.max 665 $y (F 10) $yellow
    Draw $r.last 755 $y (F 10 'Bold') $r.lC
    Draw '34/100' 855 $y (F 10) $text
    Draw $r.mac 955 $y (F 9.5) $text
    $y += 30; $i++
}

# totals footer (table width only)
$g.FillRectangle($bgDark, 0, ($H - 34), $tw, 34)
$g.FillRectangle($surface, 0, ($H - 35), $tw, 1)
Draw "$AVG Gesamt" 12 ($H - 27) (F 9 'Bold') $mauve
Draw "$AVG" 105 ($H - 27) (F 9) $sub;       Draw '15,8 ms' 120 ($H - 27) (F 9 'Bold') $green
Draw 'Min' 195 ($H - 27) (F 9) $sub;        Draw '5,5 ms' 220 ($H - 27) (F 9 'Bold') $green
Draw 'Max' 290 ($H - 27) (F 9) $sub;        Draw '46,7 ms' 318 ($H - 27) (F 9 'Bold') $green
Draw 'Letzter' 400 ($H - 27) (F 9) $sub;    Draw '15,9 ms' 445 ($H - 27) (F 9 'Bold') $green
DrawR 'Threads: 87' ($tw - 12) ($H - 27) (F 9) $sub

# ══ Sidebar ══
$sx0 = $W - $SIDEBAR
$g.FillRectangle($bgDark, $sx0, $HEADER, $SIDEBAR, $H - $HEADER)
$g.FillRectangle($surface, $sx0, $HEADER, 1, $H - $HEADER)
$x = $sx0 + 14; $xr = $W - 16

Round ($x - 2) ($HEADER + 14) 84 19 9 $green
Draw 'Netzwerk 1' ($x + 6) ($HEADER + 16) (F 8.5 'Bold') $dark
Draw '192.0.2.0/24' ($x + 92) ($HEADER + 16) (F 9.5) $blue
$pairs = @(
    @('Eigene IP', '192.0.2.34', $text), @('Gateway', '192.0.2.1', $green), @('Maske', '255.255.255.0', $text),
    @('DNS', '192.0.2.1', $text), @('MAC', '12:34:56:78:9A:22', $gray), @('Interface', 'Ethernet', $text),
    @('Online', '6', $green), @('Offline', '6', $sub), @("$AVG Latenz", '15,8 ms', $green)
)
$yy = $HEADER + 48
foreach ($p in $pairs) {
    Draw $p[0] $x $yy (F 9.5) $sub
    DrawR $p[1] $xr $yy (F 9.5) $p[2]
    $yy += 23
}
$yy += 6
$g.FillRectangle($surface, $sx0, $yy, $SIDEBAR, 1)

# internet latency table
$yy += 12
Draw 'INTERNET-LATENZ' $x $yy (F 8.5 'Bold') $sub
$c1 = $sx0 + 124; $c2 = $sx0 + 176; $c3 = $sx0 + 228; $c4 = $sx0 + 280
$yy += 24
DrawC $AVG $c1 $yy (F 8.5) $gray; DrawC 'Min' $c2 $yy (F 8.5) $gray
DrawC 'Max' $c3 $yy (F 8.5) $gray; DrawC 'Letzter' $c4 $yy (F 8.5) $gray
$hosts = @(
    @('Google 1', '8,2', '6,9', '14,1', '8,0'), @('Google 2', '8,4', '7,1', '13,8', '8,3'),
    @('Cloudflare', '5,9', '5,0', '11,2', '5,7'), @('Quad9', '12,3', '10,8', '19,5', '12,1')
)
$yy += 20
foreach ($h in $hosts) {
    Draw $h[0] $x $yy (F 9) $sub
    DrawC ($h[1] + ' ms') $c1 $yy (F 8.5 'Bold') $green
    DrawC ($h[2] + ' ms') $c2 $yy (F 8.5) $green
    DrawC ($h[3] + ' ms') $c3 $yy (F 8.5) $green
    DrawC ($h[4] + ' ms') $c4 $yy (F 8.5 'Bold') $green
    $yy += 21
}
$yy += 8
$g.FillRectangle($surface, $sx0, $yy, $SIDEBAR, 1)

# export toggles + file info
$yy += 16
Round ($sx0 + 38) $yy 104 30 7 $green;   DrawC 'TXT Export' ($sx0 + 90) ($yy + 7) (F 9 'Bold') $dark
Round ($sx0 + 156) $yy 104 30 7 $surface; DrawC 'CSV Export' ($sx0 + 208) ($yy + 7) (F 9 'Bold') $sub
DrawC 'network_scan_20260604.txt (18,4 KB)' ($sx0 + $SIDEBAR / 2) ($yy + 42) (F 8.5) $gray

$g.Dispose()
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "Screenshot written: $out"
