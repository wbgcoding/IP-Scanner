using System.Diagnostics;

namespace IpScanner.Core.Scanner;

/// <summary>Time source of the scan pacing. The real one reads a stopwatch; tests bring a fake that
/// advances on every delay so a ten second run takes no real time.</summary>
internal interface IScanClock
{
    long NowMs { get; }

    Task DelayAsync(int ms, CancellationToken ct);
}

internal sealed class SystemScanClock : IScanClock
{
    public static readonly SystemScanClock Instance = new();
    private static readonly Stopwatch Watch = Stopwatch.StartNew();

    public long NowMs => Watch.ElapsedMilliseconds;

    public Task DelayAsync(int ms, CancellationToken ct) => Task.Delay(ms, ct);
}

/// <summary>Token bucket that caps how many probes may start per second. Waiting is done through the
/// injected clock; a rate of 0 or less means unlimited.</summary>
internal sealed class TokenBucket
{
    private readonly object _sync = new();
    private readonly int _ratePerSec;
    private readonly double _burst;
    private readonly IScanClock _clock;
    private double _tokens;
    private long _lastMs;

    public TokenBucket(int ratePerSec, IScanClock clock, int burst = 0)
    {
        _ratePerSec = ratePerSec;
        _clock = clock;
        // Half a tenth of a second of burst keeps the average honest while smoothing timer jitter.
        _burst = burst > 0 ? burst : Math.Max(1, ratePerSec / 20);
        _tokens = _burst;
        _lastMs = clock.NowMs;
    }

    public int Burst => (int)_burst;

    public async Task AcquireAsync(CancellationToken ct)
    {
        if (_ratePerSec <= 0) return;
        while (true)
        {
            int waitMs;
            lock (_sync)
            {
                long now = _clock.NowMs;
                _tokens = Math.Min(_burst, _tokens + (now - _lastMs) * _ratePerSec / 1000.0);
                _lastMs = now;
                if (_tokens >= 1)
                {
                    _tokens -= 1;
                    return;
                }
                waitMs = Math.Max(1, (int)Math.Ceiling((1 - _tokens) * 1000.0 / _ratePerSec));
            }
            await _clock.DelayAsync(waitMs, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>Reserves start slots so two probes never begin closer together than the scan delay.</summary>
internal sealed class ProbePacer
{
    private readonly object _sync = new();
    private readonly int _delayMs;
    private readonly IScanClock _clock;
    private long _nextStartMs;

    public ProbePacer(int delayMs, IScanClock clock)
    {
        _delayMs = delayMs;
        _clock = clock;
    }

    public async Task WaitTurnAsync(CancellationToken ct)
    {
        if (_delayMs <= 0) return;
        long wait;
        lock (_sync)
        {
            long now = _clock.NowMs;
            long start = Math.Max(now, _nextStartMs);
            _nextStartMs = start + _delayMs;
            wait = start - now;
        }
        if (wait > 0) await _clock.DelayAsync((int)Math.Min(wait, int.MaxValue), ct).ConfigureAwait(false);
    }
}

/// <summary>The delay and rate rules of a <see cref="ScanTiming"/> as one awaitable gate in front of every probe.</summary>
internal sealed class ProbeThrottle
{
    private readonly ProbePacer _pacer;
    private readonly TokenBucket _bucket;

    public ProbeThrottle(ScanTiming timing, IScanClock clock)
    {
        _pacer = new ProbePacer(timing.EffectiveScanDelayMs, clock);
        _bucket = new TokenBucket(timing.MaxRate, clock);
    }

    public static bool IsNeeded(ScanTiming timing) => timing.EffectiveScanDelayMs > 0 || timing.MaxRate > 0;

    public async Task WaitAsync(CancellationToken ct)
    {
        await _pacer.WaitTurnAsync(ct).ConfigureAwait(false);
        await _bucket.AcquireAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>Round-trip estimate of one host as in RFC 6298: a smoothed mean plus four times the mean
/// deviation, clamped to what the template allows. Without samples, or when the template is not
/// adaptive, the timeout stays at the template's initial value.</summary>
internal sealed class RttEstimator
{
    private readonly object _sync = new();
    private readonly ScanTiming _timing;
    private double _srtt, _rttvar;
    private bool _hasSample;

    public RttEstimator(ScanTiming timing) => _timing = timing;

    public bool HasSample { get { lock (_sync) return _hasSample; } }

    public double SmoothedMs { get { lock (_sync) return _srtt; } }

    public void Update(double sampleMs)
    {
        if (sampleMs < 0) return;
        lock (_sync)
        {
            if (!_hasSample)
            {
                _srtt = sampleMs;
                _rttvar = sampleMs / 2;
                _hasSample = true;
                return;
            }
            _rttvar = 0.75 * _rttvar + 0.25 * Math.Abs(_srtt - sampleMs);
            _srtt = 0.875 * _srtt + 0.125 * sampleMs;
        }
    }

    public int TimeoutMs
    {
        get
        {
            lock (_sync)
            {
                if (!_timing.AdaptiveRtt || !_hasSample) return _timing.InitialRttMs;
                return Math.Clamp((int)Math.Ceiling(_srtt + 4 * _rttvar), _timing.MinRttMs, _timing.MaxRttMs);
            }
        }
    }
}
