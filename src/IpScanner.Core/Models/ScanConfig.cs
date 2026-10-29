using System.IO;

namespace IpScanner.Core.Models;

/// <summary>All scan options. Mirrors the exported .conf keys.</summary>
public sealed class ScanConfig
{
    public List<string> Subnets { get; set; } = new();
    public List<string> PinnedIps { get; set; } = new();
    public int PingCount { get; set; } = 10;                  // -1 = infinite
    public int PingIntervalMs { get; set; } = 100;
    public int OfflineAfterFailedPings { get; set; } = 5;
    /// <summary>Discovery attempts per IP in the first pass. Fixed at 1 in the app and in the config
    /// file; only the engine tests and the CLI set it.</summary>
    public int InitPingCount { get; set; } = 1;
    /// <summary>Recheck offline IPs every N seconds during a run (0 = off).</summary>
    public int OfflineRecheckSeconds { get; set; } = 5;
    /// <summary>Retry hostname lookup for devices with no name every N
    /// minutes, independent of whether a scan is running (0 = off).</summary>
    public int HostnameRetryMinutes { get; set; } = 10;
    public bool EnableInternetPing { get; set; } = true;
    /// <summary>ICMP timeout for the internet host pings (ms).</summary>
    public int InternetTimeoutMs { get; set; } = 1000;
    /// <summary>Entries: "ip" or "ip name", e.g. "8.8.8.8 Google 1".</summary>
    public List<string> InternetHosts { get; set; } =
        new() { "8.8.8.8 Google 1", "8.8.4.4 Google 2", "1.1.1.1 Cloudflare", "9.9.9.9 Quad9" };
    public bool KnownDevicesDb { get; set; } = true;
    /// <summary>Store each completed scan in the encrypted local archive.</summary>
    public bool ScanHistory { get; set; } = true;
    /// <summary>Known-devices database file (by default scanner.db next to the exe).</summary>
    public string DatabasePath { get; set; } = DefaultDatabasePath;
    public string OutputDirectory { get; set; } = DefaultOutputDirectory;
    public bool FileOutput { get; set; }          // TXT report off by default
    public bool ExportCsv { get; set; }
    /// <summary>Parallel ping workers per scan. 0 = one thread per device (max).</summary>
    public int ScanThreads { get; set; } = 100;
    /// <summary>UI/text scale in percent (100 = default).</summary>
    public int UiScalePercent { get; set; } = 100;
    /// <summary>UI language: "auto" (system, EN fallback) or the code of a shipped language, e.g. "de" or "en".</summary>
    public string Language { get; set; } = "auto";
    /// <summary>Folder where ip_scanner.conf is stored.</summary>
    public string ConfigDirectory { get; set; } = DefaultConfigDirectory;
    /// <summary>IPv4 address of the network adapter explicitly picked for
    /// scanning and pinging; empty means automatic (the OS-routed adapter).</summary>
    public string NetworkInterfaceIp { get; set; } = "";
    /// <summary>Show the latency history graphs in the sidebar.</summary>
    public bool GraphsEnabled { get; set; } = true;
    /// <summary>Show the small average-latency graph under each network.</summary>
    public bool NetworkGraphsEnabled { get; set; } = true;
    /// <summary>Visible time span of the graphs in seconds (10-300).</summary>
    public int GraphMaxSeconds { get; set; } = 300;
    /// <summary>Check GitHub for a newer release on startup.</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>Also log Info/Debug rows (not only errors and warnings) into the log file and viewer.</summary>
    public bool VerboseLog { get; set; }
    /// <summary>Automatically open the latency graph for pinned devices.</summary>
    public bool AutoGraphForPinned { get; set; }
    /// <summary>Show known devices from the database that are currently offline.</summary>
    public bool ShowOfflineFromDb { get; set; } = true;
    /// <summary>Minimize to the system tray instead of the taskbar.</summary>
    public bool MinimizeToTray { get; set; }
    /// <summary>UI color theme: "auto" (follow Windows), "dark" or "light".</summary>
    public string Theme { get; set; } = "dark";
    /// <summary>Sidebar width in pixels (<see cref="Bounds.SidebarWidthMin"/> to <see cref="Bounds.SidebarWidthMax"/>), remembered across restarts.</summary>
    public int SidebarWidth { get; set; } = Bounds.SidebarWidthDefault;
    /// <summary>Whether the sidebar's "recent files" section is expanded. Collapsed
    /// by default so the sidebar starts dense.</summary>
    public bool SidebarExportsExpanded { get; set; }
    /// <summary>The three main sidebar sections (subnets, pinned IPs,
    /// internet hosts) are collapsible too - unlike the exports section above,
    /// these carry the app's primary content, so they default to expanded.</summary>
    public bool SidebarNetworksExpanded { get; set; } = true;
    public bool SidebarPinnedExpanded { get; set; } = true;
    public bool SidebarInternetExpanded { get; set; } = true;
    /// <summary>Last column sorted plus direction, e.g. "Avg:desc";
    /// empty means no explicit sort was ever set. An unrecognized column name
    /// (from an older or newer build) is ignored when restoring.</summary>
    public string TableSort { get; set; } = "";
    /// <summary>Per-column pixel widths as "name:width,name:width,...";
    /// unknown names are ignored when restoring, missing ones keep their XAML
    /// default width.</summary>
    public string TableWidths { get; set; } = "";
    public string ManualExportScope { get; set; } = "Filtered";
    public string ManualExportFormat { get; set; } = "Csv";
    /// <summary>Last-selected category in the Settings overlay's left-hand
    /// navigation: "Scan", "Display", "Internet", "Graphs", "Data" or
    /// "App". Falls back to "Scan" for an unknown/old value.</summary>
    public string SettingsPage { get; set; } = "Scan";
    /// <summary>Last-selected tool in the Tools window's left-hand navigation:
    /// one of the page tags in ConfigManager.ToolsPages ("WakeOnLan", "Calculator",
    /// "MacTool", "ScanExport", ...). Falls back to "WakeOnLan" for an unknown/old value.</summary>
    public string ToolsLastPage { get; set; } = "WakeOnLan";
    /// <summary>Last five Wake-on-LAN targets, most recent first, one
    /// "MAC Label" entry each (label falls back to the MAC itself when the target
    /// was typed by hand rather than picked from a known device).</summary>
    public List<string> RecentWakeTargets { get; set; } = new();
    /// <summary>Latency heatmap thresholds in ms: at or below Heat1 is
    /// "excellent" (green) through above Heat4 "very bad" (red). Kept ascending
    /// on load/save - Palette.Heat assumes it, see ConfigManager.EnsureAscending.</summary>
    public double HeatMs1 { get; set; } = Bounds.HeatMs1Default;
    public double HeatMs2 { get; set; } = Bounds.HeatMs2Default;
    public double HeatMs3 { get; set; } = Bounds.HeatMs3Default;
    public double HeatMs4 { get; set; } = Bounds.HeatMs4Default;
    /// <summary>User-chosen accent color ("#RRGGBB") overriding the
    /// theme's own Mauve highlight (titles, focus border, selection, selected
    /// nav item). Empty = no override, follow the active theme like before.</summary>
    public string AccentColor { get; set; } = "";
    /// <summary>Date/number format override - "auto" (follow the OS
    /// regional format, today's behavior) or one of the ten supported language
    /// codes (its own regional conventions, independent of the UI language).</summary>
    public string DateNumberFormat { get; set; } = "auto";
    /// <summary>Compact table rows (22px) instead of normal (28px).</summary>
    public bool CompactRows { get; set; }
    /// <summary>Keys of table columns hidden by the user (group, avg,
    /// min, max, last, loss, jitter, progress, mac, vendor, ports, method, os, first_seen, last_seen).
    /// IP/status/hostname stay mandatory. Jitter, method, OS and the two seen-times start hidden
    /// by default for a brand-new config; an existing config file already has its own saved
    /// list and keeps it (the seen-times are added to it once, so they do not appear unasked).</summary>
    public List<string> HiddenColumns { get; set; } = new() { "jitter", "method", "os", "first_seen", "last_seen" };

