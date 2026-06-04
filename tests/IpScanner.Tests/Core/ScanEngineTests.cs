using System;
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
        var cfg = new ScanConfig { PingCount = 2, PingIntervalMs = 0, ScanThreads = 16 };
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
    public async Task Scan_OfflineDevice_JoinsRunWhenItComesOnline()
    {
        var calls = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        PingResult Fake(string ip, int _)
        {
            int n = calls.AddOrUpdate(ip, 1, (_, v) => v + 1);
            if (ip.EndsWith(".1")) return new PingResult(true, 1.0, 64);
            // .2 is offline at discovery, answers from the second ping on.
            if (ip.EndsWith(".2") && n > 1) return new PingResult(true, 2.0, 64);
            return new PingResult(false, null, null);
        }

        var engine = new ScanEngine(Fake);
        var cfg = new ScanConfig
        {
            PingCount = 25, PingIntervalMs = 100, ScanThreads = 32,
            OfflineRecheckSeconds = 1,
        };

        await engine.ScanAsync(new[] { "10.0.0" }, cfg, CancellationToken.None);

        var d2 = engine.Devices.First(d => d.Ip == "10.0.0.2");
        Assert.True(d2.IsOnline);
        Assert.True(d2.SuccessCount > 1);   // recheck reply + analysis pings
    }

    [Fact]
    public async Task Enrichers_RunParallel_BetterRankReplacesWorse()
    {
        PingResult Fake(string ip, int _) =>
            ip.EndsWith(".1") ? new PingResult(true, 1.0, 64) : new PingResult(false, null, null);

        var slowDone = new System.Threading.ManualResetEventSlim();
        var enrichers = new Func<string, (string? mac, string? host)>[]
        {
            ip => { Thread.Sleep(250); slowDone.Set(); return (null, "good-name"); }, // rank 0, slow
            ip => (null, "fast-name"),                                                 // rank 1, instant
        };

        var engine = new ScanEngine(Fake, enrichers);
        var cfg = new ScanConfig { PingCount = 1, PingIntervalMs = 0, OfflineRecheckSeconds = 0 };
        await engine.ScanAsync(new[] { "10.0.0" }, cfg, CancellationToken.None);

        Assert.True(slowDone.Wait(5000));      // enrichment is fire-and-forget
        await Task.Delay(150);
        var d = engine.Devices.First(x => x.Ip == "10.0.0.1");
        Assert.Equal("good-name", d.Hostname); // rank 0 replaced the fast rank-1 result
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
