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

    [Fact]
    public void RecordPing_NotCountedTowardTarget_UpdatesStatsButNotCurrentPings()
    {
        // Offline-recheck and offline-burst probes must update
        // online/offline state and stats like any other ping, but must not
        // grow the displayed "N/target" count - otherwise it can exceed its
        // own target once the device rejoins the counted analysis phase.
        var d = new Device("192.168.1.4");
        d.RecordPing(new PingResult(false, null, null), countsTowardTarget: false);
        Assert.Equal(0, d.CurrentPings);
        Assert.Equal(1, d.FailCount);
        d.RecordPing(new PingResult(true, 3.0, 64), countsTowardTarget: false);
        Assert.Equal(0, d.CurrentPings);
        Assert.Equal(1, d.SuccessCount);
        Assert.True(d.IsOnline);
    }

    [Fact]
    public void ResetPingCount_ZeroesCountOnly_KeepsLifetimeStats()
    {
        var d = new Device("192.168.1.5");
        d.RecordPing(new PingResult(true, 5.0, 64));
        d.RecordPing(new PingResult(true, 5.0, 64));
        Assert.Equal(2, d.CurrentPings);
        d.ResetPingCount();
        Assert.Equal(0, d.CurrentPings);
        Assert.Equal(2, d.SuccessCount);   // lifetime stats untouched
        Assert.Equal(5.0, d.AvgMs);
    }

    [Fact]
    public void LossPercent_TenPingsThreeFailures_Is30Percent()
    {
        var d = new Device("192.168.1.6") { OfflineAfterFailures = 100 };
        for (int i = 0; i < 7; i++) d.RecordPing(new PingResult(true, 1.0, 64));
        for (int i = 0; i < 3; i++) d.RecordPing(new PingResult(false, null, null));
        Assert.Equal(30.0, d.LossPercent);
    }

    [Fact]
    public void LossPercent_NoPingsYet_IsNull()
        => Assert.Null(new Device("192.168.1.7").LossPercent);

    [Fact]
    public void JitterMs_ConstantLatency_IsZero()
    {
        var d = new Device("192.168.1.8");
        for (int i = 0; i < 5; i++) d.RecordPing(new PingResult(true, 10.0, 64));
        Assert.Equal(0.0, d.JitterMs);
    }

    [Fact]
    public void JitterMs_AlternatingTenAndThirtyMs_IsTwenty()
    {
        var d = new Device("192.168.1.9");
        for (int i = 0; i < 4; i++)
            d.RecordPing(new PingResult(true, i % 2 == 0 ? 10.0 : 30.0, 64));
        Assert.Equal(20.0, d.JitterMs);
    }

    [Fact]
    public void JitterMs_OnlyOneSample_IsNull()
    {
        var d = new Device("192.168.1.10");
        d.RecordPing(new PingResult(true, 10.0, 64));
        Assert.Null(d.JitterMs);
    }
}
