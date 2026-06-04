namespace IpScanner.Core.Models;

/// <summary>All scan options. Mirrors the exported .conf keys.</summary>
public sealed class ScanConfig
{
    public List<string> Subnets { get; set; } = new();
    public List<string> PinnedIps { get; set; } = new();
    public int PingCount { get; set; } = 10;                  // -1 = infinite
    public int PingIntervalMs { get; set; } = 100;
    public int OfflineAfterFailedPings { get; set; } = 5;
    public int InitPingCount { get; set; } = 1;
    /// <summary>Ping count for the automatic sweep right after the app starts.</summary>
    public int StartupPingCount { get; set; } = 5;
    /// <summary>Recheck offline IPs every N seconds during a run (0 = off).</summary>
    public int OfflineRecheckSeconds { get; set; } = 2;
    public bool EnableInternetPing { get; set; } = true;
    /// <summary>ICMP timeout for the internet host pings (ms).</summary>
    public int InternetTimeoutMs { get; set; } = 1000;
    /// <summary>Entries: "ip" or "ip name", e.g. "8.8.8.8 Google 1".</summary>
    public List<string> InternetHosts { get; set; } =
        new() { "8.8.8.8 Google 1", "8.8.4.4 Google 2", "1.1.1.1 Cloudflare", "9.9.9.9 Quad9" };
    public bool KnownDevicesDb { get; set; } = true;
    /// <summary>Known-devices database file (lives in the scans folder by default).</summary>
    public string DatabasePath { get; set; } = DefaultDatabasePath;
    public string OutputDirectory { get; set; } = "./Scans";
    public bool FileOutput { get; set; }          // TXT report off by default
    public bool ExportCsv { get; set; }
    /// <summary>Parallel ping workers per scan. 0 = one thread per device (max).</summary>
    public int ScanThreads { get; set; } = 100;
    /// <summary>UI/text scale in percent (100 = default).</summary>
    public int UiScalePercent { get; set; } = 100;
    /// <summary>UI language: "auto" (system, EN fallback), "de" or "en".</summary>
    public string Language { get; set; } = "auto";

    // Progress-bar colors (clickable legend squares; persisted, not shown in settings).
    public string ColorOnline { get; set; } = Palette.Green;
    public string ColorOffline { get; set; } = Palette.Red;
    public string ColorSuccess { get; set; } = Palette.Green;
    public string ColorFailed { get; set; } = Palette.Red;
    public string ColorSkipped { get; set; } = Palette.MidGray;

    public const int InfinitePingCount = -1;
    public const string DefaultDatabasePath = "./Scans/scanner.db";
    public const string DefaultDatabaseFileName = "scanner.db";
    public const string ConfigFileName = "ip_scanner.conf";

    /// <summary>Copy with a different ping count (lists shared, read-only use).</summary>
    public ScanConfig CloneWith(int pingCount) => new()
    {
        Subnets = Subnets, PinnedIps = PinnedIps, PingCount = pingCount,
        PingIntervalMs = PingIntervalMs, OfflineAfterFailedPings = OfflineAfterFailedPings,
        InitPingCount = InitPingCount, StartupPingCount = StartupPingCount,
        OfflineRecheckSeconds = OfflineRecheckSeconds,
        EnableInternetPing = EnableInternetPing, InternetTimeoutMs = InternetTimeoutMs,
        InternetHosts = InternetHosts, KnownDevicesDb = KnownDevicesDb,
        DatabasePath = DatabasePath, OutputDirectory = OutputDirectory, FileOutput = FileOutput,
        ExportCsv = ExportCsv, ScanThreads = ScanThreads, UiScalePercent = UiScalePercent,
        Language = Language,
        ColorOnline = ColorOnline, ColorOffline = ColorOffline,
        ColorSuccess = ColorSuccess, ColorFailed = ColorFailed, ColorSkipped = ColorSkipped,
    };
}
