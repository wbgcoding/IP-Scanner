using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
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
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<NetService>>> _portScan;
    private readonly Func<NetworkInfo> _detectNetwork;
    private readonly Action<Action> _dispatch;
    private readonly IReadOnlyList<Func<string, CancellationToken, Task<(string? mac, string? host)>>>? _enrichers;
    // Doubled-timeout variant of the same enrich chain, used only by the
    // manual "resolve name again" entry and the background retry pass below -
    // both run against a single device outside any active scan.
    private readonly IReadOnlyList<Func<string, CancellationToken, Task<(string? mac, string? host)>>>? _retryEnrichers;
    private readonly HostnameRetryService _hostnameRetry = new();
    private readonly Func<string, IProgress<int>, CancellationToken, Action<NetService>, Task<IReadOnlyList<NetService>>>? _fullPortScan;
    private CancellationTokenSource? _cts;
    // Forward DNS lookup for a pinned hostname, injectable for tests -
    // production default hits the real resolver via System.Net.Dns.
    private readonly Func<string, Task<System.Net.IPAddress[]>> _resolveHostname;

    /// <summary>Split an "ip name" config entry; without a name the IP doubles as name.</summary>
    private static (string ip, string name) ParseHostEntry(string entry)
    {
        var e = NetEntry.Parse(entry);
        return (e.Target, e.Name.Length > 0 ? e.Name : e.Target);
    }

    public MainViewModel(Func<string, int, PingResult> pingFunc,
                         Func<NetworkInfo> detectNetwork,
                         Action<Action> dispatch,
                         IReadOnlyList<Func<string, CancellationToken, Task<(string? mac, string? host)>>>? enrichers = null,
                         IReadOnlyList<Func<string, CancellationToken, Task<(string? mac, string? host)>>>? retryEnrichers = null,
                         Func<string, Task<System.Net.IPAddress[]>>? resolveHostname = null,
                         Func<string, CancellationToken, Task<IReadOnlyList<NetService>>>? portScan = null,
                         Func<string, IProgress<int>, CancellationToken, Action<NetService>, Task<IReadOnlyList<NetService>>>? fullPortScan = null)
    {
        _fullPortScan = fullPortScan;
        _portScan = portScan ?? ((ip, ct) => PortScanner.ScanAsync(ip, ct));
        _pingFunc = pingFunc;
        _detectNetwork = detectNetwork;
        _dispatch = dispatch;
        _enrichers = enrichers;
        _retryEnrichers = retryEnrichers ?? enrichers;
        _resolveHostname = resolveHostname ?? (host => System.Net.Dns.GetHostAddressesAsync(host));

        // Empty-state hints for the three sidebar lists need a live "is this
        // list empty" signal - cheaper than a converter for three call sites.
        Networks.CollectionChanged += (_, e) => { Raise(nameof(IsNetworksEmpty)); RaiseListsRebuiltUnlessAdd(e); };
        InternetHosts.CollectionChanged += (_, e) => { Raise(nameof(IsInternetHostsEmpty)); Raise(nameof(HasInternetHosts)); RaiseListsRebuiltUnlessAdd(e); };
        PinnedEntries.CollectionChanged += (_, e) => { Raise(nameof(IsPinnedEmpty)); RaiseListsRebuiltUnlessAdd(e); };

        // Background hostname retry - runs until Shutdown, independent of whether a scan is active.
        _retryLoop = HostnameRetryLoopAsync(_lifetime.Token);
    }

    // Cancelled by Shutdown: ends the background loops that live as long as the view model.
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _retryLoop;
    private ScanEngine? _engine;

    /// <summary>Stops everything this view model started on its own: the running scan, the internet
    /// loop and the hostname retry loop. Waits at most two seconds for the loops and the cancelled
    /// scan's tail (reports, archive write) to unwind, then disposes the engine. The caller must not
    /// let the process end before this task completes.</summary>
    public async Task Shutdown()
    {
        try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
        Stop();
        CancelRowPortScans();
        await Task.WhenAny(Task.WhenAll(_retryLoop, _internetLoop, WaitForScanToFinishAsync()),
            Task.Delay(ShutdownWaitMs)).ConfigureAwait(false);
        _engine?.Dispose();
    }

    // The gate is held by a running scan until it is completely done, tail included.
    private async Task WaitForScanToFinishAsync()
    {
        if (await _scanStartGate.WaitAsync(ShutdownWaitMs).ConfigureAwait(false)) _scanStartGate.Release();
    }

    private const int ShutdownWaitMs = 2000;

    // A row removed or replaced by a list rebuild (a fresh scan rebuilds
    // Networks from scratch, RebuildPinnedEntries clears and re-adds every
    // entry) can leave a sidebar delete gesture's ArmThenConfirm state
    // pointing at an orphaned view model - a plain Add never orphans anything
    // so it is excluded to avoid disarming on every routine append.
    private void RaiseListsRebuiltUnlessAdd(NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) ListsRebuilt?.Invoke();
    }

    public ObservableCollection<DeviceViewModel> Devices { get; } = new();
    /// <summary>Raised when the online/offline mix changed, so a device filter can refresh.</summary>
    public event Action? DeviceVisibilityChanged;
    /// <summary>Raised whenever a subnet, pinned-IP or internet-host card was
    /// removed or replaced (not a plain addition) - the sidebar's arm-then-
    /// confirm delete state must drop any reference to the old instance.</summary>
    public event Action? ListsRebuilt;
    /// <summary>Raised once, right when a never-seen-before device row is
    /// first inserted during a live scan, if it is rogue (online, not in the
    /// known-devices DB, not this machine) - the tray-notification trigger.
    /// Deliberately not raised from SyncDevices, which also re-adds rows already
    /// known from a previous run/the DB at engine (re)attach.</summary>
    public event Action<DeviceViewModel>? NewUnknownDeviceFound;
    private int _filterOnline = -1, _filterOffline = -1;
    public ObservableCollection<NetworkInfoViewModel> Networks { get; } = new();

    // Which network adapter to scan and ping from.
    public sealed record InterfaceOption(string Ip, string Label);

    public ObservableCollection<InterfaceOption> AvailableInterfaces { get; } = new();

    /// <summary>Refreshes the adapter picker's choices - call after any change that
    /// could add/remove a NIC (startup, or the sidebar's manual "rescan" affordance).</summary>
    public void RefreshAvailableInterfaces()
    {
        AvailableInterfaces.Clear();
        AvailableInterfaces.Add(new InterfaceOption("", Loc.NetworkInterfaceAuto));
        foreach (var card in Core.Scanner.NetworkDetector.ListIpv4Cards())
            AvailableInterfaces.Add(new InterfaceOption(card.Ip, $"{card.Name} ({card.Ip})"));
    }

    /// <summary>Empty = automatic (OS-routed adapter). Setting this re-detects the
    /// network from the chosen adapter, rebuilds the primary card and restarts
    /// whatever scan is currently running - handled by MainWindow, which owns the
    /// scan-start/stop plumbing (subscribes to <see cref="InterfaceSelectionChanged"/>).
    /// Applies to ICMP too: IcmpSendEcho2Ex binds a source address through the same
    /// unprivileged ICMP helper driver IcmpSendEcho already used (the same API
    /// behind "ping -S" on Windows) - no administrator rights needed, unlike a raw
    /// socket would require.</summary>
    public string SelectedInterfaceIp
    {
        get => Config.NetworkInterfaceIp;
        set
        {
            var v = value ?? "";
            if (Config.NetworkInterfaceIp == v) return;
            Config.NetworkInterfaceIp = v;
            Core.Scanner.NetworkDetector.SelectedSourceIp = v.Length > 0 ? v : null;
            Raise(nameof(SelectedInterfaceIp));
            InterfaceSelectionChanged?.Invoke();
        }
    }

    public event Action? InterfaceSelectionChanged;
    public ObservableCollection<InternetHostViewModel> InternetHosts { get; } = new();
    public ObservableCollection<PinnedIpViewModel> PinnedEntries { get; } = new();
    public bool IsNetworksEmpty => Networks.Count == 0;
    public bool IsInternetHostsEmpty => InternetHosts.Count == 0;
    public bool HasInternetHosts => InternetHosts.Count > 0;
    public bool IsPinnedEmpty => PinnedEntries.Count == 0;
    public ProgressViewModel Progress { get; } = new();
    public ScanConfig Config { get; set; } = new();

    private volatile bool _internetPingingHeld;

    /// <summary>True while the first-run card is unanswered: no internet host is pinged by
    /// any path (scan, sidebar edit, profile load) until the user has decided.</summary>
    public bool InternetPingingHeld
    {
        get => _internetPingingHeld;
        set => _internetPingingHeld = value;
    }

    private bool _isScanning;
    private bool _hasScannedOnce;
    private DateTime? _lastScanAt;
    /// <summary>True while a scan (incl. startup sweep) is running.</summary>
    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (_isScanning == value) return;
            _isScanning = value;
            if (value) _hasScannedOnce = true;
            else
            {
                // A permanent "last scan" timestamp in the table footer -
                // set on every finish (including a cancelled run, which still ran).
                _lastScanAt = DateTime.Now;
                Raise(nameof(LastScanText));
                Raise(nameof(HasLastScan));
            }
            Raise(nameof(IsScanning));
            Raise(nameof(ValuesAreStale));
        }
    }

    /// <summary>Wall-clock time the last scan finished, shown permanently
    /// next to the totals row instead of a message that fades after a few seconds.</summary>
    public string LastScanText => _lastScanAt is { } t
        ? string.Format(Loc.LastScanFmt, t.ToString("t", CultureInfo.CurrentCulture))
        : "";
    public bool HasLastScan => _lastScanAt.HasValue;
    /// <summary>True once a scan has finished and no new one is running yet -
    /// drives a dimmed opacity on the device table's numeric cells so a value
    /// left over from the last run does not read as fresh.</summary>
    public bool ValuesAreStale => _hasScannedOnce && !_isScanning;

    // Guards the Networks snapshot taken by the graph worker thread.
    private readonly object _graphListLock = new();

    public IReadOnlyList<NetworkInfoViewModel> NetworksSnapshot()
    { lock (_graphListLock) return Networks.ToArray(); }

    /// <summary>When enabled, open the latency graph of every pinned device row.</summary>
    public void ApplyAutoGraph(bool on)
    {
        if (!on) return;
        DeviceViewModel[] rows;
        lock (_byIpLock) rows = _byIp.Values.ToArray();
        foreach (var vm in rows) if (vm.IsPinned) vm.ShowGraph = true;
    }

    /// <summary>Feed one per-second latency sample to every device row's graph so
    /// an expanded graph shows the full configured time window.</summary>
    public void SampleRowGraphs(int capacity, int tickIntervalSeconds)
    {
        DeviceViewModel[] rows;
        lock (_byIpLock) rows = _byIp.Values.ToArray();
        foreach (var vm in rows) vm.AddRowSample(vm.LastRaw, capacity, tickIntervalSeconds);
    }

    private bool _networkGraphsOn = true;
    /// <summary>Visibility switch for the per-network graphs (set from settings).</summary>
    public bool NetworkGraphsOn
    {
        get => _networkGraphsOn;
        set { if (_networkGraphsOn != value) { _networkGraphsOn = value; Raise(nameof(NetworkGraphsOn)); } }
    }

    private bool _isZoomed;
    /// <summary>The internet mini-graph's 1m/30s zoom toggle. Only drives the
    /// two label styles (shared with NetworkInfoViewModel.IsZoomed via
    /// ZoomLabelActiveWhenZoomed/NotZoomed) so they follow a live theme switch instead
    /// of a one-off FindResource snapshot taken at click time. The graph itself is
    /// re-rendered from MainWindow's code-behind.</summary>
    public bool IsZoomed
    {
        get => _isZoomed;
        set { if (_isZoomed != value) { _isZoomed = value; Raise(nameof(IsZoomed)); } }
    }

    public string? LastExportPath { get; private set; }
    /// <summary>"name (size)" per written report file, one line each.</summary>
    public string ExportInfo { get; private set; } = "";
    public bool HasExport => ExportInfo.Length > 0;
    /// <summary>Number of report files written by the last export, shown in the
    /// collapsed sidebar section's header.</summary>
    public int ExportFileCount { get; private set; }
    /// <summary>"Files (N)" header text for the collapsed export section.</summary>
    public string ExportsHeaderText => string.Format(Core.Localization.Loc.ExportsHeaderFmt, ExportFileCount);

    public void RecordExport(string path)
    {
        LastExportPath = path;
        var file = new System.IO.FileInfo(path);
        ExportInfo = $"{file.Name} ({Core.NumberFormat.Bytes(file.Length)})";
        ExportFileCount = 1;
        Raise(nameof(LastExportPath));
        Raise(nameof(ExportInfo));
        Raise(nameof(HasExport));
        Raise(nameof(ExportFileCount));
        Raise(nameof(ExportsHeaderText));
    }
    /// <summary>One formatted line per IP/MAC conflict found in the last
    /// scan (in-memory only - persisted once a scan archive exists).</summary>
    public ObservableCollection<string> ConflictFindingTexts { get; } = new();
    public bool HasConflictFindings => ConflictFindingTexts.Count > 0;
    public string FindingsHeaderText => string.Format(Loc.FindingsSectionHeaderFmt, ConflictFindingTexts.Count);
    /// <summary>"Threads: 12" - live count of in-flight pings for the footer.</summary>
    public string ThreadsText => _activeWorkers is { } n ? $"{Loc.Threads}: {n}" : "";
    private int? _activeWorkers;

    internal void SetActiveWorkers(int workers)
    {
        _activeWorkers = workers;
        Raise(nameof(ThreadsText));
    }
    /// <summary>User-entered subnet (e.g. "192.168.1.0/24"); overrides auto-detect.</summary>
    public string? ManualSubnet { get; set; }

    private string? _detectedCidr;
    /// <summary>The network auto-detect currently resolves to; the header subnet box
    /// shows it as its placeholder while the box is empty.</summary>
    public string? DetectedCidr
    {
        get => _detectedCidr;
        private set => SetProperty(ref _detectedCidr, value);
    }

    public void SetDetectedNetwork(NetworkInfo info) => _dispatch(() => DetectedCidr = info.Cidr);

    /// <summary>Looks the network up again (after the machine moved to another one) so the
    /// subnet box's placeholder stops showing the old network. Best-effort.</summary>
    public async Task RefreshDetectedNetworkAsync()
    {
        try { SetDetectedNetwork(await Task.Run(_detectNetwork)); }
        catch (Exception ex) { Core.Log.Warn(Core.LogArea.Scan, "Detecting the network failed", ex); /* detection best-effort */ }
    }

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
    // Hostname -> resolved IP for pinned entries, valid for the
    // current run only - rebuilt from scratch at every scan start, never
    // persisted and never carried forward if a name stops resolving.
    private Dictionary<string, string> _pinnedHostnameResolutions = new();

    /// <summary>Load manual hostname/group overrides (file lives next to the db).</summary>
    public void InitOverrides(string path) => _overrides.Load(path);

    /// <summary>Outcome of the last overrides load, for the startup notice.</summary>
    public IpScanner.Core.Data.LoadProblem OverridesLoadProblem => _overrides.LoadProblem;

    /// <summary>Every pinned entry with a usable IP target - a plain IP passes
    /// through, a hostname is swapped for its last-resolved address
    /// and dropped entirely if it has never resolved.</summary>
    private List<NetEntry> ResolvedPinnedEntries() => Config.PinnedIps.Select(NetEntry.Parse)
        .Where(e => e.Target.Length > 0)
        .Select(e => AddressSyntax.IsValidAddress(e.Target) ? e
            : _pinnedHostnameResolutions.TryGetValue(e.Target, out var ip) ? e with { Target = ip } : null)
        .Where(e => e is not null).Select(e => e!).ToList();

    /// <summary>Resolve every pinned hostname target fresh for this scan -
    /// a plain IP entry needs no lookup. Best-effort with a short timeout each;
    /// a name that fails to resolve is simply absent from the result and the
    /// scan proceeds without it (reported to the sidebar separately).</summary>
    private async Task ResolvePinnedHostnamesAsync()
    {
        var resolutions = new Dictionary<string, string>();
        var hostnames = Config.PinnedIps.Select(NetEntry.Parse).Select(e => e.Target)
            .Where(t => t.Length > 0 && !AddressSyntax.IsValidAddress(t)).Distinct().ToList();
        foreach (var host in hostnames)
        {
            try
            {
                var addresses = await _resolveHostname(host).WaitAsync(TimeSpan.FromSeconds(3));
                var v4 = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                var found = v4 ?? addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6);
                if (found is not null) resolutions[host] = found.ToString();
            }
            catch (Exception ex) { Core.Log.Debug(Core.LogArea.Dns, "Pinned host not resolved: " + ex.GetType().Name); /* unresolved this run - ApplyPinnedResolutionStates reports it */ }
        }
        _pinnedHostnameResolutions = resolutions;
    }

    /// <summary>Flags each sidebar pinned entry that is a hostname
    /// which failed to resolve during the resolution pass just run.</summary>
    private void ApplyPinnedResolutionStates()
    {
        foreach (var p in PinnedEntries)
            p.IsUnresolved = !AddressSyntax.IsValidAddress(p.Ip) && !_pinnedHostnameResolutions.ContainsKey(p.Ip);
    }

    /// <summary>Rebuild pinned-IP name/color metadata from the current config.</summary>
    private void RebuildPinnedMeta()
    {
        _pinnedMeta = ResolvedPinnedEntries().GroupBy(e => e.Target).ToDictionary(g => g.Key, g => g.First());
        _pinned = new HashSet<string>(_pinnedMeta.Keys);
    }

    /// <summary>Rebuild the sidebar's "Pinned IPs" management list from config.
    /// Independent of whether each IP currently appears in the device table.
    /// Reconciles in place instead of Clear()+Add(), so editing one pinned IP does not reset
    /// the sidebar's scroll position. An unchanged entry keeps its instance and index; a
    /// renamed or recolored one is removed and re-added at the bottom.</summary>
    public void RebuildPinnedEntries()
    {
        var wanted = Config.PinnedIps.Select(NetEntry.Parse).Where(e => e.Target.Length > 0).ToList();
        var wantedIps = new HashSet<string>(wanted.Select(e => e.Target));
        for (int i = PinnedEntries.Count - 1; i >= 0; i--)
            if (!wantedIps.Contains(PinnedEntries[i].Ip)) PinnedEntries.RemoveAt(i);

        // GroupBy+First instead of ToDictionary: config allows duplicate pinned
        // IPs (nothing here dedupes them), and a straight ToDictionary would
        // throw on a duplicate key instead of just tolerating it like the old
        // Clear()+Add() always did.
        var byIp = PinnedEntries.GroupBy(p => p.Ip).ToDictionary(g => g.Key, g => g.First());
        foreach (var e in wanted)
        {
            if (byIp.TryGetValue(e.Target, out var existing))
            {
                if (existing.Name == e.Name && existing.Color == e.Color) continue;   // unchanged
                PinnedEntries.Remove(existing);
            }
            PinnedEntries.Add(new PinnedIpViewModel(e.Target, e.Name, e.Color));
        }
    }

    /// <summary>Re-apply subnet display name/color to the currently visible network
    /// cards after a sidebar edit - no rescan needed for a rename/recolor.</summary>
    public void RefreshSubnetMeta()
    {
        _subnetMeta = new Dictionary<string, NetEntry>();
        foreach (var s in Config.Subnets)
        {
            var entry = NetEntry.Parse(s);
            foreach (var p in ManualPrefixes(entry.Target) ?? new List<string>())
                _subnetMeta[p] = entry;
        }
        lock (_graphListLock)
        {
            var present = new HashSet<string>();
            var orphaned = new List<NetworkInfoViewModel>();
            foreach (var net in Networks)
            {
                var prefix = net.Prefix;
                present.Add(prefix);
                if (_subnetMeta.TryGetValue(prefix, out var meta))
                    net.ApplyMeta(meta.Name, meta.Color, meta.Target);
                else if (net.IsPending)
                    orphaned.Add(net);   // placeholder of a subnet that was deleted again: drop the card
                else
                    net.ApplyMeta("", "", null);
            }
            foreach (var net in orphaned) Networks.Remove(net);
            // A subnet just added via the sidebar has no scan result yet - show a
            // placeholder card instead of leaving it invisible until the next
            // scan. RunScanCore's diff-rebuild picks this
            // same instance up by prefix and upgrades it in place once scanned.
            foreach (var (prefix, meta) in _subnetMeta)
            {
                if (present.Contains(prefix)) continue;
                int index = NetworkInfoViewModel.NextFreeIndex(Networks.Select(n => n.Index));
                Networks.Add(new NetworkInfoViewModel(index,
                    InfoForTarget(meta.Target, prefix) ?? new NetworkInfo { Ip = prefix + ".0" },
                    GroupColorPalette.ColorForIndex(index - 1),
                    primary: false, name: meta.Name, sourceTarget: meta.Target, rawColor: meta.Color,
                    pending: true, prefix: prefix));
            }
        }
    }

    /// <summary>Reconciles Networks against a scan's subnet list by prefix key
    /// instead of clearing and rebuilding every card - same pattern
    /// as the InternetHosts diff in PingInternetAsync. Keeps existing cards'
    /// graph history, scroll position and armed-delete state alive across a
    /// rescan; also upgrades a sidebar-added pending placeholder in place once
    /// its subnet is actually scanned. Internal (not private) for direct unit
    /// testing without a live scan pipeline.</summary>
    internal void ReconcileNetworks(IReadOnlyList<string> prefixes, NetworkInfo primaryInfo)
    {
        lock (_graphListLock)
        {
            var wanted = new HashSet<string>(prefixes);
            for (int i = Networks.Count - 1; i >= 0; i--)
                if (!wanted.Contains(Networks[i].Prefix)) Networks.RemoveAt(i);

            var byPrefix = Networks.ToDictionary(n => n.Prefix);
            for (int i = 0; i < prefixes.Count; i++)
            {
                var prefix = prefixes[i];
                // The card describes the range that is really scanned: the typed subnet, the
                // detected network, or the sidebar entry the prefix came from - never a /24 guess.
                NetworkInfo ni;
                if (i == 0 && ManualTarget(ManualSubnet) is not null && InfoForTarget(ManualSubnet, prefix) is { } typed)
                    ni = typed;
                else if (i == 0 && (primaryInfo.Ip is null || Ipv4.SubnetPrefix(primaryInfo.Ip) == prefix))
                    ni = primaryInfo;
                else
                    ni = (_subnetMeta.TryGetValue(prefix, out var entry) ? InfoForTarget(entry.Target, prefix) : null)
                         ?? new NetworkInfo { Ip = prefix + ".0" };
                if (byPrefix.TryGetValue(prefix, out var existing))
                    existing.ApplyInfo(prefix, ni, primary: i == 0);
                else
                {
                    _subnetMeta.TryGetValue(prefix, out var meta);
                    int index = NetworkInfoViewModel.NextFreeIndex(Networks.Select(n => n.Index));
                    Networks.Add(new NetworkInfoViewModel(index, ni, GroupColorPalette.ColorForIndex(index - 1),
                                                          primary: i == 0, name: meta?.Name ?? "",
                                                          sourceTarget: meta?.Target, rawColor: meta?.Color ?? "",
                                                          prefix: prefix));
                }
            }
        }
    }

    /// <summary>Rebuild pinned metadata and re-apply to all visible devices.
    /// Handles pin/unpin state, hostnames, and colors. Call after PinnedIps change.</summary>
    public void RefreshPinnedNames()
    {
        RebuildPinnedMeta();
        RebuildPinnedEntries();

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
                // lock(d) serializes these field touches with the engine's own
                // background enrichment writers (ScanEngine.ApplyEnrichResult
                // locks the same device instance) - without it, a hostname
                // resolving concurrently with this refresh could pair a new
                // Hostname with a stale HostnameRank or vice versa.
                lock (d)
                {
                    if (d.HostnameRank == -1 && _overrides.Get(StableMac(d), d.Ip)?.Hostname is null)
                    {
                        d.Hostname = d.AutoHostname;
                        d.HostnameRank = d.AutoHostnameRank;
                    }
                    if (_overrides.Get(StableMac(d), d.Ip)?.Color is null)
                        d.GroupColorOverride = null;
                    ApplyPinnedName(d);
                    ApplyColorOverride(d);
                    ApplyMuteOverride(d);
                }
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

    private void ApplyNoteOverride(Device d)
    {
        var mac = StableMac(d);
        if (mac is not null) _overrides.PromoteToMac(mac, d.Ip);
        d.Note = _overrides.Get(mac, d.Ip)?.Note;
    }

    public bool SetNote(DeviceViewModel vm, string note)
    {
        if (note.Length > OverrideStore.MaxNoteLength) return false;
        var d = vm.Model;
        _overrides.SetNote(StableMac(d), d.Ip, note);
        var saved = _overrides.Get(StableMac(d), d.Ip)?.Note;
        vm.CommitNote(saved);
        return true;
    }

    /// <summary>Mirrors the overrides-store "muted" flag onto the device
    /// so DeviceViewModel and ScanEngine can read it directly without reaching
    /// back into the overrides store themselves.</summary>
    private void ApplyMuteOverride(Device d) =>
        d.IsMuted = _overrides.Get(StableMac(d), d.Ip)?.Muted == true;

    /// <summary>"Mute device" context-menu toggle.</summary>
    public void SetMuted(DeviceViewModel vm, bool value)
    {
        var d = vm.Model;
        _overrides.SetMuted(StableMac(d), d.Ip, value);
        d.IsMuted = value;
        vm.Refresh();
    }

    public bool IsMuted(DeviceViewModel vm) => vm.Model.IsMuted;

    /// <summary>The ports shown for a row, unless they are a stored result older than the scan
    /// the report is about - a closed port must not be printed as open.</summary>
    private IReadOnlyList<NetService>? KnownServices(string ip, DateTimeOffset scanStartedAt)
    {
        DeviceViewModel? vm;
        lock (_byIpLock) _byIp.TryGetValue(ip, out vm);
        if (vm is null || !IsFreshForReport(vm.PortScanSeenAt, scanStartedAt)) return null;
        return vm.Services.ToList();
    }

    internal static bool IsFreshForReport(DateTimeOffset? storedAt, DateTimeOffset scanStartedAt)
        => storedAt is null || storedAt >= scanStartedAt;

    /// <summary>Open ports for a report: services already found for a device are reused as they
    /// are; with <paramref name="probeMissing"/> the online devices without a result are probed
    /// (eight at a time). Cancelling stops the probing and returns what is known so far.</summary>
    public async Task<Dictionary<string, IReadOnlyList<NetService>>> CollectPortsForExport(
        IEnumerable<Device> devices, Func<string, IReadOnlyList<NetService>?> known,
        bool probeMissing, CancellationToken ct)
    {
        var result = new Dictionary<string, IReadOnlyList<NetService>>();
        var missing = new List<string>();
        foreach (var d in devices)
        {
            var existing = known(d.Ip);
            if (existing is { Count: > 0 }) result[d.Ip] = existing;
            else if (probeMissing && d.IsOnline) missing.Add(d.Ip);
        }
        if (missing.Count == 0 || ct.IsCancellationRequested) return result;

        using var gate = new SemaphoreSlim(8);
        await Task.WhenAll(missing.Select(async ip =>
        {
            try
            {
                await gate.WaitAsync(ct);
                try
                {
                    var found = await ScanForExport(ip, ct);
                    lock (result) result[ip] = found;
                }
                finally { gate.Release(); }
            }
            catch (OperationCanceledException) { /* stopped */ }
            catch (Exception ex) { Core.Log.Warn(Core.LogArea.Scan, "Port collection failed", ex); /* best-effort */ }
        }));
        return result;
    }

    /// <summary>The ports the settings ask for. The standard list, and a full 1 to 65535 sweep (command line
    /// only), use the regular collector; any other list or the fast list is probed as given.</summary>
    private async Task<IReadOnlyList<NetService>> ScanForExport(string ip, CancellationToken ct)
    {
        PortSpec? spec = null;
        try
        {
            var plan = Core.Cli.ScanPlan.FromConfig(Config);
            if (plan.Ports.Length > 0)
            {
                var resolved = plan.ResolvePorts();
                if (resolved.Tcp.Count < PortSpec.MaxPort && resolved.Udp.Count < PortSpec.MaxPort && !resolved.IsEmpty)
                    spec = resolved;
            }
        }
        catch (Exception ex) { Core.Log.Warn(Core.LogArea.Scan, "Reading the port list failed", ex); }
        if (spec is null) return await _portScan(ip, ct);

        var results = await PortScanEngine.RunAsync(ip, spec, ScanOptions.Default, null, ct);
        ct.ThrowIfCancellationRequested();
        return results.Where(r => r.IsOpen && r.Service is not null).Select(r => r.Service! with { Ip = ip })
            .OrderBy(x => x.Port).ToList();
    }

    /// <summary>"Mark as known" - writes a rogue device into the
    /// known-devices DB immediately under the current network's gateway MAC, the
    /// same key a normal scan-end save would use, so it's indistinguishable from
    /// a device that was simply seen before. Hidden in the menu (see
    /// MainWindow.DeviceRows.cs) for anything that isn't currently flagged rogue.</summary>
    public async Task<Exception?> MarkAsKnownAsync(DeviceViewModel vm)
    {
        var d = vm.Model;
        if (d.Mac is not { } mac || mac == Device.Unknown) return null;
        var gatewayMac = _gatewayIp is { } gw
            ? Devices.FirstOrDefault(x => x.Ip == gw)?.Model.Mac
            : null;
        if (gatewayMac is null || gatewayMac == Device.Unknown) return null;
        var dbPath = Config.DatabasePath;
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        try
        {
            await Task.Run(() => new KnownDevicesDb(dbPath).Save(gatewayMac, new[] { d }, stamp));
        }
        catch (Exception ex) { return ex; }
        d.FromDb = true;
        vm.Refresh();
        return null;
    }

    /// <summary>Reruns the MAC/hostname enrich chain for one device
    /// outside an active scan - the manual "resolve name again" entry and the
    /// background retry pass below both share this, applying results with the
    /// same best-rank-wins rule a live scan itself uses.</summary>
    private static async Task ApplyEnrichChain(Device d, IReadOnlyList<Func<string, CancellationToken, Task<(string? mac, string? host)>>> enrichers, CancellationToken ct)
    {
        for (int rank = 0; rank < enrichers.Count; rank++)
        {
            try
            {
                var (mac, host) = await Task.Run(() => enrichers[rank](d.Ip, ct), ct).ConfigureAwait(false);
                ScanEngine.ApplyEnrichRank(d, rank, mac, host);
            }
            catch (Exception ex) { Core.Log.Debug(Core.LogArea.Scan, "Enrichment step failed: " + ex.GetType().Name); /* each technique is best-effort, same as the live scan path */ }
        }
    }

    /// <summary>"Resolve name again" context-menu entry - runs the enrich
    /// chain once for this one device with a doubled per-step budget and updates
    /// the row immediately.</summary>
    public async Task ResolveHostnameAgainAsync(DeviceViewModel vm)
    {
        if (_retryEnrichers is null) return;
        var d = vm.Model;
        await ApplyEnrichChain(d, _retryEnrichers, _lifetime.Token).ConfigureAwait(true);
        vm.Refresh();
    }

    /// <summary>"Stop looking up this name" toggle - persists per device
    /// so the background retry pass (and future manual retries) leave it alone.</summary>
    public void SetNoHostnameRetry(DeviceViewModel vm, bool value)
    {
        var d = vm.Model;
        _overrides.SetNoHostnameRetry(StableMac(d), d.Ip, value);
    }

    /// <summary>Current state of the "Stop looking up this name" flag,
    /// for the context menu's checkable item to show when it opens.</summary>
    public bool IsNoHostnameRetry(DeviceViewModel vm) =>
        _overrides.Get(StableMac(vm.Model), vm.Model.Ip)?.NoHostnameRetry == true;

    /// <summary>Background retry for devices that never got a hostname.
    /// Ticks every minute so a live config change to HostnameRetryMinutes takes
    /// effect on the next tick instead of waiting out a stale, already-started
    /// delay; a full pass over the current candidate list only happens once every
    /// HostnameRetryMinutes, one device at a time (HostnameRetryService gates
    /// concurrency), so a long backlog is worked through gradually instead of
    /// flooding DNS/mDNS/NetBIOS all at once.</summary>
    private async Task HostnameRetryLoopAsync(CancellationToken ct)
    {
        var lastPass = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            int minutes = Config.HostnameRetryMinutes;
            if (minutes <= 0 || _retryEnrichers is null) continue;
            if (DateTime.UtcNow - lastPass < TimeSpan.FromMinutes(minutes)) continue;
            lastPass = DateTime.UtcNow;

            List<DeviceViewModel> candidates;
            lock (_byIpLock)
            {
                candidates = Devices.Where(vm => string.IsNullOrEmpty(vm.Model.Hostname)
                    && _overrides.Get(StableMac(vm.Model), vm.Model.Ip)?.NoHostnameRetry != true).ToList();
            }
            foreach (var vm in candidates)
            {
                if (ct.IsCancellationRequested) return;
                await _hostnameRetry.ResolveOneAsync(
                    () => ApplyEnrichChain(vm.Model, _retryEnrichers, ct)).ConfigureAwait(false);
                _dispatch(vm.Refresh);
            }
        }
    }

    /// <summary>Asked with the address count before a scan above <see cref="TargetSpec.HardCap"/>
    /// addresses starts; false (the default) leaves the scan unstarted.</summary>
    public Func<long, bool> ConfirmLargeScan { get; set; } = _ => false;

    /// <summary>Full scan using the configured ping count; writes report/DB.</summary>
    public Task RunScanAsync(IReadOnlyList<string>? subnetOverride = null,
                             IReadOnlyList<(uint First, uint Last)>? rangeOverride = null)
        => RunScanInternal(null, persist: true, subnetOverride, rangeOverride);

    /// <summary>Automatic sweep right after startup: StartupPingCount pings per
    /// device, using the configured worker count like every other scan.</summary>
    public Task RunInitScanAsync()
        => RunScanInternal(Math.Max(0, Config.StartupPingCount), persist: false, null, null);

    // Serializes scan starts: a second start requested while one is already
    // spinning up (e.g. a double Enter-press racing the async startup sweep)
    // just no-ops instead of touching the first scan's cancellation source
    // while it may still be in use.
    private readonly SemaphoreSlim _scanStartGate = new(1, 1);

    private async Task RunScanInternal(int? pingCountOverride, bool persist,
                                       IReadOnlyList<string>? subnetOverride,
                                       IReadOnlyList<(uint First, uint Last)>? rangeOverride)
    {
        // Wait for every clear that is queued, including one that was requested while an earlier
        // one was still running, so the gate is not handed to a later clear ahead of this start.
        while (_clearInFlight is { IsCompleted: false } clearing)
        {
            try { await clearing; }
            catch (Exception ex) { Core.Log.Warn(Core.LogArea.Db, "Clearing the results failed", ex); /* a failed clear must not block the scan that was asked for */ }
        }
        if (!await _scanStartGate.WaitAsync(0)) return;
        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            IsScanning = true;
            try
            {
                await RunScanCore(pingCountOverride, persist, subnetOverride, rangeOverride);
            }
            finally { IsScanning = false; }
        }
        finally { _scanStartGate.Release(); }
    }

    private async Task RunScanCore(int? pingCountOverride, bool persist,
                                   IReadOnlyList<string>? subnetOverride,
                                   IReadOnlyList<(uint First, uint Last)>? rangeOverride)
    {
        var info = _detectNetwork();
        SetDetectedNetwork(info);
        _selfIp = info.Ip;
        _selfMac = info.Mac;
        _selfHost = Environment.MachineName;
        _gatewayIp = info.Gateway;
        // Fresh DNS lookup for every pinned hostname, every scan start -
        // RebuildPinnedMeta and the cfg.PinnedIps below both read the result.
        await ResolvePinnedHostnamesAsync();
        RebuildPinnedMeta();
        ApplyPinnedResolutionStates();
        _subnetMeta = new Dictionary<string, NetEntry>();
        foreach (var s in Config.Subnets)
        {
            var entry = NetEntry.Parse(s);
            foreach (var p in ManualPrefixes(entry.Target) ?? new List<string>())
                _subnetMeta[p] = entry;
        }
        // Always clone (never reuse the live Config instance directly) so the
        // PinnedIps override below - hostnames swapped for this run's resolved
        // addresses - never mutates the user's actual saved pinned-entry list.
        var cfg = Config.CloneWith(pingCountOverride ?? Config.PingCount);
        cfg.PinnedIps = ResolvedPinnedEntries().Select(e => e.ToString()).ToList();
        IReadOnlyList<string> prefixes;
        IReadOnlyList<(uint First, uint Last)>? ranges = null;
        if (subnetOverride is not null) { prefixes = subnetOverride; ranges = rangeOverride; }
        else (prefixes, ranges) = ResolveTargets(info, ManualSubnet, cfg.Subnets);

        List<string> scannedList;
        try
        {
            scannedList = TargetSpec.FromPrefixes(prefixes, ranges).Expand(ConfirmLargeScan).ToList();
            if (subnetOverride is null)
            {
                var have = new HashSet<string>(scannedList);
                scannedList.AddRange(Ipv6SubnetAddresses(cfg.Subnets).Where(have.Add));
            }
        }
        catch (TargetCapExceededException) { return; }   // declined: nothing has been started or cleared yet
        LastExportPath = null;
        ExportInfo = "";
        ExportFileCount = 0;
        Raise(nameof(ExportInfo));
        Raise(nameof(HasExport));
        Raise(nameof(ExportFileCount));
        Raise(nameof(ExportsHeaderText));
        ConflictFindingTexts.Clear();
        Raise(nameof(HasConflictFindings));
        Raise(nameof(FindingsHeaderText));
        bool infinite = cfg.PingCount == ScanConfig.InfinitePingCount;
        Progress.InfinitePings = infinite;
        int perIp = infinite ? 1 : Math.Max(1, cfg.PingCount);
        // Pinned IPs outside every scanned subnet are still pinged by ScanEngine
        // (README: "always pinged, even outside the subnets") but were missing
        // from this planning total - the live counter could then run past its
        // own denominator. Count them exactly the way ScanEngine.ScanAsync
        // itself dedupes ("seen.Add(target)" against the actual host set from
        // Ipv4.HostsInSubnet) rather than by subnet-prefix membership: a pinned
        // .0/.255 address is inside a scanned /24 by prefix, but HostsInSubnet
        // only yields .1-.254, so ScanEngine still pings it as "extra" too.
        var scannedHosts = new HashSet<string>(scannedList);
        var pinnedSeen = new HashSet<string>();
        int pinnedOutside = 0;
        foreach (var pin in cfg.PinnedIps)
        {
            var target = NetEntry.Parse(pin).Target;
            if (AddressSyntax.IsValidAddress(target) && pinnedSeen.Add(target) && !scannedHosts.Contains(target))
                pinnedOutside++;
        }
        _plannedDevices = Math.Max(1, scannedList.Count + pinnedOutside);
        _totalPings = Math.Max(1L, (long)_plannedDevices * perIp);
        Progress.StartScan();   // Remaining-time estimate measures from here
        var scanStartedAt = DateTimeOffset.UtcNow;
        Core.Log.Info(Core.LogArea.Scan, $"Scan started: {scannedList.Count} targets, {(infinite ? "endless" : perIp.ToString())} pings each");

        _dispatch(() =>
        {
            ClearRows();
            ReconcileNetworks(prefixes, info);
        });

        // Internet latency runs alongside the scan with the same ping count as
        // the table; a new scan (or Stop) cancels the previous loop.
        RestartInternetPinging(cfg.PingCount == 0 ? 1 : cfg.PingCount);

        var engine = new ScanEngine(_pingFunc, _enrichers);
        var previousEngine = _engine;
        _engine = engine;
        if (previousEngine is not null) _ = Task.Run(previousEngine.Dispose);
        engine.DeviceUpdated += OnDeviceUpdated;
        engine.DeviceRemoved += OnDeviceRemoved;
        // Per-device row refreshes stay immediate (OnDeviceUpdated); the heavy
        // aggregate pass (bars, groups, extremes) is throttled so a fast ping
        // stream can't flood the UI thread and stall the table.
        long lastAggregate = 0;
        void OnProgressChanged()
        {
            long now = Environment.TickCount64;
            long last = Interlocked.Read(ref lastAggregate);
            if (now - last < AggregateThrottleMs ||
                Interlocked.CompareExchange(ref lastAggregate, now, last) != last) return;
            _dispatch(() => UpdateProgress(engine));
        }
        engine.Progress.Changed += OnProgressChanged;

        // This engine's events must never reach a later scan. A new
        // RunScanCore call creates a brand-new ScanEngine, but this one's own
        // fire-and-forget follow-ups (offline burst probes, straggling
        // analysis/enrichment work) can still fire after ScanAsync returns -
        // unhooking here in a finally guarantees a stopped-then-restarted scan
        // never shows a stale row from the run that was just stopped.
        try
        {
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
            catch (Exception ex) { Core.Log.Warn(Core.LogArea.Db, "Reading the known devices failed", ex); /* DB optional */ }
        }

        var scanToken = _cts!.Token;
        try
        {
            await engine.ScanAsync(scannedList, cfg, scanToken);
        }
        catch (OperationCanceledException) { /* stopped by user */ }
        bool scanCancelled = scanToken.IsCancellationRequested;

        var devices = engine.Devices.ToList();
        var scanFinishedAt = DateTimeOffset.UtcNow;
        Core.Log.Info(Core.LogArea.Scan, $"Scan ended: {devices.Count} devices, {devices.Count(d => d.IsOnline)} online, {(scanFinishedAt - scanStartedAt).TotalSeconds:0.#} s, cancelled: {(scanCancelled ? "yes" : "no")}");

        // Previous MAC-per-IP snapshot, read before the known-devices
        // DB below overwrites it with this run's result - an empty dictionary
        // is fine when the DB is disabled or this network was never scanned
        // before, it just means the MacChanged case cannot fire yet. Runs
        // unconditionally (not gated on `persist`) so the multi-MAC cases,
        // which need no history at all, are still reported for a scan that
        // does not write reports or the DB.
        var previousMacByIp = new Dictionary<string, string>();
        if (cfg.KnownDevicesDb && info.Gateway is { } gwIpForConflicts)
        {
            try
            {
                await Task.Run(() =>
                {
                    var histDb = new KnownDevicesDb(cfg.DatabasePath);
                    if (histDb.GetNetworkMac(gwIpForConflicts) is { } netMac)
                        foreach (var d in histDb.Load(netMac))
                            if (d.Mac is { } m && m != Device.Unknown)
                                previousMacByIp[d.Ip] = m;
                });
            }
            catch (Exception ex) { Core.Log.Warn(Core.LogArea.Db, "Reading the known devices failed", ex); /* DB optional */ }
        }
        var conflictFindings = ConflictDetector.Detect(devices, previousMacByIp);
        _dispatch(() =>
        {
            var hintByIp = new Dictionary<string, string>();
            ConflictFindingTexts.Clear();
            foreach (var f in conflictFindings)
            {
                var text = f.Kind switch
                {
                    ConflictKind.MacChanged => Loc.ConflictMacChangedMsg(f.Ip, f.Detail ?? "", f.Mac),
                    ConflictKind.MacOnMultipleIps => Loc.ConflictMacOnMultipleIpsMsg(f.Mac, f.Detail ?? ""),
                    _ => Loc.ConflictMultipleMacsMsg(f.Ip),
                };
                ConflictFindingTexts.Add(text);
                if (!hintByIp.ContainsKey(f.Ip)) hintByIp[f.Ip] = text;
            }
            foreach (var (ip, vm) in _byIp)
            {
                var hint = hintByIp.TryGetValue(ip, out var h) ? h : "";
                if (vm.ConflictHint != hint) { vm.ConflictHint = hint; vm.Refresh(); }
            }
            Raise(nameof(HasConflictFindings));
            Raise(nameof(FindingsHeaderText));
        });

        if (persist)
        {
            // Export reads the model before the final UI reconciliation runs.
            // Late MAC identities must have their saved notes on the models now.
            lock (_byIpLock)
                foreach (var device in devices)
                    lock (device) ApplyNoteOverride(device);

            var now = DateTime.Now;
            var timestamp = now.ToString("yyyyMMdd_HHmmss");          // filename-safe
            // Follow the configured date/number format instead of a
            // fixed German-style pattern, like LastScanText already does.
            var displayTime = now.ToString("G", System.Globalization.CultureInfo.CurrentCulture);   // report header
            // Prefer the gateway's hostname for the filename; fall back to its IP.
            var gwDev = devices.FirstOrDefault(d => d.Ip == info.Gateway);
            var gwName = gwDev?.Hostname is { } h && h != Device.Unknown ? h : info.Gateway;
            var gatewaySlug = Slug(gwName);

            // Optionally probe every online device's open ports up front,
            // off the UI thread, so TXT/CSV/HTML can all reuse the one result
            // instead of scanning the same devices three times. A per-run cap of
            // 8 devices at once keeps this from looking like a subnet-wide port
            // scan even on a large network.
            Dictionary<string, IReadOnlyList<NetService>>? ports = null;
            if (cfg.ExportPorts && (cfg.FileOutput || cfg.ExportCsv || cfg.HtmlReport) && devices.Count > 0)
            {
                ports = await CollectPortsForExport(devices, ip => KnownServices(ip, scanStartedAt), probeMissing: !scanCancelled, scanToken);
            }

            // TXT/CSV/HTML export and the known-devices DB write are blocking
            // file/SQLite I/O; running them inline here would freeze the window
            // right when the scan just finished and the result is what the user
            // wants to see. Do the actual writes off the UI thread.
            var written = await Task.Run(() =>
            {
                var w = new List<string>();
                var outDir = ScanConfig.ResolvePath(cfg.OutputDirectory);
                if (devices.Count > 0)
                {
                    if (cfg.FileOutput)
                        w.Add(TxtExporter.Write(devices, info, outDir, timestamp, gatewaySlug, displayTime, ports));
                    if (cfg.ExportCsv)
                        w.Add(CsvExporter.Write(devices, outDir, timestamp, gatewaySlug, ports));
                    if (cfg.HtmlReport)
                        w.Add(HtmlExporter.Write(devices, info, outDir, timestamp, gatewaySlug, displayTime, ports));
                }
                if (cfg.KnownDevicesDb && info.Gateway is not null)
                {
                    var gw = devices.FirstOrDefault(d => d.Ip == info.Gateway);
                    if (gw?.Mac is { } mac && mac != Device.Unknown)
                    {
                        try
                        {
                            var db = new KnownDevicesDb(cfg.DatabasePath);
                            db.Save(mac, devices, timestamp);
                            db.SaveLatencyHistory(devices, timestamp);
                            db.PurgeOlderThan(cfg.DbRetentionDays);
                        }
                        catch (Exception ex) { Core.Log.Warn(Core.LogArea.Db, "Updating the known devices failed", ex); /* DB optional */ }
                    }
                }
                if (cfg.KnownDevicesDb && cfg.ScanHistory)
                {
                    try
                    {
                        var network = devices.FirstOrDefault(d => d.Ip == info.Gateway)?.Mac;
                        if (string.IsNullOrEmpty(network) || network == Device.Unknown)
                            network = info.Gateway ?? info.Mac ?? "unknown";
                        var snapshot = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            cfg.PingCount, cfg.PingIntervalMs, cfg.ScanThreads,
                            cfg.OfflineAfterFailedPings, cfg.NetworkInterfaceIp
                        });
                        var archive = new KnownDevicesDb(cfg.DatabasePath);
                        archive.SaveScanArchive(scanStartedAt, scanFinishedAt, network,
                            prefixes, snapshot, devices, conflictFindings, scanCancelled, ports);
                        archive.ApplyArchiveMaintenance(cfg);
                        archive.CreateAutomaticScanArchiveBackup(
                            System.IO.Path.Combine(ScanConfig.ResolvePath(cfg.OutputDirectory), "ArchiveBackups"),
                            cfg.ArchiveBackupRetentionDays);
                    }
                    catch (Exception ex) { Core.Log.Warn(Core.LogArea.Db, "Scan archive write failed", ex); }
                }
                return w;
            });
            Core.Log.Debug(Core.LogArea.Scan, $"Export and archive phase took {(DateTimeOffset.UtcNow - scanFinishedAt).TotalMilliseconds:0} ms, {written.Count} report files");

            // TXT and CSV export independently; the sidebar shows name + size.
            if (written.Count > 0)
            {
                _dispatch(() =>
                {
                    LastExportPath = written[0];
                    ExportInfo = string.Join(Environment.NewLine, written.Select(p =>
                    {
                        var f = new System.IO.FileInfo(p);
                        return $"{f.Name} ({Core.NumberFormat.Bytes(f.Length)})";
                    }));
                    ExportFileCount = written.Count;
                    Raise(nameof(LastExportPath));
                    Raise(nameof(ExportInfo));
                    Raise(nameof(HasExport));
                    Raise(nameof(ExportFileCount));
                    Raise(nameof(ExportsHeaderText));
                });
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
        finally
        {
            engine.DeviceUpdated -= OnDeviceUpdated;
            engine.DeviceRemoved -= OnDeviceRemoved;
            engine.Progress.Changed -= OnProgressChanged;
            _dispatch(Progress.EndScan);
        }
    }

    /// <summary>Ping the configured internet hosts in parallel, pingCount times
    /// each (-1 = endless), tracking min/max/avg live - mirrors the table run.</summary>
    public async Task PingInternetAsync(CancellationToken ct = default, int pingCount = 1)
    {
        if (!Config.EnableInternetPing || InternetPingingHeld) return;

        // Reconcile instead of rebuilding: a host whose IP is unchanged keeps
        // its InternetHostViewModel instance (and its min/max/avg history) -
        // only genuinely new IPs start at zero, removed ones are dropped, a
        // renamed-but-same-IP host keeps its history under the new name.
        var wanted = Config.InternetHosts.Select(ParseHostEntry).ToList();
        _dispatch(() =>
        {
            var keepIps = new HashSet<string>(wanted.Select(w => w.ip));
            for (int i = InternetHosts.Count - 1; i >= 0; i--)
                if (!keepIps.Contains(InternetHosts[i].Ip)) InternetHosts.RemoveAt(i);
            var existingIps = new HashSet<string>(InternetHosts.Select(h => h.Ip));
            foreach (var (ip, name) in wanted)
            {
                if (existingIps.Add(ip)) InternetHosts.Add(new InternetHostViewModel(name, ip));
                else if (InternetHosts.FirstOrDefault(h => h.Ip == ip) is { } h) h.Name = name;
            }
        });

        int target = pingCount == ScanConfig.InfinitePingCount ? int.MaxValue : Math.Max(1, pingCount);
        int interval = Math.Max(Config.PingIntervalMs, MinInternetIntervalMs);
        int timeout = Math.Clamp(Config.InternetTimeoutMs, 100, 10_000);

        await Task.WhenAll(wanted.Select(w => Task.Run(async () =>
        {
            for (int i = 0; i < target && !ct.IsCancellationRequested; i++)
            {
                var r = _pingFunc(w.ip, timeout);
                _dispatch(() =>
                {
                    if (InternetHosts.FirstOrDefault(h => h.Ip == w.ip) is { } host)
                    {
                        host.RecordPing(r.Success ? r.LatencyMs : null);
                        UpdateInternetTotals();
                    }
                });
                if (i + 1 >= target) break;
                try { await Task.Delay(interval, ct); }
                catch (OperationCanceledException) { break; }
            }
        })));
    }

    public void Stop() { _cts?.Cancel(); _internetCts?.Cancel(); }

    /// <summary>Unattended repeat scan: runs with the last confirmed subnet (or the detected
    /// network) and never asks anything. Without any target it logs and returns.</summary>
    public async Task RunRepeatScanAsync()
    {
        if (IsScanning) return;
        var (prefixes, _) = ResolveTargets(_detectNetwork(), ManualSubnet, Config.Subnets);
        if (prefixes.Count == 0)
        {
            Core.Log.Info(Core.LogArea.Scan, "Repeat scan skipped: no network to scan.");
            return;
        }
        await RunScanAsync();
    }

    /// <summary>Stops a running scan like the Stop button, waits until it has finished and then
    /// empties the table, so rows of one profile never linger under another.</summary>
    internal Task StopAndClearAsync()
    {
        var clearing = StopAndClearCoreAsync();
        _clearInFlight = clearing;
        return clearing;
    }

    // A scan start requested while a clear is still waiting for the running scan queues behind it
    // instead of being dropped.
    private Task? _clearInFlight;

    private async Task StopAndClearCoreAsync()
    {
        Stop();
        await _scanStartGate.WaitAsync();   // held by a running scan until it is completely done
        try
        {
            _dispatch(() =>
            {
                ClearRows();
                ClearNetworkCards();
                ResetScanSummary();
            });
        }
        finally { _scanStartGate.Release(); }
    }

    public void StopAndClear() => _ = StopAndClearAsync();

    /// <summary>Header counters, progress and the last-scan text follow the emptied table.</summary>
    private void ResetScanSummary()
    {
        Progress.Reset();
        _lastScanAt = null;
        _hasScannedOnce = false;
        Raise(nameof(LastScanText));
        Raise(nameof(HasLastScan));
        Raise(nameof(ValuesAreStale));
    }

    /// <summary>Empties the table. Port scans still running for the removed rows are cancelled
    /// first, so they stop probing devices nobody can see any more.</summary>
    private void ClearRows()
    {
        CancelRowPortScans();
        lock (_byIpLock) { Devices.Clear(); _byIp.Clear(); }
    }

    /// <summary>Drops the old profile's network cards; the current profile's sidebar subnets get
    /// their placeholder cards back.</summary>
    private void ClearNetworkCards()
    {
        lock (_graphListLock) Networks.Clear();
        RefreshSubnetMeta();
    }

    private void CancelRowPortScans()
    {
        foreach (var row in Devices.ToList()) row.CancelPortScans();
    }

    private CancellationTokenSource? _internetCts;
    private Task _internetLoop = Task.CompletedTask;
    private readonly SemaphoreSlim _internetRestartGate = new(1, 1);

    /// <summary>(Re)start the internet-latency ping loop on its own token, independent
    /// of the device scan - lets a sidebar host-list edit take effect immediately
    /// without disturbing (or being disturbed by) an in-progress device scan.
    /// Fire-and-forget wrapper: callers are synchronous UI handlers, but restarts
    /// still serialize so two quick edits can't race each other's cancel/dispose.</summary>
    public void RestartInternetPinging(int pingCount)
    {
        _ = RestartInternetPingingAsync(pingCount);
    }

    internal async Task RestartInternetPingingAsync(int pingCount)
    {
        await _internetRestartGate.WaitAsync();
        try
        {
            var oldCts = _internetCts;
            if (oldCts is not null)
            {
                oldCts.Cancel();
                // Give the previous loop a chance to actually observe cancellation and
                // return before its token source is disposed, so it never records a
                // straggling ping into the same host the new loop just started on.
                // Bounded by the configured ping timeout plus a small margin, since
                // that is the longest a single in-flight ping can still be running.
                int waitMs = Math.Clamp(Config.InternetTimeoutMs, 100, 10_000) + 250;
                await Task.WhenAny(_internetLoop, Task.Delay(waitMs));
                oldCts.Dispose();
            }
            _internetCts = new CancellationTokenSource();
            _internetLoop = PingInternetAsync(_internetCts.Token, pingCount)
                    .ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
        finally { _internetRestartGate.Release(); }
    }

    /// <summary>The networks a scan covers: the typed subnet when there is one, else the
    /// detected network, plus every configured subnet.</summary>
    internal static (IReadOnlyList<string> Prefixes, IReadOnlyList<(uint First, uint Last)> Ranges)
        ResolveTargets(NetworkInfo info, string? manualSubnet, IEnumerable<string> extra)
    {
        var list = new List<string>();
        var ranges = new List<(uint First, uint Last)>();
        var seen = new HashSet<string>();
        void Add(IEnumerable<string>? prefixes, (uint First, uint Last)? range)
        {
            if (prefixes is null) return;
            foreach (var p in prefixes) if (seen.Add(p)) list.Add(p);
            if (range is { } r) ranges.Add(r);
        }
        void AddFull(string prefix)
        {
            if (Ipv4.TryParseUInt(prefix + ".0", out var b))
                Add(new[] { prefix }, (b + Ipv4.FirstHost, b + Ipv4.LastHost));
        }

        // Manual subnet (header field + CIDR) replaces only the auto-detected net.
        var manual = ManualTarget(manualSubnet);
        if (manual is { } m) Add(m.Prefixes, m.Range);
        else if (info.Ip is not null)
        {
            var detected = Ipv4.PrefixesForCidr(info.Ip, info.CidrLength);
            if (detected is not null)
                Add(detected, Ipv4.HostRange(info.Ip, info.CidrLength));
            else AddFull(Ipv4.SubnetPrefix(info.Ip));
        }

        // Configured subnets (settings) are always scanned in addition.
        foreach (var s in extra)
            if (ManualTarget(NetEntry.Parse(s).Target) is { } t) Add(t.Prefixes, t.Range);
        return (list, ranges);
    }

    /// <summary>The addresses of the configured IPv6 subnet entries (expanded like a command line target).</summary>
    private IEnumerable<string> Ipv6SubnetAddresses(IEnumerable<string> subnets)
    {
        var texts = subnets.Select(s => NetEntry.Parse(s).Target)
            .Where(t => t.Contains(':') && Ipv6Prefix.TryParse(t, out _)).ToList();
        if (texts.Count == 0) return Array.Empty<string>();
        var spec = TargetSpec.Parse(string.Join(",", texts), new TargetSpecOptions { HostFamily = IpFamily.V6 });
        return spec.Expand(ConfirmLargeScan);
    }

    /// <summary>Card details for one /24 slice of an "ip/cidr" target, so the card shows the
    /// target's real network and mask. Null when the text is not a usable IPv4 target.</summary>
    internal static NetworkInfo? InfoForTarget(string? target, string prefix)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        var slash = target.Split('/');
        var ip = slash[0].Trim();
        if (!Ipv4.IsValid(ip)) return null;
        int cidr = slash.Length > 1 && int.TryParse(slash[1].Trim(), out var c) ? c : 24;
        if (cidr < 0 || cidr > 32) return null;
        uint mask = cidr == 0 ? 0u : 0xFFFFFFFFu << (32 - cidr);
        return new NetworkInfo
        {
            Ip = Ipv4.SubnetPrefix(ip) == prefix ? ip : prefix + ".0",
            SubnetMask = Ipv4.FromUInt(mask),
        };
    }

    private static (List<string> Prefixes, (uint First, uint Last)? Range)? ManualTarget(string? input)
    {
        var prefixes = ManualPrefixes(input);
        if (prefixes is null) return null;
        var slash = input!.Split('/');
        int cidr = slash.Length > 1 && int.TryParse(slash[1].Trim(), out var c) ? c : 24;
        return (prefixes, Ipv4.HostRange(slash[0].Trim(), cidr));
    }

    /// <summary>Expand "ip/cidr" into the exact /24 prefixes the mask covers
    /// (real network-aligned math, not a naive octet split). Returns null when
    /// the input isn't a usable IPv4 subnet or the range exceeds the hard cap.</summary>
    private static List<string>? ManualPrefixes(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var slash = input.Split('/');
        var ip = slash[0].Trim();
        int cidr = slash.Length > 1 && int.TryParse(slash[1].Trim(), out var c) ? c : 24;
        return Ipv4.IsValid(ip) ? Ipv4.PrefixesForCidr(ip, cidr) : null;
    }

    // Devices that answered at least once stay in the list - when they flip to
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
        // Self values are re-forced every ping (enrichment must not win on the
        // own row); lock(d) serializes with the engine's enrichment writers.
        if (d.Ip == _selfIp) lock (d) ApplySelfInfo(d);
        if (!d.IsOnline && !d.Seen && !d.FromDb) return;

        // Known rows are only marked dirty (flushed by the throttled aggregate
        // pass) - dispatching per ping floods the UI thread and lags the table.
        // Most override metadata is stable per row. A late MAC can reveal a
        // MAC-keyed note after row creation, so reconcile that identity once.
        DeviceViewModel? tracked;
        lock (_byIpLock) _byIp.TryGetValue(d.Ip, out tracked);
        if (tracked is not null)
        {
            if (StableMac(d) != tracked.AppliedNoteMac)
                _dispatch(() =>
                {
                    lock (_byIpLock)
                    {
                        if (!_byIp.TryGetValue(d.Ip, out var current) || !ReferenceEquals(current, tracked)) return;
                        lock (d)
                        {
                            var mac = StableMac(d);
                            if (mac == current.AppliedNoteMac) return;
                            ApplyNoteOverride(d);
                            current.AppliedNoteMac = mac;
                        }
                        current.Refresh();
                    }
                });
            _dirty.TryAdd(d.Ip, 0);
            return;
        }

        _dispatch(() =>
        {
            lock (_byIpLock)
            {
                if (_byIp.ContainsKey(d.Ip)) return;
                lock (d)
                {
                    ApplyPinnedName(d);
                    ApplyHostnameOverride(d);
                    ApplyColorOverride(d);
                    ApplyMuteOverride(d);
                    ApplyNoteOverride(d);
                }
                var vm = new DeviceViewModel(d, d.Ip == _selfIp, _pinned.Contains(d.Ip),
                    Config.KnownDevicesDb ? PortStore : null, _fullPortScan);
                vm.AppliedNoteMac = StableMac(d);
                if (Config.AutoGraphForPinned && vm.IsPinned) vm.ShowGraph = true;
                _byIp[d.Ip] = vm;
                InsertSorted(vm);
                if (vm.IsRogue) NewUnknownDeviceFound?.Invoke(vm);
            }
        });
    }

    private void OnDeviceRemoved(Device d)
    {
        _dirty.TryRemove(d.Ip, out _);
        _dispatch(() =>
        {
            lock (_byIpLock)
            {
                if (!_byIp.TryGetValue(d.Ip, out var vm)) return;
                _byIp.Remove(d.Ip);
                Devices.Remove(vm);
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
                // lock(dev): same reasoning as RefreshPinnedNames above - these
                // touch Hostname/HostnameRank/GroupColorOverride concurrently
                // with the engine's own lock(device) enrichment writes.
                lock (dev)
                {
                    ApplyPinnedName(dev);
                    ApplyHostnameOverride(dev);
                    ApplyColorOverride(dev);
                    ApplyMuteOverride(dev);
                    ApplyNoteOverride(dev);
                }
                if (!_byIp.ContainsKey(dev.Ip))
                {
                    var vm = new DeviceViewModel(dev, dev.Ip == _selfIp, _pinned.Contains(dev.Ip),
                        Config.KnownDevicesDb ? PortStore : null, _fullPortScan);
                    vm.AppliedNoteMac = StableMac(dev);
                    if (Config.AutoGraphForPinned && vm.IsPinned) vm.ShowGraph = true;
                    _byIp[dev.Ip] = vm;
                    InsertSorted(vm);
                }
                else _byIp[dev.Ip].AppliedNoteMac = StableMac(dev);
            }
        }
    }

    private (string Network, string DatabasePath) PortStore()
    {
        var network = _gatewayIp is { } gateway && _byIp.TryGetValue(gateway, out var row)
            && row.Model.Mac is { } mac && mac != Device.Unknown ? mac : _gatewayIp ?? "";
        return (network, Config.DatabasePath);
    }

    /// <summary>Insert a device row: pinned rows first, then ordered by numeric IP.
    /// Binary search over the existing (already sorted) rows instead of a linear scan -
    /// the list only ever grows one row at a time, so this is the hot path at scale.</summary>
    private void InsertSorted(DeviceViewModel vm)
    {
        int Rank(DeviceViewModel d) => d.IsPinned ? 0 : 1;
        int rank = Rank(vm);
        int lo = 0, hi = Devices.Count;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            var d = Devices[mid];
            int r = Rank(d);
            bool before = r < rank || (r == rank && d.IpSortKey.CompareTo(vm.IpSortKey) <= 0);
            if (before) lo = mid + 1; else hi = mid;
        }
        Devices.Insert(lo, vm);
    }

    /// <summary>Re-raise all row properties (e.g. after a language change).</summary>
    public void RefreshAllRows() => RefreshAll();

    /// <summary>Re-reads every color that's computed in C# (Core.Palette) rather
    /// than bound via XAML DynamicResource - call after the active theme changes
    /// so device rows, totals, network cards, pinned chips and internet hosts
    /// all re-color immediately instead of only on their next natural update.</summary>
    public void RefreshTheme()
    {
        RefreshAllRows();
        List<DeviceViewModel> rows;
        lock (_byIpLock) { rows = Devices.ToList(); }
        UpdateTotals(rows);
        UpdateInternetTotals();
        foreach (var n in Networks) n.RefreshTheme();
        foreach (var p in PinnedEntries) p.RefreshTheme();
        foreach (var h in InternetHosts) h.RefreshTheme();
    }

    /// <summary>Re-raises Loc-backed texts that live outside plain XAML bindings
    /// (progress legend, "Network N" fallback titles) after a language switch.</summary>
    public void RefreshLanguage()
    {
        Progress.RefreshLanguage();
        foreach (var n in Networks) n.RefreshLanguage();
        foreach (var d in Devices) d.RefreshLanguage();
        Raise(nameof(ExportsHeaderText));
        Raise(nameof(LastScanText));
        Raise(nameof(ThreadsText));
        RelabelAutoInterface();
    }

    /// <summary>Swaps the "Auto" adapter entry for one with the current language's label,
    /// keeping the chosen adapter selected.</summary>
    internal void RelabelAutoInterface()
    {
        if (AvailableInterfaces.Count == 0 || AvailableInterfaces[0].Ip.Length != 0) return;
        var selected = SelectedInterfaceIp;
        AvailableInterfaces[0] = new InterfaceOption("", Loc.NetworkInterfaceAuto);
        SelectedInterfaceIp = selected;
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
        // engine.Devices is already a fresh snapshot array - use it directly.
        var all = (IList<Device>)engine.Devices;
        var p = engine.Progress;

        // Live grouping is O(n) - debounce it (≤ every 400 ms) so a fast ping
        // stream doesn't pin the UI thread; force a final pass at scan end.
        long now = Environment.TickCount64;
        if (forceGroups || now - _lastGroupTick >= GroupDebounceMs)
        {
            _lastGroupTick = now;
            var groupChanged = DeviceGrouper.AssignGroups(all, _gatewayIp);
            // Refresh only rows whose group actually shifted, plus whatever else went dirty.
            var toRefresh = new HashSet<string>(groupChanged.Select(d => d.Ip));
            foreach (var ip in _dirty.Keys)
            {
                _dirty.TryRemove(ip, out _);
                toRefresh.Add(ip);
            }
            lock (_byIpLock)
            {
                foreach (var ip in toRefresh)
                    if (_byIp.TryGetValue(ip, out var vm)) vm.Refresh();
            }
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
        SetActiveWorkers(engine.ActiveWorkers);

        // One pass over the devices: online count plus, when network cards exist, each device is
        // looked up by its own /24 prefix in a dictionary built once (size = network count).
        int discovered = all.Count;
        int online = 0;
        int n = Networks.Count;
        Dictionary<string, int>? indexByPrefix = null;
        int[]? onlineByNet = null, offlineByNet = null, avgCnt = null;
        double[]? avgSum = null;
        if (n > 0)
        {
            indexByPrefix = new Dictionary<string, int>(n, StringComparer.Ordinal);
            for (int i = 0; i < n; i++) indexByPrefix[Networks[i].Prefix] = i;
            onlineByNet = new int[n]; offlineByNet = new int[n]; avgCnt = new int[n];
            avgSum = new double[n];
        }
        foreach (var d in all)
        {
            bool isOnline = d.IsOnline;
            if (isOnline) online++;
            if (indexByPrefix is null) continue;
            if (!indexByPrefix.TryGetValue(Ipv4.SubnetPrefix(d.Ip), out int i)) continue;
            if (isOnline)
            {
                onlineByNet![i]++;
                if (d.AvgMs is { } a) { avgSum![i] += a; avgCnt![i]++; }
            }
            else offlineByNet![i]++;
        }
        int offline = discovered - online;
        // Online/offline composition changed -> the offline filter may need a refresh.
        if (online != _filterOnline || offline != _filterOffline)
        {
            _filterOnline = online; _filterOffline = offline;
            DeviceVisibilityChanged?.Invoke();
        }
        Progress.SetDevices(online, offline, Math.Max(_plannedDevices, 1));
        Progress.SetPings(p.SuccessPings, p.FailedPings, p.SkippedPings, _totalPings);

        for (int i = 0; i < n; i++)
        {
            Networks[i].OnlineCount = onlineByNet![i];
            Networks[i].OfflineCount = offlineByNet![i];
            Networks[i].SetAvg(avgCnt![i] > 0 ? avgSum![i] / avgCnt[i] : null);
        }

        List<DeviceViewModel> rows;
        lock (_byIpLock) { rows = Devices.ToList(); }
        var stats = GatherColumnStats(rows);
        MarkExtremes(rows, stats);
        UpdateTotals(stats);
    }

    // ── Totals row (average of each ping column across all rows, heat-colored) ──
    public string TotalAvg { get; private set; } = "–";
    public string TotalMin { get; private set; } = "–";
    public string TotalMax { get; private set; } = "–";
    public string TotalLast { get; private set; } = "–";
    public string TotalAvgColor { get; private set; } = Core.Palette.MidGray;
    public string TotalMinColor { get; private set; } = Core.Palette.MidGray;
    public string TotalMaxColor { get; private set; } = Core.Palette.MidGray;
    public string TotalLastColor { get; private set; } = Core.Palette.MidGray;

    /// <summary>Per ping column (avg, min, max, last): sum and count for the totals row plus the best
    /// and worst row, all gathered in a single pass.</summary>
    private struct ColumnStat
    {
        public double Sum, Min, Max;
        public int Count;
        public DeviceViewModel? Best, Worst;

        public void Add(DeviceViewModel vm, double? value)
        {
            if (value is not { } v) return;
            if (Count == 0) { Min = Max = v; Best = Worst = vm; }
            else
            {
                if (v < Min) { Min = v; Best = vm; }
                if (v > Max) { Max = v; Worst = vm; }
            }
            Sum += v;
            Count++;
        }

        public readonly double? Average => Count == 0 ? null : Sum / Count;
        public readonly bool HasExtremes => Count >= 2 && Min != Max;
    }

    private readonly struct ColumnStats
    {
        public ColumnStats(ColumnStat avg, ColumnStat min, ColumnStat max, ColumnStat last)
        { Avg = avg; Min = min; Max = max; Last = last; }
        public readonly ColumnStat Avg, Min, Max, Last;
    }

    private static ColumnStats GatherColumnStats(List<DeviceViewModel> rows)
    {
        ColumnStat avg = default, min = default, max = default, last = default;
        foreach (var vm in rows)
        {
            avg.Add(vm, vm.AvgRaw);
            min.Add(vm, vm.MinRaw);
            max.Add(vm, vm.MaxRaw);
            last.Add(vm, vm.LastRaw);
        }
        return new ColumnStats(avg, min, max, last);
    }

    private void UpdateTotals(ColumnStats stats)
    {
        double? avg = stats.Avg.Average, min = stats.Min.Average,
                max = stats.Max.Average, last = stats.Last.Average;
        TotalAvg = Core.NumberFormat.Ms(avg);   TotalAvgColor = Core.Palette.Heat(avg);
        TotalMin = Core.NumberFormat.Ms(min);   TotalMinColor = Core.Palette.Heat(min);
        TotalMax = Core.NumberFormat.Ms(max);   TotalMaxColor = Core.Palette.Heat(max);
        TotalLast = Core.NumberFormat.Ms(last); TotalLastColor = Core.Palette.Heat(last);
        Raise(nameof(TotalAvg)); Raise(nameof(TotalMin));
        Raise(nameof(TotalMax)); Raise(nameof(TotalLast));
        Raise(nameof(TotalAvgColor)); Raise(nameof(TotalMinColor));
        Raise(nameof(TotalMaxColor)); Raise(nameof(TotalLastColor));
    }

    private void UpdateTotals(List<DeviceViewModel> rows) => UpdateTotals(GatherColumnStats(rows));

    // Mark best (lowest) green and worst (highest) red per ping column across rows.
    private static void MarkExtremes(List<DeviceViewModel> list, ColumnStats stats)
    {
        var (avgB, avgW) = stats.Avg.HasExtremes ? (stats.Avg.Best, stats.Avg.Worst) : (null, null);
        var (minB, minW) = stats.Min.HasExtremes ? (stats.Min.Best, stats.Min.Worst) : (null, null);
        var (maxB, maxW) = stats.Max.HasExtremes ? (stats.Max.Best, stats.Max.Worst) : (null, null);
        var (lastB, lastW) = stats.Last.HasExtremes ? (stats.Last.Best, stats.Last.Worst) : (null, null);

        static int Mark(DeviceViewModel vm, DeviceViewModel? b, DeviceViewModel? w)
            => vm == b ? -1 : vm == w ? 1 : 0;

        foreach (var vm in list)
            vm.SetMarks(Mark(vm, avgB, avgW), Mark(vm, minB, minW),
                        Mark(vm, maxB, maxW), Mark(vm, lastB, lastW));
    }

    // ── Internet totals row (after the last host): Ø/Letzter = averages,
    //    Min/Max = the extreme values across all hosts ──
    public string InternetTotalAvg { get; private set; } = "–";
    public string InternetTotalMin { get; private set; } = "–";
    public string InternetTotalMax { get; private set; } = "–";
    public string InternetTotalLast { get; private set; } = "–";
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

    private static string Slug(string? host)
    {
        if (string.IsNullOrEmpty(host)) return "";
        var safe = new string(host.Where(c => !"\\/:*?\"<>| \t".Contains(c)).ToArray());
        return safe.Length > 40 ? safe[..40] : safe;
    }
}
