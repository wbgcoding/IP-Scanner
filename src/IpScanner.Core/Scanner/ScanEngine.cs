using System.Collections.Concurrent;
using IpScanner.Core.Models;
using IpScanner.Core.Net;
using IpScanner.Core.Scanner.Discovery;
using IpScanner.Core.Scanner.Ipv6;

namespace IpScanner.Core.Scanner;

/// <summary>
/// Two-phase scan: discovery (1 ping/IP) then analysis (N pings/online device).
/// The ping function is injected for testability (production passes IcmpPinger.Ping).
/// </summary>
public sealed class ScanEngine : IDisposable
{
    /// <summary>Tracks in-flight tasks and drops each one as soon as it completes, so a
    /// long-running scan does not keep every analysis task it ever started in memory for
    /// the life of the run.</summary>
    private sealed class PendingTasks
    {
        private readonly ConcurrentDictionary<Task, byte> _pending = new();

        public void Add(Task t)
        {
            _pending[t] = 0;
            if (t.IsCompleted) _pending.TryRemove(t, out _);
            else t.ContinueWith(done => _pending.TryRemove(done, out _), TaskScheduler.Default);
        }

        public Task[] Snapshot() => _pending.Keys.ToArray();
    }


    private readonly Func<string, int, PingResult> _ping;
    private readonly IReadOnlyList<Func<string, CancellationToken, Task<(string? mac, string? host)>>>? _enrichers;
    private const int IcmpTimeoutMs = 1000;
    private const int EnrichRetryDelayMs = 4000;    // pause between enrichment retry sweeps
    private const int MaxEnrichRetries = 5;         // retry budget per device
    private const int EnrichGateTimeoutMs = 30_000; // max wait for a free enrichment slot
    private const int EnrichJoinTimeoutMs = 8000;   // max wait for all resolvers of one device combined
    private const int EnrichGateSize = 24;          // concurrent enrichment batches
    private const int EnrichShutdownWaitMs = 1000;  // max wait for enrichment to unwind on Dispose
    private const int FastRecheckWindowMs = 10_000; // burst window after a device drops
    private const int FastRecheckIntervalMs = 1000; // probe cadence inside the window
    private const int OfflineBurstPings = 5;        // immediate probes after going offline
    private const int OfflineBurstIntervalMs = 500;
    private const int ThreadPoolHeadroom = 16;      // extra pool threads beyond the workers
    private const int MaxPoolMinimum = 1024;
    private const int MaxAutoWorkers = 512;         // cap for "0 = one worker per device"

    /// <param name="ping">Ping function (injected for tests).</param>
    /// <param name="enrichers">Optional MAC/hostname techniques, ordered best-first
    /// (index = rank). All run in parallel per device; every partial result is
    /// applied immediately and replaced when a better-ranked one arrives. Each one gets a token
    /// that is cancelled when its batch times out or the engine is disposed.</param>
    public ScanEngine(Func<string, int, PingResult> ping,
                      IReadOnlyList<Func<string, CancellationToken, Task<(string? mac, string? host)>>>? enrichers = null)
    {
        _ping = ping;
        _enrichers = enrichers;
    }

    /// <summary>Tracks ping counters across the full scan lifetime.</summary>
    public ScanProgress Progress { get; } = new();

    /// <summary>Raised whenever a device's state changes (online, new ping, etc.).</summary>
    public event Action<Device>? DeviceUpdated;

    /// <summary>Raised when a stale IP entry is evicted because the same MAC was
    /// seen at a different (new) IP address.</summary>
    public event Action<Device>? DeviceRemoved;

    private readonly ConcurrentDictionary<string, Device> _devices = new();
    private readonly ConcurrentDictionary<string, string> _macToIp =
        new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyCollection<Device> Devices => _devices.Values.ToArray();

    private int _activeWorkers;
    /// <summary>Number of busy ping worker threads (discovery, analysis, recheck).</summary>
    public int ActiveWorkers => Volatile.Read(ref _activeWorkers);

    /// <summary>Network access of the non-ICMP discovery modes; tests replace it.</summary>
    public IDiscoveryNet Net { get; set; } = SystemDiscoveryNet.Instance;

    /// <summary>Reverse name lookup used by the list scan; tests replace it.</summary>
    public Func<string, CancellationToken, Task<string?>> NameResolver { get; set; } = HostnameResolver.ResolveAsync;

