using IpScanner.Core;
using IpScanner.Core.Data;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;

namespace IpScanner.ViewModels;

public sealed class DeviceViewModel : ObservableObject
{
    private readonly Device _device;
    private readonly Func<(string Network, string DatabasePath)>? _portStore;
    private readonly Func<string, IProgress<int>, System.Threading.CancellationToken, Action<NetService>, Task<IReadOnlyList<NetService>>> _fullScan;
    private string _note;

    public DeviceViewModel(Device device, bool isSelf = false, bool isPinned = false,
        Func<(string Network, string DatabasePath)>? portStore = null,
        Func<string, IProgress<int>, System.Threading.CancellationToken, Action<NetService>, Task<IReadOnlyList<NetService>>>? fullScan = null)
    {
        _device = device;
        _portStore = portStore;
        _fullScan = fullScan ?? ((ip, progress, ct, onFound) => PortScanner.ScanFullRangeAsync(ip, progress, ct, onFound));
        _note = device.Note ?? "";
        _noteDraft = _note;
        IsSelf = isSelf;
        _isPinned = isPinned;
        IpSortKey = Core.Net.IpAddressKey.TryParse(device.Ip, out var key) ? key : default;
        ServicesView = System.Windows.Data.CollectionViewSource.GetDefaultView(Services);
        ServicesView.Filter = o => o is NetService s && ServiceSearch.Matches(s.Name, s.Port, _serviceFilterText);
    }

    /// <summary>Underlying device (for override handling in the main VM).</summary>
    internal Device Model => _device;

    /// <summary>True for this machine's own row.</summary>
    public bool IsSelf { get; }
    /// <summary>True for a pinned IP (kept at the top of the list).</summary>
    private bool _isPinned;
    public bool IsPinned
    {
        get => _isPinned;
        set { _isPinned = value; Raise(nameof(IsPinned)); Raise(nameof(IpColor)); Raise(nameof(IpTooltip)); }
    }
    public string IpText => ShownIp + (IsSelf ? "  ★" : "");
    /// <summary>The address as shown: IPv6 in its short RFC 5952 form, anything else unchanged.</summary>
    private string ShownIp => Core.Net.IpAddressKey.TryParse(_device.Ip, out var k) && k.IsV6 ? k.Text + (k.Zone is null ? "" : "%" + k.Zone) : _device.Ip;
    /// <summary>Full address for the cell tooltip when the text may be trimmed.</summary>
    public string IpFullText => _device.Ip;
    /// <summary>Cell tooltip: the pin hint while unpinned, plus the full address of an IPv6 row.</summary>
    public string? IpTooltip
    {
        get
        {
            bool v6 = Core.Net.IpAddressKey.TryParse(_device.Ip, out var k) && k.IsV6;
            if (_isPinned) return v6 ? _device.Ip : null;
            return v6 ? Loc.TipPinIp + Environment.NewLine + _device.Ip : Loc.TipPinIp;
        }
    }
    // Muted wins over the pinned tint (self can't practically be muted).
    public string IpColor => IsSelf ? Core.Palette.Mauve
        : _device.IsMuted ? Core.Palette.Subtext
        : _isPinned ? Core.Palette.Peach : Core.Palette.Text;

    /// <summary>Numeric address for sorting: IPv4 before IPv6, text that is no address first.</summary>
    public Core.Net.IpAddressKey IpSortKey { get; }

    public string Ip => _device.Ip;
    public string Status => _device.IsListed ? Loc.StatusListed : _device.IsOnline ? Loc.StatusOnline : Loc.StatusOffline;
    /// <summary>Which discovery mode found the host.</summary>
    public string Method => Core.Scanner.Discovery.DiscoveryModes.Display(_device.DiscoveryMethod);

