using IpScanner.Core.Models;
using Xunit;

namespace IpScanner.Tests;

public class SmokeTest
{
    [Fact]
    public void PingResult_StoresValues()
    {
        var r = new PingResult(true, 1.5, 64);
        Assert.True(r.Success);
        Assert.Equal(1.5, r.LatencyMs);
        Assert.Equal(64, r.Ttl);
    }
}
