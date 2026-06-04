# Renders a privacy-safe demo screenshot for the README/release.
# All data is synthetic: TEST-NET IPs (192.0.2.x, RFC 5737), locally
# administered MACs (02:..) and generic hostnames — never real scan data.
# Output: docs/screenshot.png
Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot '..\docs\screenshot.png'

# Non-ASCII via char codes — PowerShell 5.1 reads BOM-less files as ANSI.
$AVG  = [string][char]0x00D8                          # Ø
$AUML = [string][char]0x00E4                          # ä
$STAR = [string][char]0x2605                          # ★
$PIN  = [string][char]0x25B8                          # pinned marker (Segoe-safe)
$PLAY = [string][char]0x25B6                          # play
$GEAR = [string][char]0x2699                          # gear

$W = 1280; $H = 760
$bmp = New-Object System.Drawing.Bitmap($W, $H)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$g.TextRenderingHint = 'ClearTypeGridFit'

function C([string]$hex) {
    [System.Drawing.ColorTranslator]::FromHtml($hex)
}
function SB([string]$hex) { New-Object System.Drawing.SolidBrush (C $hex) }
function F([single]$size, [string]$style = 'Regular') {
    New-Object System.Drawing.Font('Segoe UI', $size, [System.Drawing.FontStyle]::$style)
}
function Draw([string]$text, [single]$x, [single]$y, $font, $brush) {
    $g.DrawString($text, $font, $brush, $x, $y)
}

# palette
$bg = SB '#1E1E2E'; $bgDark = SB '#181825'; $bgAlt = SB '#252535'
$surface = SB '#313244'; $text = SB '#CDD6F4'; $sub = SB '#6C7086'
$green = SB '#A6E3A1'; $red = SB '#F38BA8'; $mauve = SB '#CBA6F7'
$yellow = SB '#F9E2AF'; $gray = SB '#585B70'; $blue = SB '#89B4FA'

$g.FillRectangle($bg, 0, 0, $W, $H)

# ── header ──
$g.FillRectangle($bgDark, 0, 0, $W, 92)
Draw 'IP-Scanner' 18 14 (F 14 'Bold') $mauve
# mini logo (ring + nodes)
$pen = New-Object System.Drawing.Pen((C '#CBA6F7'), 2)
$g.DrawEllipse($pen, 36, 44, 34, 34)
$g.FillEllipse($green, 49, 57, 9, 9)
for ($k = 0; $k -lt 6; $k++) {
    $a = ($k * 60 - 90) * [Math]::PI / 180
    $nx = 53 + 17 * [Math]::Cos($a); $ny = 61 + 17 * [Math]::Sin($a)
    $g.FillEllipse($mauve, [single]($nx - 3), [single]($ny - 3), 6, 6)
}

# progress bars
Draw "Ger$($AUML)te" 150 16 (F 9) $sub
$g.FillRectangle($surface, 200, 16, 620, 13)
$g.FillRectangle($green, 200, 16, 70, 13)
$g.FillRectangle($red, 270, 16, 26, 13)
$g.FillRectangle($gray, 296, 16, 524, 13)
Draw '254 / 254' 830 15 (F 9) $text
Draw 'Pings' 150 52 (F 9) $sub
$g.FillRectangle($surface, 200, 52, 620, 13)
$g.FillRectangle($green, 200, 52, 180, 13)
$g.FillRectangle($red, 380, 52, 12, 13)
$g.FillRectangle($gray, 392, 52, 428, 13)
Draw '1,2k / 2,6k' 830 51 (F 9) $text

# header controls
$g.FillRectangle($bgAlt, 940, 12, 130, 28); Draw '192.0.2.0' 952 17 (F 10) $text
$g.FillRectangle($bgAlt, 1078, 12, 60, 28);  Draw '/24' 1095 17 (F 10) $text
$g.FillRectangle($bgAlt, 940, 50, 70, 28);  Draw '100' 962 55 (F 10) $text
$g.FillRectangle($green, 1018, 50, 76, 28); Draw "$PLAY Scan" 1032 55 (F 10 'Bold') $bgDark
$g.FillRectangle($surface, 1102, 50, 36, 28); Draw $GEAR 1112 54 (F 11) $text

# ── table ──
$cols = @(
    @{ t = 'IP-Adresse'; x = 24 },  @{ t = 'Status'; x = 150 }, @{ t = 'Gruppe'; x = 230 },
    @{ t = 'Hostname'; x = 298 },   @{ t = $AVG; x = 420 },     @{ t = 'Min'; x = 500 },
    @{ t = 'Max'; x = 575 },        @{ t = 'Letzter'; x = 648 },@{ t = 'Fortschritt'; x = 726 },
    @{ t = 'MAC'; x = 822 }
)
$y = 108
foreach ($c in $cols) { Draw $c.t $c.x $y (F 9.5 'Bold') $sub }
$g.FillRectangle($surface, 16, 130, 950, 1)

