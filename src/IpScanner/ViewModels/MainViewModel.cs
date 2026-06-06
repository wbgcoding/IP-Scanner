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
    // UI pacing: throttle the aggregate pass, debounce the O(n) grouping,
    // and never ping public internet hosts faster than twice a second.
    private const int AggregateThrottleMs = 100;
    private const int GroupDebounceMs = 400;
    private const int MinInternetIntervalMs = 500;

    private readonly Func<string, int, PingResult> _pingFunc;
    private readonly Func<NetworkInfo> _detectNetwork;
    private readonly Action<Action> _dispatch;
    private readonly IReadOnlyList<Func<string, (string? mac, string? host)>>? _enrichers;
    private CancellationTokenSource? _cts;

    /// <summary>Split an "ip name" config entry; without a name the IP doubles as name.</summary>
    private static (string ip, string name) ParseHostEntry(string entry)
    {
        var parts = entry.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2 ? (parts[0], parts[1]) : (entry.Trim(), entry.Trim());
    }

    public MainViewModel(Func<string, int, PingResult> pingFunc,
                         Func<NetworkInfo> detectNetwork,
                         Action<Action> dispatch,
                         IReadOnlyList<Func<string, (string? mac, string? host)>>? enrichers = null)
    {
        _pingFunc = pingFunc;
        _detectNetwork = detectNetwork;
        _dispatch = dispatch;
        _enrichers = enrichers;
    }

    public ObservableCollection<DeviceViewModel> Devices { get; } = new();
    public ObservableCollection<NetworkInfoViewModel> Networks { get; } = new();
    public ObservableCollection<InternetHostViewModel> InternetHosts { get; } = new();
    public ProgressViewModel Progress { get; } = new();
    public ScanConfig Config { get; set; } = new();

    private bool _isScanning;
    /// <summary>True while a scan (incl. startup sweep) is running.</summary>
    public bool IsScanning
    {
        get => _isScanning;
        private set { if (_isScanning != value) { _isScanning = value; Raise(nameof(IsScanning)); } }
    }

    public string? LastExportPath { get; private set; }
    /// <summary>"name (size)" per written report file, one line each.</summary>
    public string ExportInfo { get; private set; } = "";
    public bool HasExport => ExportInfo.Length > 0;
    /// <summary>"Threads: 12" — live count of in-flight pings for the footer.</summary>
    public string ThreadsText { get; private set; } = "";
    /// <summary>User-entered subnet (e.g. "192.168.1.0/24"); overrides auto-detect.</summary>
    public string? ManualSubnet { get; set; }

    private readonly Dictionary<string, DeviceViewModel> _byIp = new();
    private readonly object _byIpLock = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _dirty = new();
    private long _totalPings = 1;
    private int _plannedDevices = 1;
    private long _lastGroupTick;
    private string? _selfIp;
    private string? _selfMac;
    private string? _selfHost;
    private string? _gatewayIp;
    private HashSet<string> _pinned = new();
    // Configured display metadata: pinned-IP entries (ip -> name/color) and
    // subnet entries expanded to /24 prefixes (prefix -> name/color).
    private Dictionary<string, NetEntry> _pinnedMeta = new();
    private Dictionary<string, NetEntry> _subnetMeta = new();
    private readonly OverrideStore _overrides = new();

    /// <summary>Load manual hostname/group overrides (file lives next to the db).</summary>
    public void InitOverrides(string path) => _overrides.Load(path);

    /// <summary>Rebuild pinned-IP name/color metadata from current Config and
    /// re-apply to all already-visible devices. Call when PinnedIps settings change.</summary>
    /// <summary>Rebuild pinned metadata and re-apply to all visible devices.
    /// Handles pin/unpin state, hostnames, and colors. Call after PinnedIps change.</summary>
    public void RefreshPinnedNames()
    {
        _pinnedMeta = Config.PinnedIps.Select(NetEntry.Parse)
            .Where(e => e.Target.Length > 0)
            .GroupBy(e => e.Target).ToDictionary(g => g.Key, g => g.First());
        _pinned = new HashSet<string>(_pinnedMeta.Keys);

        lock (_byIpLock)
        {
            foreach (var vm in Devices.ToList())
            {
                var d = vm.Model;
                bool nowPinned = _pinned.Contains(d.Ip);
                if (vm.IsPinned != nowPinned)
                {
                    vm.IsPinned = nowPinned;
                    Devices.Remove(vm);
                    InsertSorted(vm);
                }
                if (d.HostnameRank == -1 && _overrides.Get(StableMac(d), d.Ip)?.Hostname is null)
                {
                    d.Hostname = d.AutoHostname;
                    d.HostnameRank = d.AutoHostnameRank;
                }
                if (_overrides.Get(StableMac(d), d.Ip)?.Color is null)
                    d.GroupColorOverride = null;
                ApplyPinnedName(d);
                ApplyColorOverride(d);
                vm.Refresh();
            }
        }
    }

    private static string? StableMac(Device d) =>
        d.Mac is { } m && m != Device.Unknown ? m : null;

    /// <summary>Re-apply a stored manual hostname (rank -1 beats every resolver).</summary>
    private void ApplyHostnameOverride(Device d)
    {
        if (_overrides.Get(StableMac(d), d.Ip)?.Hostname is not { } host) return;
        if (d.HostnameRank != -1) { d.AutoHostname = d.Hostname; d.AutoHostnameRank = d.HostnameRank; }
        d.Hostname = host;
        d.HostnameRank = -1;
    }

    /// <summary>Set (or clear with null) the manual hostname of a row.</summary>
    public void SetHostnameOverride(DeviceViewModel vm, string? hostname)
    {
        var d = vm.Model;
        hostname = string.IsNullOrWhiteSpace(hostname) ? null : hostname.Trim().Replace('\t', ' ');
        _overrides.SetHostname(StableMac(d), d.Ip, hostname);
        if (hostname is null)
        {
            if (d.HostnameRank == -1) { d.Hostname = d.AutoHostname; d.HostnameRank = d.AutoHostnameRank; }
        }
        else
        {
            if (d.HostnameRank != -1) { d.AutoHostname = d.Hostname; d.AutoHostnameRank = d.HostnameRank; }
            d.Hostname = hostname;
            d.HostnameRank = -1;
        }
        vm.Refresh();
    }

    /// <summary>Set (or clear with null) the manual group-square color of a row.</summary>
    public void SetColorOverride(DeviceViewModel vm, string? color)
    {
        var d = vm.Model;
        _overrides.SetColor(StableMac(d), d.Ip, color);
        d.GroupColorOverride = color;
        vm.Refresh();
    }

    private void ApplyColorOverride(Device d)
    {
        if (_overrides.Get(StableMac(d), d.Ip)?.Color is { } color)
            d.GroupColorOverride = color;
    }

    /// <summary>Full scan using the configured ping count; writes report/DB.</summary>
    public Task RunScanAsync(IReadOnlyList<string>? subnetOverride = null)
        => RunScanInternal(null, persist: true, subnetOverride);

    /// <summary>Automatic sweep right after startup: StartupPingCount pings per
    /// device with MAX threads (0 = one per device), no file/DB output.</summary>
    public Task RunInitScanAsync()
        => RunScanInternal(Math.Max(0, Config.StartupPingCount), persist: false, null,
                           scanThreadsOverride: 0);

    private async Task RunScanInternal(int? pingCountOverride, bool persist,
                                       IReadOnlyList<string>? subnetOverride,
                                       int? scanThreadsOverride = null)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsScanning = true;
        try
        {
            await RunScanCore(pingCountOverride, persist, subnetOverride, scanThreadsOverride);
        }
        finally { IsScanning = false; }
    }

    private async Task RunScanCore(int? pingCountOverride, bool persist,
                                   IReadOnlyList<string>? subnetOverride,
                                   int? scanThreadsOverride)
    {
        GroupColorPalette.Shuffle();   // fresh random group colors per run
        var info = _detectNetwork();
        _selfIp = info.Ip;
        _selfMac = info.Mac;
        _selfHost = Environment.MachineName;
        _gatewayIp = info.Gateway;
        _pinnedMeta = Config.PinnedIps.Select(NetEntry.Parse)
            .Where(e => e.Target.Length > 0)
            .GroupBy(e => e.Target).ToDictionary(g => g.Key, g => g.First());
        _pinned = new HashSet<string>(_pinnedMeta.Keys);
        _subnetMeta = new Dictionary<string, NetEntry>();
        foreach (var s in Config.Subnets)
        {
            var entry = NetEntry.Parse(s);
            foreach (var p in ManualPrefixes(entry.Target) ?? new List<string>())
                _subnetMeta[p] = entry;
        }
        LastExportPath = null;
        ExportInfo = "";
        Raise(nameof(ExportInfo));
        Raise(nameof(HasExport));
        var cfg = pingCountOverride is null ? Config : Config.CloneWith(pingCountOverride.Value);
        if (scanThreadsOverride is { } threads && pingCountOverride is not null)
            cfg.ScanThreads = threads;   // cfg is a clone here, Config stays untouched
        var prefixes = subnetOverride ?? BuildPrefixes(info, cfg.Subnets);

        bool infinite = cfg.PingCount == ScanConfig.InfinitePingCount;
        Progress.InfinitePings = infinite;
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
                _subnetMeta.TryGetValue(prefixes[i], out var meta);
                var color = meta?.Color is { Length: > 0 } c ? c : GroupColorPalette.ColorForIndex(i);
                Networks.Add(new NetworkInfoViewModel(i + 1, ni, color,
                                                      primary: i == 0, name: meta?.Name ?? ""));
            }
        });

        // Internet latency runs alongside the scan with the same ping count as
        // the table; a new scan (or Stop) cancels the previous loop via the CTS.
        _ = PingInternetAsync(_cts!.Token, cfg.PingCount == 0 ? 1 : cfg.PingCount);

        var engine = new ScanEngine(_pingFunc, _enrichers);
        engine.DeviceUpdated += OnDeviceUpdated;
        // Per-device row refreshes stay immediate (OnDeviceUpdated); the heavy
        // aggregate pass (bars, groups, extremes) is throttled so a fast ping
        // stream can't flood the UI thread and stall the table.
        long lastAggregate = 0;
        engine.Progress.Changed += () =>
        {
            long now = Environment.TickCount64;
            long last = Interlocked.Read(ref lastAggregate);
            if (now - last < AggregateThrottleMs ||
                Interlocked.CompareExchange(ref lastAggregate, now, last) != last) return;
            _dispatch(() => UpdateProgress(engine));
        };

        // Known devices show up immediately with their stored hostname/MAC;
        // live results replace those values as the run progresses.
        if (cfg.KnownDevicesDb && info.Gateway is { } gatewayIp)
        {
            try
            {
                await Task.Run(() =>
                {
                    var db = new KnownDevicesDb(cfg.DatabasePath);
                    if (db.GetNetworkMac(gatewayIp) is { } netMac)
                        engine.Preload(db.Load(netMac), cfg);
                });
            }
            catch { /* DB optional */ }
        }

        try
        {
            await engine.ScanAsync(prefixes, cfg, _cts!.Token);
        }
        catch (OperationCanceledException) { /* stopped by user */ }

        var devices = engine.Devices.ToList();

        if (persist)
        {
            var now = DateTime.Now;
            var timestamp = now.ToString("yyyyMMdd_HHmmss");          // filename-safe
            var displayTime = now.ToString("dd.MM.yyyy HH:mm:ss");    // report header
            // Prefer the gateway's hostname for the filename; fall back to its IP.
            var gwDev = devices.FirstOrDefault(d => d.Ip == info.Gateway);
            var gwName = gwDev?.Hostname is { } h && h != Device.Unknown ? h : info.Gateway;
            var gatewaySlug = Slug(gwName);

            // TXT and CSV export independently; the sidebar shows name + size.
            var written = new List<string>();
            if (devices.Count > 0)
            {
                if (cfg.FileOutput)
                    written.Add(TxtExporter.Write(devices, info, cfg.OutputDirectory, timestamp, gatewaySlug, displayTime));
                if (cfg.ExportCsv)
                    written.Add(CsvExporter.Write(devices, cfg.OutputDirectory, timestamp, gatewaySlug));
            }
            if (written.Count > 0)
            {
                LastExportPath = written[0];
                ExportInfo = string.Join(Environment.NewLine, written.Select(p =>
                {
                    var f = new System.IO.FileInfo(p);
                    return $"{f.Name} ({Core.NumberFormat.Bytes(f.Length)})";
                }));
                Raise(nameof(LastExportPath));
                Raise(nameof(ExportInfo));
                Raise(nameof(HasExport));
            }

            if (cfg.KnownDevicesDb && info.Gateway is not null)
            {
                var gw = devices.FirstOrDefault(d => d.Ip == info.Gateway);
                if (gw?.Mac is { } mac && mac != Device.Unknown)
                {
                    try { new KnownDevicesDb(cfg.DatabasePath).Save(mac, devices, timestamp); } catch { /* DB optional */ }
                }
            }
        }

        // Final reconcile so every row shows its true end state.
        _dispatch(() =>
        {
            SyncDevices(engine);
            RefreshAll();
            UpdateProgress(engine, forceGroups: true);
        });
    }

    /// <summary>Ping the configured internet hosts in parallel, pingCount times
    /// each (-1 = endless), tracking min/max/avg live — mirrors the table run.</summary>
    public async Task PingInternetAsync(CancellationToken ct = default, int pingCount = 1)
    {
        if (!Config.EnableInternetPing) return;

        // Build the list synchronously (don't snapshot the ObservableCollection
        // after an async dispatch — it would still be empty and ping nothing).
        var hosts = Config.InternetHosts
            .Select(e => { var (ip, name) = ParseHostEntry(e); return new InternetHostViewModel(name, ip); })
            .ToList();
        _dispatch(() =>
        {
            InternetHosts.Clear();
            foreach (var h in hosts) InternetHosts.Add(h);
        });

        int target = pingCount == ScanConfig.InfinitePingCount ? int.MaxValue : Math.Max(1, pingCount);
        int interval = Math.Max(Config.PingIntervalMs, MinInternetIntervalMs);
        int timeout = Math.Clamp(Config.InternetTimeoutMs, 100, 10_000);

        await Task.WhenAll(hosts.Select(host => Task.Run(async () =>
        {
            for (int i = 0; i < target && !ct.IsCancellationRequested; i++)
            {
                var r = _pingFunc(host.Ip, timeout);
                _dispatch(() =>
                {
                    host.RecordPing(r.Success ? r.LatencyMs : null);
                    UpdateInternetTotals();
                });
                if (i + 1 >= target) break;
                try { await Task.Delay(interval, ct); }
                catch (OperationCanceledException) { break; }
            }
        })));
    }

    public void Stop() => _cts?.Cancel();

    private IReadOnlyList<string> BuildPrefixes(NetworkInfo info, List<string> extra)
    {
        var list = new List<string>();
        var seen = new HashSet<string>();
        void Add(IEnumerable<string>? prefixes)
        {
            if (prefixes is null) return;
            foreach (var p in prefixes) if (seen.Add(p)) list.Add(p);
        }

        // Manual subnet (header field + CIDR) replaces only the auto-detected net.
        var manual = ManualPrefixes(ManualSubnet);
        if (manual is { Count: > 0 }) Add(manual);
        else if (info.Ip is not null) Add(new[] { Ipv4.SubnetPrefix(info.Ip) });

        // Configured subnets (settings) are always scanned in addition.
        foreach (var s in extra) Add(ManualPrefixes(NetEntry.Parse(s).Target));
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

    // Devices that answered at least once stay in the list — when they flip to
    // offline mid-scan the row remains (status turns red). Never-seen IPs are
    // never shown.
    /// <summary>Configured pinned-IP name acts as the default hostname; the
    /// manual double-click override (applied afterwards) still wins.</summary>
    private void ApplyPinnedName(Device d)
    {
        if (!_pinnedMeta.TryGetValue(d.Ip, out var e)) return;
        if (e.Name.Length > 0)
        {
            if (d.HostnameRank != -1) { d.AutoHostname = d.Hostname; d.AutoHostnameRank = d.HostnameRank; }
            d.Hostname = e.Name;
            d.HostnameRank = -1;
        }
        // Pinned color as group color baseline; manual override (ApplyColorOverride) wins.
        if (e.Color.Length > 0 && d.GroupColorOverride is null)
            d.GroupColorOverride = e.Color;
    }

    private void OnDeviceUpdated(Device d)
    {
        ApplySelfInfo(d);
        ApplyPinnedName(d);
        ApplyHostnameOverride(d);
        ApplyColorOverride(d);
        if (!d.IsOnline && !d.Seen && !d.FromDb) return;

        // Known rows are only marked dirty (flushed by the throttled aggregate
        // pass) — dispatching per ping floods the UI thread and lags the table.
        bool tracked;
        lock (_byIpLock) tracked = _byIp.ContainsKey(d.Ip);
        if (tracked) { _dirty.TryAdd(d.Ip, 0); return; }

        _dispatch(() =>
        {
            lock (_byIpLock)
            {
                if (_byIp.ContainsKey(d.Ip)) return;
                var vm = new DeviceViewModel(d, d.Ip == _selfIp, _pinned.Contains(d.Ip));
                _byIp[d.Ip] = vm;
                InsertSorted(vm);
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
            foreach (var dev in engine.Devices.Where(d => d.IsOnline || d.Seen || d.FromDb))
            {
                ApplyPinnedName(dev);
                ApplyHostnameOverride(dev);
                ApplyColorOverride(dev);
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

    /// <summary>Re-raise all row properties (e.g. after a language change).</summary>
    public void RefreshAllRows() => RefreshAll();

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
        if (forceGroups || now - _lastGroupTick >= GroupDebounceMs)
        {
            _lastGroupTick = now;
            DeviceGrouper.AssignGroups(all, _gatewayIp);
            // Groups may have shifted: refresh every row.
            lock (_byIpLock) { foreach (var vm in Devices) vm.Refresh(); }
            _dirty.Clear();
        }
        else
        {
            // Refresh only rows whose device actually changed since last pass.
            foreach (var ip in _dirty.Keys)
            {
                _dirty.TryRemove(ip, out _);
                DeviceViewModel? vm;
                lock (_byIpLock) _byIp.TryGetValue(ip, out vm);
                vm?.Refresh();
            }
        }
        ThreadsText = $"{Loc.Threads}: {engine.ActiveWorkers}";
        Raise(nameof(ThreadsText));

        int discovered = all.Count;
        int online = all.Count(d => d.IsOnline);
        int offline = discovered - online;
        Progress.SetDevices(online, offline, Math.Max(_plannedDevices, 1));
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

        List<DeviceViewModel> rows;
        lock (_byIpLock) { rows = Devices.ToList(); }
        MarkExtremes(rows);
        UpdateTotals(rows);
    }

    // ── Totals row (average of each ping column across all rows, heat-colored) ──
    public string TotalAvg { get; private set; } = "—";
    public string TotalMin { get; private set; } = "—";
    public string TotalMax { get; private set; } = "—";
    public string TotalLast { get; private set; } = "—";
    public string TotalAvgColor { get; private set; } = Core.Palette.MidGray;
    public string TotalMinColor { get; private set; } = Core.Palette.MidGray;
    public string TotalMaxColor { get; private set; } = Core.Palette.MidGray;
    public string TotalLastColor { get; private set; } = Core.Palette.MidGray;

    private void UpdateTotals(List<DeviceViewModel> rows)
    {
        static double? Avg(List<DeviceViewModel> l, Func<DeviceViewModel, double?> sel)
        {
            var vals = l.Select(sel).Where(v => v is not null).Select(v => v!.Value).ToList();
            return vals.Count == 0 ? null : vals.Average();
        }
        double? avg = Avg(rows, v => v.AvgRaw), min = Avg(rows, v => v.MinRaw),
                max = Avg(rows, v => v.MaxRaw), last = Avg(rows, v => v.LastRaw);
        TotalAvg = Core.NumberFormat.Ms(avg);   TotalAvgColor = Core.Palette.Heat(avg);
        TotalMin = Core.NumberFormat.Ms(min);   TotalMinColor = Core.Palette.Heat(min);
        TotalMax = Core.NumberFormat.Ms(max);   TotalMaxColor = Core.Palette.Heat(max);
        TotalLast = Core.NumberFormat.Ms(last); TotalLastColor = Core.Palette.Heat(last);
        Raise(nameof(TotalAvg)); Raise(nameof(TotalMin));
        Raise(nameof(TotalMax)); Raise(nameof(TotalLast));
        Raise(nameof(TotalAvgColor)); Raise(nameof(TotalMinColor));
        Raise(nameof(TotalMaxColor)); Raise(nameof(TotalLastColor));
    }

    // Mark best (lowest) green and worst (highest) red per ping column across rows.
    private void MarkExtremes(List<DeviceViewModel> list)
    {
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
        DeviceViewModel? best = null, worst = null;
        double min = double.MaxValue, max = double.MinValue;
        int count = 0;
        foreach (var vm in list)
        {
            if (sel(vm) is not { } v) continue;
            count++;
            if (v < min) { min = v; best = vm; }
            if (v > max) { max = v; worst = vm; }
        }
        return count < 2 || min == max ? (null, null) : (best, worst);
    }

    // ── Internet totals row (after the last host): Ø/Letzter = averages,
    //    Min/Max = the extreme values across all hosts ──
    public string InternetTotalAvg { get; private set; } = "—";
    public string InternetTotalMin { get; private set; } = "—";
    public string InternetTotalMax { get; private set; } = "—";
    public string InternetTotalLast { get; private set; } = "—";
    public string InternetTotalAvgColor { get; private set; } = Core.Palette.MidGray;
    public string InternetTotalMinColor { get; private set; } = Core.Palette.MidGray;
    public string InternetTotalMaxColor { get; private set; } = Core.Palette.MidGray;
    public string InternetTotalLastColor { get; private set; } = Core.Palette.MidGray;

    private void UpdateInternetTotals()
    {
        static double? Agg(IEnumerable<double?> src, Func<List<double>, double> f)
        {
            var vals = src.Where(v => v is not null).Select(v => v!.Value).ToList();
            return vals.Count > 0 ? f(vals) : null;
        }
        var hosts = InternetHosts;
        double? avg = Agg(hosts.Select(h => h.AvgRaw), v => v.Average());
        double? min = Agg(hosts.Select(h => h.MinRaw), v => v.Min());
        double? max = Agg(hosts.Select(h => h.MaxRaw), v => v.Max());
        double? last = Agg(hosts.Select(h => h.LastRaw), v => v.Average());
        InternetTotalAvg = Core.NumberFormat.Ms(avg);   InternetTotalAvgColor = Core.Palette.Heat(avg);
        InternetTotalMin = Core.NumberFormat.Ms(min);   InternetTotalMinColor = Core.Palette.Heat(min);
        InternetTotalMax = Core.NumberFormat.Ms(max);   InternetTotalMaxColor = Core.Palette.Heat(max);
        InternetTotalLast = Core.NumberFormat.Ms(last); InternetTotalLastColor = Core.Palette.Heat(last);
        Raise(nameof(InternetTotalAvg)); Raise(nameof(InternetTotalMin));
        Raise(nameof(InternetTotalMax)); Raise(nameof(InternetTotalLast));
        Raise(nameof(InternetTotalAvgColor)); Raise(nameof(InternetTotalMinColor));
        Raise(nameof(InternetTotalMaxColor)); Raise(nameof(InternetTotalLastColor));
    }

    /// <summary>Current internet graph sample: average of the hosts' last pings.</summary>
    public double? InternetGraphValue()
    {
        var vals = InternetHosts.Select(h => h.LastRaw).Where(v => v is not null)
                                .Select(v => v!.Value).ToList();
        return vals.Count > 0 ? vals.Average() : null;
    }

    /// <summary>Current graph sample: a device's last ping (by IP) or — with
    /// null — the average of the last pings of all online devices.</summary>
    public double? GraphValue(string? ip)
    {
        lock (_byIpLock)
        {
            if (ip is not null)
                return _byIp.TryGetValue(ip, out var vm) ? vm.LastRaw : null;
            var vals = Devices.Where(v => v.IsOnline && v.LastRaw is not null)
                              .Select(v => v.LastRaw!.Value).ToList();
            return vals.Count > 0 ? vals.Average() : null;
        }
    }

    private static string Slug(string? host)
    {
        if (string.IsNullOrEmpty(host)) return "";
        var safe = new string(host.Where(c => !"\\/:*?\"<>| \t".Contains(c)).ToArray());
        return safe.Length > 40 ? safe[..40] : safe;
    }
}
