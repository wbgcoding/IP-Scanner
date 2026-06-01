using IpScanner.Core.Models;
using IpScanner.ViewModels;
using Xunit;

namespace IpScanner.Tests.ViewModels;

public class SettingsViewModelTests
{
    [Fact]
    public void ToConfig_RoundTripsFromConfig()
    {
        var cfg = new ScanConfig { PingCount = 100, ExportCsv = true, PingThreads = 50 };
        var vm = new SettingsViewModel(cfg);
        vm.PingCount = 250;
        var result = vm.ToConfig();
        Assert.Equal(250, result.PingCount);
        Assert.True(result.ExportCsv);
        Assert.Equal(50, result.PingThreads);
    }
}