    private string? _osKey;
    private Core.Scanner.Os.OsEstimate _os = Core.Scanner.Os.OsEstimate.None;
    private Core.Scanner.Os.OsEstimate CurrentOs()
    {
        var key = $"{_device.Ttl}|{_device.OsFingerprint?.Match}|{_device.OsFingerprint?.Percent}|{Services.Count}";
        if (key != _osKey)
        {
            _osKey = key;
            _os = Core.Scanner.Os.OsDisplay.For(_device, Services);
        }
        return _os;
    }
    /// <summary>Operating system estimate for the table, empty when nothing is known.</summary>
    public string Os => Core.Scanner.Os.OsDisplay.Text(CurrentOs());
    public string OsTip => Core.Scanner.Os.OsDisplay.Tip(CurrentOs());
    /// <summary>A muted device never shows the offline-red highlight -
    /// the whole row dims to Subtext instead (see the other *Color properties).</summary>
    public string StatusColor => _device.IsMuted || _device.IsListed ? Core.Palette.Subtext
        : _device.IsOnline ? Core.Palette.Green : Core.Palette.Red;
    public bool IsOnline => _device.IsOnline;
    /// <summary>"Mute device" - suppresses offline coloring, the rogue
    /// highlight and the fast offline-recheck burst.</summary>
    public bool IsMuted => _device.IsMuted;
    /// <summary>True when this row originates from the known-devices database.</summary>
    public bool IsFromDb => _device.FromDb;
    public string Hostname => _device.Hostname is null or Device.Unknown ? "—" : _device.Hostname;
    /// <summary>The real hostname, or null when none is known (the table shows a dash then; copy actions must not).</summary>
    internal string? CopyableHostname => string.IsNullOrEmpty(_device.Hostname) || _device.Hostname == Device.Unknown ? null : _device.Hostname;
    public string Note => _note;
    internal string? AppliedNoteMac { get; set; }
    internal bool HasNoteEdit => _noteDraft != _note;
    private string _noteDraft;
    public string NoteDraft
    {
        get => _noteDraft;
        set
        {
            if (!SetProperty(ref _noteDraft, value)) return;
            Raise(nameof(IsNoteTooLong));
            Raise(nameof(NoteValidationText));
        }
    }
    public bool IsNoteTooLong => _noteDraft.Length > OverrideStore.MaxNoteLength;
    public string NoteValidationText => IsNoteTooLong
        ? string.Format(Loc.NoteTooLongFmt, _noteDraft.Length, OverrideStore.MaxNoteLength)
        : "";

    internal void CommitNote(string? note)
    {
        _device.Note = note;
        _note = note ?? "";
        NoteDraft = _note;
        Raise(nameof(Note));
    }

    private void RefreshNote()
    {
        var note = _device.Note ?? "";
        if (note == _note) return;
        var draftWasUnchanged = _noteDraft == _note;
        _note = note;
        Raise(nameof(Note));
        if (draftWasUnchanged) NoteDraft = note;
    }
    public string Mac => _device.Mac is null or Device.Unknown ? "—" : _device.Mac;
    /// <summary>False when no MAC is known yet - Wake-on-LAN can't target this row.</summary>
    public bool HasMac => _device.Mac is { } m && m != Device.Unknown;

    /// <summary>Pre-formatted IP-conflict/MAC-change hint text for this
    /// device's detail view, empty when there is none. Set by MainViewModel once
    /// per completed scan (findings are computed there, not per-row).</summary>
    public string ConflictHint { get; set; } = "";
    public bool HasConflictHint => ConflictHint.Length > 0;
    // Vendor lookup result cached per MAC: the row's Vendor binding re-evaluates on
    // every UI refresh tick (hundreds of times/sec across a full subnet), but the
    // MAC itself changes at most once (when it's first resolved) - recomputing the
    // same OUI lookup every tick was pure waste.
    private string? _cachedVendorMac;
    private string _cachedVendor = "—";
    public string Vendor
    {
        get
        {
            var mac = _device.Mac is { } m && m != Device.Unknown ? m : null;
            if (mac != _cachedVendorMac)
            {
                _cachedVendorMac = mac;
                _cachedVendor = mac is not null ? Core.Export.MacVendorLookup.Instance.Lookup(mac) ?? "—" : "—";
            }
            return _cachedVendor;
        }
    }
    /// <summary>Online device whose MAC is not in the known-devices database (new/unknown).
    /// A muted device never gets the rogue highlight either.</summary>
    public bool IsRogue => !_device.IsMuted && _device.IsOnline && !_device.FromDb && !IsSelf
                           && _device.Mac is { } m && m != Device.Unknown;

    // Device-type icon next to the hostname. Detection re-runs the
    // needle/port scan every time (cheap, but not free) so it's cached the same
    // way as Vendor above - keyed on the three inputs that can actually change it.
    private string? _cachedIconKeyInputs;
    private string _cachedIconKey = "IconUnknown";
    public string DeviceTypeIconKey
    {
        get
        {
            var inputs = $"{Hostname}|{Vendor}|{Services.Count}";
            if (inputs != _cachedIconKeyInputs)
            {
                _cachedIconKeyInputs = inputs;
                var type = DeviceTypeDetector.Detect(Hostname, Vendor, Services.Select(s => s.Port));
                _cachedIconKey = DeviceTypeDetector.IconKey(type);
            }
            return _cachedIconKey;
        }
    }
    public int GroupId => _device.GroupId;

