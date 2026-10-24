using IpScanner.Core.Models;

namespace IpScanner.ViewModels;

public sealed class NetworkInfoViewModel : ObservableObject, IDeletable
{
    public NetworkInfoViewModel(int index, NetworkInfo info, string fallbackColor,
                                bool primary = true, string name = "", string? sourceTarget = null,
                                string rawColor = "", bool pending = false, string prefix = "")
    {
        Index = index; _fallbackColor = fallbackColor; _rawColor = rawColor;
        _badgeColor = rawColor.Length > 0 ? rawColor : fallbackColor;
        _name = name;
        SourceTarget = sourceTarget;
        ApplyInfo(prefix, info, primary, pending);
    }

    private string _name;
    private string _rawColor;
    private readonly string _fallbackColor;
    private string _badgeColor;
    public int    Index      { get; private set; }
    /// <summary>The configured display name, empty when none was set (raw, not the "Network N" fallback).</summary>
    public string RawName  => _name;
    /// <summary>The configured custom color, empty when none was set (raw, not the palette fallback).</summary>
    public string RawColor => _rawColor;
    private bool _isPrimary;
    /// <summary>True for the auto-detected/header-driven network (never deletable;
    /// only its display name/color can be assigned, not its subnet).</summary>
    public bool   IsPrimary  { get => _isPrimary; private set { if (SetProperty(ref _isPrimary, value)) Raise(nameof(CanDelete)); } }
    public bool   CanDelete  => !IsPrimary;
    private bool _isPending;
    /// <summary>True for a placeholder card synthesized for a configured subnet
    /// that has no scan result yet - shows a "Scan now" prompt instead
    /// of detail rows. Cleared by <see cref="ApplyInfo"/> once a real scan
    /// result comes in for this same instance.</summary>
    public bool   IsPending  { get => _isPending; private set { if (SetProperty(ref _isPending, value)) Raise(nameof(IsNotPending)); } }
    public bool   IsNotPending => !IsPending;
    /// <summary>The exact Config.Subnets entry this card was built from, or null
    /// when the primary network has no explicit override yet.</summary>
    public string? SourceTarget { get; private set; }
    /// <summary>The three-octet subnet key (e.g. "192.168.2") this card represents -
    /// the identity used to match it across rescans instead of replacing the
    /// instance.</summary>
    public string Prefix { get; private set; } = "";
    public string BadgeColor { get => _badgeColor; private set { if (_badgeColor != value) { _badgeColor = value; Raise(nameof(BadgeColor)); Raise(nameof(BadgeForeground)); } } }
    public string BadgeForeground => Core.Palette.ContrastOn(_badgeColor);
    public string Title      => _name.Length > 0 ? _name : $"{Core.Localization.Loc.Network} {Index}";
    private string _cidr = "—", _ip = "—", _mac = "—", _gateway = "—", _subnetMask = "—", _dns = "—", _dnsShort = "—", _interface = "";
    public string Cidr       { get => _cidr;       private set => SetProperty(ref _cidr, value); }
    public string Ip         { get => _ip;         private set => SetProperty(ref _ip, value); }
    public string Mac        { get => _mac;        private set => SetProperty(ref _mac, value); }
    public string Gateway    { get => _gateway;    private set => SetProperty(ref _gateway, value); }
    public string SubnetMask { get => _subnetMask; private set => SetProperty(ref _subnetMask, value); }
    public string Dns        { get => _dns;        private set => SetProperty(ref _dns, value); }
    /// <summary>First DNS server plus a "+N" suffix for the rest, so the sidebar
    /// row never wraps to a ragged multi-line block. The full list
    /// stays available via the Dns tooltip.</summary>
    public string DnsShort   { get => _dnsShort;   private set => SetProperty(ref _dnsShort, value); }
    public string Interface  { get => _interface;  private set => SetProperty(ref _interface, value); }

    /// <summary>One IPv6 address of the card's interface with its kind label (global / ULA / link-local).</summary>
    public sealed record Ipv6Line(string Kind, string Address, string Tip);

    /// <summary>Most IPv6 lines a card shows; the rest collapse into a "+N" line.</summary>
    internal const int MaxIpv6Lines = 4;

