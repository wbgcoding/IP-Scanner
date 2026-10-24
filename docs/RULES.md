# docs/RULES.md - invariants that must never regress

Each of these was a real, reproduced bug at some point. Every rule names the
test or mechanism that enforces it, or says "review only" - if you touch the
code near a rule, run that test (or add one) before you call the change done. Add a new rule here the
moment a fix proves a mechanism was fragile enough to need a standing guard.

## Localization / process-wide state

- **`Loc.German` is one process-wide static flag.** Any test that reads or
  sets it must live in the `[Collection("Localization")]` xUnit collection
  (`DisableParallelization = true`) or it can race against another test
  class doing the same - the failure only shows up in a full run, never in
  isolation. Guarded by `LocalizationTests`, `DeviceViewModelTests`,
  `TxtExporterTests` all sharing that collection.
- **Every new user-facing `Loc.cs` string needs the same English key added to
  all nine `src/IpScanner.Core/Resources/lang/*.json` files**, `en.json` included. A missing key
  falls back to English silently - no crash, but also no visible symptom, so
  it's easy to forget. Guarded automatically now:
  `LocalizationTests.EveryLocKey_ExistsInEveryLanguageFile_AndNoLanguageFileHasOrphanKeys`
  reflects over every `Loc` string property (via `Loc.Collector`) and fails if
  any of the nine files is missing a key the code actually produces, or still
  carries one nothing produces any more.
