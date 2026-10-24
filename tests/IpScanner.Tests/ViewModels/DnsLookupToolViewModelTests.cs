using IpScanner.ViewModels.Tools;
using Xunit;

namespace IpScanner.Tests.ViewModels;

public class DnsLookupToolViewModelTests
{
    [Theory]
    [InlineData("192.168.1.1", DnsLookupToolViewModel.ModeAuto, DnsLookupToolViewModel.ModeReverse)]
    [InlineData("example.com", DnsLookupToolViewModel.ModeAuto, DnsLookupToolViewModel.ModeForward)]
    [InlineData("192.168.1.1", DnsLookupToolViewModel.ModeForward, DnsLookupToolViewModel.ModeForward)]
    [InlineData("example.com", DnsLookupToolViewModel.ModeReverse, DnsLookupToolViewModel.ModeReverse)]
    public void ResolveDirection_PicksReverseForAnIpAndForwardForEverythingElseWhenAuto(
        string target, string mode, string expected)
        => Assert.Equal(expected, DnsLookupToolViewModel.ResolveDirection(target, mode));

    [Fact]
    public void GetConfiguredDnsServers_ReturnsOnlyIpv4Addresses()
    {
        // Just needs to run without throwing on the sandbox/CI machine and return
        // a list of parseable IPv4 strings - the actual servers depend on the host.
        var servers = DnsLookupToolViewModel.GetConfiguredDnsServers();
        Assert.All(servers, s => Assert.True(System.Net.IPAddress.TryParse(s, out _)));
    }

    [Fact]
    public void CanStart_FalseWhenTargetEmptyOrBusy()
    {
        var vm = new DnsLookupToolViewModel();
        Assert.False(vm.CanStart);
        vm.Target = "example.com";
        Assert.True(vm.CanStart);
        vm.IsBusy = true;
        Assert.False(vm.CanStart);
    }
}
