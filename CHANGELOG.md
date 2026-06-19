# Changelog

All notable changes, written for people who just use the app.

## 2.7 — 2026-06-19

Automatic updates and a thorough reliability pass.

### Added
- The app checks GitHub for a newer version at startup. If one exists it
  offers to download and install it in one click; the program restarts on
  the new version automatically. Can be turned off with
  `check_for_updates = false` in the configuration file.

### Changed
- When a device changes its IP address it is no longer listed twice — the
  old entry is dropped and only the current address is kept (matched by MAC).
- Smoother table and graphs under heavy scanning: per-ping and per-second
  work no longer redoes the same calculations.
- The generated configuration file is now documented in English.

### Fixed
- A rare crash that could end a running scan when a device moved to a new
  IP at the same moment it was being rechecked.
- Long hostnames now show an ellipsis with the full name on hover instead
  of being cut off, and the totals row never overlaps the thread counter.

## 2.6 — 2026-06-06

Graphs everywhere and a silky-smooth UI.

### Added
- Up to ten latency graphs: the + button under the history graph adds
  another one with its own device selector, X removes it. Pinned devices
  sort to the top of the selector.
- Every network in the sidebar gets its own small average-latency graph
  (can be turned off in the new Graphs settings).
- Time markers under all graphs (1m, 2m, ... — finer steps for short
  windows).

### Changed
- The logo animation runs on its own render thread and the graph math on
  a worker thread — nothing stutters anymore, even under heavy scan load.
- Paths inside the program folder are shown and stored Windows-style
  (.\scanner.db); database and configuration now default to the folder
  next to the exe. Existing installs keep their locations.
- The configuration-file setting shows the full file path and uses a
  file picker.
- Badge and bubble texts switch to white automatically on dark colors.
- Resetting the settings now needs a second click on a red confirm
  button ("Alle Einstellungen löschen?").

### Fixed
- Graph lines no longer cut through the min/max labels.
- The pin icon reliably turns red on hover; unpinning is a single click,
  pinning a double-click on the IP.

## 2.5 — 2026-06-06

Big usability release.

### Added
- Language switches instantly — no more restart when changing it in the
  settings.
- Known devices appear the moment a scan starts, with their stored hostname
  and MAC. Live results replace the stored values as the scan progresses.
- Devices that just went offline are pinged five more times within seconds,
  so short dropouts recover almost immediately.
- Pin and unpin straight from the table: double-click an IP to pin it, hover
  the pin icon (turns into a red X) and click to unpin. Changes sync with
  the settings.
- Subnets, pinned IPs and internet hosts are managed as colored bubbles:
  add with target + name + color, double-click a bubble to edit it.
- The pinned-IP color shows as the group color in the table; double-clicking
  a group square opens a color picker with the current color preselected.
- Hostnames are edited in a popup that spans the column; the X restores the
  automatic name.
- Latency history graphs (sidebar bottom + internet panel) with a device
  selector, time markers and a configurable time window — new "Graphs"
  settings section.
- Internet latency panel gained a totals row and a small history graph.
- Old configuration and database files from previous versions are upgraded
  automatically at startup.
- A folder button next to the export toggles opens the scans folder.
- Choose where the configuration file lives (settings, like the database
  path).

### Changed
- Offline recheck default is now 5 seconds, startup scan pings 10.
- The app file is around 8 MB smaller.
- Errors are written to error.log next to the app for easier reporting.

### Fixed
- Opening the settings during a scan could show a "Scan-Fehler" dialog.
- Typing in path fields no longer creates folders on every keystroke or
  makes the cursor jump.
- Toggling TXT/CSV export no longer resets the selected ping count.

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