    // 0/1 = none/unknown -> gray, 2 = gateway group -> fixed brand color,
    // >=3 -> diverse palette (fixed order, stable across scans).
    public string GroupColor => _device.GroupColorOverride ?? _device.GroupId switch
    {
        <= 1 => Core.Palette.Surface2,
        2 => GatewayColor(_device.Hostname),
        var g => GroupColorPalette.ColorForIndex(g - 3),
    };

    // Gateway keeps a stable color: blue = UniFi, red = Fritz, otherwise purple.
    private static string GatewayColor(string? hostname)
    {
        var h = (hostname ?? "").ToLowerInvariant();
        if (h.Contains("unifi")) return Core.Palette.UnifiBlue;
        if (h.Contains("fritz")) return Core.Palette.Red;
        return Core.Palette.Mauve;
    }

    public string AvgDisplay => Core.NumberFormat.Ms(_device.AvgMs);
    public string MinDisplay => Core.NumberFormat.Ms(_device.MinMs);
    public string MaxDisplay => Core.NumberFormat.Ms(_device.MaxMs);
    public string LastDisplay => _device.LastFailed ? Loc.NotAvailable : Core.NumberFormat.Ms(_device.LastMs);
    public string LossDisplay => Core.NumberFormat.Percent(_device.LossPercent);
    public string JitterDisplay => Core.NumberFormat.Ms(_device.JitterMs);

    // Raw latency values (for cross-row best/worst comparison).
    public double? AvgRaw => _device.AvgMs;
    public double? MinRaw => _device.MinMs;
    public double? MaxRaw => _device.MaxMs;
    public double? LastRaw => _device.LastFailed ? null : _device.LastMs;

    // Best (lowest) / worst (highest) marker per column: green ▼ best, red ▲ worst.
    private const string Best = "▼", Worst = "▲";
    private int _avgMark, _minMark, _maxMark, _lastMark;   // -1 best, 1 worst, 0 none
    public string AvgMark => MarkGlyph(_avgMark);
    public string MinMark => MarkGlyph(_minMark);
    public string MaxMark => MarkGlyph(_maxMark);
    public string LastMark => MarkGlyph(_lastMark);
    public string AvgMarkColor => MarkColor(_avgMark);
    public string MinMarkColor => MarkColor(_minMark);
    public string MaxMarkColor => MarkColor(_maxMark);
    public string LastMarkColor => MarkColor(_lastMark);
    private static string MarkGlyph(int m) => m < 0 ? Best : m > 0 ? Worst : "";
    private static string MarkColor(int m) => m < 0 ? Core.Palette.Green : m > 0 ? Core.Palette.Red : Core.Palette.Transparent;

    /// <summary>Set per-column best(-1)/worst(1)/none(0) markers.</summary>
    public void SetMarks(int avg, int min, int max, int last)
    {
        // Rows whose markers did not move raise nothing: the tick calls this for every row.
        if (avg == _avgMark && min == _minMark && max == _maxMark && last == _lastMark) return;
        _avgMark = avg; _minMark = min; _maxMark = max; _lastMark = last;
        Raise(nameof(AvgMark)); Raise(nameof(MinMark)); Raise(nameof(MaxMark)); Raise(nameof(LastMark));
        Raise(nameof(AvgMarkColor)); Raise(nameof(MinMarkColor)); Raise(nameof(MaxMarkColor)); Raise(nameof(LastMarkColor));
    }

    // Latency heatmap: green (fast) -> red (slow). A muted device dims the
    // whole row to Subtext instead of the heatmap/red-on-fail colors.
    public string AvgColor => _device.IsMuted ? Core.Palette.Subtext : Core.Palette.Heat(_device.AvgMs);
    public string MinColor => _device.IsMuted ? Core.Palette.Subtext : Core.Palette.Heat(_device.MinMs);
    public string MaxColor => _device.IsMuted ? Core.Palette.Subtext : Core.Palette.Heat(_device.MaxMs);
    public string LastColor => _device.IsMuted ? Core.Palette.Subtext
        : _device.LastFailed ? Core.Palette.Red : Core.Palette.Heat(_device.LastMs);
    public string ProgressDisplay =>
        _device.TargetPings == ScanConfig.InfinitePingCount
            ? $"{Core.NumberFormat.Short(_device.CurrentPings)}/∞"
            : $"{Core.NumberFormat.Short(_device.CurrentPings)}/{Core.NumberFormat.Short(_device.TargetPings)}";

