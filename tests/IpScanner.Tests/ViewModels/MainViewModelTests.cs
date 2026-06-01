using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IpScanner.Core.Models;
using IpScanner.ViewModels;
using Xunit;

namespace IpScanner.Tests.ViewModels;

public class MainViewModelTests
{
    [Fact]
    public async Task RunScan_PopulatesDevices()
    {
        PingResult Fake(string ip, int _) =>
            ip.EndsWith(".1") ? new PingResult(true, 1.0, 64) : new PingResult(false, null, null);

        var vm = new MainViewModel(
            pingFunc: Fake,
            detectNetwork: () => new NetworkInfo { Ip = "10.0.0.5", Gateway = "10.0.0.1" },
            dispatch: a => a());      // synchronous dispatch for tests

        vm.Config = new ScanConfig { PingCount = 1, PingIntervalMs = 0 };

        await vm.RunScanAsync(new[] { "10.0.0" });

        Assert.Contains(vm.Devices, d => d.Ip == "10.0.0.1" && d.IsOnline);
        Assert.True(vm.Progress.OnlineCount >= 1);
    }
}
