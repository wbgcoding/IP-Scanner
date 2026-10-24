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

    // IcmpSendEcho2Ex source-address path, loopback to loopback.
    [Fact]
    public void Ping_Loopback_WithMatchingSourceIp_Succeeds()
    {
        var r = IcmpPinger.Ping("127.0.0.1", 1000, sourceIp: "127.0.0.1");
        Assert.True(r.Success);
        Assert.NotNull(r.LatencyMs);
    }

    // An empty/null sourceIp must behave exactly like the 2-arg overload.
    [Fact]
    public void Ping_Loopback_WithNullSourceIp_Succeeds()
    {
        var r = IcmpPinger.Ping("127.0.0.1", 1000, sourceIp: null);
        Assert.True(r.Success);
    }
}
