using System.IO;
using IpScanner.Core.Data;
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
            ping_threads = 5000
            high_pressure_mode = true
            subnet = 192.168.1.0/24
            subnet_2 = 10.0.0.0/24
            pinned_ips = 192.168.1.1, 192.168.1.10
            """);
        var cfg = ConfigManager.Load(path);
        Assert.Equal(50, cfg.PingCount);
        Assert.Equal(1000, cfg.PingThreads);          // clamped to max 1000
        Assert.True(cfg.HighPressureMode);
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
}