    private DiscoveryRunner? _runner;     // discovery pings; null = plain ICMP echo
    private DiscoveryRunner? _follow;     // later pings (analysis, recheck); null = plain ICMP echo
    private CancellationToken _scanCt;

    /// <summary>Builds the probe set for a run. Only a non-default mode selection needs a runner; the plain
    /// ICMP echo keeps the injected ping function untouched.</summary>
    private void ConfigureDiscovery(ScanConfig cfg, CancellationToken ct)
    {
        _scanCt = ct;
        var modes = DiscoveryModes.Usable(DiscoveryModes.Parse(cfg.DiscoveryModes));
        _runner = _follow = null;
        if (modes.Count == 1 && modes[0] == DiscoveryMode.IcmpEcho) return;
        _runner = DiscoveryRunner.Create(modes, cfg.TcpPingPorts, cfg.UdpPingPorts, Net, _ping, IcmpTimeoutMs);
        if (_runner.Live.Count > 0) _follow = _runner;
    }

    private PingResult Ping(Device d, bool discovery = false)
    {
        var run = discovery ? _runner : _follow;
        // The probe set of the discovery modes is IPv4 only; an IPv6 target always goes through the
        // injected ping, which answers by echo and TCP connects.
        if (Ipv6Pinger.IsIpv6(d.Ip)) run = null;
        if (run is null || (!discovery && run.Live.Count == 0))
        {
            var plain = _ping(d.Ip, IcmpTimeoutMs);
            if (plain.Success) d.DiscoveryMethod = plain.Method ?? "icmp";
            return plain;
        }
        var r = run.Probe(d.Ip, d.DiscoveryMethod, _scanCt, discovery);
        if (r.Success && r.Method is { } m) d.DiscoveryMethod = m;
        return r;
    }

    // Database values rank just below "unknown" so every live resolver wins.
    private const int DbRank = int.MaxValue - 1;

    /// <summary>Seed devices from the known-devices database so hostname/MAC
    /// show immediately; live results replace them during the run.</summary>
    public void Preload(IEnumerable<Device> known, ScanConfig cfg)
    {
        foreach (var k in known)
        {
            var d = _devices.GetOrAdd(k.Ip, ip => new Device(ip)
            {
                TargetPings = Math.Max(1, cfg.InitPingCount),
                OfflineAfterFailures = cfg.OfflineAfterFailedPings,
            });
            d.FromDb = true;
            string? macToTrack = null;
            lock (d)
            {
                if (d.Hostname is null && k.Hostname is { } h && h != Device.Unknown)
                { d.Hostname = h; d.HostnameRank = DbRank; }
                if (d.Mac is null && k.Mac is { } m && m != Device.Unknown)
                { d.Mac = m; d.MacRank = DbRank; macToTrack = m; }
            }
            if (macToTrack is not null) TrackMacOrEvict(d, macToTrack);
            DeviceUpdated?.Invoke(d);
        }
    }

    private readonly struct WorkerScope : IDisposable
    {
        private readonly ScanEngine _e;
        public WorkerScope(ScanEngine e) { _e = e; Interlocked.Increment(ref e._activeWorkers); }
        public void Dispose() => Interlocked.Decrement(ref _e._activeWorkers);
    }

