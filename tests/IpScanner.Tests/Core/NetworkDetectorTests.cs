using IpScanner.Core.Net;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

public class NetworkDetectorTests
{
    [Fact]
    public void LocalIp_IsValidIpv4_OnConnectedMachine()
    {
        var ip = NetworkDetector.GetLocalIpFast();
        if (ip is not null) Assert.True(Ipv4.IsValid(ip));
    }

    [Fact]
    public void DetectAll_PopulatesNetworkInfo()
    {
        var info = NetworkDetector.DetectFast();
        Assert.NotNull(info);
        if (info.Ip is not null) Assert.True(Ipv4.IsValid(info.Ip));
    }
}
