using System.IO;
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
            subnet = 192.168.1.0/24
            subnet_2 = 10.0.0.0/24
            pinned_ips = 192.168.1.1, 192.168.1.10
            """);
        var cfg = ConfigManager.Load(path);
        Assert.Equal(50, cfg.PingCount);
        Assert.Equal(1000, cfg.ScanThreads);          // clamped to max 1000
        Assert.Equal(new[] { "192.168.1.0/24", "10.0.0.0/24" }, cfg.Subnets);
        Assert.Equal(new[] { "192.168.1.1", "192.168.1.10" }, cfg.PinnedIps);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var cfg = ConfigManager.Load("does-not-exist.conf");
        Assert.Equal(10, cfg.PingCount);
        Assert.True(cfg.EnableInternetPing);
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        var cfg = new ScanConfig
        {
            PingCount = 100, PingIntervalMs = 250, ExportCsv = true,
            Subnets = new() { "192.168.5.0/24" },
            PinnedIps = new() { "192.168.5.1" },
            InternetHosts = new() { "1.1.1.1" },
        };
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".conf");
        ConfigManager.Save(path, cfg);
        var loaded = ConfigManager.Load(path);
        Assert.Equal(100, loaded.PingCount);
        Assert.Equal(250, loaded.PingIntervalMs);
        Assert.True(loaded.ExportCsv);
        Assert.Equal(new[] { "192.168.5.0/24" }, loaded.Subnets);
        Assert.Equal(new[] { "192.168.5.1" }, loaded.PinnedIps);
        Assert.Equal(new[] { "1.1.1.1" }, loaded.InternetHosts);
    }
}