    /// <summary>Global first, then unique-local, then link-local; zones are dropped for display.</summary>
    internal static List<Ipv6Line> BuildIpv6Lines(IEnumerable<OwnIpv6Address> addresses)
    {
        static int Rank(Core.Net.Ipv6Kind k) => k switch { Core.Net.Ipv6Kind.Global => 0, Core.Net.Ipv6Kind.UniqueLocal => 1, Core.Net.Ipv6Kind.LinkLocal => 2, _ => 3 };
        var ordered = addresses.OrderBy(a => Rank(a.Address.Kind)).ToList();
        var lines = ordered.Take(MaxIpv6Lines).Select(a =>
        {
            var (kind, tip) = a.Address.Kind switch
            {
                Core.Net.Ipv6Kind.Global => (Core.Localization.Loc.Ipv6KindGlobal, Core.Localization.Loc.TipIpv6KindGlobal),
                Core.Net.Ipv6Kind.UniqueLocal => (Core.Localization.Loc.Ipv6KindUla, Core.Localization.Loc.TipIpv6KindUla),
                Core.Net.Ipv6Kind.LinkLocal => (Core.Localization.Loc.Ipv6KindLinkLocal, Core.Localization.Loc.TipIpv6KindLinkLocal),
                _ => ("IPv6", ""),
            };
            return new Ipv6Line(kind, a.Address.WithZone(null).ToString(), tip);
        }).ToList();
        if (ordered.Count > MaxIpv6Lines) lines.Add(new Ipv6Line("+" + (ordered.Count - MaxIpv6Lines), "", ""));
        return lines;
    }

    private List<Ipv6Line> _ipv6Lines = new();
    public IReadOnlyList<Ipv6Line> Ipv6Lines { get => _ipv6Lines; private set { _ipv6Lines = value.ToList(); Raise(nameof(Ipv6Lines)); } }
    private bool _hasIpv6;
    public bool HasIpv6 { get => _hasIpv6; private set => SetProperty(ref _hasIpv6, value); }

    private List<OwnIpv6Address> _ipv6Source = new();

    private bool _hasIp, _hasMac, _hasGateway, _hasMask, _hasDns, _hasInterface;
    public bool HasIp        { get => _hasIp;        private set => SetProperty(ref _hasIp, value); }
    public bool HasMac       { get => _hasMac;       private set => SetProperty(ref _hasMac, value); }
    public bool HasGateway   { get => _hasGateway;   private set => SetProperty(ref _hasGateway, value); }
    public bool HasMask      { get => _hasMask;      private set => SetProperty(ref _hasMask, value); }
    public bool HasDns       { get => _hasDns;       private set => SetProperty(ref _hasDns, value); }
    public bool HasInterface { get => _hasInterface; private set => SetProperty(ref _hasInterface, value); }

    /// <summary>Re-applies fresh detection data to this SAME instance instead of
    /// the caller replacing it in the Networks collection - keeps this card's
    /// graph history, scroll position and any armed delete state alive across
    /// a rescan instead of resetting on every single scan.</summary>
    public void ApplyInfo(string prefix, NetworkInfo info, bool primary, bool pending = false)
    {
        Prefix = prefix;
        IsPrimary = primary;
        IsPending = pending;
        Cidr       = info.Cidr       ?? "—";
        Ip         = info.Ip         ?? "—";
        Mac        = info.Mac        ?? "—";
        Gateway    = info.Gateway    ?? "—";
        SubnetMask = info.SubnetMask ?? "—";
        Dns        = info.DnsServers.Count > 0 ? string.Join(", ", info.DnsServers) : "—";
        DnsShort   = info.DnsServers.Count switch
        {
            0 => "—",
            1 => info.DnsServers[0],
            _ => $"{info.DnsServers[0]} +{info.DnsServers.Count - 1}",
        };
        Interface  = info.Interface ?? "";

        // Rows with no detected value are hidden in the sidebar. "Own IP" only
        // makes sense for the primary (auto-detected) network.
        HasIp        = primary && info.Ip is not null;
        HasMac       = info.Mac is not null;
        HasGateway   = info.Gateway is not null;
        HasMask      = info.SubnetMask is not null;
        HasDns       = info.DnsServers.Count > 0;
        HasInterface = primary && !string.IsNullOrEmpty(info.Interface);
        _ipv6Source = primary ? info.Ipv6.ToList() : new();
        Ipv6Lines = BuildIpv6Lines(_ipv6Source);
        HasIpv6 = _ipv6Source.Count > 0;
    }

    /// <summary>The smallest "Network N" ordinal not used by any current card, so a card keeps
    /// its default name for as long as it exists and a new card never steals another's number.</summary>
    internal static int NextFreeIndex(IEnumerable<int> used)
    {
        var taken = new HashSet<int>(used);
        int n = 1;
        while (taken.Contains(n)) n++;
        return n;
    }

    private bool _deleteArmed;
    public bool IsDeleteArmed
    {
        get => _deleteArmed;
        set { if (_deleteArmed != value) { _deleteArmed = value; Raise(nameof(IsDeleteArmed)); } }
    }