    // Scan convenience.
    /// <summary>Auto-repeat the scan every N minutes (0/5/15/30/60). 0 = off.</summary>
    public int RepeatScanMinutes { get; set; }
    /// <summary>Tray balloon when a scan finishes.</summary>
    public bool NotifyScanDone { get; set; }
    /// <summary>Tray balloon when a new, not-yet-known device is first seen.</summary>
    public bool NotifyNewDevice { get; set; }
    /// <summary>Start the app minimized to the tray instead of showing the window.</summary>
    public bool StartMinimized { get; set; }

    // Data settings.
    /// <summary>Probe each online device's open ports (same list as the device-detail
    /// scan) and write them into the TXT/CSV report. Off by default - it turns a plain
    /// ping sweep into something that looks like a port scan to the network.</summary>
    public bool ExportPorts { get; set; }
    /// <summary>Also write an HTML report (same color theme) alongside TXT/CSV.</summary>
    public bool HtmlReport { get; set; }
    /// <summary>Known-devices DB retention in days; a row not seen within this window is
    /// purged after every scan. 0 = unbegrenzt (never purged). Default 365.</summary>
    public int DbRetentionDays { get; set; } = Bounds.DbRetentionDaysDefault;
    /// <summary>Scan-archive retention in days; runs older than this are deleted after each
    /// scan. 0 = unlimited. Independent of <see cref="DbRetentionDays"/>. Default 365.</summary>
    public int ArchiveRetentionDays { get; set; } = Bounds.ArchiveRetentionDaysDefault;
    /// <summary>Encrypted archive backups older than this are removed after each scan.
    /// Zero disables backup deletion but never turns an archive purge into delete-all.</summary>
    public int ArchiveBackupRetentionDays { get; set; } = Bounds.ArchiveBackupRetentionDaysDefault;
    /// <summary>Completed runs older than this are automatically combined into daily
    /// aggregates. Zero disables automatic compaction without deleting any history.</summary>
    public int ArchiveCompactionDays { get; set; } = Bounds.ArchiveCompactionDaysDefault;

