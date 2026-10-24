using System.Linq;
using System.Net;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

public class OwnIpv6Tests
{
    private static NetworkDetector.InterfaceAddress Addr(string text, string name = "Ethernet", int index = 12,
        bool up = true, bool loopback = false, int prefix = 64, bool usable = true, long scope = 0)
    {
        var ip = IPAddress.Parse(text);
        if (scope != 0) ip.ScopeId = scope;
        return new(name, index, up, loopback, ip, prefix, usable);
    }

    [Fact]
    public void Link_local_addresses_carry_the_interface_scope_and_come_first()
    {
        var list = NetworkDetector.ListOwnIpv6(new[]
        {
            Addr("2001:db8:1::5"),
            Addr("fe80::1", scope: 12),
        });
        Assert.Equal(2, list.Count);
        Assert.True(list[0].IsLinkLocal);
        Assert.Equal("12", list[0].Address.Zone);
        Assert.Equal("fe80::1%12", list[0].Address.ToScopedString());
        Assert.Equal("2001:db8:1::5", list[1].Address.ToString());
        Assert.Null(list[1].Address.Zone);
        Assert.Equal("2001:db8:1::/64", list[1].Prefix.ToString());
    }

    [Fact]
    public void A_link_local_address_without_a_scope_falls_back_to_the_interface_index()
    {
        var list = NetworkDetector.ListOwnIpv6(new[] { Addr("fe80::2", index: 7) });
        Assert.Equal("7", list.Single().Address.Zone);
    }

    [Fact]
    public void Down_loopback_tentative_and_non_unicast_addresses_are_skipped()
    {
        var list = NetworkDetector.ListOwnIpv6(new[]
        {
            Addr("2001:db8::1", up: false),
            Addr("::1", loopback: true, name: "lo"),
            Addr("::1"),
            Addr("2001:db8::2", usable: false),
            Addr("ff02::1"),
            Addr("192.168.1.2"),
            Addr("2001:db8::3"),
        });
        Assert.Equal("2001:db8::3", list.Single().Address.ToString());
    }

    [Fact]
    public void Addresses_are_grouped_by_card()
    {
        var list = NetworkDetector.ListOwnIpv6(new[]
        {
            Addr("2001:db8::9", name: "WLAN", index: 3),
            Addr("2001:db8::8", name: "Ethernet"),
            Addr("fe80::8", name: "Ethernet", scope: 12),
        });
        Assert.Equal(new[] { "Ethernet", "Ethernet", "WLAN" }, list.Select(o => o.Interface).ToArray());
        Assert.True(list[0].IsLinkLocal);
    }

    [Fact]
    public void Live_listing_never_throws()
    {
        var list = NetworkDetector.ListOwnIpv6();
        Assert.All(list, o => Assert.NotEqual(IpScanner.Core.Net.Ipv6Kind.Multicast, o.Address.Kind));
    }
}
