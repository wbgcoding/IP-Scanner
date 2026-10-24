using System;
using System.IO;
using IpScanner.Core.Data;
using IpScanner.Core.Models;
using Xunit;

namespace IpScanner.Tests.Core;

public class ConfigListParsingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory().FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private ScanConfig LoadWith(string line)
    {
        var path = Path.Combine(_dir, ".conf");
        File.WriteAllText(path, line + "\n");
        return ConfigManager.Load(path);
    }

    [Fact]
    public void PinnedIps_SemicolonsCommasBlanksAndDuplicates_AreNormalised()
    {
        var cfg = LoadWith("pinned_ips = 10.0.0.1; 10.0.0.2 ,, 10.0.0.1 ,  ");
        Assert.Equal(new[] { "10.0.0.1", "10.0.0.2" }, cfg.PinnedIps);
    }

    [Fact]
    public void PinnedIps_EmptyValue_GivesEmptyList()
        => Assert.Empty(LoadWith("pinned_ips = ").PinnedIps);

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(1000, 1000)]
    [InlineData(5000, 1000)]
    public void ScanThreads_IsClampedToZeroAutoThroughThousand(int written, int expected)
        => Assert.Equal(expected, LoadWith($"scan_threads = {written}").ScanThreads);

    [Fact]
    public void ScanThreads_Garbage_FallsBackToDefault()
        => Assert.Equal(100, LoadWith("scan_threads = many").ScanThreads);
}