    /// <summary>Shows the "skipped" pings legend/color square. Off by
    /// default - skipped pings are still counted either way, just not shown.</summary>
    public bool ShowSkippedLegend { get; set; }
    /// <summary>Host discovery modes, comma-separated keys (arp, icmp, icmp-ts, icmp-mask, tcp, tcp-syn, tcp-ack, udp, list, skip).</summary>
    public string DiscoveryModes { get; set; } = Scanner.Discovery.DiscoveryModes.DefaultModes;
    /// <summary>Ports the TCP connect, SYN and ACK discovery modes try.</summary>
    public string TcpPingPorts { get; set; } = Scanner.Discovery.DiscoveryModes.DefaultTcpPorts;
    /// <summary>Ports the UDP discovery mode tries.</summary>
    public string UdpPingPorts { get; set; } = Scanner.Discovery.DiscoveryModes.DefaultUdpPorts;
    /// <summary>Active OS fingerprint when a device is opened. Off by default: it probes a closed port.</summary>
    public bool OsFingerprint { get; set; }
    /// <summary>Ports of the scan setup as typed after -p: "" for the standard list, "fast" for the 100 most common
    /// ports, "-" for all, or a list such as "22,80,8000-9000".</summary>
    public string ScanPortRange { get; set; } = "";
    /// <summary>Identify the service and version behind each open port, as the -sV flag does.</summary>
    public bool PortServiceDetection { get; set; } = true;
    /// <summary>Saved scan presets, one entry per preset: "name | flags". The built-in presets are not stored.</summary>
    public List<string> ScanPresets { get; set; } = new();

    /// <summary>Name of the active profile, or "" for the built-in default
    /// profile - whose Subnets/PinnedIps/InternetHosts ARE this ScanConfig's own
    /// fields above, so existing users need no migration. Every additional named
    /// profile's three lists live in ProfileStore's own file, keyed by name.</summary>
    public string ActiveProfile { get; set; } = "";
    /// <summary>Names of the additional (non-default) profiles that exist, in
    /// display order. The default profile is never listed here.</summary>
    public List<string> ProfileNames { get; set; } = new();

