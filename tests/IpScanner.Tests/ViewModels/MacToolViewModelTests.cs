using System;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core.Export;
using IpScanner.ViewModels.Tools;
using Xunit;

namespace IpScanner.Tests.ViewModels;

public class MacToolViewModelTests
{
    [Fact]
    public void VendorLookup_FindsAKnownPrefix()
        => Assert.Equal("XEROX CORPORATION", MacVendorLookup.Instance.Lookup("00:00:00:11:22:33"));

    [Fact]
    public void VendorLookup_UnknownPrefixReturnsNullCleanly()
        => Assert.Null(MacVendorLookup.Instance.Lookup("FF:FF:FF:FF:FF:FF"));

    [Fact]
    public void ArpLookup_InvalidIp_CanLookupArpIsFalse()
    {
        var vm = new MacToolViewModel { ArpIp = "not-an-ip" };
        Assert.False(vm.CanLookupArp);
    }

    [Fact]
    public void ArpLookup_ValidIp_CanLookupArpIsTrue()
    {
        var vm = new MacToolViewModel { ArpIp = "192.168.1.1" };
        Assert.True(vm.CanLookupArp);
    }
    [Fact]
    public async Task ArpLookup_RunsOffTheCallingThread_AndBlocksASecondLookupMeanwhile()
    {
        using var release = new ManualResetEventSlim();
        int? resolverThread = null;
        var vm = new MacToolViewModel(ip =>
        {
            resolverThread = Environment.CurrentManagedThreadId;
            release.Wait(TimeSpan.FromSeconds(10));
            return "AA:BB:CC:00:11:22";
        }) { ArpIp = "192.168.1.1" };

        var lookup = vm.LookupArpAsync();

        Assert.False(vm.CanLookupArp);
        Assert.False(lookup.IsCompleted);
        release.Set();
        await lookup;

        Assert.NotEqual(Environment.CurrentManagedThreadId, resolverThread);
        Assert.True(vm.HasArpResult);
        Assert.Equal("AA:BB:CC:00:11:22", vm.ArpMac);
        Assert.True(vm.CanLookupArp);
    }

    [Fact]
    public void VendorLookup_ShowsTheVendorAndHidesTheResultWhenCleared()
    {
        var vm = new MacToolViewModel { VendorMac = "00:00:00:11:22:33" };
        Assert.True(vm.HasVendorResult);
        Assert.Equal("XEROX CORPORATION", vm.VendorResult);
        vm.VendorMac = "";
        Assert.False(vm.HasVendorResult);
    }
}
