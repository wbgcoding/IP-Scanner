using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core.Models;
using IpScanner.ViewModels;
using Xunit;

namespace IpScanner.Tests.ViewModels;

public class LargeScanConfirmTests
{
    // 10.0.0.0/15: 512 /24 slices, 130048 hosts, above the 65536 cap.
    private static readonly string[] Slash15 =
        Enumerable.Range(0, 512).Select(i => $"10.{i / 256}.{i % 256}").ToArray();

    private static MainViewModel Create(Func<string, int, PingResult> ping, Func<long, bool>? confirm)
    {
        var vm = new MainViewModel(ping, () => new NetworkInfo { Ip = "10.0.0.5", Gateway = "10.0.0.1" }, a => a());
        vm.Config = new ScanConfig { PingCount = 1, PingIntervalMs = 0, FileOutput = false, KnownDevicesDb = false, EnableInternetPing = false };
        if (confirm is not null) vm.ConfirmLargeScan = confirm;
        return vm;
    }

    [Fact]
    public async Task DeclinedLargeScan_DoesNotStartAndDoesNotThrow()
    {
        int pings = 0;
        long asked = 0;
        var vm = Create((_, _) => { Interlocked.Increment(ref pings); return new PingResult(false, null, null); },
                        n => { asked = n; return false; });

        await vm.RunScanAsync(Slash15);

        Assert.Equal(512L * 254, asked);
        Assert.Equal(0, pings);
        Assert.Empty(vm.Devices);
        Assert.False(vm.IsScanning);
    }

    [Fact]
    public async Task LargeScanWithoutAHandler_IsDeclined()
    {
        int pings = 0;
        var vm = Create((_, _) => { Interlocked.Increment(ref pings); return new PingResult(false, null, null); }, null);

        await vm.RunScanAsync(Slash15);

        Assert.Equal(0, pings);
    }

    [Fact]
    public async Task ConfirmedLargeScan_Runs()
    {
        int pings = 0;
        MainViewModel? vm = null;
        vm = Create((_, _) =>
        {
            if (Interlocked.Increment(ref pings) == 1) vm!.Stop();   // proof the scan began; do not ping 130k hosts
            return new PingResult(false, null, null);
        }, _ => true);

        await vm.RunScanAsync(Slash15);

        Assert.True(pings >= 1);
    }

    [Fact]
    public async Task ScanAtTheCap_IsNeverAsked()
    {
        bool asked = false;
        var vm = Create((_, _) => new PingResult(false, null, null), _ => { asked = true; return false; });

        await vm.RunScanAsync(new[] { "10.0.0" });

        Assert.False(asked);
    }
}
