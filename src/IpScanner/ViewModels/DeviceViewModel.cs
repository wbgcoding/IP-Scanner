using IpScanner.Core.Localization;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;

namespace IpScanner.ViewModels;

public sealed class DeviceViewModel : ObservableObject
{
    private readonly Device _device;

    public DeviceViewModel(Device device, bool isSelf = false, bool isPinned = false)
    {
        _device = device;
        IsSelf = isSelf;
        _isPinned = isPinned;
        IpSortKey = Core.Net.Ipv4.SortKey(device.Ip);
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
        set { _isPinned = value; Raise(nameof(IsPinned)); Raise(nameof(IpColor)); }
    }
    public string IpText => _device.Ip + (IsSelf ? "  ★" : "");
    public string IpColor => IsSelf ? Core.Palette.Mauve : _isPinned ? Core.Palette.Peach : Core.Palette.Text;

    /// <summary>Numeric IPv4 for sorting (0 when unparseable).</summary>
    public long IpSortKey { get; }

    public string Ip => _device.Ip;
    public string Status => _device.IsOnline ? Loc.StatusOnline : Loc.StatusOffline;
    public string StatusColor => _device.IsOnline ? Core.Palette.Green : Core.Palette.Red;
    public bool IsOnline => _device.IsOnline;
    /// <summary>True when this row originates from the known-devices database.</summary>
    public bool IsFromDb => _device.FromDb;
    public string Hostname => _device.Hostname is null or Device.Unknown ? "—" : _device.Hostname;
    public string Mac => _device.Mac is null or Device.Unknown ? "—" : _device.Mac;
    public int GroupId => _device.GroupId;

    // 0/1 = none/unknown -> gray, 2 = gateway group -> fixed brand color,
    // >=3 -> diverse palette (shuffled per scan).
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

    // Raw latency values (for cross-row best/worst comparison).
    public double? AvgRaw => _device.AvgMs;
    public double? MinRaw => _device.MinMs;
    public double? MaxRaw => _device.MaxMs;
    public double? LastRaw => _device.LastFailed ? null : _device.LastMs;

    // Best (lowest) / worst (highest) marker per column: green ▼ best, red ▲ worst.
    private const string Best = "▼", Worst = "▲";
    private static readonly string BestColor = Core.Palette.Green, WorstColor = Core.Palette.Red, NoColor = Core.Palette.Transparent;
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
    private static string MarkColor(int m) => m < 0 ? BestColor : m > 0 ? WorstColor : NoColor;

    /// <summary>Set per-column best(-1)/worst(1)/none(0) markers.</summary>
    public void SetMarks(int avg, int min, int max, int last)
    {
        _avgMark = avg; _minMark = min; _maxMark = max; _lastMark = last;
        Raise(nameof(AvgMark)); Raise(nameof(MinMark)); Raise(nameof(MaxMark)); Raise(nameof(LastMark));
        Raise(nameof(AvgMarkColor)); Raise(nameof(MinMarkColor)); Raise(nameof(MaxMarkColor)); Raise(nameof(LastMarkColor));
    }

    // Latency heatmap: green (fast) -> red (slow).
    public string AvgColor => Core.Palette.Heat(_device.AvgMs);
    public string MinColor => Core.Palette.Heat(_device.MinMs);
    public string MaxColor => Core.Palette.Heat(_device.MaxMs);
    public string LastColor => _device.LastFailed ? Core.Palette.Red : Core.Palette.Heat(_device.LastMs);
    public string ProgressDisplay =>
        _device.TargetPings == ScanConfig.InfinitePingCount
            ? $"{Core.NumberFormat.Short(_device.CurrentPings)}/∞"
            : $"{Core.NumberFormat.Short(_device.CurrentPings)}/{Core.NumberFormat.Short(_device.TargetPings)}";

    // ── Expandable per-row latency graph (per-second history; follows the graph
    //    settings exactly like the sidebar graphs) ──
    public const double RowGraphHeight = 78;
    private const double DefaultRowGraphWidth = 600;
    private double _rowGraphWidth = DefaultRowGraphWidth;   // replaced by the canvas actual width on layout
    private readonly List<double?> _rowSamples = new();
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
            if (value) BuildRowGraph();   // history already buffered → shows instantly
            Raise(nameof(ShowGraph));
        }
    }

    public System.Windows.Media.PointCollection RowGraphPoints { get; private set; } = new();
    public string RowGraphMax { get; private set; } = "";
    public string RowGraphMin { get; private set; } = "";
    public IReadOnlyList<GraphSeries.Tick> RowGraphTicks { get; private set; } = Array.Empty<GraphSeries.Tick>();

    /// <summary>Record one per-second latency sample (from the graph timer). Every
    /// row buffers continuously so an opened graph shows the full window at once.</summary>
    // ponytail: sample all rows, not just open ones — cheap append, and the user
    // wants the graph populated instantly on open rather than filling over time.
    public void AddRowSample(double? value, int capacity, int tickIntervalSeconds)
    {
        lock (_rowLock)
        {
            _rowSamples.Add(value);
            while (_rowSamples.Count > capacity) _rowSamples.RemoveAt(0);
            _rowTick = tickIntervalSeconds;
        }
        if (_showGraph) BuildRowGraph();
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
        Raise(nameof(Status)); Raise(nameof(StatusColor)); Raise(nameof(IsOnline)); Raise(nameof(Hostname));
        Raise(nameof(Mac)); Raise(nameof(GroupId)); Raise(nameof(GroupColor));
        Raise(nameof(AvgDisplay)); Raise(nameof(MinDisplay)); Raise(nameof(MaxDisplay));
        Raise(nameof(LastDisplay)); Raise(nameof(ProgressDisplay));
        Raise(nameof(AvgColor)); Raise(nameof(MinColor)); Raise(nameof(MaxColor)); Raise(nameof(LastColor));
        if (_showGraph) BuildRowGraph();   // keep the open graph live
    }
}
