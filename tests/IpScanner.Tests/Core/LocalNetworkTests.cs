using System.Collections.Generic;
using IpScanner.Core.Models;
using IpScanner.Core.Net;
using Xunit;

namespace IpScanner.Tests.Core;

public class LocalNetworkTests
{
    private static OwnIpv6Address Own(string name, int index, string address, int prefix)
        => new(name, index, Ipv6Address.Parse(address), prefix);

    private static NetworkInfo Net(params OwnIpv6Address[] v6) => new()
    {
        Ip = "192.168.1.20",
        SubnetMask = "255.255.255.0",
        Ipv6 = new List<OwnIpv6Address>(v6),
    };

    [Theory]
    [InlineData("192.168.1.1", true)]
    [InlineData("192.168.1.254", true)]
    [InlineData("192.168.2.1", false)]
    [InlineData("10.0.0.5", false)]
    [InlineData("not an address", false)]
    public void Contains_Ipv4_UsesTheDetectedSubnet(string ip, bool expected)
        => Assert.Equal(expected, LocalNetwork.Contains(ip, Net()));

    [Fact]
    public void Contains_Ipv4_WithoutAMaskIsNeverLocal()
        => Assert.False(LocalNetwork.Contains("192.168.1.1", new NetworkInfo { Ip = "192.168.1.20", SubnetMask = null }));

    [Theory]
    [InlineData("fe80::1%12", true)]
    [InlineData("fe80::1%Ethernet", true)]
    [InlineData("fe80::1%ethernet", true)]
    [InlineData("fe80::1%99", false)]
    [InlineData("fe80::1", true)]
    [InlineData("2001:db8:1:2::77", true)]
    [InlineData("2001:db8:1:3::77", false)]
    [InlineData("fd00:1::5", true)]
    [InlineData("fd00:2::5", false)]
    [InlineData("::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("2606:4700::1111", false)]
    [InlineData("[2001:db8:1:2::9]", true)]
    public void Contains_Ipv6_LinkLocalByScopeAndOtherAddressesByOwnPrefix(string ip, bool expected)
    {
        var net = Net(
            Own("Ethernet", 12, "fe80::abcd%12", 64),
            Own("Ethernet", 12, "2001:db8:1:2::10", 64),
            Own("Ethernet", 12, "fd00:1::10", 64));
        Assert.Equal(expected, LocalNetwork.Contains(ip, net));
    }

    [Fact]
    public void Contains_Ipv6_WithoutAnyOwnIpv6IsNeverLocal()
    {
        var net = Net();
        Assert.False(LocalNetwork.Contains("fe80::1%12", net));
        Assert.False(LocalNetwork.Contains("2001:db8::1", net));
        Assert.True(LocalNetwork.Contains("192.168.1.5", net));
    }

    [Fact]
    public void IsOnLinkIpv6_AGlobalTargetNeverMatchesTheLinkLocalPrefix()
    {
        var own = new[] { Own("Ethernet", 12, "fe80::abcd%12", 64) };
        Assert.False(LocalNetwork.IsOnLinkIpv6("2001:db8::1", own));
        Assert.True(LocalNetwork.IsOnLinkIpv6("fe80::1%12", own));
    }

    [Fact]
    public void IsOnLinkIpv6_UsesTheOwnPrefixLength()
    {
        var own = new[] { Own("Ethernet", 12, "2001:db8:1::10", 48) };
        Assert.True(LocalNetwork.IsOnLinkIpv6("2001:db8:1:9::1", own));
        Assert.False(LocalNetwork.IsOnLinkIpv6("2001:db8:2::1", own));
    }
}