    // Advanced network settings, tucked behind an expander on the Scan
    // page so the normal view stays lean - pushed into PortScanner's own mutable
    // statics by MainWindow.ApplyPortScanSettings (same pattern as the heatmap
    // thresholds on Palette).
    /// <summary>TCP/UDP "knock" timeout for the full-device port scan (ms).</summary>
    public int PortScanConnectTimeoutMs { get; set; } = Bounds.PortScanConnectTimeoutMsDefault;
    /// <summary>Version-banner read timeout once a port has answered (ms).</summary>
    public int PortScanBannerTimeoutMs { get; set; } = Bounds.PortScanBannerTimeoutMsDefault;
    /// <summary>Process-wide parallel port probes during a full port scan.</summary>
    public int PortScanParallel { get; set; } = Bounds.PortScanParallelDefault;
    /// <summary>Timing template of the port scan, 0 (paranoid) to 5 (insane); 3 is the classic behavior.</summary>
    public int PortScanTiming { get; set; } = Bounds.PortScanTimingDefault;
    /// <summary>Extra attempts for a port probe that got no answer at all.</summary>
    public int PortScanRetries { get; set; } = Bounds.PortScanRetriesDefault;
    /// <summary>Minimum gap between two port probe starts (ms), 0 for none.</summary>
    public int PortScanDelayMs { get; set; } = Bounds.PortScanDelayMsDefault;
    /// <summary>Hard cap on port probe starts per second, 0 for none.</summary>
    public int PortScanMaxRate { get; set; } = Bounds.PortScanMaxRateDefault;
    /// <summary>How hard the service detection tries on an open port, 0 (listen only) to 9; 7 is the default.</summary>
    public int PortScanIntensity { get; set; } = Bounds.PortScanIntensityDefault;
    /// <summary>Extra ports to probe alongside the built-in list. Entries: "port" or
    /// "port name", same shape as NetEntry.</summary>
    public List<string> CustomPorts { get; set; } = new();
    /// <summary>SNMPv1 community string for the sysDescr probe on port 161/UDP.</summary>
    public string SnmpCommunity { get; set; } = "public";
    /// <summary>Runtime only, never read from a file: the SNMP community and recent Wake-on-LAN
    /// targets an older conf carried are not yet safely in the sealed settings, so a conf save keeps
    /// writing them until the sealed write succeeds.</summary>
    public bool LegacySealedPending { get; set; }

    // Progress-bar colors (clickable legend squares; persisted, not shown in settings).
    public string ColorOnline { get; set; } = Palette.Green;
    public string ColorOffline { get; set; } = Palette.Red;
    public string ColorSuccess { get; set; } = Palette.Green;
    public string ColorFailed { get; set; } = Palette.Red;
    public string ColorSkipped { get; set; } = Palette.MidGray;

    /// <summary>Shared value bounds - the single source of truth for both the
    /// .conf reader (ConfigManager) and the live Settings dialog, so a typed
    /// value can never land in the running app un-clamped just because it
    /// skipped the file round-trip, and the two never silently disagree on
    /// what a field's default or limit is.</summary>
    public static class Bounds
    {
        public const int PingCountMin = 1, PingCountMax = 10_000_000, PingCountDefault = 10;
        public const int PingIntervalMsMin = 0, PingIntervalMsMax = 10_000, PingIntervalMsDefault = 100;
        public const int OfflineAfterFailedPingsMin = 1, OfflineAfterFailedPingsMax = 100, OfflineAfterFailedPingsDefault = 5;
        public const int OfflineRecheckSecondsMin = 0, OfflineRecheckSecondsMax = 3600, OfflineRecheckSecondsDefault = 5;
        public const int HostnameRetryMinutesMin = 0, HostnameRetryMinutesMax = 1440, HostnameRetryMinutesDefault = 10;
        public const int InternetTimeoutMsMin = 100, InternetTimeoutMsMax = 10_000, InternetTimeoutMsDefault = 1000;
        public const int GraphMaxSecondsMin = 10, GraphMaxSecondsMax = 300, GraphMaxSecondsDefault = 300;
        public const int ScanThreadsMin = 0, ScanThreadsMax = 1000, ScanThreadsDefault = 100;
        public const int UiScalePercentMin = 50, UiScalePercentMax = 200, UiScalePercentDefault = 100;
        public const int SidebarWidthMin = 280, SidebarWidthMax = 560, SidebarWidthDefault = 340;
        public const double HeatMsMin = 1, HeatMsMax = 10_000;
        public const double HeatMs1Default = 50, HeatMs2Default = 100, HeatMs3Default = 200, HeatMs4Default = 400;
        /// <summary>Allowed values for RepeatScanMinutes; 0 means "off".</summary>
        public static readonly int[] RepeatScanMinutesOptions = { 0, 5, 15, 30, 60 };
        /// <summary>Allowed values for DbRetentionDays; 0 means "unbegrenzt" (never purged).</summary>
        public static readonly int[] DbRetentionDaysOptions = { 30, 90, 365, 0 };
        public const int DbRetentionDaysDefault = 365;
        /// <summary>Allowed values for ArchiveRetentionDays; 0 means unlimited.</summary>
        public static readonly int[] ArchiveRetentionDaysOptions = { 30, 90, 365, 0 };
        public const int ArchiveRetentionDaysDefault = 365;
        /// <summary>Allowed values for ArchiveBackupRetentionDays; 0 means unlimited.</summary>
        public static readonly int[] ArchiveBackupRetentionDaysOptions = { 7, 30, 90, 0 };
        public const int ArchiveBackupRetentionDaysDefault = 30;
        /// <summary>Allowed values for ArchiveCompactionDays; 0 means off.</summary>
        public static readonly int[] ArchiveCompactionDaysOptions = { 0, 7, 30, 90 };
        public const int ArchiveCompactionDaysDefault = 30;

