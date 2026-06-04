using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core.Models;
using IpScanner.ViewModels;
using Xunit;

namespace IpScanner.Tests.ViewModels;

public class MainViewModelTests
{
    private static PingResult OnlyDotOne(string ip, int _) =>
        ip.EndsWith(".1") ? new PingResult(true, 1.0, 64) : new PingResult(false, null, null);

    [Fact]
    public async Task RunScan_PopulatesDevices()
    {
        var vm = new MainViewModel(
            pingFunc: OnlyDotOne,
            detectNetwork: () => new NetworkInfo { Ip = "10.0.0.5", Gateway = "10.0.0.1" },
            dispatch: a => a());      // synchronous dispatch for tests

        vm.Config = new ScanConfig { PingCount = 1, PingIntervalMs = 0, FileOutput = false, KnownDevicesDb = false, EnableInternetPing = false };

        await vm.RunScanAsync(new[] { "10.0.0" });

        Assert.Contains(vm.Devices, d => d.Ip == "10.0.0.1" && d.IsOnline);
        Assert.True(vm.Progress.OnlineCount >= 1);
    }

    [Fact]
    public async Task PingInternet_FillsLatencies()
    {
        var vm = new MainViewModel(
            pingFunc: (ip, _) => new PingResult(true, ip == "1.1.1.1" ? 8.0 : 12.0, 64),
            detectNetwork: () => new NetworkInfo { Ip = "10.0.0.5" },
            dispatch: a => a());
        vm.Config = new ScanConfig { InternetHosts = new() { "1.1.1.1", "8.8.8.8" }, EnableInternetPing = true };

        await vm.PingInternetAsync(CancellationToken.None);

        Assert.Equal(2, vm.InternetHosts.Count);
        Assert.Contains(vm.InternetHosts, h => h.Ip == "1.1.1.1");
    }

    [Fact]
    public async Task RunScan_WithFileOutput_WritesReport()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var vm = new MainViewModel(
            pingFunc: OnlyDotOne,
            detectNetwork: () => new NetworkInfo { Ip = "10.0.0.5", Gateway = "10.0.0.1" },
            dispatch: a => a());
        vm.Config = new ScanConfig { PingCount = 1, FileOutput = true, OutputDirectory = dir, KnownDevicesDb = false, EnableInternetPing = false };

        await vm.RunScanAsync(new[] { "10.0.0" });

        Assert.NotNull(vm.LastExportPath);
        Assert.True(File.Exists(vm.LastExportPath));
    }

    [Fact]
    public async Task RunScan_ConfigSubnets_AreScannedInAddition()
    {
        var vm = new MainViewModel(
            pingFunc: OnlyDotOne,
            detectNetwork: () => new NetworkInfo { Ip = "10.0.2.5", Gateway = "10.0.2.1" },
            dispatch: a => a());
        vm.Config = new ScanConfig
        {
            PingCount = 1, PingIntervalMs = 0, FileOutput = false,
            KnownDevicesDb = false, EnableInternetPing = false,
            Subnets = new() { "10.0.1.0/24" },
        };
        vm.ManualSubnet = "10.0.0.0/24";

        await vm.RunScanAsync();

        // Manual subnet replaces auto-detect; configured subnet is scanned too.
        Assert.Contains(vm.Devices, d => d.Ip == "10.0.0.1");
        Assert.Contains(vm.Devices, d => d.Ip == "10.0.1.1");
        Assert.DoesNotContain(vm.Devices, d => d.Ip == "10.0.2.1");
    }

    [Fact]
    public async Task RunScan_UpdatesNetworkCardStats()
    {
        var vm = new MainViewModel(
            pingFunc: (ip, _) => ip.EndsWith(".1") ? new PingResult(true, 2.0, 64) : new PingResult(false, null, null),
            detectNetwork: () => new NetworkInfo { Ip = "10.0.0.5", Gateway = "10.0.0.1" },
            dispatch: a => a());
        vm.Config = new ScanConfig { PingCount = 1, KnownDevicesDb = false, FileOutput = false, EnableInternetPing = false };

        await vm.RunScanAsync(new[] { "10.0.0" });

        Assert.Single(vm.Networks);
        Assert.Equal(1, vm.Networks[0].OnlineCount);
        Assert.Equal(253, vm.Networks[0].OfflineCount);
    }
}
