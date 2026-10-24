using IpScanner.Core.Net;
using Xunit;

namespace IpScanner.Tests.Core;

public class Ipv4Tests
{
    [Theory]
    [InlineData("192.168.1.1", true)]
    [InlineData("256.1.1.1", false)]
    [InlineData("1.2.3", false)]
    [InlineData("abc", false)]
    public void IsValid_Works(string ip, bool expected)
        => Assert.Equal(expected, Ipv4.IsValid(ip));

    [Theory]
    [InlineData("192.168.1.1", true)]
    [InlineData("192.168.1.0/24", true)]
    [InlineData("192.168.1.0/32", true)]
    [InlineData("192.168.1.0/1", true)]
    [InlineData("192.168.1.0/0", false)]
    [InlineData("192.168.1.0/33", false)]
    [InlineData("192.168.1.0/24/1", false)]
    [InlineData("not-an-ip", false)]
    [InlineData("not-an-ip/24", false)]
    public void IsValidSubnetEntry_AcceptsIpOrCidr(string entry, bool expected)
        => Assert.Equal(expected, Ipv4.IsValidSubnetEntry(entry));

    [Fact]
    public void SubnetPrefix_TakesFirstThreeOctets()
        => Assert.Equal("192.168.1", Ipv4.SubnetPrefix("192.168.1.55"));

    [Fact]
    public void HostsInSubnet_Returns254()
        => Assert.Equal(254, Ipv4.HostsInSubnet("192.168.1").Count);

    [Theory]
    [InlineData("255.255.255.0", 24)]
    [InlineData("255.255.252.0", 22)]
    [InlineData("255.255.0.0", 16)]
    [InlineData("255.0.255.0", null)]   // non-contiguous - not a valid mask
    [InlineData("not-an-ip", null)]
    public void MaskToCidr_ConvertsContiguousMasksOnly(string mask, int? expected)
        => Assert.Equal(expected, Ipv4.MaskToCidr(mask));

    // The Wake-on-LAN tool's directed broadcast (host bits all set to 1).
    [Theory]
    [InlineData("192.168.1.55", "255.255.255.0", "192.168.1.255")]
    [InlineData("10.0.4.10", "255.255.252.0", "10.0.7.255")]
    [InlineData("192.168.1.55", "255.0.255.0", null)]   // non-contiguous mask
    [InlineData("not-an-ip", "255.255.255.0", null)]
    public void DirectedBroadcast_SetsHostBits(string ip, string mask, string? expected)
        => Assert.Equal(expected, Ipv4.DirectedBroadcast(ip, mask));

    [Fact]
    public void PrefixesForCidr_24_ReturnsOnePrefix()
        => Assert.Equal(new[] { "10.0.5" }, Ipv4.PrefixesForCidr("10.0.5.37", 24));

    [Fact]
    public void PrefixesForCidr_20_ReturnsExactly16NetworkAlignedPrefixes()
    {
        // /20 covers a 16-wide, 16-aligned block of the third octet - not the
        // full 0..255 range a naive "cidr>=16 -> 256 prefixes" bucket would give.
        var prefixes = Ipv4.PrefixesForCidr("10.0.37.5", 20);
        Assert.NotNull(prefixes);
        Assert.Equal(16, prefixes!.Count);
        Assert.Equal("10.0.32", prefixes[0]);
        Assert.Equal("10.0.47", prefixes[^1]);
    }

    [Fact]
    public void PrefixesForCidr_16_Returns256Prefixes()
        => Assert.Equal(256, Ipv4.PrefixesForCidr("10.0.0.0", 16)!.Count);

    [Fact]
    public void PrefixesForCidr_12_IsRejected_NotSilentlyExpandedToAFull8()
    {
        // /12 (4096 prefixes) must NOT fall back to a full /8 (65536 prefixes)
        // - it's rejected outright by the hard cap instead (4096 > MaxPrefixes).
        Assert.Null(Ipv4.PrefixesForCidr("10.0.0.0", 12));
    }

    [Fact]
    public void PrefixesForCidr_8_IsRejected_NotSilentlyCapped()
        => Assert.Null(Ipv4.PrefixesForCidr("10.0.0.0", 8));

    [Theory]
    [InlineData("10.0.0.7", 32, "10.0.0.7", "10.0.0.7")]
    [InlineData("10.0.0.7", 31, "10.0.0.6", "10.0.0.7")]
    [InlineData("10.0.0.37", 30, "10.0.0.37", "10.0.0.38")]
    [InlineData("10.0.0.37", 28, "10.0.0.33", "10.0.0.46")]
    [InlineData("10.0.5.37", 24, "10.0.5.1", "10.0.5.254")]
    [InlineData("10.0.5.37", 23, "10.0.4.1", "10.0.5.254")]
    public void HostRange_CoversExactlyTheRequestedNetwork(string ip, int cidr, string first, string last)
    {
        var range = Ipv4.HostRange(ip, cidr);
        Assert.NotNull(range);
        Ipv4.TryParseUInt(first, out var f);
        Ipv4.TryParseUInt(last, out var l);
        Assert.Equal((f, l), range!.Value);
    }

    [Theory]
    [InlineData("10.0.0.1", 0)]
    [InlineData("10.0.0.1", 33)]
    [InlineData("nope", 24)]
    public void HostRange_RejectsUnusableInput(string ip, int cidr)
        => Assert.Null(Ipv4.HostRange(ip, cidr));

    [Fact]
    public void HostsInRanges_ClipsAPrefixToTheRange()
    {
        var range = Ipv4.HostRange("10.0.0.37", 28)!.Value;
        var hosts = Ipv4.HostsInRanges(new[] { "10.0.0" }, new[] { range });
        Assert.Equal(14, hosts.Count);
        Assert.Equal("10.0.0.33", hosts[0]);
        Assert.Equal("10.0.0.46", hosts[^1]);
        Assert.Equal(254, Ipv4.HostsInRanges(new[] { "10.0.0" }, null).Count);
    }

    [Fact]
    public void HostsInRanges_SingleAddressNetworkYieldsOneHost()
    {
        var range = Ipv4.HostRange("10.0.0.7", 32)!.Value;
        Assert.Equal(new[] { "10.0.0.7" }, Ipv4.HostsInRanges(new[] { "10.0.0" }, new[] { range }));
    }
}
