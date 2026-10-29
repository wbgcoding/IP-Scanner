using System.IO;
using System.Linq;
using IpScanner.Core.Cli;
using IpScanner.Core.Data;
using IpScanner.Core.Models;
using Xunit;

namespace IpScanner.Tests.Core;

public class ConfigManagerTests
{
    private static string WriteTemp(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".conf");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Load_ParsesValues_AndClampsRange()
    {
        var path = WriteTemp("""
            ping_count = 50
            scan_threads = 5000
            sidebar_width = 9000
            subnet = 192.168.1.0/24
            subnet_2 = 10.0.0.0/24
            pinned_ips = 192.168.1.1, 192.168.1.10
            """);
        var cfg = ConfigManager.Load(path);
        Assert.Equal(50, cfg.PingCount);
        Assert.Equal(1000, cfg.ScanThreads);          // clamped to max 1000
        Assert.Equal(560, cfg.SidebarWidth);          // clamped to max 560
        Assert.Equal(new[] { "192.168.1.0/24", "10.0.0.0/24" }, cfg.Subnets);
        Assert.Equal(new[] { "192.168.1.1", "192.168.1.10" }, cfg.PinnedIps);
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips_ActiveProfileAndNames()
    {
        var cfg = new ScanConfig { ActiveProfile = "Buero", ProfileNames = new() { "Buero", "Zuhause" } };
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".conf");
        ConfigManager.Save(path, cfg);
        var loaded = ConfigManager.Load(path);

        Assert.Equal("Buero", loaded.ActiveProfile);
        Assert.Equal(new[] { "Buero", "Zuhause" }, loaded.ProfileNames);
    }

    [Fact]
    public void Load_ActiveProfileNotInProfileNames_IsAddedToTheList()
    {
        var path = WriteTemp("""
            active_profile = GhostProfile
            profile_names = Buero
            """);
        var loaded = ConfigManager.Load(path);
        Assert.Equal("GhostProfile", loaded.ActiveProfile);
        Assert.Equal(new[] { "Buero", "GhostProfile" }, loaded.ProfileNames);
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips_AdvancedNetworkSettings()
    {
        var cfg = new ScanConfig
        {
            PortScanConnectTimeoutMs = 1234,
            PortScanBannerTimeoutMs = 2345,
            PortScanParallel = 77,
            CustomPorts = new() { "8443 Admin", "9000" },
            SnmpCommunity = "mycommunity",
        };
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".conf");
        ConfigManager.Save(path, cfg);
        var loaded = ConfigManager.Load(path);

        Assert.Equal(1234, loaded.PortScanConnectTimeoutMs);
        Assert.Equal(2345, loaded.PortScanBannerTimeoutMs);
        Assert.Equal(77, loaded.PortScanParallel);
        Assert.Equal(new[] { "8443 Admin", "9000" }, loaded.CustomPorts);
        // The SNMP community is sealed separately and must not reach the conf.
        Assert.DoesNotContain("mycommunity", File.ReadAllText(path));
        Assert.DoesNotContain("snmp_community", File.ReadAllText(path));
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var cfg = ConfigManager.Load("does-not-exist.conf");
        Assert.Equal(10, cfg.PingCount);
        Assert.True(cfg.EnableInternetPing);
    }

