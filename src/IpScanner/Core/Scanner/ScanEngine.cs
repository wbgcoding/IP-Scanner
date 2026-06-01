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
    private const int IcmpTimeoutMs = 1000;

    public ScanEngine(Func<string, int, PingResult> ping) => _ping = ping;

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
                    TargetPings = cfg.PingCount,
                    OfflineAfterFailures = cfg.OfflineAfterFailedPings,
                });
                var r = _ping(ip, IcmpTimeoutMs);
                device.RecordPing(r);
                DeviceUpdated?.Invoke(device);
            });

        if (cfg.PingCount == 0) return;

        var online = _devices.Values.Where(d => d.IsOnline).Select(d => d.Ip).ToList();
        await RunParallel(online, cfg.PingThreads, ct, ip =>
        {
            var device = _devices[ip];
            int target = infinite ? int.MaxValue : cfg.PingCount;
            for (int i = 1; i < target && !ct.IsCancellationRequested; i++)
            {
                if (cfg.PingIntervalMs > 0) InterruptibleSleep(cfg.PingIntervalMs, ct);
                if (ct.IsCancellationRequested) break;
                device.RecordPing(_ping(ip, IcmpTimeoutMs));
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
