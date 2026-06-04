# Changelog

All notable changes, written for people who just use the app.

## 2.4 — 2026-06-04

Stability and cleanup release.

### Fixed
- The app could close without any message when pressing Stop. It now stays
  open and shows an error dialog instead.
- CSV exports broke when a device name contained a comma or quote. Fields
  are now quoted properly, so the files open cleanly in Excel.
- A damaged config file (for example an absurdly long subnet entry) could
  crash the app at startup. Bad values now simply fall back to defaults.
- Network cards without a name no longer show up as "eth0".

### Changed
- All latency values across the table, sidebar and totals row now use the
  same number format, following your Windows region settings.
- File dialogs and the last remaining texts are now fully translated (DE/EN).

## 2.3 — 2026-06-04

### Added
- Language can be switched in the settings (automatic, German, English).
- TXT/CSV export toggles in the sidebar, with name and size of the written
  files shown right below.
- Color picker for the progress-bar colors — click a legend square to
  change it.
- Internet latency values with one decimal and heatmap colors, centered
  under their column headers.
- The logo's center dot now glows while the scanner is working.

### Changed
- Settings save instantly — the Save button is gone.
- The device progress bar no longer shows an "unknown" segment.

## 2.2 and earlier

- Two-phase scan: fast discovery, then per-device ping analysis.
- Offline IPs are rechecked during a run and join when they come online.
- Hostname/MAC resolution via ARP, reverse DNS, mDNS and NetBIOS in parallel.
- Latency table with min/avg/max/last, heatmap colors and best/worst markers.
- Device grouping, pinned IPs, multiple subnets, known-devices database.
- German/English interface, adjustable text size, dark theme.