    [Fact]
    public void Load_PortScanParallel_ClampsToTheGlobalLimit()
    {
        var path = WriteTemp("port_scan_parallel = 999\n");
        Assert.Equal(500, ConfigManager.Load(path).PortScanParallel);
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips_AllSettings()
    {
        var cfg = new ScanConfig
        {
            PingCount = 100, PingIntervalMs = 250, ExportCsv = true, FileOutput = true,
            OfflineAfterFailedPings = 7, InitPingCount = 3, StartupPingCount = 8, OfflineRecheckSeconds = 9,
            EnableInternetPing = false, InternetTimeoutMs = 2200,
            KnownDevicesDb = false, DatabasePath = "./Data/other.db",
            OutputDirectory = "./Out", ScanThreads = 77, UiScalePercent = 120, SidebarWidth = 420,
            Subnets = new() { "192.168.5.0/24" },
            PinnedIps = new() { "192.168.5.1" },
            InternetHosts = new() { "1.1.1.1 Cloudflare" },
            ColorOnline = "#112233", ColorFailed = "#AABBCC",
        };
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".conf");
        ConfigManager.Save(path, cfg);
        var loaded = ConfigManager.Load(path);

        Assert.Equal(100, loaded.PingCount);
        Assert.Equal(250, loaded.PingIntervalMs);
        Assert.True(loaded.ExportCsv);
        Assert.True(loaded.FileOutput);
        Assert.Equal(7, loaded.OfflineAfterFailedPings);
        Assert.Equal(3, loaded.InitPingCount);
        Assert.Equal(8, loaded.StartupPingCount);
        Assert.Equal(9, loaded.OfflineRecheckSeconds);
        Assert.False(loaded.EnableInternetPing);
        Assert.Equal(2200, loaded.InternetTimeoutMs);
        Assert.False(loaded.KnownDevicesDb);
        Assert.Equal("./Data/other.db", loaded.DatabasePath);
        Assert.Equal("./Out", loaded.OutputDirectory);
        Assert.Equal(77, loaded.ScanThreads);
        Assert.Equal(120, loaded.UiScalePercent);
        Assert.Equal(420, loaded.SidebarWidth);
        Assert.Equal(new[] { "192.168.5.0/24" }, loaded.Subnets);
        Assert.Equal(new[] { "192.168.5.1" }, loaded.PinnedIps);
        Assert.Equal(new[] { "1.1.1.1 Cloudflare" }, loaded.InternetHosts);
        Assert.Equal("#112233", loaded.ColorOnline);
        Assert.Equal("#AABBCC", loaded.ColorFailed);
    }

