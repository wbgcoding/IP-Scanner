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
    public IReadOnlyCollection<Device> Devices => (IReadOnlyCollection<Device>)_devices.Values;

    public async Task ScanAsync(IReadOnlyList<string> subnetPrefixes, ScanConfig cfg,
                                CancellationToken ct)
    {
        var ips = subnetPrefixes.SelectMany(Ipv4.HostsInSubnet).ToList();
        bool infinite = cfg.PingCount == ScanConfig.InfinitePingCount;

        await RunParallel(ips, cfg.InitPingThreads <= 0 ? ips.Count : cfg.InitPingThreads, ct,
            ip =>
            {
                if (ct.IsCancellationRequested) return;
                var device = _devices.GetOrAdd(ip, x => new Device(x)
                {
                    TargetPings = cfg.PingCount == 0 ? 1 : cfg.PingCount,
                    OfflineAfterFailures = cfg.OfflineAfterFailedPings,
                });
                var r = _ping(ip, IcmpTimeoutMs);
                device.RecordPing(r);
                if (r.Success) Progress.AddSuccess(); else Progress.AddFailed();
                Progress.AddProcessed();
                Progress.NotifyChanged();
                DeviceUpdated?.Invoke(device);
                // First reply: resolve MAC + hostname OFF the critical path so
                // discovery finishes fast and analysis pings start without delay.
                if (r.Success && device.SuccessCount == 1 && _enrich is not null)
                {
                    var d = device;
                    var addr = ip;
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var (mac, host) = _enrich(addr);
                            if (!string.IsNullOrEmpty(mac)) d.Mac = mac;
                            if (!string.IsNullOrEmpty(host)) d.Hostname = host;
                            DeviceUpdated?.Invoke(d);
                        }
                        catch { /* enrichment is best-effort */ }
                    });
                }
            });

        if (cfg.PingCount == 0) return;

        var online = _devices.Values.Where(d => d.IsOnline).Select(d => d.Ip).ToList();

        int analysisPerIp = infinite ? 0 : Math.Max(0, cfg.PingCount - 1);
        int offlineCount = _devices.Count - online.Count;
        if (analysisPerIp > 0 && offlineCount > 0)
        {
            Progress.AddSkipped(offlineCount * analysisPerIp);
            Progress.NotifyChanged();
        }

        await RunParallel(online, cfg.PingThreads, ct, ip =>
        {
            var device = _devices[ip];
            int target = infinite ? int.MaxValue : cfg.PingCount;
            for (int i = 1; i < target && !ct.IsCancellationRequested; i++)
            {
                if (cfg.PingIntervalMs > 0) InterruptibleSleep(cfg.PingIntervalMs, ct);
                if (ct.IsCancellationRequested) break;
                var ar = _ping(ip, IcmpTimeoutMs);
                device.RecordPing(ar);
                if (ar.Success) Progress.AddSuccess(); else Progress.AddFailed();
                Progress.NotifyChanged();
                DeviceUpdated?.Invoke(device);
            }
        });
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

    private static void InterruptibleSleep(int ms, CancellationToken ct)
    {
        try { Task.Delay(ms, ct).Wait(ct); }
        catch (OperationCanceledException) { }
        catch (AggregateException) { }
    }
}
