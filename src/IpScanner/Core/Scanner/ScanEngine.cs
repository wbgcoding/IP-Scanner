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
    private readonly Func<string, (string? mac, string? host)>? _enrich;
    private const int IcmpTimeoutMs = 1000;

    /// <param name="ping">Ping function (injected for tests).</param>
    /// <param name="enrich">Optional MAC/hostname resolver, called once per device
    /// when it first answers. Null in tests; production passes ARP+DNS+NetBIOS.</param>
    public ScanEngine(Func<string, int, PingResult> ping,
                      Func<string, (string? mac, string? host)>? enrich = null)
    {
        _ping = ping;
        _enrich = enrich;
    }

    /// <summary>Tracks ping counters across the full scan lifetime.</summary>
    public ScanProgress Progress { get; } = new();

    /// <summary>Raised whenever a device's state changes (online, new ping, etc.).</summary>
    public event Action<Device>? DeviceUpdated;

    private readonly ConcurrentDictionary<string, Device> _devices = new();
    public IReadOnlyCollection<Device> Devices => _devices.Values.ToArray();

    private int _activePings;
    /// <summary>Number of ping operations in flight right now.</summary>
    public int ActivePings => Volatile.Read(ref _activePings);

    private PingResult Ping(string ip)
    {
        Interlocked.Increment(ref _activePings);
        try { return _ping(ip, IcmpTimeoutMs); }
        finally { Interlocked.Decrement(ref _activePings); }
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
        using var analysisSem = new SemaphoreSlim(Math.Max(1, workers));
        var analysisTasks = new ConcurrentBag<Task>();
        // IPs whose analysis loop has started (prevents double-starts from rechecks).
        var analyzing = new ConcurrentDictionary<string, byte>();

        await RunParallel(ips, workers, ct,
            ip =>
            {
                if (ct.IsCancellationRequested) return;
                var device = _devices.GetOrAdd(ip, x => new Device(x)
                {
                    TargetPings = cfg.PingCount == 0 ? 1 : cfg.PingCount,
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
                    // First reply: resolve MAC + hostname OFF the critical path so
                    // discovery finishes fast and analysis pings start without delay.
                    TryEnrich(device);
                    if ((infinite || analysisPerIp > 0) && analyzing.TryAdd(ip, 0))
                        analysisTasks.Add(AnalyzeDeviceAsync(device, cfg, infinite, analysisSem, ct));
                }
                else if (analysisPerIp > 0)
                {
                    // Offline at discovery: its analysis pings are skipped.
                    Progress.AddSkipped(analysisPerIp);
                    Progress.NotifyChanged();
                }
            });

        // Recheck offline IPs periodically while the run is active; a device
        // that comes online joins the analysis immediately.
        using var recheckCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var recheckTask = cfg.OfflineRecheckSeconds > 0 && cfg.PingCount != 0
            ? RecheckOfflineLoopAsync(cfg, infinite, analysisPerIp, workers, analysisSem,
                                      analysisTasks, analyzing, ct, recheckCts.Token)
            : Task.CompletedTask;

        // Wait until no analysis task is left — rechecks may add new ones mid-wait.
        while (true)
        {
            var snapshot = analysisTasks.ToArray();
            await Task.WhenAll(snapshot).ConfigureAwait(false);
            if (snapshot.Length == analysisTasks.Count) break;
        }
        recheckCts.Cancel();
        try { await recheckTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        await Task.WhenAll(analysisTasks.ToArray()).ConfigureAwait(false);
    }

    /// <summary>Every OfflineRecheckSeconds, ping all still-offline IPs once;
    /// devices that answer start their analysis loop right away.</summary>
    private async Task RecheckOfflineLoopAsync(ScanConfig cfg, bool infinite, int analysisPerIp,
        int workers, SemaphoreSlim sem, ConcurrentBag<Task> tasks,
        ConcurrentDictionary<string, byte> analyzing, CancellationToken scanCt, CancellationToken loopCt)
    {
        while (!loopCt.IsCancellationRequested)
        {
            try { await Task.Delay(cfg.OfflineRecheckSeconds * 1000, loopCt).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            var offline = _devices.Values
                .Where(d => !d.IsOnline && !analyzing.ContainsKey(d.Ip))
                .Select(d => d.Ip).ToList();
            if (offline.Count == 0) continue;

            await RunParallel(offline, workers, loopCt, ip =>
            {
                if (loopCt.IsCancellationRequested) return;
                var device = _devices[ip];
                var r = Ping(ip);
                device.RecordPing(r);
                // Failed probes are not counted as scan pings (they would grow
                // unbounded on long runs); a reply counts and joins the run.
                if (!r.Success) { DeviceUpdated?.Invoke(device); return; }
                Progress.AddSuccess();
                Progress.NotifyChanged();
                DeviceUpdated?.Invoke(device);
                TryEnrich(device);
                if ((infinite || analysisPerIp > 0) && analyzing.TryAdd(ip, 0))
                {
                    if (analysisPerIp > 0) Progress.AddSkipped(-analysisPerIp);   // un-skip: it scans now
                    tasks.Add(AnalyzeDeviceAsync(device, cfg, infinite, sem, scanCt));
                }
            }).ConfigureAwait(false);
        }
    }

    /// <summary>N analysis pings for one device, gated by the analysis semaphore.</summary>
    private async Task AnalyzeDeviceAsync(Device device, ScanConfig cfg, bool infinite,
                                          SemaphoreSlim sem, CancellationToken ct)
    {
        try { await sem.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        try
        {
            await Task.Run(() =>
            {
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
                    // Retry MAC/hostname a few pings in — the first attempt often
                    // misses for routed/other-subnet devices (slow DNS/NetBIOS).
                    if ((i == 5 || i == 60 || i == 600) &&
                        (device.Mac is null || device.Hostname is null))
                        TryEnrich(device);
                }
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally { sem.Release(); }
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
    private static readonly SemaphoreSlim EnrichGate = new(12);

    /// <summary>Fire-and-forget MAC/hostname resolution; never blocks the ping path.
    /// LongRunning = dedicated thread, so enrichment isn't starved while the
    /// thread pool is saturated with blocking ping loops.</summary>
    private void TryEnrich(Device device)
    {
        if (_enrich is null) return;
        _ = Task.Factory.StartNew(() =>
        {
            if (!EnrichGate.Wait(30000)) return;   // give up quietly under extreme load
            try
            {
                var (mac, host) = _enrich(device.Ip);
                if (!string.IsNullOrEmpty(mac)) device.Mac = mac;
                if (!string.IsNullOrEmpty(host)) device.Hostname = host;
                DeviceUpdated?.Invoke(device);
            }
            catch { /* enrichment is best-effort */ }
            finally { EnrichGate.Release(); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private static void InterruptibleSleep(int ms, CancellationToken ct)
    {
        try { Task.Delay(ms, ct).Wait(ct); }
        catch (OperationCanceledException) { }
        catch (AggregateException) { }
    }
}
