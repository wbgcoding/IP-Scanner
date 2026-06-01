# IP-Scanner — WPF Native Windows GUI: Design Spec
**Date:** 2026-06-01  
**Stack:** C# · WPF · .NET 8 · self-contained single `.exe`  
**Replaces:** `network_scanner.py` (Python terminal app, 4517 lines)

---

## 1. Goals

Convert the existing Python terminal-based network scanner into a native Windows GUI application with identical scanning functionality, all existing settings/options, and a modern dark GUI. No Python installation required — ships as one standalone `.exe`.

---

## 2. Technology

| Concern | Choice | Reason |
|---|---|---|
| UI framework | WPF (.NET 8) | Native Windows, data binding, no terminal aesthetics |
| Distribution | Self-contained single `.exe` | No runtime install needed |
| Theme | Dark Modern (Catppuccin palette) | Non-terminal, polished, table-focused |
| Networking | P/Invoke → iphlpapi, ws2_32 | Same APIs as Python ctypes calls |
| Database | SQLite via `Microsoft.Data.Sqlite` | Replaces Python sqlite3 |
| Patterns | MVVM (no third-party MVVM framework) | Clean separation, testable ViewModels |
| Build | `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true` | One file output |

---

## 3. Window Layout

```
┌─────────────────────────────────────────────────────────────────────────────┐
│ IP-Scanner  [192.168.1.0/24] [10.0.0.0/24]  ░░Geräte░░ ░░Pings░░  [Scan][⏸][■][⚙] │
├────────────────────────────────────────────────────────┬────────────────────┤
│  IP          Status  Gruppe  Hostname  Avg  Min  Max  │  Netzwerk 1        │
│                              Letzt  Fortschritt  MAC  │  Netzwerk 2        │
│  ...                                                   │  Internet-Latenz   │
│  (scrollable, sortable, live-updating DataGrid)        │  Phase / Export    │
└────────────────────────────────────────────────────────┴────────────────────┘
```

### 3.1 Header Bar

- **Left:** App title + subnet pills (one per scanned subnet, auto-detected + configured)
- **Center:** Two segmented progress bars
  - *Geräte:* Green (online) · Red (offline) · Dark-gray (unknown/not yet scanned)
  - *Pings:* Blue (success) · Orange (failure) · Mid-gray (skipped — device offline)
  - Each bar has a small legend row below it with counts
- **Right:** Ping-count dropdown (10 / 100 / 1k / 10k / 100k / ∞) · `▶ Scan` · `⏸ Pause` · `■ Stop` · `⚙ Settings`

### 3.2 Device Table (main area)

Columns (all resizable, sortable by click):

| Column | Content |
|---|---|
| IP | IPv4 address; gateway row is bold |
| Status | `● ONLINE` (green) / `○ OFFLINE` (red) |
| Gruppe | Colored square block — same group = same color |
| Hostname | Reverse-DNS / NetBIOS name |
| Avg | Average ping latency (ms) |
| Min | Minimum latency |
| Max | Maximum latency |
| Letzt | Most recent ping |
| Fortschritt | Mini segmented bar (success/fail/skip) + `done/total` |
| MAC | Hardware address |

- Online devices sorted to top; offline below; not-yet-seen hidden (or dimmed)
- Known DB devices shown immediately as OFFLINE until they respond
- Pinned IPs always at the very top
- Color coding for latency: ≤50ms green · ≤100ms yellow · ≤200ms orange · ≤400ms red · >400ms dark red
- Highest latency device gets a red marker; lowest gets a green marker (per-render)

### 3.3 Right Sidebar (220 px fixed width)

Scrollable. One card per scanned network, then Internet section, then Phase/Export.

**Per-network card:**
- Colored badge with "Netzwerk N" + CIDR
- Eigene IP · MAC · Gateway · Subnetzmaske · DNS (all entries) · Interface name
- Online badge (green) · Offline badge (gray) · Ø Latenz

**Internet-Latenz section:**
- One row per configured public host (Cloudflare / Google / Google DNS / Quad9 by default)
- Latency colored by same thresholds as device pings

**Phase / Export:**
- Current scan phase chip: Discovery · Analysis · Saving · Ready
- Path of last saved report (clickable → opens folder in Explorer)

---

## 4. Settings Window (`⚙`)

A separate modal window (opens via `⚙` button, blocks main window) with tabbed sections — mirrors every option from `network_scanner.conf`:

### Tab: Netzwerk
- Subnet list (add/remove rows): `subnet`, `subnet_2`, …
- Pinned IPs list (add/remove)

### Tab: Ping-Verhalten
- Ping count (numeric spinner, same presets as dropdown)
- Ping interval ms (slider + numeric, 0–10000)
- Offline after N failed pings (1–100)
- Init ping count (1–100)
- High-pressure mode (toggle)
- Infinite mode toggle

### Tab: Internet
- Enable internet ping (toggle)
- Internet hosts list (add/remove/reorder)

### Tab: Ausgabe
- Output directory (folder picker)
- Write TXT report (toggle)
- Export CSV (toggle)

### Tab: Performance
- Analysis ping threads (1–1000)
- Discovery ping threads (0–1000)
- Refresh rate (0.1–60 s)

### Tab: Datenbank
- Known-devices DB enabled (toggle)
- DB file path (shown, not editable)
- "DB leeren" button with confirmation

Settings are saved to `ip_scanner.conf` (same flat key=value format for compatibility) and take effect on the next scan start.

---

## 5. Scanning Engine

The WPF app wraps the same networking logic as the Python script, re-implemented in C#:

### 5.1 ICMP Pings
- Primary: `IcmpSendEcho` via P/Invoke (iphlpapi) — sub-millisecond precision, no admin required
- Fallback: `ping.exe` output parsing (same as Python fallback)
- `CancellationToken` replaces `ScannerControl` stop/pause flags

### 5.2 Scan Phases
1. **Discovery** — 1 ping per IP, `ThreadPool` / `SemaphoreSlim` limiting concurrency
2. **Analysis** — N pings per online device, starts as soon as device responds (pipeline, not batch)
3. **Offline monitor** — background task re-probes offline IPs every 5 s; promotes online ones into analysis
4. **Internet pings** — parallel background task, same ping infrastructure

### 5.3 Network Info
- Own IP: UDP socket trick (no packet sent)
- Gateway: `GetBestRoute` P/Invoke
- MAC + subnet mask: `GetAdaptersInfo` P/Invoke
- DNS: `GetNetworkParams` P/Invoke
- Full PowerShell query runs in background to fill interface name

### 5.4 Device Grouping
- Same logic as Python: group by hostname prefix pattern or MAC OUI prefix
- Minimum 2 devices to form a group
- Gateway always gets its own group (colored by vendor: UniFi=blue, FritzBox=red, other=green)
- Colors assigned by max-diversity algorithm (same as Python)

### 5.5 MAC Vendor Lookup
- `arp -a` / ARP table for local devices
- NetBIOS (`nbtstat -A`) for hostname + MAC across subnets
- OUI prefix → vendor name lookup via bundled `oui.txt` (IEEE public list, ~5 MB, embedded as resource)

---

## 6. Known-Devices Database

- SQLite file: `scanner.db` next to the `.exe`
- Schema identical to Python version: `known_devices(network_mac, mac, ip, hostname, last_seen)`
- On scan start: gateway MAC looked up → known devices pre-loaded as OFFLINE placeholders
- On scan end: all seen devices upserted with `last_seen` timestamp
- UI: "Known network" indicator in sidebar when gateway MAC is recognized

---

## 7. Export

### TXT Report
- Written to `output_directory` (default `./Scans/`) on scan completion (or on Q-stop)
- Filename: `network_scan_YYYYMMDD_HHmmss-<gateway-slug>.txt`
- Same plain-text format as Python version

### CSV Export
- Optional, same filename prefix with `.csv` extension
- Columns: `ip, status, hostname, vendor, mac, ping_avg_ms, ping_min_ms, ping_max_ms, last_ping_ms, pings_done, pings_target, from_db`

---

## 8. Data Flow

```
UI Thread (WPF Dispatcher)
  └─ MainViewModel
       ├─ ScanCommand (async, CancellationToken)
       │    ├─ NetworkDetector  → fills SidebarViewModel per network
       │    ├─ DiscoveryWorker  → feeds DeviceViewModel into ObservableCollection
       │    ├─ AnalysisWorker   → updates DeviceViewModel.PingStats live
       │    ├─ OfflineMonitor   → promotes devices back to analysis
       │    └─ InternetPinger   → fills InternetLatencyViewModel
       ├─ ProgressViewModel     → drives header progress bars
       └─ SettingsViewModel     → binds to settings window
```

All worker updates are marshalled back to the UI thread via `Application.Current.Dispatcher.InvokeAsync` (or `Progress<T>`). The `ObservableCollection<DeviceViewModel>` drives the DataGrid directly — no manual refresh calls.

---

## 9. Project Structure

```
IP-Scanner/
├── IP-Scanner.csproj          # .NET 8 WPF, PublishSingleFile
├── App.xaml / App.xaml.cs
├── MainWindow.xaml / .cs      # Shell: header + DataGrid + sidebar
├── ViewModels/
│   ├── MainViewModel.cs       # scan lifecycle, commands
│   ├── DeviceViewModel.cs     # one row in the DataGrid
│   ├── NetworkInfoViewModel.cs# one card in the sidebar
│   ├── ProgressViewModel.cs   # segmented progress bar state
│   └── SettingsViewModel.cs   # settings window binding
├── Views/
│   ├── SettingsWindow.xaml/.cs
│   └── Controls/
│       ├── SegmentedProgressBar.xaml  # custom control
│       └── NetworkCard.xaml           # sidebar network card
├── Core/
│   ├── Scanner/
│   │   ├── IcmpPinger.cs      # P/Invoke IcmpSendEcho
│   │   ├── NetworkDetector.cs # local IP/MAC/GW/DNS via P/Invoke
│   │   ├── ArpHelper.cs       # ARP + NetBIOS lookup
│   │   ├── ScanEngine.cs      # discovery + analysis pipeline
│   │   └── DeviceGrouper.cs   # grouping logic + color assignment
│   ├── Data/
│   │   ├── KnownDevicesDb.cs  # SQLite access
│   │   └── ConfigManager.cs   # read/write ip_scanner.conf
│   └── Export/
│       ├── TxtExporter.cs
│       └── CsvExporter.cs
├── Resources/
│   ├── Styles.xaml            # Catppuccin dark theme
│   └── network.ico
├── scanner.db                 # created at runtime
├── ip_scanner.conf            # created at runtime (template on first run)
└── Scans/                     # created at runtime
```

---

## 10. Non-Goals (out of scope)

- Linux / macOS support (Windows only, WPF is Windows-only)
- Console / terminal output mode
- Plugin system
- Remote scanning (only local subnets)

---

## 11. Distribution

```
dotnet publish IP-Scanner.csproj \
  -r win-x64 \
  --self-contained \
  -c Release \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o dist/
```

Output: `dist/IP-Scanner.exe` — single file, ~120 MB (trimmed ~60 MB with `PublishTrimmed`).  
Runtime files (`scanner.db`, `ip_scanner.conf`, `Scans/`) are created next to the `.exe` on first run.