    // ── Expandable per-row latency graph (per-second history; follows the graph
    //    settings exactly like the sidebar graphs) ──
    public const double RowGraphHeight = 60;
    private const double DefaultRowGraphWidth = 600;
    private double _rowGraphWidth = DefaultRowGraphWidth;   // replaced by the canvas actual width on layout
    private readonly RingBuffer<double?> _rowSamples = new();
    private readonly object _rowLock = new();
    private int _rowTick = 30;

    private bool _showGraph;
    /// <summary>Latency graph shown under this row.</summary>
    public bool ShowGraph
    {
        get => _showGraph;
        set
        {
            if (_showGraph == value) return;
            _showGraph = value;
            if (value)
            {
                // Always reopen on the live "last minute" view - a
                // stale 24h/7d selection from a previous open would otherwise
                // show its button highlighted with no history loaded yet.
                SelectedRange = LatencyRange.Minute;
                Raise(nameof(IsMinuteRange)); Raise(nameof(IsDayRange)); Raise(nameof(IsWeekRange));
                BuildRowGraph(); BackgroundTask.Observe(LoadStoredPortScan(), "Stored port scan");
            }
            else { _serviceScanCts?.Cancel(); _fullScanCts?.Cancel(); }   // detail closed - stop probing
            Raise(nameof(ShowGraph));
        }
    }

#if DEBUG
    /// <summary>Surface tool: opens the detail with a fixed service list and no network access.</summary>
    internal void ReviewOpenDetail(IEnumerable<NetService> services, string note)
    {
        Services.ReplaceAll(services);
        _servicesScanned = true;
        NoteDraft = note;
        ShowGraph = true;
    }
#endif

    /// <summary>Open TCP/UDP services on this device (filled when the detail opens).</summary>
    public BatchObservableCollection<NetService> Services { get; } = new();
    public string PortScanResultHeader { get; private set; } = "";
    public bool HasPortScanResult => PortScanResultHeader.Length > 0;
    public string PortScanErrorText { get; private set; } = "";
    public bool HasPortScanError => PortScanErrorText.Length > 0;
    private DateTimeOffset? _portScanSeenAt;
    /// <summary>When the shown port result was stored, or null for a result found in this session.</summary>
    internal DateTimeOffset? PortScanSeenAt => _portScanSeenAt;
    private bool _portScanPartial;
    private bool _portScanLoadFailed;
    private bool _loadingStoredPortScan;