    /// <summary>Live-apply an edited display name/color (and its source key)
    /// without rebuilding the card - keeps online/offline counts and graph history.</summary>
    public void ApplyMeta(string name, string color, string? sourceTarget)
    {
        if (_name != name) { _name = name; Raise(nameof(Title)); }
        _rawColor = color;
        BadgeColor = color.Length > 0 ? color : _fallbackColor;
        SourceTarget = sourceTarget;
    }

    private int     _online, _offline;
    private double? _avg;

    public int OnlineCount  { get => _online;  set { if (SetProperty(ref _online,  value)) Raise(nameof(OnlineOfflineText)); } }
    public int OfflineCount { get => _offline; set { if (SetProperty(ref _offline, value)) Raise(nameof(OnlineOfflineText)); } }
    public string OnlineOfflineText => $"{_online}/{_offline}";

    public string AvgLatency => Core.NumberFormat.Ms(_avg);
    public string AvgLatencyColor => Core.Palette.Heat(_avg);
    public void SetAvg(double? v) { _avg = v; Raise(nameof(AvgLatency)); Raise(nameof(AvgLatencyColor)); }
    public void RefreshTheme() => Raise(nameof(AvgLatencyColor));

    /// <summary>Re-raises the "Network N" fallback title and the IPv6 kind labels after a language switch.</summary>
    public void RefreshLanguage()
    {
        if (_name.Length == 0) Raise(nameof(Title));
        if (_ipv6Source.Count > 0) Ipv6Lines = BuildIpv6Lines(_ipv6Source);   // kind labels are localized
    }

    // ── Average-latency history graph (rendered in the sidebar item) ──
    public const double GraphHeight = 38;
    private const double DefaultGraphWidth = 272;
    private double _graphWidth = DefaultGraphWidth;   // replaced by the canvas actual width on layout
    private const int ZoomWindowSeconds = 30;
    private readonly List<double?> _graphSamples = new();
    private int _lastTick = 60;
    // AddGraphSample runs on the graph-timer worker thread; Render can also run
    // synchronously on the UI thread (1m/30s zoom click). Both touch
    // _graphSamples/_lastTick, so both must go through this lock - mirrors
    // DeviceViewModel's _rowLock pattern for the per-row graph.
    private readonly object _graphLock = new();

    private bool _zoomed;
    /// <summary>False = the full configured window ("1m" ticks); true = a short
    /// recent window with second-level ticks.</summary>
    public bool IsZoomed
    {
        get => _zoomed;
        set { if (_zoomed != value) { _zoomed = value; Raise(nameof(IsZoomed)); Render(); } }
    }

    public System.Windows.Media.PointCollection GraphPoints { get; private set; } = new();
    public string GraphMaxText { get; private set; } = "";
    public string GraphMinText { get; private set; } = "";
    public IReadOnlyList<GraphSeries.Tick> GraphTicks { get; private set; } = Array.Empty<GraphSeries.Tick>();

    /// <summary>Record the current average (one sample per second) and rebuild the plot.</summary>
    public void AddGraphSample(int capacity, int tickIntervalSeconds)
    {
        lock (_graphLock)
        {
            _graphSamples.Add(_avg);
            while (_graphSamples.Count > capacity) _graphSamples.RemoveAt(0);
            _lastTick = tickIntervalSeconds;
        }
        Render();
    }

    private void Render()
    {
        List<double?> samples;
        int tick;
        lock (_graphLock)
        {
            tick = _lastTick;
            if (_zoomed)
            {
                int n = Math.Min(_graphSamples.Count, ZoomWindowSeconds);
                samples = _graphSamples.GetRange(_graphSamples.Count - n, n);
                tick = GraphSeries.NiceTickStep(ZoomWindowSeconds);
            }
            else
            {
                samples = new List<double?>(_graphSamples);
            }
        }
        var r = GraphSeries.Compute(samples, _graphWidth, GraphHeight, tick);
        GraphPoints = r.Points;
        GraphMaxText = r.MaxText;
        GraphMinText = r.MinText;
        GraphTicks = r.Ticks;
        Raise(nameof(GraphPoints)); Raise(nameof(GraphMaxText));
        Raise(nameof(GraphMinText)); Raise(nameof(GraphTicks));
    }

    /// <summary>Let the network graph fill the sidebar width (called from the
    /// canvas SizeChanged) instead of a bare 272px literal duplicated in XAML.</summary>
    public void SetGraphWidth(double width)
    {
        if (width <= 1) return;
        bool changed;
        lock (_graphLock)
        {
            changed = Math.Abs(width - _graphWidth) >= 1;
            if (changed) _graphWidth = width;
        }
        if (changed) Render();
    }
}