    [Fact]
    public void ArchiveRetention_IsIndependentOfDeviceRetentionAndFallsBackOnBadValues()
    {
        var cfg = new ScanConfig { DbRetentionDays = 30, ArchiveRetentionDays = 0 };
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".conf");
        ConfigManager.Save(path, cfg);
        var loaded = ConfigManager.Load(path);
        Assert.Equal(30, loaded.DbRetentionDays);
        Assert.Equal(0, loaded.ArchiveRetentionDays);
        Assert.Equal(365, new ScanConfig().ArchiveRetentionDays);
        Assert.Equal(365, ConfigManager.Load(WriteTemp("archive_retention_days = 17")).ArchiveRetentionDays);
    }

    [Fact]
    public void ArchiveCompactionAndBackupRetention_RoundTripAndFallBackOnBadValues()
    {
        var cfg = new ScanConfig { ArchiveCompactionDays = 0, ArchiveBackupRetentionDays = 90 };
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".conf");
        ConfigManager.Save(path, cfg);
        var loaded = ConfigManager.Load(path);
        Assert.Equal(0, loaded.ArchiveCompactionDays);
        Assert.Equal(90, loaded.ArchiveBackupRetentionDays);
        var bad = ConfigManager.Load(WriteTemp("archive_compaction_days = abc\narchive_backup_retention_days = "));
        Assert.Equal(ScanConfig.Bounds.ArchiveCompactionDaysDefault, bad.ArchiveCompactionDays);
        Assert.Equal(ScanConfig.Bounds.ArchiveBackupRetentionDaysDefault, bad.ArchiveBackupRetentionDays);
        Assert.Contains(ScanConfig.Bounds.ArchiveCompactionDaysDefault, ScanConfig.Bounds.ArchiveCompactionDaysOptions);
        Assert.Contains(ScanConfig.Bounds.ArchiveBackupRetentionDaysDefault, ScanConfig.Bounds.ArchiveBackupRetentionDaysOptions);
    }

    [Fact]
    public void HandEditedArchiveDays_AreKeptAndClampedToTheDocumentedRange()
    {
        var custom = ConfigManager.Load(WriteTemp("archive_compaction_days = 365\narchive_backup_retention_days = 365"));
        Assert.Equal(365, custom.ArchiveCompactionDays);
        Assert.Equal(365, custom.ArchiveBackupRetentionDays);
        var huge = ConfigManager.Load(WriteTemp("archive_compaction_days = 99999\narchive_backup_retention_days = -4"));
        Assert.Equal(3650, huge.ArchiveCompactionDays);
        Assert.Equal(0, huge.ArchiveBackupRetentionDays);
    }

    [Fact]
    public void WithCustom_AddsTheValueAmongTheDayCountsAndKeepsZeroInPlace()
    {
        var days = ScanConfig.Bounds.WithCustom(ScanConfig.Bounds.ArchiveBackupRetentionDaysOptions, 60);
        Assert.Equal(new[] { 7, 30, 60, 90, 0 }, days);
        Assert.Equal(new[] { 0, 7, 30, 90, 365 }, ScanConfig.Bounds.WithCustom(ScanConfig.Bounds.ArchiveCompactionDaysOptions, 365));
        Assert.Same(ScanConfig.Bounds.ArchiveCompactionDaysOptions, ScanConfig.Bounds.WithCustom(ScanConfig.Bounds.ArchiveCompactionDaysOptions, 30));
    }

    // The Tools window remembers its last-open page the same way the
    // Settings overlay does (SettingsPage) - same round-trip plus fallback shape.
    [Theory]
    [InlineData("Certificate")]
    [InlineData("MacTool")]
    public void Save_ThenLoad_RoundTrips_ToolsLastPage(string page)
    {
        var cfg = new ScanConfig { ToolsLastPage = page };
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".conf");
        ConfigManager.Save(path, cfg);
        var loaded = ConfigManager.Load(path);

        Assert.Equal(page, loaded.ToolsLastPage);
    }

    // The new Help settings page must round-trip like every other
    // known settings_page value, not fall back to "Scan".
    [Fact]
    public void Save_ThenLoad_RoundTrips_HelpSettingsPage()
    {
        var cfg = new ScanConfig { SettingsPage = "Help" };
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".conf");
        ConfigManager.Save(path, cfg);
        var loaded = ConfigManager.Load(path);

        Assert.Equal("Help", loaded.SettingsPage);
    }

    // The selected adapter's IPv4 address round-trips like every other string setting.
    [Fact]
    public void Save_ThenLoad_RoundTrips_NetworkInterfaceIp()
    {
        var cfg = new ScanConfig { NetworkInterfaceIp = "192.168.1.50" };
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".conf");
        ConfigManager.Save(path, cfg);
        var loaded = ConfigManager.Load(path);

        Assert.Equal("192.168.1.50", loaded.NetworkInterfaceIp);
    }

    [Fact]
    public void Load_UnknownToolsLastPage_FallsBackToWakeOnLan()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".conf");
        File.WriteAllText(path, "tools_last_page = Nonsense\n");

        Assert.Equal("WakeOnLan", ConfigManager.Load(path).ToolsLastPage);
    }

    // The recent Wake-on-LAN targets live in the sealed settings file; the conf never carries them.
    [Fact]
    public void Save_DoesNotWriteRecentWakeTargets_ToTheConf()
    {
        var cfg = new ScanConfig { RecentWakeTargets = new() { "AA:BB:CC:DD:EE:FF Living Room TV", "11:22:33:44:55:66" } };
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".conf");
        ConfigManager.Save(path, cfg);
        var loaded = ConfigManager.Load(path);

        Assert.Empty(loaded.RecentWakeTargets);
        Assert.DoesNotContain("recent_wake_targets", File.ReadAllText(path));
        Assert.DoesNotContain("Living Room TV", File.ReadAllText(path));
    }

    [Fact]
    public void Save_DropsTheRetiredAdminHintKey()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".conf");
        File.WriteAllText(path, "never_show_admin_hint = true" + System.Environment.NewLine);
        ConfigManager.Save(path, ConfigManager.Load(path));

        Assert.DoesNotContain("never_show_admin_hint", File.ReadAllText(path));
    }

    [Fact]
    public void Load_RecentWakeTargets_CapsAtFive()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".conf");
        File.WriteAllText(path, "recent_wake_targets = A, B, C, D, E, F\n");

        Assert.Equal(5, ConfigManager.Load(path).RecentWakeTargets.Count);
    }

    // Loss/jitter columns start hidden by default but the user's own
    // choice (shown or hidden) must survive a save/load round trip either way.
    [Fact]
    public void Save_ThenLoad_RoundTrips_HiddenColumns_IncludingLossAndJitter()
    {
        var cfg = new ScanConfig { HiddenColumns = new() { "loss", "jitter", "vendor" } };
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".conf");
        ConfigManager.Save(path, cfg);
        var loaded = ConfigManager.Load(path);

        Assert.Equal(new[] { "loss", "jitter", "vendor" }, loaded.HiddenColumns);
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips_HiddenColumns_LossAndJitterExplicitlyShown()
    {
        var cfg = new ScanConfig { HiddenColumns = new() };   // user unhid both
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".conf");
        ConfigManager.Save(path, cfg);
        var loaded = ConfigManager.Load(path);

        Assert.Empty(loaded.HiddenColumns);
    }

    [Fact]
    public void NewScanConfig_ShowsLossAndHidesJitterByDefault()
        => Assert.Equal(new[] { "jitter", "method", "os" }, new ScanConfig().HiddenColumns);

    // Background hostname-retry cadence, 0 = off.
    [Fact]
    public void Save_ThenLoad_RoundTrips_HostnameRetryMinutes()
    {
        var cfg = new ScanConfig { HostnameRetryMinutes = 30 };
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".conf");
        ConfigManager.Save(path, cfg);
        var loaded = ConfigManager.Load(path);

        Assert.Equal(30, loaded.HostnameRetryMinutes);
    }

    [Fact]
    public void NewScanConfig_DefaultsHostnameRetryMinutesToTen()
        => Assert.Equal(10, new ScanConfig().HostnameRetryMinutes);

    // Remembered device-table sort column/direction and widths.
    [Fact]
    public void Save_ThenLoad_RoundTrips_TableSortAndWidths()
    {
        var cfg = new ScanConfig { TableSort = "Avg:desc", TableWidths = "Ip:120,Vendor:90" };
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".conf");
        ConfigManager.Save(path, cfg);
        var loaded = ConfigManager.Load(path);

        Assert.Equal("Avg:desc", loaded.TableSort);
        Assert.Equal("Ip:120,Vendor:90", loaded.TableWidths);
    }

    [Fact]
    public void NewScanConfig_DefaultsTableSortAndWidthsToEmpty()
    {
        var cfg = new ScanConfig();
        Assert.Equal("", cfg.TableSort);
        Assert.Equal("", cfg.TableWidths);
    }

    [Fact]
    public void Save_ThenLoad_RemembersManualExportChoices()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".conf");
        ConfigManager.Save(path, new ScanConfig { ManualExportScope = "Selected", ManualExportFormat = "Json" });
        var loaded = ConfigManager.Load(path);
        Assert.Equal("Selected", loaded.ManualExportScope);
        Assert.Equal("Json", loaded.ManualExportFormat);
    }

    [Fact]
    public void Load_InfinitePingCount_RoundTrips()
    {
        var path = WriteTemp("ping_count = -1");
        Assert.Equal(ScanConfig.InfinitePingCount, ConfigManager.Load(path).PingCount);
    }

    [Theory]
    [InlineData(@"output_directory = C:\Reports#2\Scans", @"C:\Reports#2\Scans")]
    [InlineData(@"output_directory = C:\Reports;2026\Scans", @"C:\Reports;2026\Scans")]
    public void Load_PathValueContainingHashOrSemicolon_IsNotTruncated(string line, string expected)
    {
        // '#'/';' embedded in a legal Windows path (no preceding
        // whitespace) must survive - only an actual "value #comment"-style
        // trailing comment gets cut.
        var path = WriteTemp(line);
        Assert.Equal(expected, ConfigManager.Load(path).OutputDirectory);
    }

    [Fact]
    public void Load_TrailingInlineComment_IsStillStripped()
    {
        var path = WriteTemp(@"output_directory = C:\Scans   # comment here");
        Assert.Equal(@"C:\Scans", ConfigManager.Load(path).OutputDirectory);
    }

    [Fact]
    public void Save_WritesAtomically_AndKeepsABackupOfThePreviousVersion()
    {
        // A crash mid-write must never leave the config truncated/gone.
        // Save() writes to a sibling .tmp file and swaps it in, so no half-written
        // state is ever visible at `path` - and the swap leaves a .bak of the
        // prior generation as a manual-recovery fallback.
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".conf");
        var tmp = path + ".tmp";
        var bak = path + ".bak";

        ConfigManager.Save(path, new ScanConfig { PingCount = 11 });
        Assert.False(File.Exists(tmp));    // temp file cleaned up (renamed away)
        Assert.False(File.Exists(bak));    // no prior version yet on the first save

        ConfigManager.Save(path, new ScanConfig { PingCount = 22 });
        Assert.False(File.Exists(tmp));
        Assert.True(File.Exists(bak));     // second save backs up the first
        Assert.Equal(11, ConfigManager.Load(bak).PingCount);
        Assert.Equal(22, ConfigManager.Load(path).PingCount);
    }

    [Fact]
    public void Save_PreservesHandEditedLinesAcrossThreeSaves()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "ip_scanner.conf");
        File.WriteAllText(path, "# my scan note\nscan_threads = 12\ncustom.key = stays\n\n# trailing note\n");
        foreach (var count in new[] { 13, 14, 15 })
        {
            Assert.True(ConfigManager.Save(path, new ScanConfig { ScanThreads = count }));
            var text = File.ReadAllText(path);
            var normalized = text.Replace("\r\n", "\n");
            Assert.Contains($"# my scan note\nscan_threads = {count}\ncustom.key = stays\n", normalized);
            Assert.EndsWith("\n# trailing note\n", normalized);
        }
        Assert.Equal(15, ConfigManager.Load(path).ScanThreads);
        Assert.Contains("scan_threads = 14", File.ReadAllText(path + ".bak"));
    }

    [Theory]
    [InlineData("scan_threads = 12\nscan_threads = 13\n")]
    [InlineData("this is not an assignment\n")]
    public void Save_UnsafeMergeRequiresConfirmation_AndCancelKeepsData(string content)
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "ip_scanner.conf");
        File.WriteAllText(path, content);
        Assert.Throws<UnsafeConfigMergeException>(() => ConfigManager.Save(path, new ScanConfig()));
        var prompted = false;
        Assert.False(ConfigManager.Save(path, new ScanConfig(), () => { prompted = true; return false; }));
        Assert.True(prompted);
        Assert.Equal(content, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void Save_UnsupportedEncoding_CancelKeepsOriginalBytes()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "ip_scanner.conf");
        var original = System.Text.Encoding.Unicode.GetBytes("scan_threads = 12\r\n");
        File.WriteAllBytes(path, original);
        Assert.False(ConfigManager.Save(path, new ScanConfig(), () => false));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void Save_ConfirmedOverwrite_KeepsPreviousFileAsBackup()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "ip_scanner.conf");
        File.WriteAllText(path, "broken line\n");
        Assert.True(ConfigManager.Save(path, new ScanConfig(), () => true));
        Assert.Equal("broken line\n", File.ReadAllText(path + ".bak"));
        var rewritten = File.ReadAllText(path);
        Assert.True(rewritten.Contains("Comments you add by hand are preserved.") ||
                    rewritten.Contains("Von Hand eingefügte Kommentare bleiben erhalten."));
    }

    [Fact]
    public void Save_ConfigCommentUsesTheSettingsTooltipDescription()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "ip_scanner.conf");
        ConfigManager.Save(path, new ScanConfig());
        Assert.Contains(IpScanner.Core.Localization.Loc.TipScanThreads, File.ReadAllText(path));
    }

    [Fact]
    public void Save_RemovedSubnetsAndPinnedIps_DoNotReturnOnLoad()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "ip_scanner.conf");
        File.WriteAllText(path, "subnet = 192.0.2.0/24\nsubnet_2 = 198.51.100.0/24\npinned_ips = 192.0.2.1\n# keep this\n");
        ConfigManager.Save(path, new ScanConfig());
        var loaded = ConfigManager.Load(path);
        Assert.Empty(loaded.Subnets);
        Assert.Empty(loaded.PinnedIps);
        Assert.Contains("# keep this", File.ReadAllText(path));
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips_ScanPresetsAndTheirSettings()
    {
        var cfg = new ScanConfig
        {
            ScanPortRange = "22,80,8000-9000", PortServiceDetection = false,
            ScanPresets = ["Office | -T4 -p 22,80 -sV", "Lab | -T2 -F -O"],
        };
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "ip_scanner.conf");
        ConfigManager.Save(path, cfg);
        var loaded = ConfigManager.Load(path);
        Assert.Equal(cfg.ScanPresets, loaded.ScanPresets);
        Assert.Equal("22,80,8000-9000", loaded.ScanPortRange);
        Assert.False(loaded.PortServiceDetection);
    }

    [Fact]
    public void Save_RemovedScanPresets_DoNotReturnOnLoad()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "ip_scanner.conf");
        File.WriteAllText(path, "scan_presets = A | -T4\nscan_presets_2 = B | -F\nscan_presets_3 = C | -sn\n# keep this\n");
        var cfg = ConfigManager.Load(path);
        Assert.Equal(3, cfg.ScanPresets.Count);
        cfg.ScanPresets.RemoveAt(1);
        ConfigManager.Save(path, cfg);
        Assert.Equal(["A | -T4", "C | -sn"], ConfigManager.Load(path).ScanPresets);
        cfg.ScanPresets.Clear();
        ConfigManager.Save(path, cfg);
        Assert.Empty(ConfigManager.Load(path).ScanPresets);
        Assert.Contains("# keep this", File.ReadAllText(path));
    }

    [Fact]
    public void Load_ScanPresets_AreCappedAndDeduplicated_AndABadPortRangeFallsBack()
    {
        var lines = Enumerable.Range(0, ScanPresets.MaxUserPresets + 20).Select(i => $"scan_presets_{i + 1} = P{i} | -T4");
        var path = WriteTemp(string.Join("\n", lines) + "\nscan_presets_900 = P0 | -T4\nscan_port_range = not ports\n");
        var cfg = ConfigManager.Load(path);
        Assert.Equal(ScanPresets.MaxUserPresets, cfg.ScanPresets.Count);
        Assert.Equal("", cfg.ScanPortRange);
    }

    [Fact]
    public void Save_PreservesInlineCommentAsAComment()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "ip_scanner.conf");
        File.WriteAllText(path, "scan_threads = 12   # local note\n");
        ConfigManager.Save(path, new ScanConfig { ScanThreads = 14 });
        Assert.Contains("scan_threads = 14   # local note", File.ReadAllText(path));
        Assert.Equal(14, ConfigManager.Load(path).ScanThreads);
    }
}
