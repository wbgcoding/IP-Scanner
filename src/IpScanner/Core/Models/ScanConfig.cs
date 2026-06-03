namespace IpScanner.Core.Models;

/// <summary>All scan options. Mirrors network_scanner.conf keys.</summary>
public sealed class ScanConfig
{
    public List<string> Subnets { get; set; } = new();
    public List<string> PinnedIps { get; set; } = new();
    public int PingCount { get; set; } = 10;                  // -1 = infinite
    public int PingIntervalMs { get; set; } = 100;
    public int OfflineAfterFailedPings { get; set; } = 5;
    public int InitPingCount { get; set; } = 1;
    public bool HighPressureMode { get; set; }
    public bool EnableInternetPing { get; set; } = true;
    public List<string> InternetHosts { get; set; } =
        new() { "8.8.8.8", "8.8.4.4", "1.1.1.1", "9.9.9.9" };
    public bool KnownDevicesDb { get; set; } = true;
    public string OutputDirectory { get; set; } = "./Scans";
    public bool FileOutput { get; set; }          // TXT report off by default
    public bool ExportCsv { get; set; }
    public int PingThreads { get; set; } = 100;
    public int InitPingThreads { get; set; } = 254;

    public const int InfinitePingCount = -1;

    /// <summary>Copy with a different ping count (lists shared, read-only use).</summary>
    public ScanConfig CloneWith(int pingCount) => new()
    {
        Subnets = Subnets, PinnedIps = PinnedIps, PingCount = pingCount,
        PingIntervalMs = PingIntervalMs, OfflineAfterFailedPings = OfflineAfterFailedPings,
        InitPingCount = InitPingCount, HighPressureMode = HighPressureMode,
        EnableInternetPing = EnableInternetPing, InternetHosts = InternetHosts,
        KnownDevicesDb = KnownDevicesDb, OutputDirectory = OutputDirectory,
        FileOutput = FileOutput, ExportCsv = ExportCsv, PingThreads = PingThreads,
        InitPingThreads = InitPingThreads,
    };
}
