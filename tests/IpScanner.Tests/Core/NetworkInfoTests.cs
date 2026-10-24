using IpScanner.Core.Models;
using Xunit;

namespace IpScanner.Tests.Core;

public class NetworkInfoTests
{
    [Theory]
    [InlineData("192.168.5.77", "255.255.252.0", "192.168.4.0/22")]
    [InlineData("192.168.1.200", "255.255.255.128", "192.168.1.128/25")]
    [InlineData("192.168.1.20", "255.255.255.0", "192.168.1.0/24")]
    public void Cidr_UsesTheNetworkAddressOfTheMask(string ip, string mask, string expected)
        => Assert.Equal(expected, new NetworkInfo { Ip = ip, SubnetMask = mask }.Cidr);

    [Fact]
    public void Cidr_IsNullWithoutIp()
        => Assert.Null(new NetworkInfo().Cidr);
}
