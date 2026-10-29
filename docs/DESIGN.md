# docs/DESIGN.md - IP-Scanner design conventions

Read before adding a UI surface or pattern; record new decisions here.

## Colors and theming

- Palette: Catppuccin Mocha (dark) / Latte-ish (light). Keys in `Theme.Dark.xaml` and `Theme.Light.xaml`, **identical key set**: `Bg`, `BgAlt`, `BgDark`, `Surface`, `Surface2`, `Text`, `Subtext`, `Green`, `Lime`, `Red`, `Blue`, `Mauve`, `Peach`, `Yellow`, `Teal`, `GrayBlock`, `MidGray`, `RogueBg`, `OverlayDim`, `OnAccentText`, `OnDangerText`. `ThemeKeysMatchTests` asserts parity. The progress-bar colors are config values (`ScanConfig.ColorOnline` etc.), not theme keys.
- XAML color references are `{DynamicResource Name}`, never `{StaticResource}`: `ThemeManager.Switch()` swaps the merged dictionary at runtime and a static reference would freeze.
- C#-computed colors (heat maps, status) live in `Core/Palette.cs` with the same hex values as the themes. A ViewModel caching such a color needs an explicit `RefreshTheme()` after a theme switch (see `DeviceViewModel`, `NetworkInfoViewModel`, `MainViewModel`).
- New color: same key in both theme files in the same commit, or the light theme falls back to nothing.
- Text/icon color on an accent surface (button, checked pill/checkbox) is its own key (`OnAccentText`, `OnDangerText`), never a hex in `Styles.xaml` (it can't swap per theme). `ThemeKeysMatchTests.Styles_HaveNoHardcodedHexColors` gates this.

### Contrast (WCAG AA: 4.5:1 text, 3:1 large/bold >=19px and UI components)

| Pair | Dark | Light | Verdict |
|---|---|---|---|
| `OnAccentText` on `Green` (AccentButton, checked pill/checkbox) | 13.19:1 | 5.87:1 | pass |
| `OnDangerText` on `Red` (DangerButton) | 8.47:1 | 5.43:1 | pass |
| `Text` on `Bg` / `BgAlt` / `Surface` | 11.34 / 10.41 / 8.69 | 7.06 / 6.57 / 5.17 | pass |
| `Blue` / `Mauve` / `Red` on `Bg` | 7.79 / 8.07 / 7.08 | 4.34 / 4.79 / 4.80 | light `Blue` is a marginal fail: used only at 15px Bold for headers/labels. Left as is; darkening it is a palette change (owner decision) |
| `Green` / `Peach` on `Bg` | 11.03 / 9.27 | 2.96 / 2.64 | light fail for body text: only large/bold status glyphs and short badges. **Re-check this row before using either for readable light-theme text** |
| `Subtext` on `Bg` / `BgAlt` | 3.36 / 3.08 | 2.83 / 2.63 | fail, accepted: meta text only |
| `MidGray` on `Bg` | 2.46 | 2.30 | fail, accepted: meta text only |

The `Subtext`/`MidGray` and light `Green`/`Peach` rows are a known AA gap; fixing it means an app-wide recolor (owner decision).

## Window layout

- `MainWindow` has no outer scrolling: the header degrades via a `WrapPanel` (right-hand subnet/scan controls) and a stretching center progress block. Only the `DataGrid` scrolls horizontally, on its own.
- `ScaledContent` rows: header, the packet reader info bar (`DriverBar`, collapsed when the reader is available or dismissed), main grid.
- `MinWidth="700"` fits the whole header with no element offscreen (checked on the test VM via UI-Automation dump; WPF renders blank in VM screenshots). Re-verify if the header gains a control.

## Window chrome

- `MainWindow` uses `WindowChrome` (CaptionHeight 36), no native title bar. Row 0 of `RootLayout` is the 36px title row (logo, title, minimize/maximize/close). Caption buttons (`CaptionButton`/`CaptionCloseButton`, 46x36) need `WindowChrome.IsHitTestVisibleInChrome="True"` or the drag region swallows clicks.
- Maximized WindowChrome content clips past the screen edge: set `RootLayout.Margin` to `SystemParameters.WindowResizeBorderThickness` only while `WindowState == Maximized`, else zero (`MainWindow.Chrome.cs`). Any custom-chrome window needs it (`ToolsWindow` does).
- `WindowTheme.Apply` (DWM tint) targets the invisible native frame / Alt+Tab preview only.

## Popup depth

- No `DropShadowEffect` anywhere. Popups and menus (`ContextMenu`, `ColorPickerPopup`, `CellEditPopup`, `EntryEditPopup`) signal depth with a 1px `Surface` border, `BgDark` fill and `CornerRadius` 8-10.
- The tray menu is a WinForms `ContextMenuStrip` (`MainWindow.Tray.cs`) and deliberately keeps the native look: a system-tray affordance, not worth a custom renderer.

## Design tokens

A new surface uses these values only.

- **Spacing:** 4 / 8 / 12 / 16 / 22 only. Odd legacy values migrate when touched.
- **Type:** 10 (meta) / 11 (secondary) / 12 (body) / 13 (form) / 15 (section) / 20 (title). Weights Normal / SemiBold / Bold only.
- **Radii:** 4 (chip) / 6 (input, button) / 7 (pill) / 9-10 (popup, panel).
- **Icons:** Material Design Icons paths as `Geometry` resources in `Resources/Icons.xaml` (Apache 2.0, attribution on the About page, 24x24 viewBox, `Stretch="Uniform"`). Current set: device types `IconServer`, `IconRouter`, `IconPrinter`, `IconCamera`, `IconPhone`, `IconTv`, `IconNas`, `IconPc`, `IconUnknown`, picked by `IconKeyConverter`. A new icon is one more `Geometry` there. No other source, no vendor logos.
- **States:** hover = `Surface2` border or 0.85 opacity; focus = `Mauve` border + visible focus ring; disabled = 0.4 opacity; armed-destructive = `DangerButton`.
- **Text on surface:** `Text` on `Bg`/`BgAlt`/`BgDark`/`Surface`; `Subtext` on `Bg`/`BgDark`; `MidGray` for meta text only. Never for a value the user must read.

## Row details, pending cards, notices

- **Lazy row details:** the expanded device row is `DeviceDetailTemplate` (`MainWindow.xaml`), set as the `ContentTemplate` of a `ContentControl` only while `DeviceViewModel.ShowGraph` is true. `RowDetailsTemplate` itself holds just that empty control, so a collapsed row never builds the detail tree. Opening the detail starts the graph build and the stored-port load, closing it cancels the running service/port probes. Heavy per-row content goes into the detail template, never into the row.
- **Detail layout:** two fixed-width columns, so the detail stays compact and never stretches with the grid's column widths. Left: latency graph, vendor/MAC, then the note (2 lines high, grows to about 4 on input). Right (360 wide): service actions, filter and a virtualized one-port-per-row list capped at about six rows that scrolls, so a full 65535-port sweep neither widens the detail nor realizes every row. Surfaces `main.devicedetail` and `main.devicedetail.ports` render it.
- **Pending network card:** a subnet added in the sidebar before any scan shows a placeholder `NetworkInfoViewModel` (`IsPending`, built in `MainViewModel` from `_subnetMeta`). `ReconcileNetworks` matches cards by prefix, so the first scan upgrades the same instance in place (graph history and armed-delete state survive); a pending card whose entry was deleted again is dropped. Use the same prefix-keyed diff for any new card list, never clear-and-rebuild.
- **Notice banner (the toast host):** there is no separate toast system. `MainWindow.ShowNotice(text, actionLabel, action, sticky)` drives the single `NoticeBanner` (bottom-center, popup look, one optional action button, hides after 6 s unless `sticky`). A sticky notice is held while a transient one shows and comes back after. Use it for undo hints and short results instead of a message box.
- **Settings page structure:** the settings overlay is a left `SettingsNavList` (`Scan`, `Display`, `Internet`, `Graphs`, `Data`, `App`, `About`, `Help`) next to one page panel per tag. `OnSettingsNavChanged` (`MainWindow.Settings.cs`) shows exactly the panel whose name matches the selected `Tag` and collapses the rest. A new page is a nav item with a tag, a panel, and one line in that method; every nav item has a tooltip. Values are read back by `ReadSettings()`, see Persistence.

## Tools window

- `ToolsWindow` (Wake-on-LAN, port check, DNS lookup, ping/route, subnet calculator with subnet split, MAC address (vendor lookup, address table), certificate, and the scan pages host discovery, service detection, OS estimate, checks, scan export) is a **non-modal, single-instance** window: `MainWindow.OnToolsClick` shows it with the main window as owner, a second click only activates it (`_toolsWindow` is cleared on `Closed`). It follows theme, text size and reading direction of the main window (`RefreshTheme`, `ScaleAndFlow`) and remembers the last page in `ToolsLastPage`.
- Pages are **lazy and keep running**: `BuildPage(tag)` runs on first selection only and the page is cached in `_pages`, so switching the nav does not cancel a running scan, ping or lookup. Each page has its own ViewModel in `ToolsViewModel`. Closing the window stops every tool's in-flight work.
- Shared layout of the scan pages (`ScanToolPage`, one view, one ViewModel per tool in `ScanToolPages.cs`): input row (target box, start/stop button), one status/validation line, options, result `DataGrid`, and a bottom row for save/copy. New scan-style tool: a ViewModel on `ScanToolViewModel` and a `BuildPage` case, not a new layout. Tool pages call into the main window only through `MainWindow` internals (`Config`, persist helpers).

## Sidebar

- Order: profile row, exports, findings (only when any), networks, pinned IPs, internet hosts + internet latency graph.
- The profile row is first because a profile owns the lists below. With only the default profile it shows just "+"; with a named profile it adds the label, combo box and (for a named one) the rename button. The settings header carries only theme, language, reset, close.

## MVVM patterns

- `ObservableObject`: `Raise()` plain notify; `SetProperty(ref field, value)` adds a no-op-on-equal guard.
- Code-behind is topic partials of `MainWindow` (`.Archive`, `.Chrome`, `.ColorPicker`, `.DeviceRows`, `.Export`, `.Graphs`, `.Import`, `.Log`, `.Profiles`, `.Review`, `.Settings`, `.Sidebar`, `.Tools`, `.Tray`, `.Validation`). It owns dialogs, file pickers and anything needing a `Window`; display logic belongs in a ViewModel.
- `IDeletable` + `ArmThenConfirm` (arm-then-confirm delete state) is a documented exception to strict MVVM. The sidebar lists (Networks, Pinned IPs, Internet Hosts) share it. Reset-Defaults (`MainWindow.Settings.cs`) and the entry-edit popup's delete button have their own implementations, not yet consolidated. Reuse `ArmThenConfirm` for any new click-to-arm, click-again-to-confirm control.
- `SidebarController` (in `ViewModels`) holds the sidebar's add / edit / delete / arm / undo logic (no WPF types; config and side effects via `SidebarHooks`); the `MainWindow.Sidebar.cs` click handlers are one-line calls into it. Tested in `SidebarControllerTests`. New sidebar behaviour goes there, not into the handlers.
- Editable value with display and raw forms (e.g. `NetworkInfoViewModel.Title`/`RawName`): display property falls back from the raw field; `ApplyMeta(...)` updates the raw field and re-raises only what changed.
- A property depending on `Loc`/language is not re-raised automatically. Add it to `RefreshLanguage()` (`ProgressViewModel`, `NetworkInfoViewModel`, `MainViewModel`), not a one-off `if`; tray equivalent: `MainWindow.RefreshTrayLanguage()`. Both run from `RefreshDynamicTexts()`.

## Shared XAML styles

Full list in `Resources/Styles.xaml`:

- `SettingNumberBox` / `SettingUnitLabel`: settings number field + fixed-width unit suffix.
- `DeleteChipStyle`: the delete chip of the three sidebar lists (bound to `IsDeleteArmed`).
- `AddRowPlusStyle` / `AddRowLabelStyle`: the "+ Add ..." row.
- `ZoomLabelActiveWhenZoomed` / `ZoomLabelActiveWhenNotZoomed`: the "1m / 30s" graph-zoom toggle (bound to `IsZoomed`).
- `AccentButton` / `DangerButton` / `GhostButton` / `IconButton`: button variants; `DangerButton` is also the armed state of a two-click confirm.
- An inline block repeated **twice** becomes a style immediately.

## Localization

- English + German are inline in `Loc.cs` via `S("<de>", "<en>")`: no JSON round-trip, always in sync.
- The other eight languages (`ar, es, fr, hi, ja, pt, ru, zh`) load from embedded `src/IpScanner.Core/Resources/lang/<code>.json`, keyed by the **exact English string**, English fallback for a missing key. `en.json` is the canonical key reference but is not loaded at runtime.
- New user-facing string: `Loc.cs` property first (EN+DE), then the identical English key in all nine JSON files including `en.json`.
- Values baked into exported files (TXT report) read from `Loc` too. The CSV export is the exception: its column headers are a stable machine-readable schema, English-only.
- Scan comparison export (`ScanCompareExport`): text follows the UI language; JSON, XML and grepable are English-only schemas. XML root `scanCompare version="1"` holds `runA`/`runB` (`id`, `startedAt` ISO 8601, `network`), `options`, `portsCompared`, `summary` (one `count type value` per change type) and `changes` with one `change` element per row (`type`, `ip`, optional `mac`, `hostname`, `before`, `after`; a missing value is an absent attribute). Grepable: `#` header line, then one line per change, tab-separated `Host: ip`, `Change: type`, `Mac:`, `Name:`, `Before:`, `After:` (`-` when empty, tabs and line breaks in values become spaces).
- Scan report formats: one result model (`ScanReport`, built by `ReportBuilder.Build`) is rendered by four writers in `ReportWriters` (`Text`, `Xml`, `Json`, `Grepable`; `ReportFormats` maps them to `txt`/`xml`/`json`/`gnmap`). The GUI exporters (`TxtExporter`, `XmlExporter`, `JsonExporter`, `GrepableExporter`) and the command line both go through `ReportFile`, so the same scan gives byte-identical files. Text follows the UI language and is UTF-8 with BOM; XML, JSON and grepable are English-only, UTF-8 without BOM. The archive compare export reuses the same codec helpers. Only CSV defuses leading `= + - @`; the other formats keep the raw value. The text report lists open and open|filtered ports; for ACK, Window and Maimon runs (`ReportMeta.ListsAllStates`) it lists every state like nmap and summarises a state that holds more than 25 ports as `Not shown: N state`.
  - JSON: top level `schemaVersion` (1), `meta` (`tool`, `toolVersion`, `startedAt` ISO 8601, `command`, `targets`, `partial`, `technique` (TCP scan type in lower case, omitted without a TCP scan), `interface`, `localIp`, `gateway`, `subnetMask`, `dns`), `hosts`, `findings`. Names are camelCase, null values are omitted. A host has `ip`, `family` (`ipv4`/`ipv6`), `status` (`ONLINE`/`OFFLINE`/`LISTED`), `hostname`, `mac`, `vendor`, `group`, `discovery`, `avgMs`/`minMs`/`maxMs`/`lastMs`, `pingsDone`, `pingsTarget`, `fromDb`, `note`, `os` (`family` windows/unixLike/networkGear/printer/media, `percent`, `fromFingerprint`, `match`, `matchPercent`, `hops`), `ports` (`port`, `protocol` tcp/udp, `state` open/closed/filtered/open|filtered/unfiltered, `reason`, `service`, `version`, `confidence` confirmed/probable) and `checks` (`id`, `category`, `service`, `outcome` Found/NothingFound/NoAnswer, `findings` as key/value list). A finding has `ip`, `mac`, `kind` (macChanged/macOnMultipleIps/multipleMacsForOneIp), `detail`.
  - XML: root `scanReport` with `meta` (attributes `tool`, `version`, `startedAt`, `partial`; children `command`, `targets`, `network` with `dns` elements), `hosts/host` (the same fields as attributes; `note`, `os`, `ports/port`, `checks/check/finding` as children) and `findings/finding`. The schema is `src/IpScanner.Core/Resources/scan-report.xsd` (embedded, `ReportWriters.XmlSchema()`); it describes this tool's own output and is not an nmap DTD. Characters illegal in XML are dropped, line breaks in values are kept as character references.
  - Grepable: `#` comment lines (header, `# Finding:`, `# Done: N hosts`) and exactly one line per host, tab-separated: `Host: ip (name)`, `Status: Up|Down|Listed`, `MAC:`, `Vendor:`, `Ports: number/state/protocol//service//version/` joined by `, `, `OS:`, optional `Checks:` and `Note:`. Control characters (tabs, line breaks), `/` and `,` inside values become spaces.
- A `Loc` value read once into a field/cache (not a live binding) needs its own refresh hook, see `RefreshLanguage()` above.

## Persistence conventions

- Every relative path (`DatabasePath`, `OutputDirectory`, `ConfigDirectory`) resolves through `ScanConfig.ResolvePath()`, anchored at `AppContext.BaseDirectory`, never `Environment.CurrentDirectory`: a shortcut "Start in" folder or "Run as Administrator" must not move the app's files.
- `ConfigManager.Save` writes atomically (`AtomicFile`: temp file + `File.Replace`, `.bak` of the previous generation). Never a direct `File.WriteAllText` on the real path.
- Config saves merge known values into the existing file, keeping hand-written comments, blank lines and unknown keys. Unsafe input requires the themed overwrite/cancel choice before the replace. Descriptions shared with settings tooltips are mapped by key in `Loc.ConfigDescriptions`.
- A new `ScanConfig` field needs: the property (sensible default), an entry in `ScanConfig.Bounds` if it is a clamped number, a case in `ConfigManager.Load`'s switch, a line with a one-line comment in `ConfigManager.Save`'s writer, and its key in `ConfigManager.RequiredKeys` (so an old `.conf` missing it gets a migration rewrite instead of defaulting forever).
- A value an invalid (red-bordered) input could hold is never written into `ScanConfig`; keep the last known-good value (see `ReadSettings()`'s `IsValidPathText` guards).
- Files the app writes (config, reports, database, key) go through `FileProtection`: a folder the app created (it holds the `.ip-scanner` marker) is locked to the current user, SYSTEM and Administrators; any other existing folder keeps its permissions and only the files get the private ACL (`RestrictFile`). Callers use `PrepareDirectory` before and `RestrictFile` after the write when it returns false. Config, export and log stay unencrypted by design; the known-devices DB (`scanner.db`) and manual renames (`overrides.dat`) are additionally sealed with `LocalCrypto` (AesGcm, DPAPI-protected per-installation key), see `docs/RULES.md`.
- Sensitive settings (SNMP community, recent Wake-on-LAN targets, profiles) live in the sealed `settings.dat` next to `scanner.db` (`SealedSettings`, same key as `overrides.dat`); the conf never carries them and a save removes legacy copies. An unavailable key falls back to in-memory defaults and never overwrites the sealed file. Sealed or backed-up files are written through `AtomicFile` (temp file, `.bak`); an unreadable file is moved aside (`.unreadable-<time>`), the backup is used, and the notice banner reports it once (`LoadProblem`).
- Plaintext of `overrides.dat` starts with `# overrides-v2`. The sixth tab-separated field is a base64 note so line breaks cannot split a row; the reader still accepts headerless three- to five-field legacy rows and rejects unknown future versions.

## Async/threading conventions

- A ping/probe loop sharing mutable state with another thread (recheck, analysis, burst probe) goes through the owner's lock: `Device.RecordPing`'s `lock (this)`; `_rowLock`/`_graphLock` for graph sample buffers in `DeviceViewModel`/`NetworkInfoViewModel`.
- A property rebuilt from buffered samples on a timer (row/network graphs) rebuilds only when the samples changed (once/sec), not on every `Refresh()`.
- Real I/O with an external endpoint (TCP connect, TLS handshake, UDP) honors a `CancellationToken` on every awaited call, not just the outermost: a silent endpoint must never hang a scan.
- A best-effort operation allowed to fail silently gets a one-line comment (`catch { /* best-effort */ }`), never a bare empty catch.

## Command line

- One exe, one runner: `IP-Scanner.exe <args>` runs `CliApp.Run` in `src/IpScanner.Core/Cli` without a window (the exe is a window subsystem program: `Program.Main` calls `AttachConsole(ATTACH_PARENT_PROCESS)`, binds stdout and stderr to the parent console, keeps handles that were redirected to a file or pipe, never allocates a console of its own, and returns the exit code from `Main`). There is no separate console exe. cmd.exe does not wait for a window exe, so the prompt may return before the output and the exit code below only reaches the caller through `start /wait` (never combined with `>`, the redirect would apply to `start`), a batch file, or PowerShell with a pipe (`| Out-Host`, `| Out-File f`; plain `>` writes an empty file) or `(Start-Process -Wait -PassThru ...).ExitCode`; the help text says so. Debug builds keep the surface tool flags (`--render` etc.) on the GUI path. The in-app updater installs the release asset `IP-Scanner.exe` (with its `.sha256` and `.sig`), which now contains the command line too.
- `Core/` lives in the class library `src/IpScanner.Core`; the WPF project references it. `CliServices` hands the runner everything that touches the machine (ping, port runner, check runner, network info), tests pass fakes.
- The flag table `CliFlags.All` (`CliFlag.cs`) is the single source: the parser, the help text (`CliHelp.Text`) and the generated docs read it. A new flag is one table row plus its field in `CliOptions`. `CliHelp.Examples` are parsed by a test, so a help example can never go stale.
- Flags are nmap-shaped (`-sn -Pn -sL -sT -sS -sU -sF -sX -sN -sA -sW -sM -p -F --top-ports -T0..5 --min-rate --max-rate --max-retries --host-timeout -sV --version-intensity -O --checks --reason --open -iL --exclude --exclude-file -oN -oX -oJ -oG -6 -v -vv -d --interface --help --version`). Values may be attached (`-p22`, `--ports=22`). A bare `--host-timeout` number means seconds; units `ms s m h` are accepted. An unknown flag exits 2 with the nearest flag as a suggestion.
- Exit codes: 0 ok, 1 scan error (output not writable, scan type unavailable), 2 usage error, 3 stdout closed before the report was printed (for example `| head`; the report files are written first and are complete), 130 cancelled. One failing host (port scan or service check) only prints a warning on stderr; the other hosts and the report are kept. Ctrl+C stops the scan and still writes the partial result (`meta.partial` true).
- Safety gates need an explicit flag: a chosen port list (`-p`) of more than 100 ports (TCP and UDP counted separately, the same rule as the Tools port check) or a `--top-ports` count above 1000 (the default 1000 and `-F` stay ungated) `--allow-full-scan`, FIN/NULL/Xmas/ACK/Window/Maimon scans `--allow-aggressive`, intrusive checks `--allow-intrusive`, large target ranges `--allow-large`.
- Raw scan types (`-sS` and friends) report unavailable while no raw transport is wired for the CLI; they exit 1 with the reason. IPv6: `-6` is required to scan IPv6 targets (an IPv6 target without it, or an IPv4 one with it, is a usage error naming the flag). Hosts are pinged by `Ipv6Pinger` (echo, then the discovery fallbacks) and enriched from the neighbour cache; the port scanner and service detection run over IPv6 sockets unchanged. Each scan type states its IPv6 support in the capability matrix (`Capabilities.SupportsIpv6`, shown in the help); driver-only types grey out while a configured subnet or pinned entry is IPv6. Addresses are normalized to RFC 5952 (`IpAddressKey`, `AddressSyntax.Normalize`), sorted IPv4 first, then IPv6, numerically. The main table shows the address in monospace, trims long ones and carries the full address in the tooltip. Reports carry the family (`addrtype` in XML, `family` in JSON). Known IPv4-only spots: the header subnet box, the calculator, the network cards and the local-subnet check.
- Output goes through `ReportFile` (see the report formats under Localization); `-oJ -` sends one format to stdout.

## Licensing (MIT project)

- Every shipped data set must trace to a license-compatible source. `Resources/oui.txt` is regenerated from the public IEEE registration files (MA-L/MA-M/MA-S) by `scripts/generate-oui.ps1`, never copied from another tool's output.
- Service/version probe signatures, OS fingerprints and the port frequency table are written from scratch from our own measurements and public protocol docs/RFCs. Do not copy `nmap-service-probes`, `nmap-os-db`, `nmap-services` or Wireshark signature data.
- A packet-capture driver (Npcap) is detected and linked to, never bundled, downloaded or installed.
- Packet reader binding: `Wpcap` P/Invokes the seven wpcap entry points it needs (open live, compile, set filter, free code, send, next, close) through function-pointer delegates, loaded by absolute path from `System32\Npcap`. No wrapper package: the surface is tiny, a wrapper would add a dependency for nothing, and the Npcap runtime itself is never shipped. Detection (`PacketDriver`) reads the install folder and the registry once per session and again after an install (`Redetect`, `WaitForInstallAsync`), so no app restart is needed. Every driver-only capability goes through `Capabilities.Evaluate` (driver missing, administrator rights missing, IPv6 unsupported) and the UI greys it out with that reason. The info bar has its own row under the header, is dismissable per session and only opens the official download page in the browser.
- SharpZipLib 1.4.2 (MIT, package license verified) writes and reads the AES-256 (WinZip AE-2) ZIP of the full backup; a backup without a password is a plain ZIP.

## Deliberately out of scope

- DNS record types beyond A, AAAA and PTR (MX, TXT, SRV, ...) in the DNS lookup tool: .NET has no built-in resolver for them and a third-party one adds a dependency. The tool does forward (`Dns.GetHostAddressesAsync`) and reverse (`Dns.GetHostEntryAsync`) lookups only.

## Doc upkeep

- `docs/RULES.md`: invariants that must never regress; add one when a bug shows a mechanism was fragile enough to need a standing guard, and name the test that enforces it.
- This file: a new surface pattern or token is recorded here in the commit that introduces it.
- `WORK.md` is the live plan; `WORK-ARCHIVE.md` is cold storage of finished items, read only on demand.
