using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

/// <summary>Timing templates, the adaptive round-trip estimate, the delay and the rate cap. All pacing
/// runs on a fake clock so a ten second scan costs no real time.</summary>
[Collection("PortScannerGate")]
public class ScanTimingTests
{
    /// <summary>Time for one caller at a time: a delay advances it by exactly that much.</summary>
    private sealed class SerialClock : IScanClock
    {
        private readonly object _sync = new();
        private long _now;

        public long NowMs { get { lock (_sync) return _now; } }

        public Task DelayAsync(int ms, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_sync) _now += ms;
            return Task.CompletedTask;
        }
    }

    /// <summary>Virtual time for concurrent callers: a delay completes when the clock reaches its target, and the
    /// clock jumps to the earliest target once every caller has been waiting for a moment.</summary>
    private sealed class VirtualClock : IScanClock, IDisposable
    {
        private readonly object _sync = new();
        private readonly SortedList<(long At, int Id), TaskCompletionSource> _waiters = new();
        private readonly System.Diagnostics.Stopwatch _quiet = System.Diagnostics.Stopwatch.StartNew();
        private readonly Thread _pump;
        private volatile bool _stop;
        private long _now;
        private int _ids;

        public VirtualClock()
        {
            _pump = new Thread(Pump) { IsBackground = true };
            _pump.Start();
        }

        public long NowMs { get { lock (_sync) return _now; } }

        public Task DelayAsync(int ms, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                _waiters.Add((_now + ms, _ids++), tcs);
                _quiet.Restart();
            }
            return tcs.Task;
        }

        private void Pump()
        {
            while (!_stop)
            {
                TaskCompletionSource? due = null;
                lock (_sync)
                {
                    if (_waiters.Count > 0 && _quiet.ElapsedMilliseconds >= 1)
                    {
                        var first = _waiters.First();
                        _waiters.RemoveAt(0);
                        _now = Math.Max(_now, first.Key.At);
                        _quiet.Restart();
                        due = first.Value;
                    }
                }
                if (due != null) due.SetResult(); else Thread.Sleep(0);
            }
        }

        public void Dispose() => _stop = true;
    }

    /// <summary>Records when and how many probes ran at once, answers from a script.</summary>
    private sealed class RecordingStrategy : IPortStrategy
    {
        private readonly IScanClock _clock;
        private readonly Func<int, int, PortResult> _answer;
        private int _inFlight, _maxInFlight;
        public List<(int Port, long AtMs)> Starts { get; } = new();
        public int MaxInFlight => _maxInFlight;

        public RecordingStrategy(IScanClock clock, Func<int, int, PortResult>? answer = null)
        {
            _clock = clock;
            _answer = answer ?? ((port, _) => new PortResult(port, NetProtocol.Tcp, PortState.Open, PortReason.Connected));
        }

        public NetProtocol Protocol => NetProtocol.Tcp;

        public async Task<PortResult?> ProbeAsync(string ip, int port, string name, CancellationToken ct)
        {
            int attempt;
            lock (Starts)
            {
                attempt = Starts.Count(s => s.Port == port);
                Starts.Add((port, _clock.NowMs));
            }
            int now = Interlocked.Increment(ref _inFlight);
            int seen;
            while (now > (seen = _maxInFlight)) Interlocked.CompareExchange(ref _maxInFlight, now, seen);
            await Task.Yield();
            Interlocked.Decrement(ref _inFlight);
            return _answer(port, attempt);
        }
    }

    private static Task<IReadOnlyList<PortResult>> Run(RecordingStrategy tcp, int[] ports, ScanTiming timing, IScanClock clock,
        CancellationToken ct = default)
        => PortScanEngine.RunCoreAsync("127.0.0.1", ports, tcp, Array.Empty<int>(), null, null, null, ct, null, timing, clock);

    [Fact]
    public void Template3_EqualsTheConstantsTheScannerAlwaysUsed()
    {
        var t = ScanTiming.For(ScanTiming.Normal);
        Assert.Equal(500, t.InitialRttMs);
        Assert.Equal(600, t.BannerTimeoutMs);
        Assert.Equal(800, t.UdpTimeoutMs);
        Assert.Equal(40, t.Parallelism);
        Assert.Equal(0, t.Retries);
        Assert.Equal(0, t.ScanDelayMs);
        Assert.Equal(0, t.MaxRate);
        Assert.False(t.AdaptiveRtt);

        int connect = PortScanner.ConnectTimeoutMs, banner = PortScanner.BannerTimeoutMs, udp = PortScanner.UdpTimeoutMs;
        int parallel = PortScanner.MaxConcurrentProbes;
        var before = PortScanner.Timing;
        try
        {
            PortScanner.ApplyTiming(ScanTiming.Normal3);
            Assert.Equal(500, PortScanner.ConnectTimeoutMs);
            Assert.Equal(600, PortScanner.BannerTimeoutMs);
            Assert.Equal(800, PortScanner.UdpTimeoutMs);
            Assert.Equal(40, PortScanner.MaxConcurrentProbes);
        }
        finally
        {
            PortScanner.ApplyTiming(before);
            PortScanner.ConnectTimeoutMs = connect;
            PortScanner.BannerTimeoutMs = banner;
            PortScanner.UdpTimeoutMs = udp;
            PortScanner.ConfigureParallelism(parallel);
        }
    }

    [Fact]
    public void Templates_GetSlowerAndMoreCarefulTowardsZero()
    {
        var all = Enumerable.Range(0, ScanTiming.TemplateCount).Select(ScanTiming.For).ToArray();
        for (int i = 1; i < all.Length; i++)
        {
            Assert.True(all[i].Parallelism >= all[i - 1].Parallelism);
            Assert.True(all[i].InitialRttMs <= all[i - 1].InitialRttMs);
            Assert.True(all[i].ScanDelayMs <= all[i - 1].ScanDelayMs);
            Assert.True(all[i].Retries <= all[i - 1].Retries);
        }
        Assert.Equal(1, all[0].Parallelism);
        Assert.True(all[0].ScanDelayMs > 0);
        Assert.Equal(ScanTiming.For(5), ScanTiming.For(99));
        Assert.Equal(ScanTiming.For(0), ScanTiming.For(-4));
        Assert.All(all, t => Assert.InRange(t.InitialRttMs, t.MinRttMs, t.MaxRttMs));
    }

    [Fact]
    public void FromSettings_LaysTheOverridesOverTheTemplate()
    {
        var t = ScanTiming.FromSettings(2, parallelism: 7, connectMs: 900, bannerMs: 0, retries: 3, scanDelayMs: 40, maxRate: 200);
        Assert.Equal(7, t.Parallelism);
        Assert.Equal(900, t.InitialRttMs);
        Assert.Equal(900, t.UdpTimeoutMs);
        Assert.Equal(ScanTiming.For(2).BannerTimeoutMs, t.BannerTimeoutMs);
        Assert.Equal(3, t.Retries);
        Assert.Equal(40, t.ScanDelayMs);
        Assert.Equal(200, t.MaxRate);
        Assert.InRange(t.InitialRttMs, t.MinRttMs, t.MaxRttMs);

        // The classic stored settings reproduce the classic scan.
        var classic = ScanTiming.FromSettings(3, 40, 500, 600, 0, 0, 0);
        Assert.Equal(ScanTiming.Normal3 with { UdpTimeoutMs = 500 }, classic);
    }

    [Fact]
    public void MinRate_ShortensTheScanDelayToFit()
    {
        var t = ScanTiming.For(1) with { ScanDelayMs = 1000, MinRate = 10 };
        Assert.Equal(100, t.EffectiveScanDelayMs);
        Assert.Equal(1000, (ScanTiming.For(1) with { MinRate = 0 }).EffectiveScanDelayMs);
        Assert.Equal(0, (ScanTiming.Normal3 with { MinRate = 50 }).EffectiveScanDelayMs);
    }

    [Fact]
    public async Task Template0_SendsStrictlySerializedWithTheConfiguredDelay()
    {
        using var clock = new VirtualClock();
        var tcp = new RecordingStrategy(clock);
        var timing = ScanTiming.For(ScanTiming.Paranoid);
        var results = await Run(tcp, new[] { 1, 2, 3, 4, 5, 6 }, timing, clock);

        Assert.Equal(6, results.Count);
        Assert.Equal(1, tcp.MaxInFlight);
        var starts = tcp.Starts.OrderBy(s => s.AtMs).Select(s => s.AtMs).ToArray();
        for (int i = 1; i < starts.Length; i++)
            Assert.True(starts[i] - starts[i - 1] >= timing.ScanDelayMs, $"gap {i}: {starts[i] - starts[i - 1]} ms");
    }

    [Fact]
    public async Task ScanDelay_SpacesProbeStartsEvenWithManyWorkers()
    {
        using var clock = new VirtualClock();
        var tcp = new RecordingStrategy(clock);
        var timing = ScanTiming.Normal3 with { ScanDelayMs = 50 };
        await Run(tcp, Enumerable.Range(1, 30).ToArray(), timing, clock);

        var starts = tcp.Starts.Select(s => s.AtMs).OrderBy(x => x).ToArray();
        Assert.Equal(30, starts.Length);
        for (int i = 1; i < starts.Length; i++)
            Assert.True(starts[i] - starts[i - 1] >= 50, $"gap {i}: {starts[i] - starts[i - 1]} ms");
    }

    [Fact]
    public async Task TokenBucket_NeverExceedsItsCapOverATenSecondRun()
    {
        const int rate = 100;
        var clock = new SerialClock();
        var bucket = new TokenBucket(rate, clock);
        var times = new List<long>();
        while (clock.NowMs < 10_000)
        {
            await bucket.AcquireAsync(default);
            times.Add(clock.NowMs);
        }

        Assert.True(times.Count <= rate * 10 + bucket.Burst, $"{times.Count} starts in 10 s");
        Assert.True(times.Count >= rate * 10 * 9 / 10, "the limiter should also use most of its allowance");
        // No one-second window may hold more than the rate plus the burst.
        for (int i = 0; i < times.Count; i++)
        {
            int inWindow = times.Count(t => t >= times[i] && t < times[i] + 1000);
            Assert.True(inWindow <= rate + bucket.Burst, $"{inWindow} starts in the window at {times[i]} ms");
        }
    }

    [Fact]
    public async Task TokenBucket_ZeroRateIsUnlimited()
    {
        var clock = new SerialClock();
        var bucket = new TokenBucket(0, clock);
        for (int i = 0; i < 1000; i++) await bucket.AcquireAsync(default);
        Assert.Equal(0, clock.NowMs);
    }

    [Fact]
    public async Task MaxRate_CapsProbeStartsInTheEngine()
    {
        using var clock = new VirtualClock();
        var tcp = new RecordingStrategy(clock);
        var timing = ScanTiming.Normal3 with { MaxRate = 20, Parallelism = 4 };
        await Run(tcp, Enumerable.Range(1, 60).ToArray(), timing, clock);

        var starts = tcp.Starts.Select(s => s.AtMs).OrderBy(x => x).ToArray();
        Assert.Equal(60, starts.Length);
        int burst = new TokenBucket(20, clock).Burst;
        foreach (long s in starts)
            Assert.True(starts.Count(x => x >= s && x < s + 1000) <= 20 + burst);
    }

    [Fact]
    public async Task Retries_RepeatASilentPortUntilItAnswersOrTheyRunOut()
    {
        using var clock = new VirtualClock();
        var tcp = new RecordingStrategy(clock, (port, attempt) => attempt < 2
            ? new PortResult(port, NetProtocol.Tcp, PortState.Filtered, PortReason.NoResponse)
            : new PortResult(port, NetProtocol.Tcp, PortState.Open, PortReason.Connected));

        var results = await Run(tcp, new[] { 9 }, ScanTiming.Normal3 with { Retries = 2 }, clock);
        Assert.Equal(3, tcp.Starts.Count);
        Assert.Equal(PortState.Open, results.Single().State);

        var never = new RecordingStrategy(clock, (port, _) => new PortResult(port, NetProtocol.Tcp, PortState.Filtered, PortReason.NoResponse));
        var filtered = await Run(never, new[] { 9 }, ScanTiming.Normal3 with { Retries = 2 }, clock);
        Assert.Equal(3, never.Starts.Count);
        Assert.Equal(PortState.Filtered, filtered.Single().State);
    }

    [Fact]
    public async Task Retries_DoNotRepeatAnAnsweredPort()
    {
        using var clock = new VirtualClock();
        var tcp = new RecordingStrategy(clock, (port, _) => new PortResult(port, NetProtocol.Tcp, PortState.Closed, PortReason.ConnectRefused));
        await Run(tcp, new[] { 9 }, ScanTiming.Normal3 with { Retries = 3 }, clock);
        Assert.Single(tcp.Starts);
    }

    [Fact]
    public void RttEstimator_StaysAtTheInitialValueWhenNotAdaptiveOrWithoutSamples()
    {
        var fixedTiming = ScanTiming.For(3);
        var est = new RttEstimator(fixedTiming);
        Assert.Equal(500, est.TimeoutMs);
        est.Update(10);
        Assert.Equal(500, est.TimeoutMs);

        var adaptive = new RttEstimator(ScanTiming.For(4));
        Assert.False(adaptive.HasSample);
        Assert.Equal(400, adaptive.TimeoutMs);
    }

    [Fact]
    public void RttEstimator_TracksTheRoundTripAndStaysInsideTheClamp()
    {
        var timing = ScanTiming.For(4);
        var est = new RttEstimator(timing);
        for (int i = 0; i < 40; i++) est.Update(30);
        Assert.True(est.HasSample);
        Assert.InRange(est.SmoothedMs, 29, 31);
        Assert.Equal(timing.MinRttMs, est.TimeoutMs); // 30 ms plus deviation sits below the floor

        var slow = new RttEstimator(timing);
        for (int i = 0; i < 40; i++) slow.Update(100_000);
        Assert.Equal(timing.MaxRttMs, slow.TimeoutMs);

        var mid = new RttEstimator(timing);
        mid.Update(100);
        Assert.Equal(300, mid.TimeoutMs); // first sample: srtt 100 + 4 * 50
        mid.Update(-5); // negative samples are ignored
        Assert.Equal(300, mid.TimeoutMs);
    }

    [Fact]
    public async Task HostTimeout_KeepsWhatWasFoundAndReturns()
    {
        using var clock = new VirtualClock();
        var stuck = new HangAfterFirst();
        var timing = ScanTiming.Normal3 with { Parallelism = 2, HostTimeoutMs = 300 };
        var finished = Task.Run(() => PortScanEngine.RunCoreAsync("127.0.0.1", new[] { 1, 2, 3 }, stuck,
            Array.Empty<int>(), null, null, null, default, null, timing, clock));
        var winner = await Task.WhenAny(finished, Task.Delay(10_000));
        Assert.Same(finished, winner);
        var results = await finished;
        Assert.Contains(results, r => r.Port == 1 && r.State == PortState.Open);
        Assert.DoesNotContain(results, r => r.Port == 2);
    }

    private sealed class HangAfterFirst : IPortStrategy
    {
        public NetProtocol Protocol => NetProtocol.Tcp;

        public async Task<PortResult?> ProbeAsync(string ip, int port, string name, CancellationToken ct)
        {
            if (port == 1) return new PortResult(port, NetProtocol.Tcp, PortState.Open, PortReason.Connected);
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        }
    }

    [Fact]
    public void AdaptiveTimeout_OverridesOnlyInsideItsScope()
    {
        int before = PortScanner.EffectiveConnectTimeoutMs;
        using (PortScanner.UseAdaptiveTimeout(123))
            Assert.Equal(123, PortScanner.EffectiveConnectTimeoutMs);
        Assert.Equal(before, PortScanner.EffectiveConnectTimeoutMs);
    }
}
