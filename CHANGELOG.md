# Changelog

All notable changes, written for people who just use the app.

## Unreleased

A redesigned scan archive, reworked tool pages and a set of everyday
improvements in the main window.

### Added
- Scan archive: summary cards (runs, devices, availability, latency, jitter,
  open ports, findings), a chart of online devices per run, quick ranges
  (24 hours, 7 days, 30 days, all), a filter for IP, name or MAC, and a run
  detail with tabs for devices, ports and findings, and history.
- DNS lookup with your own DNS server, every common record type, the answer
  time and a mode that asks up to six servers at once and marks differing
  answers.
- A MAC address page (vendor lookup and the address table of this PC), and the
  subnet calculator can split a network into smaller subnets.
- Continuous ping keeps the whole session, with zoom, an outage count and CSV
  save. Route tracing can run continuously with loss and times per hop.
- Main window: a Ports column, First seen and Last seen columns, private
  (random) MAC addresses are labelled, outages show in the graphs, and an
  optional alert when a device goes offline and when it is back.
- Right click on a device: continuous ping, trace route and "Open with"
  (browser, remote desktop, SSH, network shares). The tools window starts with
  the selected device as target.
- The log page has the switch for detailed logging and records routine events.

### Changed
- The Loss column is visible by default.
- CSV exports from the app use the list separator and decimal sign of your
  Windows region, so they open correctly in Excel. Command line exports keep
  the comma.
- The ping count is set in one place only, the header. The first-run boxes for
  update and internet checks start unticked.
- `--top-ports` accepts up to 1000 ports without an extra switch.

### Fixed
- The window stays maximised when restored from the tray, and the tray message
  says "1 device online".
- Both tables on the port check page can be resized, and the time axis and
  values under the ping graph show again.
- Selection boxes on the tool pages showed internal text instead of the label.

## 2.11 - 2026-10-24

The biggest release so far: a tools window, a scan archive with compare and
backup, a command line mode, IPv6 and much deeper port, service and system
detection. Stored device data is now encrypted on your PC, and the interface
was tidied up for narrow windows, large text and right-to-left languages.

### Added
- A Tools window (button in the header) with Wake-on-LAN, a port check for any
  address, DNS lookup, continuous ping with route tracing, a subnet
  calculator, a certificate inspector and scan pages for host discovery,
  service detection, system estimate, service checks and report export. It
  stays open next to the main window, pages keep running while you switch, and
  closing it stops everything.
- A scan archive page: browse earlier scans, compare any two runs (new,
  missing and changed devices and ports) and export the comparison as text,
  JSON, XML or a grep-friendly list. Retention and compaction of old runs are
  configurable.
- Full backup and restore of your settings and data to one ZIP file, with an
  optional password (AES-256). A safety copy is made before every restore.
- A command line mode: start IP-Scanner.exe with options and it runs the same
  scans without a window, printing into the terminal it was started from, and
  writes text, XML, JSON or grep-friendly reports. It is the same single file
  as the app, there is no separate command line program. Windows does not wait
  for it: in cmd use `start /wait` (not together with `>`) or a batch file, in
  PowerShell pipe the output (`| Out-File` or `| Out-Host`) instead of using `>`. Its options follow the familiar nmap style, with
  safety switches for large or aggressive scans.
- IPv6: scan IPv6 targets, see IPv6 addresses in the table and sidebar, find
  neighbours and resolve names over mDNS; reports and exports carry the
  address family.
- Flexible scan targets: CIDR, ranges, single hosts, lists from a file and
  exclusions. Scans of more than 65,536 addresses ask first.
- Real port states (open, closed, filtered) with several scan types, scan
  speed templates, optional host discovery modes and ready-made scan presets.
  Scan types that need a packet capture driver are greyed out with the reason
  until one is installed.
- Service and version detection with a confidence mark, built-in service
  checks and an optional system estimate (OS column) based on TTL, ports and
  an active fingerprint.
- Profiles: separate lists of networks, pinned addresses and internet hosts
  you can switch from the top of the sidebar.
- Mute for devices that go offline routinely, on-demand and background
  hostname re-resolution, IP conflict and MAC change reports, and a button to
  accept a rogue device as known.
- Packet loss and jitter columns, a remaining-time estimate on the progress
  bar, filter pills (online, pinned, unknown), search prefixes in the filter
  box and a remembered sort order and column widths.
- Per-device notes that are saved and exported, copy actions for rows and
  network cards, more keyboard shortcuts with a help page, and a welcome card
  on first run.
- A log page with filters, search and copy, and a verbose logging setting.
- Show and switch the network adapter used for scanning.
- Every button and field now has a tooltip, and the Help page explains the
  scan options.

