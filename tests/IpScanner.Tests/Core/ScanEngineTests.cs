using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core.Models;
using IpScanner.Core.Net;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

public class ScanEngineTests
{
    [Fact]
    public void Preload_SeedsHostnameAndMac_WithWeakestRank()
    {
        var engine = new ScanEngine((_, _) => new PingResult(false, null, null));
        var known = new Device("10.0.0.5") { Hostname = "nas", Mac = "AA:BB:CC:00:00:05" };

        engine.Preload(new[] { known }, new ScanConfig());

        var d = Assert.Single(engine.Devices);
        Assert.Equal("nas", d.Hostname);
        Assert.Equal("AA:BB:CC:00:00:05", d.Mac);
        Assert.True(d.FromDb);
        // DB rank is the weakest - any live resolver (rank 0..n) replaces it.
        Assert.True(d.HostnameRank > 1000);
        Assert.True(d.MacRank > 1000);
    }

    [Fact]
    public async Task ScanAsync_WithRange_PingsOnlyTheRequestedAddresses()
    {
        var pinged = new System.Collections.Concurrent.ConcurrentBag<string>();
        PingResult Fake(string ip, int _)
        {
            pinged.Add(ip);
            return new PingResult(false, null, null);
        }

        var engine = new ScanEngine(Fake);
        var cfg = new ScanConfig { PingCount = 1, InitPingCount = 1, PingIntervalMs = 0, ScanThreads = 16 };
        var range = IpScanner.Core.Net.Ipv4.HostRange("10.0.0.20", 28)!.Value;

        await engine.ScanAsync(TargetSpec.FromPrefixes(new[] { "10.0.0" }, new[] { range }).Expand(), cfg, CancellationToken.None);

        Assert.Equal(14, pinged.Count);
        Assert.All(pinged, ip => Assert.Contains(ip, Enumerable.Range(17, 14).Select(i => $"10.0.0.{i}")));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task OfflineBurst_RecoversShortDropout()
    {
        int calls = 0;
        PingResult Fake(string ip, int _)
        {
            if (!ip.EndsWith(".1")) return new PingResult(false, null, null);
            int n = Interlocked.Increment(ref calls);
            // 1: discovery ok; 2-3: dropout (device goes offline); 4+: back again
            return n is 2 or 3 ? new PingResult(false, null, null) : new PingResult(true, 1.0, 64);
        }

        var engine = new ScanEngine(Fake);
        var cfg = new ScanConfig
        {
            PingCount = 3, PingIntervalMs = 0, ScanThreads = 16,
            OfflineAfterFailedPings = 2, OfflineRecheckSeconds = 0,
        };

        await engine.ScanAsync(TargetSpec.FromPrefixes(new[] { "10.0.0" }).Expand(), cfg, CancellationToken.None);
        var device = engine.Devices.Single(d => d.Ip == "10.0.0.1");
        Assert.False(device.IsOnline);          // dropped at scan end

        await Task.Delay(2000);                 // burst pings every 500 ms
        Assert.True(device.IsOnline);           // burst brought it back
    }

    // A muted device gets no rapid-fire offline burst when it drops -
    // it stays offline instead of recovering the way OfflineBurst would.
    [Fact]
    [Trait("Category", "Slow")]
    public async Task OfflineBurst_SkippedWhenDeviceIsMuted()
    {
        int calls = 0;
        using var muted = new ManualResetEventSlim();
        PingResult Fake(string ip, int _)
        {
            if (!ip.EndsWith(".1")) return new PingResult(false, null, null);
            int n = Interlocked.Increment(ref calls);
            if (n == 2) muted.Wait(10_000);   // hold the first failure until the device is muted
            return n is 2 or 3 ? new PingResult(false, null, null) : new PingResult(true, 1.0, 64);
        }

        var engine = new ScanEngine(Fake);
        var cfg = new ScanConfig
        {
            PingCount = 3, PingIntervalMs = 0, ScanThreads = 16,
            OfflineAfterFailedPings = 2, OfflineRecheckSeconds = 0,
        };

        var scanTask = engine.ScanAsync(TargetSpec.FromPrefixes(new[] { "10.0.0" }).Expand(), cfg, CancellationToken.None);
        Device? device = null;
        for (int i = 0; i < 200 && device is null; i++)
        {
            device = engine.Devices.FirstOrDefault(d => d.Ip == "10.0.0.1");
            if (device is null) await Task.Delay(5);
        }
        Assert.NotNull(device);
        device!.IsMuted = true;
        muted.Set();   // must land before the discovery->offline transition

        await scanTask;
        Assert.False(device.IsOnline);

        await Task.Delay(2000);           // would recover via the burst if not muted
        Assert.False(device.IsOnline);    // muted -> no burst -> stays offline
    }

    [Fact]
    public async Task Scan_OnlyOneHostOnline_ProducesOneOnlineDevice()
    {
        PingResult Fake(string ip, int _) =>
            ip.EndsWith(".1") ? new PingResult(true, 1.0, 64) : new PingResult(false, null, null);

        var engine = new ScanEngine(Fake);
        var cfg = new ScanConfig { PingCount = 2, PingIntervalMs = 0, ScanThreads = 16 };
        var devices = new List<Device>();
        engine.DeviceUpdated += d => { lock (devices) { if (!devices.Contains(d)) devices.Add(d); } };

        await engine.ScanAsync(TargetSpec.FromPrefixes(new[] { "10.0.0" }).Expand(), cfg, CancellationToken.None);

        var online = devices.Where(d => d.IsOnline).ToList();
        Assert.Single(online);
        Assert.Equal("10.0.0.1", online[0].Ip);
        Assert.Equal(2, online[0].SuccessCount);
        // The displayed "N/target" must reach its own target exactly - not
        // fall short (ResetPingCount wiping the discovery credit) and not
        // overshoot (the analysis loop re-supplying a ping already credited).
        Assert.Equal(online[0].TargetPings, online[0].CurrentPings);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task Scan_OfflineAtDiscoveryThenReconnects_DisplayReachesTargetAndGlobalTotalStaysWithinBudget()
    {
        // .2 stays online the whole time purely to keep ScanAsync's own
        // wait-loop alive (it cancels the recheck loop the moment every
        // queued analysis task finishes) long enough for .1 below to actually
        // be caught by that recheck loop. .1 fails discovery outright, so it
        // never goes online mid-analysis and never triggers the separate
        // OfflineBurst path - the recheck loop is the only way it reconnects,
        // which is exactly the path this test needs to exercise.
        int calls = 0;
        PingResult Fake(string ip, int _)
        {
            if (ip.EndsWith(".2")) return new PingResult(true, 1.0, 64);
            if (!ip.EndsWith(".1")) return new PingResult(false, null, null);
            int n = Interlocked.Increment(ref calls);
            return n <= 1 ? new PingResult(false, null, null) : new PingResult(true, 1.0, 64);
        }

        var engine = new ScanEngine(Fake);
        var cfg = new ScanConfig
        {
            // Pinned IPs only, no subnets scanned - isolates the global totals
            // to exactly these two devices instead of mixing in 253 unrelated
            // hosts' own failed-discovery contributions.
            PinnedIps = new List<string> { "10.0.0.1", "10.0.0.2" },
            PingCount = 6, PingIntervalMs = 600, ScanThreads = 16,
            InitPingCount = 1, OfflineRecheckSeconds = 1,
        };

        await engine.ScanAsync(TargetSpec.FromPrefixes(Array.Empty<string>()).Expand(), cfg, CancellationToken.None);

        var target = engine.Devices.Single(d => d.Ip == "10.0.0.1");
        Assert.True(target.IsOnline);   // recheck loop caught the reconnect before ScanAsync returned
        // The reconnect's own re-analysis phase must reach its own target too...
        Assert.Equal(target.TargetPings, target.CurrentPings);
        // ...but must not re-spend the global Pings X/Y budget a second time:
        // that denominator is sized once, for one pass per device (6 each for
        // the two pinned IPs here) - .1's failed discovery attempt is its only
        // legitimate contribution; its reconnect-triggered re-analysis must
        // add nothing more to it.
        long budget = cfg.PingCount * 2;
        long spent = engine.Progress.SuccessPings + engine.Progress.FailedPings;
        Assert.True(spent <= budget, $"global total overshot: {spent} > {budget}");
    }

    [Fact]
    public async Task Scan_Cancelled_StopsEarly()
    {
        var engine = new ScanEngine((_, _) => new PingResult(true, 1.0, 64));
        var cfg = new ScanConfig { PingCount = 1000000, PingIntervalMs = 5 };
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(100);
        await engine.ScanAsync(TargetSpec.FromPrefixes(new[] { "10.0.0" }).Expand(), cfg, cts.Token); // must return, not hang
        Assert.True(true);
    }

    [Fact]
    [Trait("Category", "Slow")]
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

        await engine.ScanAsync(TargetSpec.FromPrefixes(new[] { "10.0.0" }).Expand(), cfg, CancellationToken.None);

        var d2 = engine.Devices.First(d => d.Ip == "10.0.0.2");
        Assert.True(d2.IsOnline);
        Assert.True(d2.SuccessCount > 1);   // recheck reply + analysis pings
    }

    [Fact]
    public async Task Scan_InitPingCount_ControlsDiscoveryAttempts()
    {
        var engine = new ScanEngine((_, _) => new PingResult(false, null, null));
        var cfg = new ScanConfig { PingCount = 0, InitPingCount = 3, ScanThreads = 64, OfflineRecheckSeconds = 0 };

        await engine.ScanAsync(TargetSpec.FromPrefixes(new[] { "10.0.0" }).Expand(), cfg, CancellationToken.None);

        // Every offline IP gets exactly InitPingCount discovery attempts.
        Assert.Equal(254L * 3, engine.Progress.FailedPings);
    }

    [Fact]
    public async Task Enrichers_RunParallel_BetterRankReplacesWorse()
    {
        PingResult Fake(string ip, int _) =>
            ip.EndsWith(".1") ? new PingResult(true, 1.0, 64) : new PingResult(false, null, null);

        var slowDone = new System.Threading.ManualResetEventSlim();
        var enrichers = new[]
        {
            EnrichTechnique.FromSync(ip => { Thread.Sleep(250); slowDone.Set(); return (null, "good-name"); }), // rank 0, slow
            EnrichTechnique.FromSync(ip => (null, "fast-name")),                                                 // rank 1, instant
        };

        var engine = new ScanEngine(Fake, enrichers);
        var cfg = new ScanConfig { PingCount = 1, PingIntervalMs = 0, OfflineRecheckSeconds = 0 };
        await engine.ScanAsync(TargetSpec.FromPrefixes(new[] { "10.0.0" }).Expand(), cfg, CancellationToken.None);

        Assert.True(slowDone.Wait(5000));      // enrichment is fire-and-forget
        await Task.Delay(150);
        var d = engine.Devices.First(x => x.Ip == "10.0.0.1");
        Assert.Equal("good-name", d.Hostname); // rank 0 replaced the fast rank-1 result
    }

    [Fact]
    public async Task Enrichment_GateIsPerEngine_StuckLookupOnOneEngineDoesNotBlockAnother()
    {
        PingResult AllOnline(string ip, int _) => new PingResult(true, 1.0, 64);
        var release = new ManualResetEventSlim();

        // Saturate the first engine's enrichment gate (24 slots) with resolvers that
        // block until released - a whole /24 comes online, far more than the gate size.
        var stuckEnrichers = new[]
        {
            EnrichTechnique.FromSync(ip => { release.Wait(5000); return (null, "stuck"); }),
        };
        var engine1 = new ScanEngine(AllOnline, stuckEnrichers);
        var cfg = new ScanConfig { PingCount = 1, PingIntervalMs = 0, OfflineRecheckSeconds = 0 };
        await engine1.ScanAsync(TargetSpec.FromPrefixes(new[] { "10.1.0" }).Expand(), cfg, CancellationToken.None);
        await Task.Delay(200); // let the fire-and-forget enrichment actually grab gate slots

        var fastEnrichers = new[] { EnrichTechnique.FromSync(ip => (null, "quick-name")) };
        var engine2 = new ScanEngine(AllOnline, fastEnrichers);
        await engine2.ScanAsync(TargetSpec.FromPrefixes(new[] { "10.2.0" }).Expand(), cfg, CancellationToken.None);

        // If the gate were still shared across engine instances, this would block behind
        // engine1's 24 stuck permits instead of resolving within the wait below.
        var d = engine2.Devices.First(x => x.Ip == "10.2.0.1");
        Assert.True(SpinWait.SpinUntil(() => d.Hostname == "quick-name", 2000));

        release.Set();
    }

    [Fact]
    public async Task Scan_DeviceDropsMidAnalysisAndRecovers_EndsOnlineWithoutHanging()
    {
        // A device whose analysis quota ends with it online (whether
        // it stayed online throughout, or dropped and recovered mid-loop) must
        // not leave the engine's internal bookkeeping in a state that blocks
        // any later recheck for that IP - exercised here via a mid-loop drop
        // and recovery, the one case the old code (`!device.IsOnline` guard on
        // the analyzing-set release) handled differently from a device that
        // was online the whole time. The main assertion is a clean, timely
        // completion with the device correctly online.
        int calls = 0;
        PingResult Fake(string ip, int _)
        {
            if (!ip.EndsWith(".1")) return new PingResult(false, null, null);
            int n = Interlocked.Increment(ref calls);
            // n=1: discovery ok. n=2: one analysis ping fails (drop). n=3+: recovered.
            return n == 2 ? new PingResult(false, null, null) : new PingResult(true, 1.0, 64);
        }

        var engine = new ScanEngine(Fake);
        var cfg = new ScanConfig
        {
            PingCount = 4, PingIntervalMs = 50, ScanThreads = 16,
            OfflineAfterFailedPings = 1, OfflineRecheckSeconds = 0,
        };

        await engine.ScanAsync(TargetSpec.FromPrefixes(new[] { "10.0.0" }).Expand(), cfg, CancellationToken.None);

        var device = engine.Devices.Single(d => d.Ip == "10.0.0.1");
        Assert.True(device.IsOnline);
    }

    [Fact]
    public async Task Scan_TracksPingCounts()
    {
        PingResult Fake(string ip, int _) =>
            ip.EndsWith(".1") ? new PingResult(true, 1.0, 64) : new PingResult(false, null, null);
        var engine = new ScanEngine(Fake);
        var cfg = new ScanConfig { PingCount = 3, PingIntervalMs = 0 };

        await engine.ScanAsync(TargetSpec.FromPrefixes(new[] { "10.0.0" }).Expand(), cfg, CancellationToken.None);

        // .1 = 3 successes; remaining 253 hosts each got 1 failed discovery ping.
        Assert.Equal(3, engine.Progress.SuccessPings);
        Assert.Equal(253, engine.Progress.FailedPings);
        Assert.True(engine.Progress.SkippedPings >= 253 * 2); // 2 analysis pings/offline host skipped
    }

    // ApplyEnrichRank flagging a same-run MAC conflict.
    [Fact]
    public void ApplyEnrichRank_SameOrWorseRankDisagreesWithAppliedMac_FlagsConflict()
    {
        var device = new Device("10.0.0.9") { Mac = "AA:BB:CC:00:00:01", MacRank = 0 };

        var (changed, appliedMac) = ScanEngine.ApplyEnrichRank(device, rank: 1, mac: "AA:BB:CC:00:00:02", host: null);

        Assert.True(changed);
        Assert.Null(appliedMac); // worse rank never overwrites the applied MAC itself
        Assert.Equal("AA:BB:CC:00:00:01", device.Mac);
        Assert.True(device.HasMacConflictThisRun);
    }

    [Fact]
    public void ApplyEnrichRank_BetterRankReplacesMac_NoConflictFlagged()
    {
        var device = new Device("10.0.0.9") { Mac = "AA:BB:CC:00:00:01", MacRank = 5 };

        var (changed, appliedMac) = ScanEngine.ApplyEnrichRank(device, rank: 1, mac: "AA:BB:CC:00:00:02", host: null);

        Assert.True(changed);
        Assert.Equal("AA:BB:CC:00:00:02", appliedMac);
        Assert.Equal("AA:BB:CC:00:00:02", device.Mac);
        Assert.False(device.HasMacConflictThisRun);
    }

    [Fact]
    public void ApplyEnrichRank_SameOrWorseRankAgreesWithAppliedMac_NoConflictFlagged()
    {
        var device = new Device("10.0.0.9") { Mac = "AA:BB:CC:00:00:01", MacRank = 0 };

        var (changed, appliedMac) = ScanEngine.ApplyEnrichRank(device, rank: 1, mac: "AA:BB:CC:00:00:01", host: null);

        Assert.False(changed);
        Assert.Null(appliedMac);
        Assert.False(device.HasMacConflictThisRun);
    }

    [Fact]
    public async Task Dispose_RightAfterScan_WithResolversThatNeverReturn_ReturnsWithinThreeSeconds()
    {
        PingResult AllOnline(string ip, int _) => ip.EndsWith(".1")
            ? new PingResult(true, 1.0, 64) : new PingResult(false, null, null);
        var hanging = new[]
        {
            // Ignores its token completely: the engine must still tear down on time.
            (Func<string, CancellationToken, Task<(string? mac, string? host)>>)((ip, ct) =>
                new TaskCompletionSource<(string? mac, string? host)>().Task),
            EnrichTechnique.FromSync(ip => { Thread.Sleep(6000); return (null, "late"); }),
        };
        var engine = new ScanEngine(AllOnline, hanging);
        var cfg = new ScanConfig { PingCount = 1, PingIntervalMs = 0, OfflineRecheckSeconds = 0 };
        await engine.ScanAsync(TargetSpec.FromPrefixes(new[] { "10.3.0" }).Expand(), cfg, CancellationToken.None);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var failure = Record.Exception(engine.Dispose);
        started.Stop();

        Assert.Null(failure);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(3), "Dispose took " + started.Elapsed);
    }

    [Fact]
    public async Task Dispose_CancelsTheTokenOfResolversStillRunning()
    {
        PingResult AllOnline(string ip, int _) => ip.EndsWith(".1")
            ? new PingResult(true, 1.0, 64) : new PingResult(false, null, null);
        var sawCancel = new ManualResetEventSlim();
        var started = new ManualResetEventSlim();
        var enrichers = new[]
        {
            (Func<string, CancellationToken, Task<(string? mac, string? host)>>)(async (ip, ct) =>
            {
                started.Set();
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { sawCancel.Set(); throw; }
                return (null, null);
            }),
        };
        var engine = new ScanEngine(AllOnline, enrichers);
        var cfg = new ScanConfig { PingCount = 1, PingIntervalMs = 0, OfflineRecheckSeconds = 0 };
        await engine.ScanAsync(TargetSpec.FromPrefixes(new[] { "10.4.0" }).Expand(), cfg, CancellationToken.None);
        Assert.True(started.Wait(5000));

        engine.Dispose();

        Assert.True(sawCancel.Wait(3000));
    }
}
