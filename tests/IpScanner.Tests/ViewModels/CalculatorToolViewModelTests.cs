using System;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core.Export;
using IpScanner.ViewModels.Tools;
using Xunit;

namespace IpScanner.Tests.ViewModels;

public class CalculatorToolViewModelTests
{
    [Theory]
    [InlineData("192.168.1.10", "/24", "192.168.1.0", "192.168.1.255", "192.168.1.1", "192.168.1.254", 254, "0.0.0.255", 24)]
    [InlineData("10.0.0.5", "255.255.255.0", "10.0.0.0", "10.0.0.255", "10.0.0.1", "10.0.0.254", 254, "0.0.0.255", 24)]
    [InlineData("172.16.5.20", "/20", "172.16.0.0", "172.16.15.255", "172.16.0.1", "172.16.15.254", 4094, "0.0.15.255", 20)]
    [InlineData("192.168.1.5", "/30", "192.168.1.4", "192.168.1.7", "192.168.1.5", "192.168.1.6", 2, "0.0.0.3", 30)]
    [InlineData("192.168.1.5", "/31", "192.168.1.4", "192.168.1.5", "192.168.1.4", "192.168.1.5", 2, "0.0.0.1", 31)]
    [InlineData("192.168.1.5", "/32", "192.168.1.5", "192.168.1.5", "192.168.1.5", "192.168.1.5", 1, "0.0.0.0", 32)]
    public void Calculate_MatchesHandCheckedSubnets(
        string address, string mask, string network, string broadcast,
        string firstHost, string lastHost, long usableHosts, string wildcard, int cidr)
    {
        var r = CalculatorToolViewModel.Calculate(address, mask);
        Assert.NotNull(r);
        Assert.Equal(network, r!.Value.Network);
        Assert.Equal(broadcast, r.Value.Broadcast);
        Assert.Equal(firstHost, r.Value.FirstHost);
        Assert.Equal(lastHost, r.Value.LastHost);
        Assert.Equal(usableHosts, r.Value.UsableHosts);
        Assert.Equal(wildcard, r.Value.Wildcard);
        Assert.Equal(cidr, r.Value.Cidr);
    }

    [Fact]
    public void Calculate_AcceptsThePrefixInTheAddressBox()
    {
        var inline = CalculatorToolViewModel.Calculate("192.168.2.0/24", "");
        Assert.NotNull(inline);
        Assert.Equal(CalculatorToolViewModel.Calculate("192.168.2.0", "/24"), inline);
        Assert.Null(CalculatorToolViewModel.Calculate("192.168.2.0/33", ""));
    }

    [Fact]
    public void Calculate_BareNumberMaskEqualsSlashAndDottedForm()
    {
        var bare = CalculatorToolViewModel.Calculate("192.168.1.5", "24");
        Assert.NotNull(bare);
        Assert.Equal(CalculatorToolViewModel.Calculate("192.168.1.5", "/24"), bare);
        Assert.Equal(CalculatorToolViewModel.Calculate("192.168.1.5", "255.255.255.0"), bare);
        Assert.Null(CalculatorToolViewModel.Calculate("192.168.1.5", "33"));
    }

    [Theory]
    [InlineData("not-an-ip", "/24")]
    [InlineData("192.168.1.1", "")]
    [InlineData("192.168.1.1", "/33")]
    [InlineData("192.168.1.1", "255.0.255.0")]   // non-contiguous mask
    public void Calculate_InvalidInputReturnsNull(string address, string mask)
        => Assert.Null(CalculatorToolViewModel.Calculate(address, mask));

    [Fact]
    public void VendorLookup_FindsAKnownPrefix()
        => Assert.Equal("XEROX CORPORATION", MacVendorLookup.Instance.Lookup("00:00:00:11:22:33"));

    [Fact]
    public void VendorLookup_UnknownPrefixReturnsNullCleanly()
        => Assert.Null(MacVendorLookup.Instance.Lookup("FF:FF:FF:FF:FF:FF"));

    [Fact]
    public void ArpLookup_InvalidIp_CanLookupArpIsFalse()
    {
        var vm = new CalculatorToolViewModel { ArpIp = "not-an-ip" };
        Assert.False(vm.CanLookupArp);
    }