        /// <summary>The preset list with <paramref name="value"/> added when it is not one of them (a hand-edited
        /// conf value), placed among the day counts; 0 keeps its place.</summary>
        public static int[] WithCustom(int[] options, int value)
        {
            if (Array.IndexOf(options, value) >= 0) return options;
            var list = options.ToList();
            var at = list.FindIndex(o => o != 0 && o > value);
            if (at < 0) at = list.FindLastIndex(o => o != 0) + 1;
            list.Insert(at, value);
            return list.ToArray();
        }
        public const int PortScanConnectTimeoutMsMin = 100, PortScanConnectTimeoutMsMax = 5000, PortScanConnectTimeoutMsDefault = 500;
        public const int PortScanBannerTimeoutMsMin = 100, PortScanBannerTimeoutMsMax = 5000, PortScanBannerTimeoutMsDefault = 600;
        public const int PortScanParallelMin = 1, PortScanParallelMax = 500, PortScanParallelDefault = 40;
        public const int PortScanTimingMin = 0, PortScanTimingMax = 5, PortScanTimingDefault = 3;
        public const int PortScanRetriesMin = 0, PortScanRetriesMax = 5, PortScanRetriesDefault = 0;
        public const int PortScanDelayMsMin = 0, PortScanDelayMsMax = 60000, PortScanDelayMsDefault = 0;
        public const int PortScanMaxRateMin = 0, PortScanMaxRateMax = 100000, PortScanMaxRateDefault = 0;
        public const int PortScanIntensityMin = 0, PortScanIntensityMax = 9, PortScanIntensityDefault = 7;
        public const int SnmpCommunityMaxLength = 32;
    }

    public const int InfinitePingCount = -1;
    public const string DefaultDatabasePath = @".\scanner.db";
    public const string DefaultDatabaseFileName = "scanner.db";
    public const string ConfigFileName = "ip_scanner.conf";
    // Sealed (AesGcm) file, not plain text; OverrideStore migrates a legacy
    // plain "overrides.conf" next to it once, then deletes the old file.
    public const string OverridesFileName = "overrides.dat";
    public const string DefaultOutputDirectory = @".\Scans";
    public const string DefaultConfigDirectory = @".\";

    /// <summary>Resolves a possibly-relative config/db/output path to an absolute
    /// one anchored at the exe's own folder - never the process's current working
    /// directory. A shortcut with a different "Start in" folder, or "Run as
    /// Administrator" (which can change the effective CWD), would make every
    /// "./..."-relative default write somewhere other than next to the exe. An already-absolute path passes through unchanged.</summary>
    public static string ResolvePath(string path) =>
        string.IsNullOrEmpty(path) ? AppContext.BaseDirectory
            : Path.GetFullPath(path, AppContext.BaseDirectory);

