using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

    [Fact]
    public void Split_Slash24IntoFour26s_ListsEachSubnet()
    {
        var (rows, total) = CalculatorToolViewModel.Split("192.168.1.77", "/24", 26)!.Value;
        Assert.Equal(4, total);
        Assert.Equal(4, rows.Count);
        Assert.Equal(new CalculatorToolViewModel.SubnetRow("192.168.1.0", "192.168.1.1", "192.168.1.62", "192.168.1.63", "62"), rows[0]);
        Assert.Equal(new CalculatorToolViewModel.SubnetRow("192.168.1.64", "192.168.1.65", "192.168.1.126", "192.168.1.127", "62"), rows[1]);
        Assert.Equal("192.168.1.192", rows[3].Network);
        Assert.Equal("192.168.1.255", rows[3].Broadcast);
    }

    [Fact]
    public void Split_AcceptsDottedMask()
    {
        var (rows, total) = CalculatorToolViewModel.Split("10.0.0.0", "255.255.255.0", 25)!.Value;
        Assert.Equal(2, total);
        Assert.Equal(new[] { "10.0.0.0", "10.0.0.128" }, rows.Select(r => r.Network));
    }

    [Fact]
    public void Split_HostPrefixes_HandleTheSpecialCases()
    {
        var p31 = CalculatorToolViewModel.Split("10.0.0.0", "/30", 31)!.Value;
        Assert.Equal(2, p31.Total);
        Assert.Equal(new CalculatorToolViewModel.SubnetRow("10.0.0.0", "10.0.0.0", "10.0.0.1", "10.0.0.1", "2"), p31.Rows[0]);
        var p32 = CalculatorToolViewModel.Split("10.0.0.0", "/31", 32)!.Value;
        Assert.Equal(new CalculatorToolViewModel.SubnetRow("10.0.0.0", "10.0.0.0", "10.0.0.0", "10.0.0.0", "1"), p32.Rows[0]);
    }

    [Fact]
    public void Split_CapsTheRowsButReportsTheFullCount()
    {
        var (rows, total) = CalculatorToolViewModel.Split("10.0.0.0", "/8", 24)!.Value;
        Assert.Equal(65536, total);
        Assert.Equal(CalculatorToolViewModel.SplitRowCap, rows.Count);
        Assert.Equal("10.0.255.0", rows[255].Network);
        var widest = CalculatorToolViewModel.Split("0.0.0.0", "/1", 32)!.Value;
        Assert.Equal(1L << 31, widest.Total);
        Assert.Equal(CalculatorToolViewModel.SplitRowCap, widest.Rows.Count);
    }

    [Theory]
    [InlineData("192.168.1.0", "/24", 24)]
    [InlineData("192.168.1.0", "/24", 20)]
    [InlineData("192.168.1.0", "/24", 33)]
    [InlineData("nonsense", "/24", 26)]
    [InlineData("192.168.1.0", "", 26)]
    public void Split_InvalidInputReturnsNull(string address, string mask, int newPrefix)
        => Assert.Null(CalculatorToolViewModel.Split(address, mask, newPrefix));

    [Fact]
    public void SplitSection_FollowsTheSubnetAndThePrefixInput()
    {
        var vm = new CalculatorToolViewModel();
        Assert.False(vm.ShowSplit);
        vm.SubnetAddress = "192.168.1.0";
        vm.SubnetMask = "/24";
        Assert.True(vm.ShowSplit);
        Assert.False(vm.HasSplitRows);

        vm.SplitPrefix = "/26";
        Assert.True(vm.HasSplitRows);
        Assert.Equal(4, vm.SplitRows.Count);
        Assert.Equal("", vm.SplitError);
        Assert.Contains("4", vm.SplitSummary);

        vm.SplitPrefix = "26";
        Assert.Equal(4, vm.SplitRows.Count);

        vm.SplitPrefix = "24";
        Assert.False(vm.HasSplitRows);
        Assert.NotEqual("", vm.SplitError);
        Assert.Equal("", vm.SplitSummary);

        vm.SplitPrefix = "";
        Assert.Equal("", vm.SplitError);

        vm.SplitPrefix = "/27";
        vm.SubnetMask = "/23";
        Assert.Equal(16, vm.SplitRows.Count);
        Assert.Equal("192.168.0.0", vm.SplitRows[0].Network);
    }

    [Fact]
    public void SplitSection_ShowsAHintWhenTheTableIsCapped()
    {
        var vm = new CalculatorToolViewModel { SubnetAddress = "10.0.0.0", SubnetMask = "/8", SplitPrefix = "/24" };
        Assert.Equal(CalculatorToolViewModel.SplitRowCap, vm.SplitRows.Count);
        Assert.Contains("65536", vm.SplitSummary.Replace(".", "").Replace(",", ""));
    }

    [Fact]
    public void SplitSection_IsHiddenForIpv6AndInvalidNetworks()
    {
        var vm = new CalculatorToolViewModel { SubnetAddress = "192.168.1.0", SubnetMask = "/24", SplitPrefix = "/26" };
        Assert.Equal(4, vm.SplitRows.Count);

        vm.SubnetAddress = "2001:db8::";
        vm.SubnetMask = "/64";
        Assert.False(vm.ShowSplit);
        Assert.Empty(vm.SplitRows);

        vm.SubnetAddress = "garbage";
        Assert.False(vm.ShowSplit);
        Assert.Empty(vm.SplitRows);
    }
}