    private void SetPortScanResultHeader(DateTimeOffset seenAt, bool partial)
    {
        _portScanSeenAt = seenAt;
        _portScanPartial = partial;
        PortScanResultHeader = string.Format(partial ? Loc.PartialPortResultFmt : Loc.PortResultFmt,
            seenAt.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture));
        Raise(nameof(PortScanResultHeader)); Raise(nameof(HasPortScanResult));
    }

    private async Task LoadStoredPortScan()
    {
        if (_portStore is null || IsFullPortScanRunning) { ObserveServiceScan(); return; }
        _loadingStoredPortScan = true;
        var (network, path) = _portStore();
        var mac = _device.Mac ?? Device.Unknown;
        try
        {
            var stored = await Task.Run(() => new KnownDevicesDb(path).LoadPortScan(network, mac, _device.Ip));
            if (!_showGraph || IsFullPortScanRunning) return;
            if (stored is null) { ObserveServiceScan(); return; }
            Services.ReplaceAll(stored.Services);
            _servicesScanned = true;
            SetPortScanResultHeader(stored.SeenAt, stored.Partial);
            Raise(nameof(IsServicesEmptyVisible)); Raise(nameof(ServicesEmptyText));
        }
        catch (Exception ex)
        {
            Core.Log.Warn(Core.LogArea.Db, "Loading the stored port result failed", ex);
            _portScanLoadFailed = true;
            PortScanErrorText = Loc.PortResultLoadFailed;
            Raise(nameof(PortScanErrorText)); Raise(nameof(HasPortScanError));
            if (_showGraph && !IsFullPortScanRunning) ObserveServiceScan();
        }
        finally { _loadingStoredPortScan = false; }
    }
    // Typo-tolerant search over the service list. A live CollectionView
    // (not a manually re-Raised computed list) so it keeps tracking Services'
    // own Add/Clear from ScanServices/StartFullPortScan without extra wiring -
    // only the filter criteria (the search text) needs an explicit Refresh().
    public System.ComponentModel.ICollectionView ServicesView { get; }
    private string _serviceFilterText = "";
    public string ServiceFilterText
    {
        get => _serviceFilterText;
        set
        {
            if (_serviceFilterText == value) return;
            _serviceFilterText = value;
            ServicesView.Refresh();
            Raise(nameof(ServiceFilterText)); Raise(nameof(IsServicesEmptyVisible)); Raise(nameof(ServicesEmptyText));
        }
    }
    private bool _servicesScanned;
    private bool _scanningServices;
    public bool IsScanningServices => _scanningServices;
    /// <summary>True whenever the "nothing to show" placeholder text should be
    /// visible - an empty result, a scan in progress covers itself separately.</summary>
    public bool IsServicesEmptyVisible => !_scanningServices && !ServicesView.Cast<object>().Any();
    /// <summary>Four honest states instead of one generic "no services" text:
    /// offline devices were never even attempted, a device that has not been
    /// scanned yet (still queued behind a full port scan) is not the same as one
    /// that was scanned and genuinely came back empty, and a search that matches
    /// nothing is not the same as an empty device.</summary>
    public string ServicesEmptyText => !_device.IsOnline ? Loc.ServicesOfflineText
        : !_servicesScanned ? Loc.ServicesNotCheckedYet
        : _serviceFilterText.Length > 0 && Services.Count > 0 ? Loc.NoServicesMatchFilter
        : Loc.NoServices;

    private System.Threading.CancellationTokenSource? _serviceScanCts;

    /// <summary>Probe common ports once, the first time the detail view is opened.
    /// Cancelled from the ShowGraph setter if the view closes before it finishes.</summary>
    private void ObserveServiceScan() => BackgroundTask.Observe(ScanServices(), "Service scan");

    private async Task ScanServices()
    {
        if (_servicesScanned || !_device.IsOnline || IsFullPortScanRunning) return;
        _servicesScanned = true;
        _scanningServices = true;
        Raise(nameof(IsScanningServices)); Raise(nameof(IsServicesEmptyVisible)); Raise(nameof(ServicesEmptyText));
        _serviceScanCts = new System.Threading.CancellationTokenSource();
        bool retryAfterCancel = false;
        try
        {
            var found = await PortScanner.ScanAsync(_device.Ip, _serviceScanCts.Token);
            Services.ReplaceAll(found);
            Raise(nameof(Os)); Raise(nameof(OsTip));
            await RunOsFingerprint(_serviceScanCts.Token);
        }
        catch (OperationCanceledException)
        {
            _servicesScanned = false;
            // A close+reopen faster than this cancellation unwinds would see
            // _servicesScanned still true and skip starting a new scan - retry
            // once this call's own state (CTS, scanning flag) is fully reset
            // below, instead of leaving the reopened view stuck empty forever.
            retryAfterCancel = _showGraph;
        }
        catch (Exception ex) { Core.Log.Debug(Core.LogArea.Ports, "Port scan step failed: " + ex.GetType().Name); /* best-effort */ }
        finally
        {
            _serviceScanCts.Dispose();
            _serviceScanCts = null;
            _scanningServices = false;
            Raise(nameof(IsScanningServices)); Raise(nameof(IsServicesEmptyVisible)); Raise(nameof(ServicesEmptyText));
        }
        if (retryAfterCancel) ObserveServiceScan();
    }

    /// <summary>Optional active fingerprint after the quick scan; a missing packet reader leaves the heuristic in place.</summary>
    internal async Task RunOsFingerprint(System.Threading.CancellationToken ct)
    {
        if (!Core.Scanner.Os.OsFingerprinter.Enabled || !Core.Scanner.Os.OsFingerprinter.Available) return;
        var tcp = Services.Where(s => s.Protocol == NetProtocol.Tcp).Select(s => s.Port).ToList();
        try
        {
            var est = await Core.Scanner.Os.OsFingerprinter.EstimateAsync(Core.Scanner.Os.OsFingerprinter.Net, _device.Ip, tcp,
                Core.Scanner.Os.OsFingerprinter.TimeoutMs, ct);
            if (est is null) return;
            _device.OsFingerprint = est;
            Raise(nameof(Os)); Raise(nameof(OsTip));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Core.Log.Debug(Core.LogArea.Ports, "OS fingerprint failed: " + ex.GetType().Name); }
    }

    // ── Optional full 1-65535 TCP port scan - user-triggered only;
    // the default quick scan above (189 known ports) stays the automatic one,
    // since a full sweep reads as a port scan to firewalls/network monitoring. ──
    private System.Threading.CancellationTokenSource? _fullScanCts;
    public bool IsFullPortScanRunning { get; private set; }
    private int _fullScanDone;
    private int _fullScanFoundCount;
    private DateTime _fullScanStartedAt;
    public int FullPortScanTotal => PortScanner.MaxPort;
    public double FullPortScanFraction => (double)_fullScanDone / FullPortScanTotal;
    public string FullPortScanStatusText => string.Format(Loc.ScanningAllPortsFmt, _fullScanDone, FullPortScanTotal);
    public string FullPortScanFoundText => string.Format(Loc.FullPortScanFoundFmt, _fullScanFoundCount);
    public string FullPortScanEtaText { get; private set; } = "";

    // A full 1-65535 sweep completes tens of thousands of probes. Reports are coalesced so the UI
    // thread gets at most one progress update per this interval, never one message per probe.
    private const int FullPortScanUiThrottleMs = 200;

    public async Task StartFullPortScan()
    {
        if (_scanningServices || IsFullPortScanRunning) return;
        IsFullPortScanRunning = true;
        _portScanSeenAt = null;
        PortScanResultHeader = "";
        Raise(nameof(PortScanResultHeader)); Raise(nameof(HasPortScanResult));
        _portScanLoadFailed = false;
        PortScanErrorText = "";
        Raise(nameof(PortScanErrorText)); Raise(nameof(HasPortScanError));
        _fullScanDone = 0;
        _fullScanFoundCount = 0;
        _fullScanStartedAt = DateTime.UtcNow;
        FullPortScanEtaText = "";
        Raise(nameof(IsFullPortScanRunning)); Raise(nameof(FullPortScanFraction)); Raise(nameof(FullPortScanStatusText));
        Raise(nameof(FullPortScanFoundText)); Raise(nameof(FullPortScanEtaText));
        _fullScanCts = new System.Threading.CancellationTokenSource();
        var progress = new CoalescingProgress<int>(done =>
        {
            _fullScanDone = done;
            var elapsed = DateTime.UtcNow - _fullScanStartedAt;
            FullPortScanEtaText = done > 0 && done < FullPortScanTotal && elapsed.TotalSeconds > 0.5
                ? Core.NumberFormat.Eta(TimeSpan.FromSeconds(elapsed.TotalSeconds / done * (FullPortScanTotal - done)))
                : "";
            Raise(nameof(FullPortScanFraction)); Raise(nameof(FullPortScanStatusText)); Raise(nameof(FullPortScanEtaText));
            Raise(nameof(FullPortScanFoundText));
        }, TimeSpan.FromMilliseconds(FullPortScanUiThrottleMs));
        // Called from probe threads: only counts, the progress tick above shows it.
        void OnFound(NetService _) => System.Threading.Interlocked.Increment(ref _fullScanFoundCount);
        try
        {
            bool partial;
            try
            {
                var token = _fullScanCts.Token;
                // Off the UI thread from the first instruction: the engine setup and any synchronous
                // part of a probe must not run inside the click handler.
                var found = await Task.Run(() => _fullScan(_device.Ip, progress, token, OnFound));
                partial = _fullScanCts.IsCancellationRequested;
                // ScanFullRangeAsync never throws on cancellation, it just returns
                // whatever it found so far. Merge instead of replace (a cancelled sweep
                // keeps what it already found) - and merge rather than append so a
                // completed sweep still can't lose a quick-scan TCP entry that, for
                // whatever reason, isn't in this result.
                var merged = Services.Where(s => s.Protocol == NetProtocol.Tcp).ToDictionary(s => s.Port);
                foreach (var s in found) merged[s.Port] = s;   // full-scan entry wins (may carry a banner)
                var udp = Services.Where(s => s.Protocol == NetProtocol.Udp).ToList();
                Services.ReplaceAll(udp.Concat(merged.Values.OrderBy(s => s.Port)));
                if (!partial)
                    _servicesScanned = true;   // only a completed sweep satisfies the quick scan too
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Core.Log.Warn(Core.LogArea.Ports, "Port scan failed", ex);
                _portScanLoadFailed = false;
                PortScanErrorText = Loc.PortCheckFailed;
                Raise(nameof(PortScanErrorText)); Raise(nameof(HasPortScanError));
                return;
            }
            if (_portStore is not null)
            {
                try
                {
                    var (network, path) = _portStore();
                    var mac = _device.Mac ?? Device.Unknown;
                    var seenAt = DateTimeOffset.UtcNow;
                    var shown = Services.ToList();   // the merged TCP and UDP list the user sees
                    var saved = await Task.Run(() => new KnownDevicesDb(path).SavePortScan(network, mac, _device.Ip,
                        shown, seenAt, partial));
                    if (!saved) throw new InvalidOperationException("Database key is unavailable.");
                    SetPortScanResultHeader(seenAt, partial);
                }
                catch (Exception ex)
                {
                    Core.Log.Warn(Core.LogArea.Db, "Saving the port result failed", ex);
                    _portScanLoadFailed = false;
                    PortScanErrorText = Loc.PortResultSaveFailed;
                    Raise(nameof(PortScanErrorText)); Raise(nameof(HasPortScanError));
                }
            }
        }
        finally
        {
            progress.Complete();
            _fullScanCts.Dispose();
            _fullScanCts = null;
            IsFullPortScanRunning = false;
            Raise(nameof(FullPortScanFoundText));
            Raise(nameof(IsFullPortScanRunning)); Raise(nameof(IsScanningServices)); Raise(nameof(IsServicesEmptyVisible)); Raise(nameof(ServicesEmptyText));
        }
    }

    public void CancelFullPortScan() => _fullScanCts?.Cancel();

    /// <summary>Stops the quick service scan and the full sweep of this row, whichever is running.</summary>
    public void CancelPortScans()
    {
        _serviceScanCts?.Cancel();
        _fullScanCts?.Cancel();
    }

    public System.Windows.Media.PointCollection RowGraphPoints { get; private set; } = new();
    public string RowGraphMax { get; private set; } = "";
    public string RowGraphMin { get; private set; } = "";
    public IReadOnlyList<GraphSeries.Tick> RowGraphTicks { get; private set; } = Array.Empty<GraphSeries.Tick>();

    /// <summary>Record one per-second latency sample (from the graph timer). Every
    /// row buffers continuously - not just rows with an open graph - so a graph
    /// shows its full history the instant it's opened instead of filling over time.
    /// The append itself is cheap enough that buffering unopened rows is not a
    /// concern.</summary>
    public void AddRowSample(double? value, int capacity, int tickIntervalSeconds)
    {
        // Only online rows (which have data worth showing) and any open graph
        // buffer - skips the flat offline majority.
        if (!_device.IsOnline && !_showGraph) return;
        lock (_rowLock)
        {
            _rowSamples.Add(value, capacity);
            _rowTick = tickIntervalSeconds;
        }
        // Keep buffering even while a 24h/7d history view is shown (so
        // switching back to "last minute" has a filled buffer), but don't let the
        // live per-second render stomp the historical polyline that's on screen.
        if (_showGraph && SelectedRange == LatencyRange.Minute) BuildRowGraph();
    }

    /// <summary>Which latency view the detail panel currently shows.</summary>
    public LatencyRange SelectedRange { get; private set; } = LatencyRange.Minute;
    public bool IsMinuteRange => SelectedRange == LatencyRange.Minute;
    public bool IsDayRange => SelectedRange == LatencyRange.Day;
    public bool IsWeekRange => SelectedRange == LatencyRange.Week;

    /// <summary>Switches the detail panel's latency view. Switching back to
    /// "last minute" rebuilds immediately from the live sample buffer; switching
    /// to 24h/7d only updates the highlighted button here - the caller (which
    /// owns the DB) loads history and calls SetHistoryGraph once it's ready.</summary>
    public void SetRange(LatencyRange range)
    {
        if (SelectedRange == range) return;
        SelectedRange = range;
        Raise(nameof(SelectedRange)); Raise(nameof(IsMinuteRange)); Raise(nameof(IsDayRange)); Raise(nameof(IsWeekRange));
        if (range == LatencyRange.Minute) BuildRowGraph();
    }

    /// <summary>Renders a bucketed 24h/7d history series through the
    /// same polyline/labels the live per-second graph uses.</summary>
    public void SetHistoryGraph(IReadOnlyList<double?> samples, int secondsPerBucket, int tickIntervalSeconds)
    {
        var r = GraphSeries.Compute(samples, _rowGraphWidth, RowGraphHeight, tickIntervalSeconds, secondsPerBucket);
        RowGraphPoints = r.Points; RowGraphMax = r.MaxText; RowGraphMin = r.MinText; RowGraphTicks = r.Ticks;
        Raise(nameof(RowGraphPoints)); Raise(nameof(RowGraphMax));
        Raise(nameof(RowGraphMin)); Raise(nameof(RowGraphTicks));
    }

    /// <summary>Let the row graph fill the table width (called from the canvas SizeChanged).</summary>
    public void SetRowGraphWidth(double width)
    {
        if (width <= 1) return;
        bool changed;
        lock (_rowLock)
        {
            changed = Math.Abs(width - _rowGraphWidth) >= 1;
            if (changed) _rowGraphWidth = width;
        }
        if (changed && _showGraph) BuildRowGraph();
    }

    private void BuildRowGraph()
    {
        GraphSeries.Result r;
        lock (_rowLock) r = GraphSeries.Compute(_rowSamples, _rowGraphWidth, RowGraphHeight, _rowTick);
        RowGraphPoints = r.Points; RowGraphMax = r.MaxText; RowGraphMin = r.MinText; RowGraphTicks = r.Ticks;
        Raise(nameof(RowGraphPoints)); Raise(nameof(RowGraphMax));
        Raise(nameof(RowGraphMin)); Raise(nameof(RowGraphTicks));
    }

    /// <summary>Push the underlying device's latest values to the UI.</summary>
    public void Refresh()
    {
        RefreshNote();
        Raise(nameof(Status)); Raise(nameof(StatusColor)); Raise(nameof(IsOnline)); Raise(nameof(Hostname)); Raise(nameof(Method));
        Raise(nameof(Os)); Raise(nameof(OsTip));
        Raise(nameof(ServicesEmptyText));
        Raise(nameof(Mac)); Raise(nameof(HasMac)); Raise(nameof(Vendor)); Raise(nameof(IsRogue)); Raise(nameof(GroupId)); Raise(nameof(GroupColor));
        Raise(nameof(DeviceTypeIconKey));
        Raise(nameof(IsMuted)); Raise(nameof(IpColor));
        Raise(nameof(ConflictHint)); Raise(nameof(HasConflictHint));
        Raise(nameof(AvgDisplay)); Raise(nameof(MinDisplay)); Raise(nameof(MaxDisplay));
        Raise(nameof(LastDisplay)); Raise(nameof(ProgressDisplay));
        Raise(nameof(LossDisplay)); Raise(nameof(JitterDisplay));
        Raise(nameof(AvgColor)); Raise(nameof(MinColor)); Raise(nameof(MaxColor)); Raise(nameof(LastColor));
        Raise(nameof(AvgMarkColor)); Raise(nameof(MinMarkColor));
        Raise(nameof(MaxMarkColor)); Raise(nameof(LastMarkColor));
        // Re-raised here (not just from the progress callback) so a language
        // switch while a full port scan is running updates this text too -
        // Refresh() fires on every ping, which keeps running during the scan.
        if (IsFullPortScanRunning) Raise(nameof(FullPortScanStatusText));
        // No BuildRowGraph() here: Refresh() runs on every ping (can be many times
        // a second), but _rowSamples only gains a new point once a second via
        // AddRowSample, which already rebuilds the graph then. Rebuilding here too
        // recomputed the identical polyline on every ping for nothing.
        // ShowGraph's setter still does the one-time build when a graph is opened.

        // The detail view can be opened while the device is offline - ScanServices
        // no-ops then and never retries on its own. Catch the online transition
        // here so the services list fills in once the device answers again,
        // instead of staying empty for the rest of the session.
        if (_showGraph && _device.IsOnline && !_servicesScanned && !_loadingStoredPortScan && !IsFullPortScanRunning) ObserveServiceScan();
    }

    internal void RefreshLanguage()
    {
        Raise(nameof(NoteValidationText));
        if (HasPortScanError)
        {
            PortScanErrorText = _portScanLoadFailed ? Loc.PortResultLoadFailed : Loc.PortResultSaveFailed;
            Raise(nameof(PortScanErrorText));
        }
        if (_portScanSeenAt is { } seenAt) SetPortScanResultHeader(seenAt, _portScanPartial);
    }
}