### Changed
- Names, MACs and IPs in the known-devices database, manual renames and
  sensitive settings (SNMP community, recent Wake-on-LAN targets) are now
  stored encrypted for your Windows account. Exports, the settings file and
  the log stay readable by design.
- Updates are only installed after the download matches the published
  checksum and signature, and only from the official release file.
- The first start asks before checking for updates or pinging internet hosts.
- Full port scans and large port selections need an explicit confirmation.
- Scans of networks smaller than /24 now cover exactly the configured range.
- The window uses its own title bar, the profile switcher moved to the top of
  the sidebar, and dialogs and the archive date pickers follow the theme.
- Layouts adapt to narrow windows, large text sizes and right-to-left
  languages without clipped text; the sidebar collapses in narrow windows.
- Long cells are trimmed with an ellipsis and a tooltip; many settings and
  messages were reworded for clearer wording and consistent terms.
- Scans, database work and lookups run faster and off the interface thread,
  and background work shuts down cleanly when you close the app.
- The error log hides personal paths and private host names.
- Spanish, French and Portuguese command line help and many other texts were
  translated.

### Fixed
- Light theme buttons had weak contrast, and some colors did not follow a
  live theme switch.
- The Arabic layout mirrored graphs and icons.
- Text scaled to 150-200 percent could clip the header.
- Clicks on controls inside an expanded device row could be swallowed.
- Network cards were rebuilt on every scan and lost their history; cards for
  deleted subnets stayed behind.
- Stopping a scan or clearing the table right before starting another could
  lose the new start.
- The ping counter could count skipped pings against the estimate.
- A cancelled quick port scan could report results from a half-finished
  probe.
- Port answers from unreachable targets are now marked as only probable.
- Config imports with oversized lists or network paths are refused, and an
  update script failed for folders with brackets or ampersands.
- The Wake-on-LAN MAC box was too narrow for a full address.

## 2.10 - 2026-08-30

A quality and security pass over the whole app: a long-standing fake vendor
database is now real, several counting/display bugs are fixed, and the
sidebar and port scanner both got more capable.

### Added
- The MAC vendor database is now the real IEEE list (~40,000 manufacturers)
  instead of three placeholder entries - the "Vendor" column and CSV export
  actually work now.
- Optional full 1-65535 TCP port scan per device, with a progress bar and
  cancel button, in addition to the automatic ~190-port scan (which stays the
  default - a full sweep looks like a port scan to firewalls/monitoring).
- The sidebar has a drag handle (240-560 px), remembered across restarts.
- Per-Monitor-V2 DPI awareness - no more blur moving the window between
  monitors with different scaling.

### Changed
- TXT scan reports now follow the selected UI language instead of always
  being English.
- The system tray icon/menu and the progress-bar legends now update
  immediately on a language switch instead of staying in the old language.
- Port scans stop immediately when a device's detail view is closed, and run
  throttled instead of opening every connection at once.
- The settings file and known-devices database folder are now restricted to
  your Windows account; config saves are now crash-safe (atomic, with an
  automatic backup of the previous version).

### Fixed
- A device's ping counter could show a number past its own target (e.g.
  "30/25") after reconnecting or during offline rechecks.
- The total ping counter could do the same when pinned IPs outside the
  scanned subnets were involved.
- An invalid export/database path could be saved even while shown in red,
  only failing once a scan had already finished.
- Opening a device's port list while it was offline could leave it empty for
  the rest of the session, even after the device came back online.
- CSV export could be abused for formula injection in Excel/Sheets via a
  crafted hostname.
- Reports, the database and the config file now always resolve relative to
  the program folder, not whatever the current working directory happened to
  be at launch.
- A device that accepted a connection on port 443 but never spoke TLS could
  hang the entire port scan forever.
- The auto-updater now checks that its file swap actually succeeded instead
  of assuming it did.
- A device or network name ending in something that looked like a hex color
  could silently lose part of its name when saved.
- Config values containing "#" or ";" - for example a path like
  `C:\Reports#2\Scans` - were truncated as if the rest were a comment.
- A device's expanded latency graph no longer recomputes on every single
  ping; only once a second, as intended.
- The overall "Pings X/Y" total could exceed its own budget when a device
  went offline and reconnected during a scan.
- Cancelling a full port scan (by closing the detail view mid-scan) could
  wipe out services already found by the earlier quick scan.
- The sidebar's remembered width could silently reset after certain settings
  changes.
- The three sidebar delete-confirmation buttons could leak a timer if used
  in quick succession.
- Reopening a device's detail view immediately after closing it could leave
  its port list stuck and never filled in.
- A rare race between the background hostname lookup and the UI's own
  refresh could corrupt a device's displayed name.
- The exported TXT report's status column could truncate longer translated
  status words (e.g. French "HORS LIGNE").

## 2.9 - 2026-07-12

