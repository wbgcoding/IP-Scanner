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

    [Fact]
    public void PrefixToMask_Converts24()
        => Assert.Equal("255.255.255.0", Ipv4.PrefixToMask(24));

    [Fact]
    public void SubnetPrefix_TakesFirstThreeOctets()
        => Assert.Equal("192.168.1", Ipv4.SubnetPrefix("192.168.1.55"));

    [Fact]
    public void HostsInSubnet_Returns254()
        => Assert.Equal(254, Ipv4.HostsInSubnet("192.168.1").Count);
}
