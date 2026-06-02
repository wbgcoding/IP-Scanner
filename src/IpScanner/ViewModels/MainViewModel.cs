using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core.Data;
using IpScanner.Core.Export;
using IpScanner.Core.Models;
using IpScanner.Core.Net;
using IpScanner.Core.Scanner;

namespace IpScanner.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly Func<string, int, PingResult> _pingFunc;
    private readonly Func<NetworkInfo> _detectNetwork;
    private readonly Action<Action> _dispatch;
    private readonly Func<string, (string? mac, string? host)>? _enrich;
    private CancellationTokenSource? _cts;

    private const string DbPath = "scanner.db";
    private const int UiThrottleMs = 100;   // coalesce live UI updates to ~10/s

    private static readonly Dictionary<string, string> KnownHostNames = new()
    {
        ["1.1.1.1"] = "Cloudflare", ["8.8.8.8"] = "Google",
        ["8.8.4.4"] = "Google DNS", ["9.9.9.9"] = "Quad9",
    };

    public MainViewModel(Func<string, int, PingResult> pingFunc,
                         Func<NetworkInfo> detectNetwork,
                         Action<Action> dispatch,
                         Func<string, (string? mac, string? host)>? enrich = null)
    {
        _pingFunc = pingFunc;
        _detectNetwork = detectNetwork;
        _dispatch = dispatch;
        _enrich = enrich;
    }

    public ObservableCollection<DeviceViewModel> Devices { get; } = new();
    public ObservableCollection<NetworkInfoViewModel> Networks { get; } = new();
    public ObservableCollection<InternetHostViewModel> InternetHosts { get; } = new();
    public ProgressViewModel Progress { get; } = new();
    public ScanConfig Config { get; set; } = new();
    public string? LastExportPath { get; private set; }

    private readonly Dictionary<string, DeviceViewModel> _byIp = new();
    private readonly object _byIpLock = new();
    private int _totalPings = 1;
    private long _lastProgressTick;
    private long _lastRefreshTick;

    public async Task RunScanAsync(IReadOnlyList<string>? subnetOverride = null)
    {
        _cts = new CancellationTokenSource();
        var info = _detectNetwork();
        var prefixes = subnetOverride ?? BuildPrefixes(info, Config.Subnets);

        bool infinite = Config.PingCount == ScanConfig.InfinitePingCount;
        int perIp = infinite ? 1 : Math.Max(1, Config.PingCount);
        _totalPings = Math.Max(1, prefixes.Count * (Ipv4.LastHost - Ipv4.FirstHost + 1) * perIp);

        _dispatch(() =>
        {
            Networks.Clear();
            for (int i = 0; i < prefixes.Count; i++)
            {
                var ni = i == 0 ? info : new NetworkInfo { Ip = prefixes[i] + ".0" };
                Networks.Add(new NetworkInfoViewModel(i + 1, ni, GroupColorPalette.ColorForIndex(i)));
            }
            Progress.Phase = "Discovery";
        });

        // Internet latency runs in the background — never blocks the scan.
        _ = PingInternetAsync(_cts.Token);

        var engine = new ScanEngine(_pingFunc, _enrich);
        engine.DeviceUpdated += OnDeviceUpdated;
        engine.Progress.Changed += () =>
        {
            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastProgressTick) < UiThrottleMs) return;
            Interlocked.Exchange(ref _lastProgressTick, now);
            _dispatch(() => UpdateProgress(engine.Progress));
        };

        try
        {
            await engine.ScanAsync(prefixes, Config, _cts.Token);
        }
        catch (OperationCanceledException) { /* stopped by user */ }

        // Assign device groups (colors) now that all devices/MACs are known.
        var devices = engine.Devices.ToList();
        DeviceGrouper.AssignGroups(devices, info.Gateway);

        // Export + DB persistence after the scan (also runs after a user stop).
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var gatewaySlug = Slug(info.Gateway);

        if (Config.FileOutput && devices.Count > 0)
        {
            LastExportPath = TxtExporter.Write(devices, info, Config.OutputDirectory, timestamp, gatewaySlug);
            if (Config.ExportCsv)
                CsvExporter.Write(devices, Config.OutputDirectory, timestamp, gatewaySlug);
            Raise(nameof(LastExportPath));
        }

        if (Config.KnownDevicesDb && info.Gateway is not null)
        {
            var gw = devices.FirstOrDefault(d => d.Ip == info.Gateway);
            if (gw?.Mac is { } mac && mac != "Unknown")
            {
                try { new KnownDevicesDb(DbPath).Save(mac, devices, timestamp); } catch { /* DB optional */ }
            }
        }

        // Final reconcile so every row shows its true end state.
        _dispatch(() =>
        {
            RefreshAll();
            UpdateProgress(engine.Progress);
            Progress.Phase = "Bereit";
        });
    }

    public async Task PingInternetAsync(CancellationToken ct)
    {
        if (!Config.EnableInternetPing) return;
        _dispatch(() =>
        {
            InternetHosts.Clear();
            foreach (var ip in Config.InternetHosts)
                InternetHosts.Add(new InternetHostViewModel(
                    KnownHostNames.GetValueOrDefault(ip, ip), ip));
        });

        var hosts = InternetHosts.ToList();
        await Task.Run(() =>
        {
            foreach (var host in hosts)
            {
                if (ct.IsCancellationRequested) break;
                var r = _pingFunc(host.Ip, 1000);
                _dispatch(() => host.SetLatency(r.Success ? r.LatencyMs : null));
            }
        }, ct);
    }

    public void Stop() => _cts?.Cancel();

    private static IReadOnlyList<string> BuildPrefixes(NetworkInfo info, List<string> extra)
    {
        var list = new List<string>();
        if (info.Ip is not null) list.Add(Ipv4.SubnetPrefix(info.Ip));
        foreach (var s in extra)
        {
            var prefix = Ipv4.SubnetPrefix(s.Split('/')[0]);
            if (!list.Contains(prefix)) list.Add(prefix);
        }
        return list;
    }

    private void OnDeviceUpdated(Device d)
    {
        bool isNew;
        lock (_byIpLock) { isNew = !_byIp.ContainsKey(d.Ip); }

        if (isNew)
        {
            _dispatch(() =>
            {
                lock (_byIpLock)
                {
                    if (_byIp.ContainsKey(d.Ip)) return;
                    var vm = new DeviceViewModel(d);
                    _byIp[d.Ip] = vm;
                    Devices.Add(vm);
                }
            });
            return;
        }

        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastRefreshTick) < UiThrottleMs) return;
        Interlocked.Exchange(ref _lastRefreshTick, now);
        _dispatch(() =>
        {
            lock (_byIpLock)
            {
                if (_byIp.TryGetValue(d.Ip, out var vm)) vm.Refresh();
            }
        });
    }

    private void RefreshAll()
    {
        lock (_byIpLock)
        {
            foreach (var vm in Devices) vm.Refresh();
        }
    }

    private void UpdateProgress(ScanProgress p)
    {
        List<DeviceViewModel> snapshot;
        lock (_byIpLock) { snapshot = Devices.ToList(); }

        int online = snapshot.Count(d => d.IsOnline);
        int total = snapshot.Count;
        Progress.SetDevices(online, total - online, 0, Math.Max(total, 1));
        Progress.SetPings(p.SuccessPings, p.FailedPings, p.SkippedPings, _totalPings);

        foreach (var net in Networks)
        {
            var prefix = Ipv4.SubnetPrefix(net.Cidr.Split('/')[0]);
            var inNet = snapshot.Where(d => d.Ip.StartsWith(prefix + ".")).ToList();
            net.OnlineCount = inNet.Count(d => d.IsOnline);
            net.OfflineCount = inNet.Count(d => !d.IsOnline);
        }
    }

    private static string Slug(string? host)
    {
        if (string.IsNullOrEmpty(host)) return "";
        var safe = new string(host.Where(c => !"\\/:*?\"<>| \t".Contains(c)).ToArray());
        return safe.Length > 40 ? safe[..40] : safe;
    }
}
