using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core.Data;
using IpScanner.Core.Export;
using IpScanner.Core.Localization;
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

    internal const string DbPath = "scanner.db";

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
    public bool HasExport => !string.IsNullOrEmpty(LastExportPath);
    /// <summary>User-entered subnet (e.g. "192.168.1.0/24"); overrides auto-detect.</summary>
    public string? ManualSubnet { get; set; }

    private readonly Dictionary<string, DeviceViewModel> _byIp = new();
    private readonly object _byIpLock = new();
    private long _totalPings = 1;
    private int _plannedDevices = 1;
    private long _lastGroupTick;
    private string? _selfIp;
    private string? _selfMac;
    private string? _selfHost;
    private string? _gatewayIp;
    private HashSet<string> _pinned = new();

    /// <summary>Full scan using the configured ping count; writes report/DB.</summary>
    public Task RunScanAsync(IReadOnlyList<string>? subnetOverride = null)
        => RunScanInternal(null, persist: true, subnetOverride);

    /// <summary>Startup discovery sweep: 1 ping per IP, no analysis, no file/DB.
    /// Populates the network sidebar and online devices immediately.</summary>
    public Task RunInitScanAsync()
        => RunScanInternal(0, persist: false, null);

    private async Task RunScanInternal(int? pingCountOverride, bool persist,
                                       IReadOnlyList<string>? subnetOverride)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var info = _detectNetwork();
        _selfIp = info.Ip;
        _selfMac = info.Mac;
        _selfHost = Environment.MachineName;
        _gatewayIp = info.Gateway;
        _pinned = new HashSet<string>(Config.PinnedIps);
        LastExportPath = null;
        Raise(nameof(HasExport));
        var cfg = pingCountOverride is null ? Config : Config.CloneWith(pingCountOverride.Value);
        var prefixes = subnetOverride ?? BuildPrefixes(info, cfg.Subnets);

        bool infinite = cfg.PingCount == ScanConfig.InfinitePingCount;
        int perIp = infinite ? 1 : Math.Max(1, cfg.PingCount);
        int hostsPerSubnet = Ipv4.LastHost - Ipv4.FirstHost + 1;
        _plannedDevices = Math.Max(1, prefixes.Count * hostsPerSubnet);
        _totalPings = Math.Max(1L, (long)_plannedDevices * perIp);

        _dispatch(() =>
        {
            lock (_byIpLock) { Devices.Clear(); _byIp.Clear(); }
            Networks.Clear();
            for (int i = 0; i < prefixes.Count; i++)
            {
                var ni = i == 0 ? info : new NetworkInfo { Ip = prefixes[i] + ".0" };
                Networks.Add(new NetworkInfoViewModel(i + 1, ni, GroupColorPalette.ColorForIndex(i)));
            }
            Progress.Phase = Loc.PhaseSearching;
        });

        // Internet latency runs in the background, independent of the scan's
        // cancellation, so it always completes even on a quick init sweep / stop.
        _ = PingInternetAsync();

        var engine = new ScanEngine(_pingFunc, _enrich);
        engine.DeviceUpdated += OnDeviceUpdated;
        engine.Progress.Changed += () => _dispatch(() => UpdateProgress(engine));

        try
        {
            await engine.ScanAsync(prefixes, cfg, _cts.Token);
        }
        catch (OperationCanceledException) { /* stopped by user */ }

        var devices = engine.Devices.ToList();

        if (persist)
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            // Prefer the gateway's hostname for the filename; fall back to its IP.
            var gwDev = devices.FirstOrDefault(d => d.Ip == info.Gateway);
            var gwName = gwDev?.Hostname is { } h && h != Device.Unknown ? h : info.Gateway;
            var gatewaySlug = Slug(gwName);

            if (cfg.FileOutput && devices.Count > 0)
            {
                LastExportPath = TxtExporter.Write(devices, info, cfg.OutputDirectory, timestamp, gatewaySlug);
                if (cfg.ExportCsv)
                    CsvExporter.Write(devices, cfg.OutputDirectory, timestamp, gatewaySlug);
                Raise(nameof(LastExportPath));
                Raise(nameof(HasExport));
            }

            if (cfg.KnownDevicesDb && info.Gateway is not null)
            {
                var gw = devices.FirstOrDefault(d => d.Ip == info.Gateway);
                if (gw?.Mac is { } mac && mac != Device.Unknown)
                {
                    try { new KnownDevicesDb(DbPath).Save(mac, devices, timestamp); } catch { /* DB optional */ }
                }
            }
        }

        // Final reconcile so every row shows its true end state.
        _dispatch(() =>
        {
            SyncDevices(engine);
            RefreshAll();
            UpdateProgress(engine, forceGroups: true);
            Progress.Phase = Loc.PhaseReady;
        });
    }

    public async Task PingInternetAsync(CancellationToken ct = default)
    {
        if (!Config.EnableInternetPing) return;

        // Build the list synchronously (don't snapshot the ObservableCollection
        // after an async dispatch — it would still be empty and ping nothing).
        var hosts = Config.InternetHosts
            .Select(ip => new InternetHostViewModel(KnownHostNames.GetValueOrDefault(ip, ip), ip))
            .ToList();
        _dispatch(() =>
        {
            InternetHosts.Clear();
            foreach (var h in hosts) InternetHosts.Add(h);
        });

        // Ping all hosts in parallel, two attempts each (robust against a dropped packet).
        await Task.WhenAll(hosts.Select(host => Task.Run(() =>
        {
            double? latency = null;
            for (int attempt = 0; attempt < 2 && latency is null; attempt++)
            {
                if (ct.IsCancellationRequested) break;
                var r = _pingFunc(host.Ip, 1500);
                if (r.Success) latency = r.LatencyMs;
            }
            _dispatch(() => host.SetLatency(latency));
        })));
    }

    public void Stop() => _cts?.Cancel();

    private IReadOnlyList<string> BuildPrefixes(NetworkInfo info, List<string> extra)
    {
        // Manual subnet (header field + CIDR) wins and replaces auto-detect + config.
        var manual = ManualPrefixes(ManualSubnet);
        if (manual is { Count: > 0 }) return manual;

        var list = new List<string>();
        if (info.Ip is not null) list.Add(Ipv4.SubnetPrefix(info.Ip));
        foreach (var s in extra)
        {
            var prefix = Ipv4.SubnetPrefix(s.Split('/')[0]);
            if (!list.Contains(prefix)) list.Add(prefix);
        }
        return list;
    }

    /// <summary>Expand "ip/cidr" into the list of /24 prefixes to scan.
    /// /24 -> one prefix; /16 -> 256 (a.b.0..255); /8 -> 65536 (a.0..255.0..255).
    /// Returns null when the input isn't a usable IPv4 subnet.</summary>
    private static List<string>? ManualPrefixes(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var slash = input.Split('/');
        var oct = slash[0].Trim().Split('.');
        int cidr = slash.Length > 1 && int.TryParse(slash[1].Trim(), out var c) ? c : 24;

        bool Ok(int i) => i < oct.Length && int.TryParse(oct[i], out var n) && n is >= 0 and <= 255;

        if (cidr >= 24)
        {
            if (!(Ok(0) && Ok(1) && Ok(2))) return null;
            return new List<string> { $"{oct[0]}.{oct[1]}.{oct[2]}" };
        }
        if (cidr >= 16)
        {
            if (!(Ok(0) && Ok(1))) return null;
            var list = new List<string>(256);
            for (int t = 0; t <= 255; t++) list.Add($"{oct[0]}.{oct[1]}.{t}");
            return list;
        }
        if (!Ok(0)) return null;                          // /8 (or smaller)
        var big = new List<string>(65536);
        for (int s = 0; s <= 255; s++)
            for (int t = 0; t <= 255; t++) big.Add($"{oct[0]}.{s}.{t}");
        return big;
    }

    // Only ONLINE devices are shown in the list; offline ones are added/removed
    // from the grid as their state flips.
    private void OnDeviceUpdated(Device d)
    {
        ApplySelfInfo(d);
        _dispatch(() =>
        {
        lock (_byIpLock)
        {
            bool tracked = _byIp.TryGetValue(d.Ip, out var vm);
            if (d.IsOnline)
            {
                if (!tracked) { vm = new DeviceViewModel(d, d.Ip == _selfIp, _pinned.Contains(d.Ip)); _byIp[d.Ip] = vm; InsertSorted(vm); }
                else vm!.Refresh();
            }
            else if (tracked)
            {
                Devices.Remove(vm!);
                _byIp.Remove(d.Ip);
            }
        }
        });
    }

    // Own machine can't be ARP'd / reverse-resolved reliably: force detection
    // values (override any enrichment that may run afterwards on the self row).
    private void ApplySelfInfo(Device d)
    {
        if (d.Ip != _selfIp) return;
        if (!string.IsNullOrEmpty(_selfMac)) d.Mac = _selfMac;
        if (!string.IsNullOrEmpty(_selfHost)) d.Hostname = _selfHost;
    }

    /// <summary>Reconcile the visible (online-only) list against the engine.</summary>
    private void SyncDevices(ScanEngine engine)
    {
        lock (_byIpLock)
        {
            foreach (var dev in engine.Devices.Where(d => d.IsOnline))
            {
                if (!_byIp.ContainsKey(dev.Ip))
                {
                    var vm = new DeviceViewModel(dev, dev.Ip == _selfIp, _pinned.Contains(dev.Ip));
                    _byIp[dev.Ip] = vm;
                    InsertSorted(vm);
                }
            }
        }
    }

    /// <summary>Insert a device row: pinned rows first, then ordered by numeric IP.</summary>
    private void InsertSorted(DeviceViewModel vm)
    {
        int Rank(DeviceViewModel d) => d.IsPinned ? 0 : 1;
        int i = 0;
        while (i < Devices.Count &&
               (Rank(Devices[i]) < Rank(vm) ||
                (Rank(Devices[i]) == Rank(vm) && Devices[i].IpSortKey <= vm.IpSortKey)))
            i++;
        Devices.Insert(i, vm);
    }

    private void RefreshAll()
    {
        lock (_byIpLock)
        {
            foreach (var vm in Devices) vm.Refresh();
        }
    }

    private void UpdateProgress(ScanEngine engine, bool forceGroups = false)
    {
        var all = engine.Devices.ToList();
        var p = engine.Progress;

        // Live grouping is O(n) — debounce it (≤ every 400 ms) so a fast ping
        // stream doesn't pin the UI thread; force a final pass at scan end.
        long now = Environment.TickCount64;
        if (forceGroups || now - _lastGroupTick >= 400)
        {
            _lastGroupTick = now;
            DeviceGrouper.AssignGroups(all, _gatewayIp);
        }
        lock (_byIpLock) { foreach (var vm in Devices) vm.Refresh(); }

        int discovered = all.Count;
        int online = all.Count(d => d.IsOnline);
        int offline = discovered - online;
        int unknown = Math.Max(0, _plannedDevices - discovered);   // not yet scanned
        Progress.SetDevices(online, offline, unknown, Math.Max(_plannedDevices, 1));
        Progress.SetPings(p.SuccessPings, p.FailedPings, p.SkippedPings, _totalPings);

        foreach (var net in Networks)
        {
            var prefix = Ipv4.SubnetPrefix(net.Cidr.Split('/')[0]);
            var inNet = all.Where(d => d.Ip.StartsWith(prefix + ".")).ToList();
            net.OnlineCount = inNet.Count(d => d.IsOnline);
            net.OfflineCount = inNet.Count(d => !d.IsOnline);
            var avgs = inNet.Where(d => d.IsOnline && d.AvgMs is not null).Select(d => d.AvgMs!.Value).ToList();
            net.SetAvg(avgs.Count > 0 ? avgs.Average() : null);
        }

        MarkExtremes();
    }

    // Mark best (lowest) green and worst (highest) red per ping column across rows.
    private void MarkExtremes()
    {
        List<DeviceViewModel> list;
        lock (_byIpLock) { list = Devices.ToList(); }

        var (avgB, avgW) = Extremes(list, v => v.AvgRaw);
        var (minB, minW) = Extremes(list, v => v.MinRaw);
        var (maxB, maxW) = Extremes(list, v => v.MaxRaw);
        var (lastB, lastW) = Extremes(list, v => v.LastRaw);

        static int Mark(DeviceViewModel vm, DeviceViewModel? b, DeviceViewModel? w)
            => vm == b ? -1 : vm == w ? 1 : 0;

        foreach (var vm in list)
            vm.SetMarks(Mark(vm, avgB, avgW), Mark(vm, minB, minW),
                        Mark(vm, maxB, maxW), Mark(vm, lastB, lastW));
    }

    private static (DeviceViewModel? best, DeviceViewModel? worst) Extremes(
        List<DeviceViewModel> list, Func<DeviceViewModel, double?> sel)
    {
        var withVal = list.Where(v => sel(v) is not null).ToList();
        if (withVal.Count < 2) return (null, null);
        var best = withVal.OrderBy(v => sel(v)!.Value).First();
        var worst = withVal.OrderByDescending(v => sel(v)!.Value).First();
        return sel(best)!.Value == sel(worst)!.Value ? (null, null) : (best, worst);
    }

    private static string Slug(string? host)
    {
        if (string.IsNullOrEmpty(host)) return "";
        var safe = new string(host.Where(c => !"\\/:*?\"<>| \t".Contains(c)).ToArray());
        return safe.Length > 40 ? safe[..40] : safe;
    }
}