    /// <summary>Copy with a different ping count (lists shared, read-only use).</summary>
    public ScanConfig CloneWith(int pingCount) => new()
    {
        Subnets = Subnets, PinnedIps = PinnedIps, PingCount = pingCount,
        PingIntervalMs = PingIntervalMs, OfflineAfterFailedPings = OfflineAfterFailedPings,
        InitPingCount = InitPingCount,
        OfflineRecheckSeconds = OfflineRecheckSeconds, HostnameRetryMinutes = HostnameRetryMinutes,
        EnableInternetPing = EnableInternetPing, InternetTimeoutMs = InternetTimeoutMs,
        InternetHosts = InternetHosts, KnownDevicesDb = KnownDevicesDb, ScanHistory = ScanHistory,
        DatabasePath = DatabasePath, OutputDirectory = OutputDirectory, FileOutput = FileOutput,
        ExportCsv = ExportCsv, ScanThreads = ScanThreads, UiScalePercent = UiScalePercent,
        Language = Language, ConfigDirectory = ConfigDirectory, NetworkInterfaceIp = NetworkInterfaceIp,
        GraphsEnabled = GraphsEnabled, GraphMaxSeconds = GraphMaxSeconds,
        NetworkGraphsEnabled = NetworkGraphsEnabled, CheckForUpdates = CheckForUpdates, VerboseLog = VerboseLog,
        AutoGraphForPinned = AutoGraphForPinned, ShowOfflineFromDb = ShowOfflineFromDb,
        MinimizeToTray = MinimizeToTray, Theme = Theme,
        ColorOnline = ColorOnline, ColorOffline = ColorOffline,
        ColorSuccess = ColorSuccess, ColorFailed = ColorFailed, ColorSkipped = ColorSkipped,
        SidebarWidth = SidebarWidth, SidebarExportsExpanded = SidebarExportsExpanded,
        TableSort = TableSort, TableWidths = TableWidths,
        ManualExportScope = ManualExportScope, ManualExportFormat = ManualExportFormat,
        SidebarNetworksExpanded = SidebarNetworksExpanded, SidebarPinnedExpanded = SidebarPinnedExpanded,
        SidebarInternetExpanded = SidebarInternetExpanded,
        SettingsPage = SettingsPage,
        HeatMs1 = HeatMs1, HeatMs2 = HeatMs2, HeatMs3 = HeatMs3, HeatMs4 = HeatMs4,
        AccentColor = AccentColor, DateNumberFormat = DateNumberFormat, CompactRows = CompactRows,
        HiddenColumns = HiddenColumns,
        RepeatScanMinutes = RepeatScanMinutes, NotifyScanDone = NotifyScanDone,
        NotifyNewDevice = NotifyNewDevice, StartMinimized = StartMinimized,
        ExportPorts = ExportPorts, HtmlReport = HtmlReport, DbRetentionDays = DbRetentionDays,
        ArchiveRetentionDays = ArchiveRetentionDays,
        ArchiveBackupRetentionDays = ArchiveBackupRetentionDays,
        ArchiveCompactionDays = ArchiveCompactionDays,
        ShowSkippedLegend = ShowSkippedLegend,
        DiscoveryModes = DiscoveryModes, TcpPingPorts = TcpPingPorts, UdpPingPorts = UdpPingPorts, OsFingerprint = OsFingerprint,
        ScanPortRange = ScanPortRange, PortServiceDetection = PortServiceDetection, ScanPresets = ScanPresets,
        ActiveProfile = ActiveProfile, ProfileNames = ProfileNames,
        PortScanConnectTimeoutMs = PortScanConnectTimeoutMs, PortScanBannerTimeoutMs = PortScanBannerTimeoutMs,
        PortScanParallel = PortScanParallel, PortScanTiming = PortScanTiming, PortScanRetries = PortScanRetries,
        PortScanDelayMs = PortScanDelayMs, PortScanMaxRate = PortScanMaxRate, PortScanIntensity = PortScanIntensity, CustomPorts = CustomPorts, SnmpCommunity = SnmpCommunity,
        ToolsLastPage = ToolsLastPage, RecentWakeTargets = RecentWakeTargets,
        LegacySealedPending = LegacySealedPending,
    };

    /// <summary>The settings reset: every option back to its default, but the sidebar lists and
    /// the profiles survive (networks, pinned IPs, internet hosts, profile names, active profile).</summary>
    public ScanConfig ResetKeepingLists() => new()
    {
        Subnets = new List<string>(Subnets),
        PinnedIps = new List<string>(PinnedIps),
        InternetHosts = new List<string>(InternetHosts),
        ProfileNames = new List<string>(ProfileNames),
        ActiveProfile = ActiveProfile,
    };

    /// <summary>Independent deep copy of every public setting; each list is a new instance.</summary>
    public ScanConfig Clone()
    {
        var c = CloneWith(PingCount);
        c.Subnets = new List<string>(Subnets);
        c.PinnedIps = new List<string>(PinnedIps);
        c.InternetHosts = new List<string>(InternetHosts);
        c.HiddenColumns = new List<string>(HiddenColumns);
        c.ProfileNames = new List<string>(ProfileNames);
        c.CustomPorts = new List<string>(CustomPorts);
        c.ScanPresets = new List<string>(ScanPresets);
        c.RecentWakeTargets = new List<string>(RecentWakeTargets);
        return c;
    }
}
