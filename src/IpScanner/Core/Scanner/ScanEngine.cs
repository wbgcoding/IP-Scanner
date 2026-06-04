using System.Collections.Concurrent;
using IpScanner.Core.Models;
using IpScanner.Core.Net;

namespace IpScanner.Core.Scanner;

/// <summary>
/// Two-phase scan: discovery (1 ping/IP) then analysis (N pings/online device).
/// The ping function is injected for testability (production passes IcmpPinger.Ping).
/// </summary>
public sealed class ScanEngine
{
    private readonly Func<string, int, PingResult> _ping;
    private readonly IReadOnlyList<Func<string, (string? mac, string? host)>>? _enrichers;
    private const int IcmpTimeoutMs = 1000;
    private const int EnrichRetryDelayMs = 4000;    // pause between enrichment retry sweeps
    private const int MaxEnrichRetries = 5;         // retry budget per device
    private const int EnrichGateTimeoutMs = 30_000; // max wait for a free enrichment slot
    private const int EnrichJoinTimeoutMs = 8000;   // max wait per resolver thread
    private const int EnrichGateSize = 24;          // concurrent enrichment batches
    private const int FastRecheckWindowMs = 10_000; // burst window after a device drops
    private const int FastRecheckIntervalMs = 1000; // probe cadence inside the window

    /// <param name="ping">Ping function (injected for tests).</param>
    /// <param name="enrichers">Optional MAC/hostname techniques, ordered best-first
    /// (index = rank). All run in parallel per device; every partial result is
    /// applied immediately and replaced when a better-ranked one arrives.</param>
    public ScanEngine(Func<string, int, PingResult> ping,
                      IReadOnlyList<Func<string, (string? mac, string? host)>>? enrichers = null)
    {
        _ping = ping;
        _enrichers = enrichers;
    }

    /// <summary>Tracks ping counters across the full scan lifetime.</summary>
    public ScanProgress Progress { get; } = new();

    /// <summary>Raised whenever a device's state changes (online, new ping, etc.).</summary>
    public event Action<Device>? DeviceUpdated;

    private readonly ConcurrentDictionary<string, Device> _devices = new();
    public IReadOnlyCollection<Device> Devices => _devices.Values.ToArray();

    private int _activeWorkers;
    /// <summary>Number of busy ping worker threads (discovery, analysis, recheck).</summary>
    public int ActiveWorkers => Volatile.Read(ref _activeWorkers);

    private PingResult Ping(string ip) => _ping(ip, IcmpTimeoutMs);

    private readonly struct WorkerScope : IDisposable
    {
        private readonly ScanEngine _e;
        public WorkerScope(ScanEngine e) { _e = e; Interlocked.Increment(ref e._activeWorkers); }
        public void Dispose() => Interlocked.Decrement(ref _e._activeWorkers);
    }