- **A scan report's on-disk file name keeps its fixed `yyyyMMdd_HHmmss`
  pattern regardless of the UI language or the date/number format setting**:
  it is a sortable schema like the CSV column headers above, not UI text. Only
  the timestamp shown *inside* the report follows the configured format
  (`MainViewModel.RunScanInternal`'s `displayTime`, via `CultureInfo.CurrentCulture`).
  The same holds for every other generated file name and stored stamp: backup
  `yyyyMMdd-HHmm`, `pre-restore-yyyyMMdd-HHmmss`, `.unreadable-yyyyMMdd-HHmmss`, compare
  export `yyyyMMdd-HHmm`, and the database `yyyyMMdd_HHmmss` stamps (parsed back with
  `ParseExact`, so a localized pattern would orphan old rows). Format them with a fixed
  pattern, never the current culture. Enforced by review only.
- **An exported/written value (TXT report, tray icon, progress legend) that
  is set once from `Loc` instead of live-bound must be explicitly refreshed
  on a language switch** - it will not update on its own. Guarded by
  `LocalizationTests.TxtReport_FollowsActiveLanguage_NotFixedEnglish` and the
  `RefreshLanguage()`/`RefreshTrayLanguage()` wiring in `RefreshDynamicTexts()`.

## Config file parsing and persistence

- **`ConfigManager`'s inline-comment stripper only treats `#`/`;` as a
  comment start when preceded by whitespace or at position 0** - never mid
  value. A Windows path like `C:\Reports#2\Scans` must round-trip intact.
  Guarded by `ConfigManagerTests`.
- **`ConfigManager.Save` must write atomically** (temp file + `File.Replace`
  with a `.bak` of the previous generation) - never a direct
  `File.WriteAllText` over the live file. A crash mid-write must corrupt only
  the throwaway temp file.
- **A new `ScanConfig` field must be added to `ConfigManager.RequiredKeys`**
  so an old `.conf` missing it triggers `MigrateIfNeeded`'s rewrite instead of
  silently defaulting forever with no on-disk trace. Guarded by
  `MigrationTests`.
- **Every relative path (`DatabasePath`, `OutputDirectory`,
  `ConfigDirectory`) resolves via `ScanConfig.ResolvePath` against
  `AppContext.BaseDirectory`, never `Environment.CurrentDirectory`.** A
  shortcut with a different "Start in" folder must not change where the app
  reads/writes.
- **A path flagged invalid (red border) by `ValidatePath` must never be
  written into the live `ScanConfig`** - keep the last known-good value
  instead of persisting a value that only fails later, at scan/export time.

## Network parsing and export

- **`NetEntry.Parse`'s trailing hex-looking token only counts as a color if
  it contains at least one A-F letter.** A bare 6-digit numeric suffix (e.g.
  a name ending in "123456") is data, not a color, and must not be
  swallowed. New entries write an unambiguous `~RRGGBB` marker; old bare-hex
  files still load under the letter-required rule.
- **CSV export must defuse formula injection before RFC 4180 escaping** - a
  hostname/name starting with `=`, `+`, `-`, `@`, tab or CR gets a leading
  apostrophe. Untrusted input (hostnames from other devices on the network)
  must never reach Excel/Sheets as an executable formula.
- **CSV column headers stay English/machine-stable by design** - they are a
  schema, not UI text; do not localize them (the TXT report is the one that
  must follow the UI language instead).
- **Every address that is stored, shown, compared or sorted goes through `IpAddressKey`** (family, 128-bit value, RFC 5952 text,
  zone). Never compare addresses as strings and never sort them with `OrderBy(ip)`; use `IpAddressKey.TextComparer` or the
  device `IpSortKey`. IPv4 sorts first, then IPv6, then non-address text. Entry fields normalize on save
  (`AddressSyntax.Normalize`), so `2001:DB8:0:0::10` and `2001:db8::10` are one host.

## Network I/O

- **`CertificateToolViewModel`'s TLS validation callback accepting every certificate
  (recording `SslPolicyErrors` instead of rejecting on them) is deliberate, not a bug** -
  it is a certificate *inspection* tool: the errors are the result the user
  came to see, not something to hide by refusing the handshake. This is the only place
  in the app that accepts an invalid certificate *in order to report it*. Two other probes
  accept any certificate only to read a banner or page from a device (never to trust it, never
  sending credentials or user data): `PortScanner.HttpsServerHeader` and the HTTP fetch in
  `Scanner/Checks/CheckIo.cs`; any further accept-all callback needs a rule entry here first.
  Enforced by review only (`CertificateToolViewModelTests` cover how the recorded errors are
  described, not which callbacks exist). `UpdateService`'s download path must keep strict default certificate
  validation (no custom callback at all) - the checksum/signature tests guard
  the download pipeline, and nothing here may weaken it.
- **Every UDP probe socket must be `Connect()`-ed to the probed `ip:port` before sending.**
  Without it, any other host on the LAN can spoof a reply and fake an open port - `Connect()`
  makes the OS itself drop datagrams from any other source. Guarded by
  `PortScannerTests.ScanAsync_Udp161ReplyFromDifferentAddress_IsIgnored`; `MdnsHelper.Resolve`
  needs a manual address check instead because it must also listen on the multicast group.
- **Any hostname/banner/version text read from a remote device must go through
  `NetworkText.Sanitize` before it reaches a report, the database, or the UI** - it strips
  control characters and caps the length; a hostile device can put anything in its own
  advertisement. Centralized at `ScanEngine.ApplyEnrichResult` (DNS/mDNS/NetBIOS) and
  `PortScanner.Truncate` (banner grab), not at each individual resolver.
- **Every log write goes through the `Log` class** (`Core/Log.cs`: `Error`, `Warn`, `Info`,
  `Debug`) - the one file (`error.log` next to the exe), the one rotation, the one in-memory
  ring buffer the log page reads. Warn and Error are always written; Info and Debug only while
  the verbose setting is on, so an expected condition (offline update check) is Info, never Warn.
  A second call site writing its own `File.AppendAllText("error.log",
  ...)` resolves against `Environment.CurrentDirectory` (wherever the process was started from,
  not necessarily the exe's folder) and has no size limit at all. Enforced by `LogTests`
  (buffering, rotation, levels, concurrent writes) for the class itself; a second writer is
  review only.
- **Every awaited step of a TCP/TLS probe must receive the caller's
  `CancellationToken`, not just the outermost `ConnectAsync`.** A device that
  accepts the TCP connection but never completes (or never starts) a TLS
  handshake must not hang the entire scan. Guarded by
  `PortScannerTests.ScanAsync_DeviceAcceptsTcpButNeverSpeaksTls_DoesNotHangForever`.
- **`PortScanner.ScanFullRangeAsync`/`ScanRangeAsync` must never throw on
  cancellation** - a user-cancelled full port scan returns whatever partial
  results it already found, not an exception. Guarded by
  `ScanRangeAsync_AlreadyCancelled_ReturnsQuicklyWithoutThrowing`.
- **A device's displayed "N/target" ping count only increments for pings
  that belong to its current counted phase.** Offline-recheck probes and
  offline-burst follow-ups pass `countsTowardTarget: false`; `TargetPings`
  is never reassigned without an immediately preceding `ResetPingCount()`.
  Otherwise the count can exceed its own target after a reconnect. Guarded
  by `DeviceTests.RecordPing_NotCountedTowardTarget_*` and `ResetPingCount_*`.
- **The overall planned-ping/-device denominator must include pinned IPs
  outside every scanned subnet** - they are still pinged
  (`ScanEngine.ScanAsync` adds them), so the UI total must count them too or
  the live counter can run past its own denominator.
- **`MainViewModel.RunScanCore` unhooks `engine.DeviceUpdated`/`DeviceRemoved`/
  `Progress.Changed` in a `finally` before returning.** A new scan builds a new
  `ScanEngine`, but the old one's fire-and-forget follow-ups (offline-burst
  probes, straggling analysis) can still fire after `ScanAsync` returns - an
  unhooked handler would let a stopped run's stale device write into the row
  the next run just built for the same IP.
- **`ScanEngine.AnalyzeDeviceAsync` releases its `analyzing` bookkeeping entry
  on every normal completion, not only when the device ends up offline.**
  Only skip it on a cancelled run. Leaving an online-finished device marked
  "analyzing" forever would make it permanently invisible to
  `RecheckOfflineLoopAsync`, which only ever looks at IPs absent from that set.
- **`ScanEngine.TrackMacOrEvict`'s read-check-write against `_macToIp`/
  `_devices` runs under a lock** - two enrichment threads resolving the same
  MAC near-simultaneously must not race into evicting an entry twice or
  missing an eviction.

## UI refresh and performance

- **A per-row/per-network latency graph rebuilds only when its buffered
  samples actually changed (the once-a-second timer tick, or the moment its
  detail view opens) - never from a generic `Refresh()` that can fire many
  times per second under load.** Recomputing an unchanged polyline on every
  ping is pure waste at scale.
- **Opening a device's detail view while it is offline must not leave its
  service list empty forever.** `DeviceViewModel.Refresh()` retries
  `ScanServices()` the moment the device is seen online again while the view
  is still open. Guarded by
  `ShowGraph_OpenedWhileOffline_RetriesServiceScanOnceOnline`.
- **`OnDeviceRowClick` (`MainWindow.Sidebar.cs`) must check for a
  `DataGridDetailsPresenter` ancestor before its "close the open graph on any
  row click" logic.** Without that check, every control inside the expanded
  row detail (scan-all-ports, the cancel ✕, service chips) gets its click
  swallowed by the graph-close handler instead of acting - one bug, three
  symptoms. The same goes for any new row-level click, preview-click or drag handler on the
  device grid: it must let clicks that start inside the detail view through. Enforced by
  review only.
- **`Theme.Dark.xaml` and `Theme.Light.xaml` must define the exact same set
  of resource keys.** A color used via `{DynamicResource}` that exists in
  only one file breaks silently (falls back to nothing) in the other theme.
- **Every color in XAML is `{DynamicResource}`, never `{StaticResource}`**:
  `StaticResource` freezes at first load and breaks live theme switching.
- **A named (`x:Key`ed) style whose `TargetType` also has an implicit style
  (`TextBox`, `TextBlock`, `Button`, `ComboBox`, `CheckBox`) must set
  `BasedOn="{StaticResource {x:Type ...}}"`** - without it the control falls
  back to the unstyled default WPF chrome (white boxes, light gutters) even
  though every other control of that type looks correct. `Styles.xaml:131`
  (`SettingNumberBox`) shipped without it for a release. Enforced by review only
  (`XamlLocalValueTriggerTests` guards the related local-value-beats-trigger defect, not this).
- **`ApplyUiScale`'s text-scale `LayoutTransform` must never sit on the
  Window's whole `RootLayout`.** It goes on `ScaledContent` (header + main
  content) and `SettingsOverlay` only, both wrapped by a `ScrollViewer`
  (`HorizontalScrollBarVisibility="Auto"`) - the title-bar row stays outside
  both, unscaled and unscrolled, so WindowChrome dragging never shares space
  with a scrollable region again (the "bottom drag bar" regression:
  removing the *only* ScrollViewer that existed also silently removed 200%
  scale's overflow protection, since it was the same element).
  Checked on the test VM (UIA `dump`, WPF screenshots blank
  there): at 100% scale/1320px default width no header element is
  `{offscreen}`; at 200% scale/1320px width no header or Settings-overlay
  element is `{offscreen}` either, only intentional inner scrollers (DataGrid
  columns, Settings' own vertical `ScrollViewer`). **Not verified:** the
  compound corner of `MinWidth=700` together with 200% scale - the VM's
  Scheduled-Task execution model isolates it from the interactive desktop, so
  a raw `SetWindowPos`/`MainWindowHandle` resize from an ssh session sees no
  window handle at all (a session-isolation gap, not proof the app is fine),
  and the VM tooling has no dedicated resize verb to work around it. Re-verify
  this corner once such a verb exists, or test it by hand.

## Data integrity

- **Saving the configuration must not discard hand-written comments, blank lines
  or unknown keys.** Unsafe merges (malformed lines, duplicate keys or invalid
  encoding) require an explicit overwrite choice; cancel must leave the file
  and its backup untouched. The normal save remains atomic with a `.bak`.

- **A note stored under a device MAC must be applied when that MAC resolves after
  the row was created, before reports read the device.** An untouched note editor
  must not write an empty draft back on focus loss; a genuinely changed draft
  still saves. Guarded by `LateMac_ReappliesStoredNoteBeforeExport_AndUntouchedDraftDoesNotEraseIt`.
- **Ctrl+C in a note editor inside the device table copies selected text, not
  the selected device row.** Guarded by `CopyRowShortcut_LeavesTextEditorsToHandleCopy`.

- **`MacVendorLookup`'s embedded `oui.txt` must stay the real IEEE list, not
  a stub.** Guarded by `MacVendorLookupTests.Lookup_RealDatabase_HasThousandsOfEntries`
  (`EntryCount > 30000`) - this test existing at all is the point: it fires
  if the database is ever swapped back to a placeholder.
- **`gitleaks` runs clean over the working tree and full history before any
  release.**
- **Every file the app writes gets its ACL, none is created with inherited permissions.**
  Config, reports and exports, backups, the log, the database and the key all go through
  `FileProtection`: `PrepareDirectory` for the folder, then `CreatePrivateFile` (private from the
  first byte, used by `AtomicFile`, `BackupService`, `Log`) or `RestrictFile` / `WriteReport` after
  the write. A folder the app did not create keeps its own ACL and only the file is restricted. A
  new writer calls the same helpers and never a bare `File.WriteAllText` / `File.Create` on a
  user-visible path. Enforced by `FileProtectionTests` (`CreatePrivateFile_StartsWithThePrivateAcl_AndReplacesAnExistingFile`,
  `ConfigSave_IntoExistingUnmarkedFolder_KeepsFolderAcl_AndRestrictsTheFile`,
  `NotOwnedFolder_OnlyTheFileGetsAnExplicitAcl_FolderAclUnchanged`); that a new call site uses the
  helper is review only.
- **`scanner.db` and `overrides.dat` carry no readable device data at rest.**
  Every MAC/IP/network-MAC becomes a one-way `LocalCrypto.Token` (HMAC) column; the
  actual values live only inside an `AesGcm`-sealed blob (`known_devices_v2.payload`,
  the whole body of `overrides.dat`). The key (`scanner.key`, DPAPI-protected,
  `DataProtectionScope.CurrentUser`) never leaves the machine and is never asked of the
  user. `ip_scanner.conf` (hand-editable, drives items 22/23), `Scans\*` reports (the
  export) and `error.log` (diagnostics) stay intentionally plain - folder-restricted via
  `FileProtection.RestrictToCurrentUser` like before, not encrypted. A test opens the raw
  `.db` file as bytes and asserts none of a known IP/MAC/hostname appear
  (`KnownDevicesDbTests.Save_StoresNoReadablePlainText_InTheRawDatabaseFile`).
- **Every test that constructs a `KnownDevicesDb` needs its own directory** (never the
  flat `%TEMP%` root) - `scanner.key` sits next to the `.db` file, so two databases
  sharing a folder share a key file too; unrelated tests (or test *classes*, which xUnit
  runs in parallel by default) racing to create/read the same key file produced
  widespread, hard-to-reproduce failures across the whole suite the first time this was
  missed. `Directory.CreateTempSubdirectory()` per test, matching `LocalCryptoTests`.
- **A database sealed under a different Windows account's key is genuinely unreadable,
  not a bug.** `KnownDevicesDb.MergeFrom` throws `ForeignDatabaseException` rather than
  silently merging garbage; `KnownDevicesDb.ExportCsv`/`ImportCsv` are the only supported
  path to move device history to a different account/machine.

## Release and updates

- **Every release uploads `IP-Scanner.exe`, `IP-Scanner.exe.sha256` and `IP-Scanner.exe.sig` together.**
  `build.bat` (manual run, signing key present) writes the last two next to the exe through
  `scripts\ReleaseSigner`; the automatic session-end rebuild never signs and removes stale
  signature files. A release without the `.sig` asset is announced in the app but never
  installed from inside it (the user gets a button to the download page instead).
- **The app installs an update only after the downloaded bytes match the published checksum AND
  carry a valid ECDSA P-256 / SHA-256 signature for the public key constant in `UpdateService`.**
  While that constant is the placeholder nothing verifies. The private key lives DPAPI-protected
  outside the repository (`%APPDATA%\IP-Scanner-signing\key.bin`); `ReleaseSigner init` refuses to
  replace an existing key and runs only on an explicit command, never from `build.bat`. Guarded by
  `UpdateSignatureTests`.
- **Update downloads follow redirects by hand: at most 5 hops, every target https on
  `github.com`, `objects.githubusercontent.com` or `release-assets.githubusercontent.com`.** The
  update `HttpClientHandler` keeps `AllowAutoRedirect = false` and strict default certificate
  validation. The exe is streamed straight into the per-run, current-user-only folder with a size
  cap; if that folder cannot be restricted the update is aborted.

## Surface tool

- **A new surface (window, dialog, settings page, menu, combo box with its own list) gets its
  id in `Views/Review/ReviewSurfaces.cs` in the same change.** The tool is Debug-only and never
  runs on the host: `IP-Scanner.exe --render|--shot|--review|--ids <id|group|all> [WxH]
  [locale,...|all] [dark|light|both] [--out dir] [--scale percent]`. Ids come from `--ids`;
  groups are `g.main`, `g.settings`, `g.tools`, `g.dialog`, `g.popups`, `g.archive`, `g.log`. `render` and
  `shot` write one PNG per surface, locale and theme plus `clip-report.json` (text wider or taller
  than its slot, element bounds outside the parent) and the report's `positiveControl` must read
  `narrowFlagged: true, fitsFlagged: false`. A layout change is done only when the report for the
  touched surfaces is empty in en, de, ru and ar at the default and the minimum window size.

## Test runs

- **The default run excludes slow tests: `dotnet test IP-Scanner.slnx --filter "Category!=Slow"`.**
  A test that waits out a real timeout or sleeps longer than about one second carries
  `[Trait("Category", "Slow")]`. Security and safety guards (spoofed-reply check, probe limits,
  the full-scan safety gate) never carry it: shorten their waits with `FastProbeTimeouts` in the
  `PortScannerGate` collection instead. The default run stays under a minute on the host. Run the
  full suite, without a filter, before every release. A new needless sleep belongs behind an
  existing timer seam, not behind the Slow trait.
- **`test-all.bat` is the one command for every check.** It runs, in order: build with zero
  warnings, the full unit suite, the CLI examples (README and `--help` examples), the report
  schemas (XML against `scan-report.xsd`, JSON against its documented fields), the language keys,
  the database migration (including the checked-in old-format database), and `build.bat auto`
  with a check that `dist\IP-Scanner.exe` is fresh and `IP-Scanner.exe --help` exits 0. It prints a
  PASS/FAIL/duration table and exits non-zero when any step failed; logs go to `.tmp\test-all`.
  The whole run stays under 10 minutes; steps that would break that budget go behind
  `test-all.bat --full`. A new kind of check is a new step there, not a separate habit.
