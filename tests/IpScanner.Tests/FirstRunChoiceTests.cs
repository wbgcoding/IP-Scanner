using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core;
using IpScanner.Core.Models;
using IpScanner.ViewModels;
using Xunit;

namespace IpScanner.Tests;

public class FirstRunChoiceTests
{
    [Fact]
    public void FreshInstall_DefaultConfig_NoUpdateCheckAndInternetHeldBeforeTheAnswer()
    {
        var config = new ScanConfig();
        Assert.True(config.CheckForUpdates);
        Assert.True(config.EnableInternetPing);

        Assert.False(FirstRunChoice.StartUpdateCheckAtLaunch(freshInstall: true, config));
        Assert.True(FirstRunChoice.HoldInternetPinging(freshInstall: true));
    }

    [Fact]
    public void ExistingInstall_KeepsItsValues()
    {
        Assert.True(FirstRunChoice.StartUpdateCheckAtLaunch(false, new ScanConfig { CheckForUpdates = true }));
        Assert.False(FirstRunChoice.StartUpdateCheckAtLaunch(false, new ScanConfig { CheckForUpdates = false }));
        Assert.False(FirstRunChoice.HoldInternetPinging(freshInstall: false));
    }

    [Fact]
    public void Apply_UntickedBoxesPersistAsFalse_AndStartNothing()
    {
        var config = new ScanConfig();

        var start = FirstRunChoice.Apply(config, checkUpdates: false, measureInternet: false);

        Assert.False(config.CheckForUpdates);
        Assert.False(config.EnableInternetPing);
        Assert.Equal((false, false), start);
    }

    [Fact]
    public void Apply_TickedBoxesStartBothFeatures()
    {
        var config = new ScanConfig { CheckForUpdates = false, EnableInternetPing = false };

        var start = FirstRunChoice.Apply(config, checkUpdates: true, measureInternet: true);

        Assert.True(config.CheckForUpdates);
        Assert.True(config.EnableInternetPing);
        Assert.Equal((true, true), start);
    }

    [Fact]
    public async Task HeldViewModel_PingsNoInternetHostUntilReleased()
    {
        int pings = 0;
        var vm = new MainViewModel(
            pingFunc: (_, _) => { Interlocked.Increment(ref pings); return new PingResult(true, 5.0, 64); },
            detectNetwork: () => new NetworkInfo { Ip = "10.0.0.5" },
            dispatch: a => a());
        vm.Config = new ScanConfig { InternetHosts = new() { "203.0.113.1" }, EnableInternetPing = true };
        vm.InternetPingingHeld = true;

        await vm.PingInternetAsync(CancellationToken.None);
        Assert.Equal(0, pings);
        Assert.Empty(vm.InternetHosts);

        vm.InternetPingingHeld = false;
        await vm.PingInternetAsync(CancellationToken.None);
        Assert.Equal(1, pings);
    }
}
