using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

public class ScanEngineTests
{
    [Fact]
    public async Task Scan_OnlyOneHostOnline_ProducesOneOnlineDevice()
    {
        PingResult Fake(string ip, int _) =>
            ip.EndsWith(".1") ? new PingResult(true, 1.0, 64) : new PingResult(false, null, null);

        var engine = new ScanEngine(Fake);
        var cfg = new ScanConfig { PingCount = 2, PingIntervalMs = 0, InitPingThreads = 16, PingThreads = 16 };
        var devices = new List<Device>();
        engine.DeviceUpdated += d => { lock (devices) { if (!devices.Contains(d)) devices.Add(d); } };

        await engine.ScanAsync(new[] { "10.0.0" }, cfg, CancellationToken.None);

        var online = devices.Where(d => d.IsOnline).ToList();
        Assert.Single(online);
        Assert.Equal("10.0.0.1", online[0].Ip);
        Assert.Equal(2, online[0].SuccessCount);
    }

    [Fact]
    public async Task Scan_Cancelled_StopsEarly()
    {
        var engine = new ScanEngine((_, _) => new PingResult(true, 1.0, 64));
        var cfg = new ScanConfig { PingCount = 1000000, PingIntervalMs = 5 };
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(100);
        await engine.ScanAsync(new[] { "10.0.0" }, cfg, cts.Token); // must return, not hang
        Assert.True(true);
    }

    [Fact]
    public async Task Scan_TracksPingCounts()
    {
        PingResult Fake(string ip, int _) =>
            ip.EndsWith(".1") ? new PingResult(true, 1.0, 64) : new PingResult(false, null, null);
        var engine = new ScanEngine(Fake);
        var cfg = new ScanConfig { PingCount = 3, PingIntervalMs = 0 };

        await engine.ScanAsync(new[] { "10.0.0" }, cfg, CancellationToken.None);

        // .1 = 3 successes; remaining 253 hosts each got 1 failed discovery ping.
        Assert.Equal(3, engine.Progress.SuccessPings);
        Assert.Equal(253, engine.Progress.FailedPings);
        Assert.True(engine.Progress.SkippedPings >= 253 * 2); // 2 analysis pings/offline host skipped
    }
}
