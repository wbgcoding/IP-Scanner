using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

public class IcmpPingerTests
{
    [Fact]
    public void Ping_Loopback_Succeeds()
    {
        var r = IcmpPinger.Ping("127.0.0.1", 1000);
        Assert.True(r.Success);
        Assert.NotNull(r.LatencyMs);
    }

    [Fact]
    public void Ping_InvalidAddress_Fails()
    {
        var r = IcmpPinger.Ping("192.0.2.1", 200); // TEST-NET-1, unroutable
        Assert.False(r.Success);
    }
}
