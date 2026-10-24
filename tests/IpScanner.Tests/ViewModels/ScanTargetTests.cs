using System.Collections.Generic;
using IpScanner.Core.Models;
using IpScanner.Core.Net;
using IpScanner.ViewModels;
using Xunit;

namespace IpScanner.Tests.ViewModels;

public class ScanTargetTests
{
    private static NetworkInfo Net(string ip, string mask) => new() { Ip = ip, SubnetMask = mask };

    private static int HostCount(string? manual, NetworkInfo info)
    {
        var (prefixes, ranges) = MainViewModel.ResolveTargets(info, manual, new List<string>());
        return Ipv4.HostsInRanges(prefixes, ranges).Count;
    }

    [Fact]
    public void EmptyBox_UsesTheDetectedNetwork()
    {
        var (prefixes, _) = MainViewModel.ResolveTargets(Net("192.168.5.77", "255.255.252.0"), null, new List<string>());
        Assert.Equal(new[] { "192.168.4", "192.168.5", "192.168.6", "192.168.7" }, prefixes);
        Assert.Equal(1022, HostCount(null, Net("192.168.5.77", "255.255.252.0")));
    }

    [Fact]
    public void TypedValue_ReplacesTheDetectedNetwork()
    {
        var (prefixes, _) = MainViewModel.ResolveTargets(Net("192.168.5.77", "255.255.252.0"), "10.1.2.0/24", new List<string>());
        Assert.Equal(new[] { "10.1.2" }, prefixes);
    }

    [Fact]
    public void ChangedDetectedNetwork_IsFollowedByTheNextScan()
    {
        var (before, _) = MainViewModel.ResolveTargets(Net("192.168.1.20", "255.255.255.0"), null, new List<string>());
        var (after, _) = MainViewModel.ResolveTargets(Net("10.20.30.40", "255.255.255.0"), null, new List<string>());
        Assert.Equal(new[] { "192.168.1" }, before);
        Assert.Equal(new[] { "10.20.30" }, after);
    }

    [Fact]
    public void ConfiguredSubnets_AreAddedToTheDetectedNetwork()
    {
        var (prefixes, _) = MainViewModel.ResolveTargets(Net("192.168.1.20", "255.255.255.0"), null, new List<string> { "10.9.9.0/24" });
        Assert.Equal(new[] { "192.168.1", "10.9.9" }, prefixes);
    }
}
