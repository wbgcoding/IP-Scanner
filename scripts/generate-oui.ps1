<#
.SYNOPSIS
    Regenerates src/IpScanner.Core/Resources/oui.txt from the official IEEE registration
    authority files (OUI/MA-L, MA-M, MA-S). Public registry data, no third-party tool output.

.DESCRIPTION
    Downloads (or reads local copies of) the three IEEE registry files and writes a single
    tab-separated "prefix<TAB>vendor" file, one line per assignment:
      - MA-L (24-bit OUI)   -> 6 hex chars
      - MA-M (28-bit)       -> 7 hex chars (OUI + top nibble of the assigned range)
      - MA-S (36-bit)       -> 9 hex chars (OUI + top 3 hex digits of the assigned range)
    MacVendorLookup tries the longest prefix first, so a MA-M/MA-S entry overrides the
    coarser MA-L one for the same OUI.

.PARAMETER SourceDir
    Optional directory with pre-downloaded oui.txt/mam.txt/oui36.txt. When omitted, the
    script downloads fresh copies from standards-oui.ieee.org.
#>
param(
    [string]$SourceDir = ""
)

$ErrorActionPreference = "Stop"

$urls = @{
    "oui.txt"   = "https://standards-oui.ieee.org/oui/oui.txt"
    "mam.txt"   = "https://standards-oui.ieee.org/oui28/mam.txt"
    "oui36.txt" = "https://standards-oui.ieee.org/oui36/oui36.txt"
}

$work = if ($SourceDir) { $SourceDir } else { Join-Path $env:TEMP "oui-src-$([guid]::NewGuid())" }
if (-not $SourceDir) {
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    foreach ($f in $urls.Keys) {
        Invoke-WebRequest -Uri $urls[$f] -OutFile (Join-Path $work $f) -TimeoutSec 60
    }
}

function Get-Entries($path, $extraHexFromRange) {
    $lines = Get-Content -Path $path -Encoding UTF8
    $result = New-Object System.Collections.Generic.List[object]
    $pendingOui = $null
    foreach ($line in $lines) {
        if ($line -match '^([0-9A-Fa-f]{2})-([0-9A-Fa-f]{2})-([0-9A-Fa-f]{2})\s+\(hex\)') {
            $pendingOui = ($Matches[1] + $Matches[2] + $Matches[3]).ToUpperInvariant()
            continue
        }
        if ($extraHexFromRange -eq 0) {
            # MA-L (oui.txt): plain 6 hex chars, no range.
            if ($line -match '^([0-9A-Fa-f]{6})\s+\(base 16\)\s*\t*(.*)$') {
                $result.Add([pscustomobject]@{ Prefix = $Matches[1].ToUpperInvariant(); Vendor = $Matches[2].Trim() })
            }
            continue
        }
        if ($line -match '^([0-9A-Fa-f]{6})-[0-9A-Fa-f]{6}\s+\(base 16\)\s*\t*(.*)$') {
            $rangeStart = $Matches[1].ToUpperInvariant()
            $vendor = $Matches[2].Trim()
            if ($pendingOui) {
                $extra = $rangeStart.Substring(0, $extraHexFromRange)
                $result.Add([pscustomobject]@{ Prefix = ($pendingOui + $extra); Vendor = $vendor })
            }
            continue
        }
    }
    return $result
}

$entries = New-Object System.Collections.Generic.List[object]
$entries.AddRange((Get-Entries (Join-Path $work "oui.txt") 0))
$entries.AddRange((Get-Entries (Join-Path $work "mam.txt") 1))
$entries.AddRange((Get-Entries (Join-Path $work "oui36.txt") 3))

$seen = New-Object System.Collections.Generic.HashSet[string]
$out = New-Object System.Collections.Generic.List[string]
$date = Get-Date -Format "yyyy-MM-dd"
$out.Add("# MAC vendor prefixes from the IEEE public registration authority registries:")
$out.Add("# MA-L (24-bit) $($urls['oui.txt'])")
$out.Add("# MA-M (28-bit) $($urls['mam.txt'])")
$out.Add("# MA-S (36-bit) $($urls['oui36.txt'])")
$out.Add("# Regenerated $date by scripts/generate-oui.ps1. Public registry data, MIT-compatible.")

foreach ($e in ($entries | Sort-Object Prefix)) {
    $key = "$($e.Prefix)"
    if ($seen.Add($key) -and $e.Vendor) {
        $out.Add("$($e.Prefix)`t$($e.Vendor)")
    }
}

$destination = Join-Path $PSScriptRoot "..\src\IpScanner.Core\Resources\oui.txt"
[System.IO.File]::WriteAllLines($destination, $out, (New-Object System.Text.UTF8Encoding($false)))

if (-not $SourceDir) { Remove-Item -Recurse -Force $work }

Write-Host "Wrote $($out.Count - 5) entries to $destination"