    /// <param name="targets">The addresses to scan, in order (see <see cref="TargetSpec"/>).</param>
    public async Task ScanAsync(IEnumerable<string> targets, ScanConfig cfg, CancellationToken ct)
    {
        var ips = targets.ToList();
        // Pinned IPs are always scanned, even outside the chosen subnet(s).
        // Entries may carry "name color" metadata - only the target counts here.
        var seen = new HashSet<string>(ips);
        foreach (var pin in cfg.PinnedIps)
        {
            var target = NetEntry.Parse(pin).Target;
            if ((Ipv4.IsValid(target) || IpAddressKey.TryParse(target, out _)) && seen.Add(target)) ips.Add(target);
        }
        ConfigureDiscovery(cfg, ct);
        if (_runner is { ListScan: true })
        {
            await ListTargetsAsync(ips, cfg, ct).ConfigureAwait(false);
            return;
        }
        bool infinite = cfg.PingCount == ScanConfig.InfinitePingCount;
        int analysisPerIp = infinite ? 0 : Math.Max(0, cfg.PingCount - 1);

        // Analysis runs per device as soon as its discovery ping answers -
        // no barrier between the two phases. ScanThreads caps both phases;
        // 0 = one worker per device (max parallelism), capped so a large
        // range can't flood the OS with tens of thousands of live sockets.
        int workers = cfg.ScanThreads <= 0 ? Math.Min(ips.Count, MaxAutoWorkers) : cfg.ScanThreads;

        // ICMP waits block pool threads and the pool grows only ~1 thread/s -
        // raise the minimum so all workers run from the start.
        ThreadPool.GetMinThreads(out int minWorker, out int minIo);
        int wanted = Math.Min(workers * 2 + ThreadPoolHeadroom, MaxPoolMinimum);
        if (minWorker < wanted) ThreadPool.SetMinThreads(wanted, minIo);

        using var analysisSem = new SemaphoreSlim(Math.Max(1, workers));
        var analysisTasks = new PendingTasks();
        // IPs whose analysis loop is running (prevents double-starts from rechecks).
        var analyzing = new ConcurrentDictionary<string, byte>();
        // IPs whose analysis pings were skipped at discovery - only those are
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
                    r = Ping(device, discovery: true);
                    device.RecordPing(r);
                    if (r.Success) Progress.AddSuccess(); else Progress.AddFailed();
                    if (r.Success) break;
                }
                Progress.AddProcessed();
                Progress.NotifyChanged();
                DeviceUpdated?.Invoke(device);
                if (r.Success)
                {
                    // New counted phase (discovery -> analysis): start its
                    // "N/target" display at 1/target, crediting the discovery
                    // ping that just succeeded - not the raw attempt count (up
                    // to InitPingCount tries), and not 0 either, or the display
                    // would fall one short of its own target once analysis ends.
                    device.ResetPingCount(credit: 1);
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

        // Wait until no analysis task is left - rechecks may add new ones mid-wait.
        // PendingTasks self-trims, so an empty snapshot after awaiting the current one
        // means nothing new arrived while we were waiting.
        while (true)
        {
            var snapshot = analysisTasks.Snapshot();
            if (snapshot.Length == 0) break;
            await Task.WhenAll(snapshot).ConfigureAwait(false);
        }
        recheckCts.Cancel();
        try { await Task.WhenAll(recheckTask, enrichTask).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        await Task.WhenAll(analysisTasks.Snapshot()).ConfigureAwait(false);
    }

    /// <summary>List scan: every target is listed and named by reverse lookup; no probe is sent.</summary>
    private async Task ListTargetsAsync(IReadOnlyList<string> ips, ScanConfig cfg, CancellationToken ct)
    {
        int workers = cfg.ScanThreads <= 0 ? Math.Min(Math.Max(1, ips.Count), 64) : cfg.ScanThreads;
        await RunParallelAsync(ips, workers, ct, async ip =>
        {
            if (ct.IsCancellationRequested) return;
            using var _ = new WorkerScope(this);
            var device = _devices.GetOrAdd(ip, x => new Device(x) { OfflineAfterFailures = cfg.OfflineAfterFailedPings });
            device.MarkListed();
            try
            {
                var name = await NameResolver(ip, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(name)) { device.Hostname = name; device.HostnameRank = 0; }
            }
            catch (OperationCanceledException) { }
            Progress.AddProcessed();
            Progress.NotifyChanged();
            DeviceUpdated?.Invoke(device);
        });
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
    /// fast burst (every second for a short window - they often reappear right
    /// away), afterwards the regular OfflineRecheckSeconds cadence applies.</summary>
    private async Task RecheckOfflineLoopAsync(ScanConfig cfg, bool infinite, int analysisPerIp,
        int workers, SemaphoreSlim sem, PendingTasks tasks,
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

            // Plain loop instead of a Where/Where/Select chain - this runs every second
            // over every device in the scan, and a LINQ chain re-allocates its enumerator
            // and closures on every single pass.
            long now = Environment.TickCount64;
            List<string>? due = null;
            foreach (var d in _devices.Values)
            {
                if (d.IsOnline || analyzing.ContainsKey(d.Ip)) continue;
                // Muted devices never get the fast burst window, only the plain slow cadence.
                bool fresh = !d.IsMuted && d.OfflineSince is { } t && now - t < FastRecheckWindowMs;
                long wait = fresh ? FastRecheckIntervalMs : slowMs;
                long anchor = lastProbe.GetValueOrDefault(d.Ip, d.OfflineSince ?? loopStart);
                if (now - anchor < wait) continue;
                (due ??= new List<string>()).Add(d.Ip);
            }
            if (due is null) continue;
            foreach (var ip in due) lastProbe[ip] = now;

            await RunParallel(due, workers, loopCt, ip =>
            {
                if (loopCt.IsCancellationRequested) return;
                using var worker = new WorkerScope(this);
                // The entry may have been evicted (MAC moved to a new IP) since
                // the due-list was built - skip it rather than throwing.
                if (!_devices.TryGetValue(ip, out var device)) return;
                var r = Ping(device);
                // Recheck probes are housekeeping, not part of the displayed
                // "N/target" progress - never counted, success or fail.
                device.RecordPing(r, countsTowardTarget: false);
                if (!r.Success) { DeviceUpdated?.Invoke(device); return; }
                // Reconnected: fresh counted phase starts its display at 1/target,
                // crediting this probe itself as the first ping of the new phase
                // (mirrors the discovery-success credit above) - it was recorded
                // with countsTowardTarget:false, so without this credit the
                // display would land one short of its own target.
                device.ResetPingCount(credit: 1);
                device.TargetPings = cfg.PingCount == 0 ? 1 : cfg.PingCount;
                // Not added to the global Pings X/Y total: that denominator was
                // sized once at scan start for one pass per device, and a
                // reconnect's own re-analysis run is excluded from it too
                // (countGlobally: false below) - a flapping device must not keep
                // re-spending a budget that was only ever allocated once.
                Progress.NotifyChanged();
                DeviceUpdated?.Invoke(device);
                TryEnrich(device);
                if ((infinite || analysisPerIp > 0) && analyzing.TryAdd(ip, 0))
                {
                    skippedAtDiscovery.TryRemove(ip, out _);
                    tasks.Add(AnalyzeDeviceAsync(device, cfg, infinite, sem, analyzing, scanCt, countGlobally: false));
                }
            }).ConfigureAwait(false);
        }
    }

    /// <summary>N analysis pings for one device, gated by the analysis semaphore.
    /// <paramref name="countGlobally"/> is false when this run was triggered by a
    /// reconnect: the device's own "N/target" display still counts these pings
    /// (fresh phase, reset to 0/target), but the run-wide Pings X/Y total was
    /// sized once at scan start for one pass per device - a device that flaps
    /// online/offline repeatedly must not keep re-spending that same budget.</summary>
    private async Task AnalyzeDeviceAsync(Device device, ScanConfig cfg, bool infinite,
                                          SemaphoreSlim sem, ConcurrentDictionary<string, byte> analyzing,
                                          CancellationToken ct, bool countGlobally = true)
    {
        try { await sem.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        try
        {
            await Task.Run(async () =>
            {
                using var _ = new WorkerScope(this);
                int target = infinite ? int.MaxValue : cfg.PingCount;
                // Starts at 1, not 0: the ping that triggered this phase (discovery
                // success, or a reconnect probe) already supplied its first counted
                // ping via ResetPingCount(credit: 1) below - this loop only needs
                // to supply the remaining target-1 to reach the display target
                // without sending target+1 real pings per device.
                for (int i = 1; i < target && !ct.IsCancellationRequested; i++)
                {
                    // Async wait releases the pool thread between pings instead
                    // of blocking it for the whole interval.
                    if (cfg.PingIntervalMs > 0)
                    {
                        try { await Task.Delay(cfg.PingIntervalMs, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { break; }
                    }
                    if (ct.IsCancellationRequested) break;
                    bool wasOnline = device.IsOnline;
                    var ar = Ping(device);
                    device.RecordPing(ar);
                    if (countGlobally) { if (ar.Success) Progress.AddSuccess(); else Progress.AddFailed(); }
                    Progress.NotifyChanged();
                    DeviceUpdated?.Invoke(device);
                    // A muted device (e.g. a printer switched off at night)
                    // gets no rapid-fire burst when it drops - it stays on the plain
                    // slow recheck cadence.
                    if (wasOnline && !device.IsOnline && !device.IsMuted) OfflineBurst(device, ct);
                }
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            sem.Release();
            // Always release the "analyzing" slot on normal completion - even
            // for a device that finished its quota still online. Otherwise it
            // stays stuck in `analyzing` forever, and RecheckOfflineLoopAsync
            // (which only considers IPs absent from `analyzing`) can never pick
            // it back up if it drops offline afterwards. A cancelled run skips
            // this: the scan is shutting down and nothing will read it again.
            if (!ct.IsCancellationRequested)
                analyzing.TryRemove(device.Ip, out _);
        }
    }

    private readonly ConcurrentDictionary<string, byte> _bursting = new();

    /// <summary>Right after a device drops offline, probe it a few times in
    /// quick succession so short dropouts recover within seconds.</summary>
    private void OfflineBurst(Device device, CancellationToken ct)
    {
        if (!_bursting.TryAdd(device.Ip, 0)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var worker = new WorkerScope(this);
                for (int i = 0; i < OfflineBurstPings && !ct.IsCancellationRequested; i++)
                {
                    await Task.Delay(OfflineBurstIntervalMs, ct).ConfigureAwait(false);
                    if (device.IsOnline) return;   // recovered by another path
                    var r = Ping(device);
                    // Follow-up burst probe, run alongside the analysis loop's own
                    // pings - not counted toward the device's own target (or it
                    // would push CurrentPings past TargetPings within the same
                    // phase) and not counted toward the global Pings X/Y total
                    // either (housekeeping, same as the recheck loop's probes).
                    device.RecordPing(r, countsTowardTarget: false);
                    DeviceUpdated?.Invoke(device);
                    if (r.Success) return;
                }
            }
            catch (OperationCanceledException) { }
            finally { _bursting.TryRemove(device.Ip, out _); }
        }, CancellationToken.None);
    }

    /// <summary>Like <see cref="RunParallel"/> for bodies that await, so no pool thread sits blocked per item.</summary>
    private static async Task RunParallelAsync(IReadOnlyList<string> items, int maxParallel,
                                               CancellationToken ct, Func<string, Task> body)
    {
        if (items.Count == 0) return;
        using var sem = new SemaphoreSlim(Math.Max(1, maxParallel));
        var tasks = items.Select(async item =>
        {
            try { await sem.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            try { await body(item).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            finally { sem.Release(); }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
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

    // At most N devices enrich at once - hundreds in parallel exhaust sockets
    // and nbtstat processes, and every lookup then dies in its timeout. Owned
    // by this instance (not shared across engines) so a stuck enricher from a
    // stopped scan can never starve the next one.
    private readonly SemaphoreSlim _enrichGate = new(EnrichGateSize);
    private readonly ConcurrentDictionary<string, byte> _enriching = new();
    private readonly PendingTasks _enrichTasks = new();
    private readonly CancellationTokenSource _enrichCts = new();
    private bool _disposed;

    /// <summary>Cancels all enrichment still running for this engine, gives it a moment to
    /// unwind and then releases the enrichment gate. A resolver that ignores its token is
    /// abandoned after the short wait instead of stalling teardown.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _enrichCts.Cancel();
        try { Task.WhenAll(_enrichTasks.Snapshot()).Wait(EnrichShutdownWaitMs); }
        catch (Exception ex) { Log.Warn(LogArea.Scan, "Stopping the enrichment failed", ex); /* enrichment failures are already swallowed per technique */ }
        _enrichGate.Dispose();
        _enrichCts.Dispose();
    }

    private readonly object _macTrackLock = new();

    /// <summary>Register MAC→IP. If this MAC was previously at a different offline IP,
    /// evict that stale entry and raise <see cref="DeviceRemoved"/>. The read-check-write
    /// against `_macToIp`/`_devices` runs under a lock - two enrichment threads resolving
    /// the same MAC at nearly the same time must not both decide to evict (or neither).</summary>
    private void TrackMacOrEvict(Device owner, string mac)
    {
        Device? stale = null;
        lock (_macTrackLock)
        {
            if (_macToIp.TryGetValue(mac, out var oldIp) && oldIp != owner.Ip)
            {
                if (_devices.TryGetValue(oldIp, out var s) && !s.IsOnline)
                {
                    _devices.TryRemove(oldIp, out _);
                    stale = s;
                }
            }
            _macToIp[mac] = owner.Ip;
        }
        if (stale is not null) DeviceRemoved?.Invoke(stale);
    }

    /// <summary>Background MAC/hostname resolution (each resolver may block on the network);
    /// better-ranked results replace worse ones. The work is tracked so Dispose can cancel it.</summary>
    private void TryEnrich(Device device)
    {
        if (_enrichers is null || _enrichers.Count == 0 || _disposed) return;
        if (!_enriching.TryAdd(device.Ip, 0)) return;   // batch already running
        CancellationToken engineToken;
        try { engineToken = _enrichCts.Token; }
        catch (ObjectDisposedException) { _enriching.TryRemove(device.Ip, out _); return; }
        _enrichTasks.Add(Task.Run(() => EnrichDeviceAsync(device, engineToken)));
    }

    private async Task EnrichDeviceAsync(Device device, CancellationToken engineToken)
    {
        bool gateHeld = false;
        try
        {
            if (!await _enrichGate.WaitAsync(EnrichGateTimeoutMs, engineToken).ConfigureAwait(false)) return;
            gateHeld = true;
            // One shared budget for the whole batch, not per resolver - a resolver
            // that never returns must not push the others past the same deadline.
            using var batch = CancellationTokenSource.CreateLinkedTokenSource(engineToken);
            var tasks = new Task[_enrichers!.Count];
            for (int i = 0; i < tasks.Length; i++)
                tasks[i] = ApplyEnrichResultAsync(device, i, _enrichers[i], batch.Token);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(engineToken);
            await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(EnrichJoinTimeoutMs, deadline.Token)).ConfigureAwait(false);
            deadline.Cancel();
            batch.Cancel();   // whatever is still running is told to stop, not just left behind
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            if (gateHeld)
            {
                try { _enrichGate.Release(); }
                catch (ObjectDisposedException) { }
            }
            _enriching.TryRemove(device.Ip, out _);
        }
    }

    private async Task ApplyEnrichResultAsync(Device device, int rank,
        Func<string, CancellationToken, Task<(string? mac, string? host)>> resolve, CancellationToken ct)
    {
        try
        {
            // Run on the pool so a resolver that blocks before its first await cannot hold up
            // the other techniques of the same batch.
            var (mac, host) = await Task.Run(() => resolve(device.Ip, ct), ct).ConfigureAwait(false);
            var (changed, appliedMac) = ApplyEnrichRank(device, rank, mac, host);
            if (appliedMac is not null) TrackMacOrEvict(device, appliedMac);
            if (changed) DeviceUpdated?.Invoke(device);
        }
        catch (Exception ex) { Log.Debug(LogArea.Scan, "Enrichment step failed: " + ex.GetType().Name); /* each technique is best-effort */ }
    }

    /// <summary>The same best-rank-wins merge as above, factored out so a
    /// one-off "resolve name again" outside an active scan (manual context-menu
    /// entry, background retry pass in MainViewModel) applies results exactly
    /// the way a live scan itself would. Doesn't touch MAC-eviction tracking or
    /// raise DeviceUpdated - callers outside a scan have no `_macToIp` map to
    /// evict from and refresh their own view model directly.</summary>
    internal static (bool changed, string? appliedMac) ApplyEnrichRank(
        Device device, int rank, string? mac, string? host)
    {
        // Host comes straight from the remote device's own DNS/
        // mDNS/NetBIOS reply - strip control characters and cap the length
        // before it can reach a report or the database.
        var cleanHost = host is null ? null : NetworkText.Sanitize(host);
        bool changed = false;
        string? appliedMac = null;
        lock (device)
        {
            if (!string.IsNullOrEmpty(cleanHost) && rank < device.HostnameRank)
            { device.Hostname = cleanHost; device.HostnameRank = rank; changed = true; }
            if (!string.IsNullOrEmpty(mac))
            {
                if (rank < device.MacRank)
                { device.Mac = mac; device.MacRank = rank; changed = true; appliedMac = mac; }
                else if (device.Mac is { } current && current != Device.Unknown
                         && !string.Equals(current, mac, StringComparison.OrdinalIgnoreCase))
                {
                    // A second, different MAC answered for this IP this
                    // run - a same-or-worse-ranked technique disagreeing with the
                    // MAC already applied (rank check above already excludes a
                    // simple "better technique overrides worse" replacement).
                    device.HasMacConflictThisRun = true;
                    changed = true;
                }
            }
        }
        return (changed, appliedMac);
    }
}
