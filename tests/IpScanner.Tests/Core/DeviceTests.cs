using IpScanner.Core.Models;
using Xunit;

namespace IpScanner.Tests.Core;

public class DeviceTests
{
    [Fact]
    public void RecordPing_UpdatesStats()
    {
        var d = new Device("192.168.1.1");
        d.RecordPing(new PingResult(true, 2.0, 64));
        d.RecordPing(new PingResult(true, 4.0, 64));
        Assert.Equal(2, d.SuccessCount);
        Assert.Equal(2.0, d.MinMs);
        Assert.Equal(4.0, d.MaxMs);
        Assert.Equal(3.0, d.AvgMs);
        Assert.Equal(4.0, d.LastMs);
        Assert.True(d.IsOnline);
    }

    [Fact]
    public void RecordPing_Failure_CountsAndCanGoOffline()
    {
        var d = new Device("192.168.1.2") { OfflineAfterFailures = 2 };
        d.RecordPing(new PingResult(false, null, null));
        Assert.False(d.IsOnline);   // never answered
        d.RecordPing(new PingResult(true, 1.0, 64));
        Assert.True(d.IsOnline);
        d.RecordPing(new PingResult(false, null, null));
        d.RecordPing(new PingResult(false, null, null));
        Assert.True(d.WentOffline);  // 2 consecutive misses after being online
    }

    [Fact]
    public void OfflineSince_SetOnTransition_ClearedWhenBackOnline()
    {
        var d = new Device("192.168.1.3") { OfflineAfterFailures = 1 };
        Assert.Null(d.OfflineSince);                    // never seen
        d.RecordPing(new PingResult(false, null, null));
        Assert.Null(d.OfflineSince);                    // never online -> no transition
        d.RecordPing(new PingResult(true, 1.0, 64));
        d.RecordPing(new PingResult(false, null, null));
        var first = d.OfflineSince;
        Assert.NotNull(first);                          // online -> offline stamps the drop
        d.RecordPing(new PingResult(false, null, null));
        Assert.Equal(first, d.OfflineSince);            // repeated misses keep the stamp
        d.RecordPing(new PingResult(true, 1.0, 64));
        Assert.Null(d.OfflineSince);                    // back online clears it
    }
}