Live theme switching, a full port scan, and networks/pinned IPs/internet hosts
now manage themselves right from the sidebar.

### Added
- Networks, Pinned IPs and Internet Latency are now fully editable from the
  sidebar: a "+" to add, click an entry to rename/re-target it, and a small
  ✕ (click twice to confirm) to remove it. Everything saves instantly - the
  matching editors are gone from Settings.
- The device detail view now shows every open TCP and UDP port, not just a
  handful of well-known ones, each with its detected name and version where
  the service offers one (HTTP/HTTPS server header, SSH/FTP/SMTP/POP3/IMAP
  banners). Openable services (HTTP, HTTPS, SSH, RDP, SMB, FTP) launch the
  right app or browser on click, including on non-standard ports.
- Theme (dark/light/auto) now switches live - no restart, no flicker.
- Each network card's history graph can toggle between the normal window and
  a tight 30-second view; same for the internet-latency graph.

### Changed
- A network's Online/Offline counts are now one line instead of two.
- The IP address column is narrower by default.

### Fixed
- The HTTP/HTTPS quick-open buttons now always target the right port instead
  of assuming the default one.

## 2.8 - 2026-07-04

Per-device graphs, ten languages, and a cleaner main window.

### Added
- Click a device's latency column (Ø / Min / Max / Last) to expand a live
  graph of its ping history right under the row; click the row again to close
  it. The graph spans the full width with time markers and follows the same
  time window as the sidebar graphs.
- New setting: automatically open that graph for pinned devices.
- A quick "Offline (DB)" switch in the header hides or shows known devices
  from the database that are currently offline - handy to declutter the table.
- Eight more languages, for ten in total: English, German, Spanish, French,
  Chinese, Hindi, Arabic, Portuguese, Russian and Japanese. Switch instantly
  in the settings; the app also follows your Windows language automatically.

### Changed
- MAC addresses are now also found for devices in additional local subnets,
  not just the main one.
- The export buttons (TXT/CSV) moved to the top of the sidebar, above the
  networks.
- The device and ping progress bars now start at the same position and stay
  aligned in every language.
- The table no longer shifts sideways when the scrollbar appears.

### Fixed
- Device graphs open instantly with their full history instead of filling up
  slowly.
- The "max time span" field in the graph settings is no longer oversized.

## 2.7 - 2026-06-19

Automatic updates and a thorough reliability pass.

### Added
- The app checks GitHub for a newer version at startup. If one exists it
  offers to download and install it in one click; the program restarts on
  the new version automatically. Can be turned off with
  `check_for_updates = false` in the configuration file.

### Changed
- When a device changes its IP address it is no longer listed twice - the
  old entry is dropped and only the current address is kept (matched by MAC).
- Smoother table and graphs under heavy scanning: per-ping and per-second
  work no longer redoes the same calculations.
- The generated configuration file is now documented in English.

### Fixed
- A rare crash that could end a running scan when a device moved to a new
  IP at the same moment it was being rechecked.
- Long hostnames now show an ellipsis with the full name on hover instead
  of being cut off, and the totals row never overlaps the thread counter.

## 2.6 - 2026-06-06

Graphs everywhere and a silky-smooth UI.

### Added
- Up to ten latency graphs: the + button under the history graph adds
  another one with its own device selector, X removes it. Pinned devices
  sort to the top of the selector.
- Every network in the sidebar gets its own small average-latency graph
  (can be turned off in the new Graphs settings).
- Time markers under all graphs (1m, 2m, ... - finer steps for short
  windows).

### Changed
- The logo animation runs on its own render thread and the graph math on
  a worker thread - nothing stutters anymore, even under heavy scan load.
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

## 2.5 - 2026-06-06

Big usability release.

### Added
- Language switches instantly - no more restart when changing it in the
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
  selector, time markers and a configurable time window - new "Graphs"
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

## 2.4 - 2026-06-04

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

## 2.3 - 2026-06-04

### Added
- Language can be switched in the settings (automatic, German, English).
- TXT/CSV export toggles in the sidebar, with name and size of the written
  files shown right below.
- Color picker for the progress-bar colors - click a legend square to
  change it.
- Internet latency values with one decimal and heatmap colors, centered
  under their column headers.
- The logo's center dot now glows while the scanner is working.

### Changed
- Settings save instantly - the Save button is gone.
- The device progress bar no longer shows an "unknown" segment.

## 2.2 and earlier

- Two-phase scan: fast discovery, then per-device ping analysis.
- Offline IPs are rechecked during a run and join when they come online.
- Hostname/MAC resolution via ARP, reverse DNS, mDNS and NetBIOS in parallel.
- Latency table with min/avg/max/last, heatmap colors and best/worst markers.
- Device grouping, pinned IPs, multiple subnets, known-devices database.
- German/English interface, adjustable text size, dark theme.