    [Fact]
    public void ArpLookup_ValidIp_CanLookupArpIsTrue()
    {
        var vm = new CalculatorToolViewModel { ArpIp = "192.168.1.1" };
        Assert.True(vm.CanLookupArp);
    }
    [Fact]
    public async Task ArpLookup_RunsOffTheCallingThread_AndBlocksASecondLookupMeanwhile()
    {
        using var release = new ManualResetEventSlim();
        int? resolverThread = null;
        var vm = new CalculatorToolViewModel(ip =>
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

    [Theory]
    [InlineData("2001:db8:abcd:12::5", "/64", "2001:db8:abcd:12::", "2001:db8:abcd:12:ffff:ffff:ffff:ffff", "2^64", 64)]
    [InlineData("2001:db8::1/48", "", "2001:db8::", "2001:db8:0:ffff:ffff:ffff:ffff:ffff", "2^80", 48)]
    [InlineData("2001:db8::7", "/104", "2001:db8::", "2001:db8::ff:ffff", "16777216", 104)]
    [InlineData("fe80::1", "128", "fe80::1", "fe80::1", "1", 128)]
    [InlineData("2001:db8::", "/32", "2001:db8::", "2001:db8:ffff:ffff:ffff:ffff:ffff:ffff", "2^96", 32)]
    public void CalculateIpv6_MatchesHandCheckedPrefixes(string address, string mask, string network, string last, string count, int prefix)
    {
        var r = CalculatorToolViewModel.CalculateIpv6(address, mask);
        Assert.NotNull(r);
        Assert.Equal(network, r!.Value.Network);
        Assert.Equal(last, r.Value.Last);
        Assert.Equal(count, r.Value.Count);
        Assert.Equal(prefix, r.Value.Prefix);
    }

    [Theory]
    [InlineData("2001:db8::1", "")]
    [InlineData("2001:db8::1", "/129")]
    [InlineData("2001:db8::1/64", "/64")]
    [InlineData("2001:db8::1", "abc")]
    [InlineData("not:an:address", "/64")]
    public void CalculateIpv6_RejectsUnusableInput(string address, string mask)
        => Assert.Null(CalculatorToolViewModel.CalculateIpv6(address, mask));

    [Theory]
    [InlineData("2001:db8::", "/32", "2001:db8:1::7", true)]
    [InlineData("2001:db8::", "/32", "2001:db9::1", false)]
    [InlineData("2001:db8::", "/32", "192.168.1.1", false)]
    [InlineData("192.168.1.0", "/24", "192.168.1.77", true)]
    [InlineData("192.168.1.0", "/24", "192.168.2.1", false)]
    [InlineData("192.168.1.0", "/24", "2001:db8::1", false)]
    [InlineData("192.168.1.0", "/24", "garbage", null)]
    [InlineData("2001:db8::", "/32", "garbage", null)]
    public void IsInside_WorksAcrossBothFamilies(string address, string mask, string candidate, bool? expected)
        => Assert.Equal(expected, CalculatorToolViewModel.IsInside(address, mask, candidate));

    [Fact]
    public void Ipv6Input_RelabelsTheResultAndHidesHostRange()
    {
        var vm = new CalculatorToolViewModel { SubnetAddress = "2001:db8::", SubnetMask = "/64" };
        Assert.True(vm.HasSubnetResult);
        Assert.True(vm.IsIpv6Result);
        Assert.Equal("2001:db8::", vm.NetworkResult);
        Assert.Equal("", vm.FirstHostResult);
        Assert.Equal("", vm.WildcardResult);
        Assert.Equal("/64", vm.CidrResult);

        vm.ContainsAddress = "2001:db8::42";
        Assert.True(vm.HasContainsResult);
        Assert.Equal(IpScanner.Core.Localization.Loc.CalcContainsYes, vm.ContainsResult);

        vm.SubnetAddress = "192.168.1.0"; vm.SubnetMask = "/24";
        Assert.False(vm.IsIpv6Result);
        Assert.Equal("192.168.1.1", vm.FirstHostResult);
    }
}
