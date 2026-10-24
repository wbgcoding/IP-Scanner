using System.Linq;
using IpScanner.Core.Net;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

public class NetworkMathEdgeTests
{
    [Theory]
    [InlineData("192.168.7.9", "255.255.0.0", "192.168.255.255")]   // /16
    [InlineData("10.20.30.40", "255.0.0.0", "10.255.255.255")]      // /8
    [InlineData("192.168.1.5", "255.255.255.252", "192.168.1.7")]   // /30
    [InlineData("192.168.1.5", "255.255.255.254", "192.168.1.5")]   // /31
    [InlineData("192.168.1.5", "255.255.255.255", "192.168.1.5")]   // /32
    public void DirectedBroadcast_CoversEveryCardPrefixLength(string ip, string mask, string expected)
        => Assert.Equal(expected, Ipv4.DirectedBroadcast(ip, mask));

    [Theory]
    [InlineData("192.168.1.9", "")]
    [InlineData("192.168.1.9", null)]
    [InlineData("999.1.1.1", "255.255.255.0")]
    public void DirectedBroadcast_UnusableInput_IsNull(string ip, string? mask)
        => Assert.Null(Ipv4.DirectedBroadcast(ip, mask));

    [Theory]
    [InlineData("255.255.255.0", 24)]
    [InlineData("255.255.255.252", 30)]
    [InlineData("255.255.255.255", 32)]
    [InlineData("0.0.0.0", 0)]
    [InlineData("255.255.0.255", null)]
    public void MaskToCidr_EdgeMasks(string mask, int? expected)
        => Assert.Equal(expected, Ipv4.MaskToCidr(mask));

    [Fact]
    public void HostRange_Slash24_SkipsNetworkAndBroadcast()
    {
        var r = Ipv4.HostRange("192.168.1.77", 24)!.Value;
        Assert.Equal("192.168.1.1", Ipv4.FromUInt(r.First));
        Assert.Equal("192.168.1.254", Ipv4.FromUInt(r.Last));
    }

    [Fact]
    public void HostRange_Slash31AndSlash32_KeepEveryAddress()
    {
        var p2p = Ipv4.HostRange("10.0.0.3", 31)!.Value;
        Assert.Equal("10.0.0.2", Ipv4.FromUInt(p2p.First));
        Assert.Equal("10.0.0.3", Ipv4.FromUInt(p2p.Last));
        var one = Ipv4.HostRange("10.0.0.3", 32)!.Value;
        Assert.Equal(one.First, one.Last);
    }

    [Theory]
    [InlineData("10.0.0.1", 0)]
    [InlineData("10.0.0.1", 33)]
    [InlineData("nope", 24)]
    public void HostRange_Unusable_IsNull(string ip, int cidr)
        => Assert.Null(Ipv4.HostRange(ip, cidr));

    [Fact]
    public void PrefixesForCidr_Slash16_YieldsAll256Prefixes_AndSlash8IsTooLarge()
    {
        var list = Ipv4.PrefixesForCidr("172.16.99.4", 16)!;
        Assert.Equal(256, list.Count);
        Assert.Equal("172.16.0", list[0]);
        Assert.Equal("172.16.255", list[^1]);
        Assert.Null(Ipv4.PrefixesForCidr("10.0.0.0", 8));
    }

    [Fact]
    public void PrefixesForCidr_Slash20_AlignsToTheRealNetwork()
    {
        var list = Ipv4.PrefixesForCidr("172.16.21.7", 20)!;
        Assert.Equal(16, list.Count);
        Assert.Equal("172.16.16", list[0]);
        Assert.Equal("172.16.31", list[^1]);
    }

    [Theory]
    [InlineData("65536")]
    [InlineData("abc")]
    [InlineData("5-3")]
    [InlineData("0-10")]
    [InlineData("1-65536")]
    [InlineData("22,abc")]
    public void PortSpec_TryParse_RejectsOutOfRangeAndGarbage(string text)
        => Assert.False(PortSpec.TryParse(text, out _));

    [Theory]
    [InlineData("1")]
    [InlineData("65535")]
    [InlineData("1-65535")]
    public void PortSpec_TryParse_AcceptsTheBounds(string text)
    {
        Assert.True(PortSpec.TryParse(text, out var spec));
        Assert.NotEmpty(spec.Tcp.Concat(spec.Udp));
    }
}