$rows = @(
    @{ ip = "192.0.2.1  $STAR"; host = 'gateway'; grp = '#CBA6F7'; avg = '2,1 ms';  min = '1,4 ms'; max = '6,8 ms';  last = '2,0 ms';  mac = '02:11:5C:01:00:01'; aC = $green;  lC = $green }
    @{ ip = "$PIN 192.0.2.10";  host = 'nas';     grp = '#89B4FA'; avg = '0,8 ms';  min = '0,5 ms'; max = '2,1 ms';  last = '0,7 ms';  mac = '02:11:5C:01:00:0A'; aC = $green;  lC = $green }
    @{ ip = '192.0.2.21';    host = 'printer';   grp = '#F9E2AF'; avg = '12,4 ms'; min = '4,2 ms'; max = '48,9 ms'; last = '9,8 ms';  mac = '02:11:5C:01:00:15'; aC = $green;  lC = $green }
    @{ ip = '192.0.2.34';    host = 'desk-01';   grp = '#89B4FA'; avg = '64,2 ms'; min = '21,0 ms';max = '180,3 ms';last = '77,1 ms'; mac = '02:11:5C:01:00:22'; aC = $yellow; lC = $yellow }
    @{ ip = '192.0.2.57';    host = 'cam-flur';  grp = '#F9E2AF'; avg = '5,5 ms';  min = '2,2 ms'; max = '14,0 ms'; last = '4,9 ms';  mac = '02:11:5C:01:00:39'; aC = $green;  lC = $green }
    @{ ip = '192.0.2.80';    host = 'tv-wohnen'; grp = '#A6E3A1'; avg = '9,7 ms';  min = '3,8 ms'; max = '30,2 ms'; last = 'N/A';     mac = '02:11:5C:01:00:50'; aC = $green;  lC = $red }
)
$y = 140
$i = 0
foreach ($r in $rows) {
    if ($i % 2 -eq 1) { $g.FillRectangle($bgAlt, 16, $y - 4, 950, 30) }
    Draw $r.ip 24 $y (F 10 'Bold') $text
    Draw 'ONLINE' 150 $y (F 10 'Bold') $green
    $g.FillRectangle((SB $r.grp), 237, ($y + 2), 14, 14)
    Draw $r.host 298 $y (F 10) $text
    Draw $r.avg 420 $y (F 10 'Bold') $r.aC
    Draw $r.min 500 $y (F 10) $green
    Draw $r.max 575 $y (F 10) $yellow
    Draw $r.last 648 $y (F 10 'Bold') $r.lC
    Draw '34/100' 726 $y (F 10) $text
    Draw $r.mac 822 $y (F 9) $text
    $y += 30; $i++
}

# totals row
$g.FillRectangle($bgDark, 0, ($H - 36), $W - 300, 36)
Draw "$AVG Gesamt" 24 ($H - 28) (F 9.5 'Bold') $mauve
Draw "$AVG 15,8 ms    Min 5,5 ms    Max 46,7 ms    Letzter 15,9 ms" 110 ($H - 28) (F 9.5) $green
Draw 'Threads: 87' ($W - 410) ($H - 28) (F 9.5) $sub

# ── sidebar ──
$g.FillRectangle($bgDark, $W - 300, 92, 300, $H - 92)
$x = $W - 282
Draw 'Netzwerk 1' $x 110 (F 9 'Bold') $bgDark
$g.FillRectangle($green, ($x - 4), 108, 78, 18); Draw 'Netzwerk 1' $x 110 (F 9 'Bold') $bgDark
Draw '192.0.2.0/24' ($x + 86) 110 (F 9.5) $blue
$pairs = @(
    @('Eigene IP', '192.0.2.34'), @('Gateway', '192.0.2.1'), @('Maske', '255.255.255.0'),
    @('DNS', '192.0.2.1'), @('MAC', '02:11:5C:01:00:22'), @('Interface', 'Ethernet'),
    @('Online', '6'), @('Offline', '248'), @("$AVG Latenz", '15,8 ms')
)
$yy = 140
foreach ($p in $pairs) {
    Draw $p[0] $x $yy (F 9.5) $sub
    Draw $p[1] ($x + 130) $yy (F 9.5) $text
    $yy += 24
}
$g.FillRectangle($surface, ($W - 300), ($yy + 4), 300, 1)
Draw 'INTERNET-LATENZ' $x ($yy + 16) (F 8.5 'Bold') $sub
Draw "        $AVG        Min      Max     Letzter" ($x + 40) ($yy + 40) (F 8.5) $sub
$hosts = @(
    @('Google 1', '8,2', '6,9', '14,1', '8,0'), @('Google 2', '8,4', '7,1', '13,8', '8,3'),
    @('Cloudflare', '5,9', '5,0', '11,2', '5,7'), @('Quad9', '12,3', '10,8', '19,5', '12,1')
)
$yy += 60
foreach ($h in $hosts) {
    Draw $h[0] $x $yy (F 9) $sub
    Draw ($h[1] + ' ms') ($x + 80) $yy (F 8.5 'Bold') $green
    Draw ($h[2] + ' ms') ($x + 135) $yy (F 8.5) $green
    Draw ($h[3] + ' ms') ($x + 185) $yy (F 8.5) $green
    Draw ($h[4] + ' ms') ($x + 235) $yy (F 8.5 'Bold') $green
    $yy += 22
}
$g.FillRectangle($green, ($x + 10), ($yy + 14), 100, 30); Draw 'TXT Export' ($x + 25) ($yy + 20) (F 9 'Bold') $bgDark
$g.FillRectangle($surface, ($x + 124), ($yy + 14), 100, 30); Draw 'CSV Export' ($x + 139) ($yy + 20) (F 9 'Bold') $sub
Draw 'network_scan_20260604_1.txt (18,4 KB)' $x ($yy + 56) (F 8.5) $gray

$g.Dispose()
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "Screenshot written: $out"