    public async Task ScanAsync(IReadOnlyList<string> subnetPrefixes, ScanConfig cfg,
                                CancellationToken ct)
    {
        var ips = subnetPrefixes.SelectMany(Ipv4.HostsInSubnet).ToList();
        // Pinned IPs are always scanned, even outside the chosen subnet(s).
        var seen = new HashSet<string>(ips);
        foreach (var pin in cfg.PinnedIps)
            if (Ipv4.IsValid(pin) && seen.Add(pin)) ips.Add(pin);
        bool infinite = cfg.PingCount == ScanConfig.InfinitePingCount;
        int analysisPerIp = infinite ? 0 : Math.Max(0, cfg.PingCount - 1);

        // Analysis runs per device as soon as its discovery ping answers —
        // no barrier between the two phases. ScanThreads caps both phases;
        // 0 = one worker per device (max parallelism).
        int workers = cfg.ScanThreads <= 0 ? ips.Count : cfg.ScanThreads;

        // Discovery + analysis block up to 2×workers pool threads with ICMP
        // waits. The pool only grows ~1 thread/s past its minimum, so without
        // this, early devices appear instantly and the rest trickle in —
        // raise the minimum so all workers run from the start.
        ThreadPool.GetMinThreads(out int minWorker, out int minIo);
        int wanted = Math.Min(workers * 2 + 16, 1024);
        if (minWorker < wanted) ThreadPool.SetMinThreads(wanted, minIo);

        using var analysisSem = new SemaphoreSlim(Math.Max(1, workers));
        var analysisTasks = new ConcurrentBag<Task>();
        // IPs whose analysis loop is running (prevents double-starts from rechecks).
        var analyzing = new ConcurrentDictionary<string, byte>();
        // IPs whose analysis pings were skipped at discovery — only those are
        // un-skipped when a recheck brings them into the run.
        var skippedAtDiscovery = new ConcurrentDictionary<string, byte>();

        await RunParallel(ips, workers, ct,
            ip =>
            {
                if (ct.IsCancellationRequested) return;
                using var _ = new WorkerScope(this);
                var device = _devices.GetOrAdd(ip, x => new Device(x)
                {
                    // Discovery target first (x/init pings); switches to the run
                    // target once the device answers.
                    TargetPings = Math.Max(1, cfg.InitPingCount),
                    OfflineAfterFailures = cfg.OfflineAfterFailedPings,
                });
                // Discovery: up to InitPingCount attempts, stop at first reply.
                var r = new PingResult(false, null, null);
                int tries = Math.Max(1, cfg.InitPingCount);
                for (int t = 0; t < tries && !ct.IsCancellationRequested; t++)
                {
                    r = Ping(ip);
                    device.RecordPing(r);
                    if (r.Success) Progress.AddSuccess(); else Progress.AddFailed();
                    if (r.Success) break;
                }
                Progress.AddProcessed();
                Progress.NotifyChanged();
                DeviceUpdated?.Invoke(device);
                if (r.Success)
                {
                    device.TargetPings = cfg.PingCount == 0 ? 1 : cfg.PingCount;
                    // First reply: resolve MAC + hostname OFF the critical path so
                    // discovery finishes fast and analysis pings start without delay.
                    TryEnrich(device);
                    if ((infinite || analysisPerIp > 0) && analyzing.TryAdd(ip, 0))
                        analysisTasks.Add(AnalyzeDeviceAsync(device, cfg, infinite, analysisSem, analyzing, ct));
                }
                else if (analysisPerIp > 0)
                {
                    // Offline at discovery: its analysis pings are skipped.
                    skippedAtDiscovery.TryAdd(ip, 0);
                    Progress.AddSkipped(analysisPerIp);
                    Progress.NotifyChanged();
                }
            });

        // Recheck offline IPs periodically while the run is active; a device
        // that comes online joins the analysis immediately.
        using var recheckCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var recheckTask = cfg.OfflineRecheckSeconds > 0 && cfg.PingCount != 0
            ? RecheckOfflineLoopAsync(cfg, infinite, analysisPerIp, workers, analysisSem,
                                      analysisTasks, analyzing, skippedAtDiscovery, ct, recheckCts.Token)
            : Task.CompletedTask;
        // Supervisor: periodically re-runs enrichment for online devices that
        // still miss MAC or hostname (first attempts often lose the race
        // against scan load); up to 5 extra attempts per device.
        var enrichTask = _enrichers is { Count: > 0 }
            ? EnrichMissingLoopAsync(recheckCts.Token)
            : Task.CompletedTask;

        // Wait until no analysis task is left — rechecks may add new ones mid-wait.
        while (true)
        {
            var snapshot = analysisTasks.ToArray();
            await Task.WhenAll(snapshot).ConfigureAwait(false);
            if (snapshot.Length == analysisTasks.Count) break;
        }
        recheckCts.Cancel();
        try { await Task.WhenAll(recheckTask, enrichTask).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        await Task.WhenAll(analysisTasks.ToArray()).ConfigureAwait(false);
    }

    /// <summary>Every few seconds, retry enrichment for online devices still
    /// missing MAC or hostname (max 5 retries per device).</summary>
    private async Task EnrichMissingLoopAsync(CancellationToken ct)
    {
        var attempts = new Dictionary<string, int>();
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(EnrichRetryDelayMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            foreach (var d in _devices.Values)
            {
                if (!d.IsOnline || (d.Mac is not null && d.Hostname is not null)) continue;
                int n = attempts.GetValueOrDefault(d.Ip);
                if (n >= MaxEnrichRetries) continue;
                attempts[d.Ip] = n + 1;
                TryEnrich(d);
            }
        }
    }

    /// <summary>Ping still-offline IPs periodically; devices that answer start
    /// their analysis loop right away. Devices that just dropped offline get a
    /// fast burst (every second for a short window — they often reappear right
    /// away), afterwards the regular OfflineRecheckSeconds cadence applies.</summary>
    private async Task RecheckOfflineLoopAsync(ScanConfig cfg, bool infinite, int analysisPerIp,
        int workers, SemaphoreSlim sem, ConcurrentBag<Task> tasks,
        ConcurrentDictionary<string, byte> analyzing, ConcurrentDictionary<string, byte> skippedAtDiscovery,
        CancellationToken scanCt, CancellationToken loopCt)
    {
        long slowMs = cfg.OfflineRecheckSeconds * 1000L;
        long loopStart = Environment.TickCount64;
        var lastProbe = new Dictionary<string, long>();   // loop-thread only

        while (!loopCt.IsCancellationRequested)
        {
            try { await Task.Delay(FastRecheckIntervalMs, loopCt).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            long now = Environment.TickCount64;
            var due = _devices.Values
                .Where(d => !d.IsOnline && !analyzing.ContainsKey(d.Ip))
                .Where(d =>
                {
                    bool fresh = d.OfflineSince is { } t && now - t < FastRecheckWindowMs;
                    long wait = fresh ? FastRecheckIntervalMs : slowMs;
                    long anchor = lastProbe.GetValueOrDefault(d.Ip, d.OfflineSince ?? loopStart);
                    return now - anchor >= wait;
                })
                .Select(d => d.Ip).ToList();
            if (due.Count == 0) continue;
            foreach (var ip in due) lastProbe[ip] = now;

            await RunParallel(due, workers, loopCt, ip =>
            {
                if (loopCt.IsCancellationRequested) return;
                using var worker = new WorkerScope(this);
                var device = _devices[ip];
                var r = Ping(ip);
                device.RecordPing(r);
                // Failed probes are not counted as scan pings (they would grow
                // unbounded on long runs); a reply counts and joins the run.
                if (!r.Success) { DeviceUpdated?.Invoke(device); return; }
                device.TargetPings = cfg.PingCount == 0 ? 1 : cfg.PingCount;
                Progress.AddSuccess();
                Progress.NotifyChanged();
                DeviceUpdated?.Invoke(device);
                TryEnrich(device);
                if ((infinite || analysisPerIp > 0) && analyzing.TryAdd(ip, 0))
                {
                    // Un-skip only pings that were actually skipped at discovery.
                    if (analysisPerIp > 0 && skippedAtDiscovery.TryRemove(ip, out _))
                        Progress.AddSkipped(-analysisPerIp);
                    tasks.Add(AnalyzeDeviceAsync(device, cfg, infinite, sem, analyzing, scanCt));
                }
            }).ConfigureAwait(false);
        }
    }

    /// <summary>N analysis pings for one device, gated by the analysis semaphore.</summary>
    private async Task AnalyzeDeviceAsync(Device device, ScanConfig cfg, bool infinite,
                                          SemaphoreSlim sem, ConcurrentDictionary<string, byte> analyzing,
                                          CancellationToken ct)
    {
        try { await sem.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        try
        {
            await Task.Run(() =>
            {
                using var _ = new WorkerScope(this);
                int target = infinite ? int.MaxValue : cfg.PingCount;
                for (int i = 1; i < target && !ct.IsCancellationRequested; i++)
                {
                    if (cfg.PingIntervalMs > 0) InterruptibleSleep(cfg.PingIntervalMs, ct);
                    if (ct.IsCancellationRequested) break;
                    var ar = Ping(device.Ip);
                    device.RecordPing(ar);
                    if (ar.Success) Progress.AddSuccess(); else Progress.AddFailed();
                    Progress.NotifyChanged();
                    DeviceUpdated?.Invoke(device);
                }
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            sem.Release();
            // A device that dropped offline during analysis goes back to the
            // recheck pool, so it rejoins the run if it answers again.
            if (!device.IsOnline && !ct.IsCancellationRequested)
                analyzing.TryRemove(device.Ip, out _);
        }
    }

    private static async Task RunParallel(IReadOnlyList<string> items, int maxParallel,
                                          CancellationToken ct, Action<string> body)
    {
        if (items.Count == 0) return;
        using var sem = new SemaphoreSlim(Math.Max(1, maxParallel));
        var tasks = items.Select(async item =>
        {
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try { await Task.Run(() => body(item), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            finally { sem.Release(); }
        });
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    // At most N devices enrich at once — hundreds in parallel exhaust sockets
    // and nbtstat processes, and every lookup then dies in its timeout.
    private static readonly SemaphoreSlim EnrichGate = new(EnrichGateSize);
    private readonly ConcurrentDictionary<string, byte> _enriching = new();

    /// <summary>Fire-and-forget MAC/hostname resolution. Every technique runs on
    /// its own dedicated thread (the pool is saturated with blocking pings, queued
    /// work would die in its timeout). Each partial result lands immediately;
    /// better-ranked results replace worse ones.</summary>
    private void TryEnrich(Device device)
    {
        if (_enrichers is null || _enrichers.Count == 0) return;
        if (!_enriching.TryAdd(device.Ip, 0)) return;   // batch already running
        _ = Task.Factory.StartNew(() =>
        {
            if (!EnrichGate.Wait(EnrichGateTimeoutMs)) { _enriching.TryRemove(device.Ip, out _); return; }
            try
            {
                var threads = new List<Thread>();
                for (int i = 0; i < _enrichers.Count; i++)
                {
                    int rank = i;
                    var resolve = _enrichers[i];
                    var t = new Thread(() => ApplyEnrichResult(device, rank, resolve)) { IsBackground = true };
                    t.Start();
                    threads.Add(t);
                }
                foreach (var t in threads) t.Join(EnrichJoinTimeoutMs);
            }
            finally
            {
                EnrichGate.Release();
                _enriching.TryRemove(device.Ip, out _);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private void ApplyEnrichResult(Device device, int rank,
                                   Func<string, (string? mac, string? host)> resolve)
    {
        try
        {
            var (mac, host) = resolve(device.Ip);
            bool changed = false;
            lock (device)
            {
                if (!string.IsNullOrEmpty(host) && rank < device.HostnameRank)
                { device.Hostname = host; device.HostnameRank = rank; changed = true; }
                if (!string.IsNullOrEmpty(mac) && rank < device.MacRank)
                { device.Mac = mac; device.MacRank = rank; changed = true; }
            }
            if (changed) DeviceUpdated?.Invoke(device);
        }
        catch { /* each technique is best-effort */ }
    }

    private static void InterruptibleSleep(int ms, CancellationToken ct)
    {
        try { Task.Delay(ms, ct).Wait(); }
        catch (AggregateException) { /* cancelled */ }
    }
}
