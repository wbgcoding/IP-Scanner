using System.Globalization;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;

namespace IpScanner.ViewModels;

public sealed class DeviceViewModel : ObservableObject
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
    private readonly Device _device;

    public DeviceViewModel(Device device, bool isSelf = false, bool isPinned = false)
    {
        _device = device;
        IsSelf = isSelf;
        IsPinned = isPinned;
        IpSortKey = ToSortKey(device.Ip);
    }

    /// <summary>True for this machine's own row.</summary>
    public bool IsSelf { get; }
    /// <summary>True for a pinned IP (kept at the top of the list).</summary>
    public bool IsPinned { get; }
    public string IpDisplay => (IsPinned ? "📌 " : "") + _device.Ip + (IsSelf ? "  ★" : "");
    public string IpColor => IsSelf ? Core.Palette.Mauve : IsPinned ? Core.Palette.Peach : Core.Palette.Text;

    /// <summary>Numeric IPv4 for sorting (0 when unparseable).</summary>
    public long IpSortKey { get; }

    private static long ToSortKey(string ip)
    {
        var p = ip.Split('.');
        if (p.Length != 4) return 0;
        long key = 0;
        foreach (var part in p)
        {
            if (!int.TryParse(part, out var n)) return 0;
            key = key * 256 + n;
        }
        return key;
    }

    public string Ip => _device.Ip;
    public string Status => _device.IsOnline ? "ONLINE" : "OFFLINE";
    public bool IsOnline => _device.IsOnline;
    public string Hostname => _device.Hostname is null or Device.Unknown ? "—" : _device.Hostname;
    public string Mac => _device.Mac is null or Device.Unknown ? "—" : _device.Mac;
    public int GroupId => _device.GroupId;

    // 0/1 = none/unknown -> gray, 2 = gateway -> green, >=3 -> diverse palette.
    public string GroupColor => _device.GroupId switch
    {
        <= 1 => Core.Palette.Surface2,
        2 => Core.Palette.Green,
        var g => GroupColorPalette.ColorForIndex(g - 3),
    };

    public string AvgDisplay => Fmt(_device.AvgMs);
    public string MinDisplay => Fmt(_device.MinMs);
    public string MaxDisplay => Fmt(_device.MaxMs);
    public string LastDisplay => Fmt(_device.LastMs);

    // Raw latency values (for cross-row best/worst comparison).
    public double? AvgRaw => _device.AvgMs;
    public double? MinRaw => _device.MinMs;
    public double? MaxRaw => _device.MaxMs;
    public double? LastRaw => _device.LastMs;

    // Best (lowest) / worst (highest) marker per column: green ● best, red ● worst.
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

    // Latency heatmap: green (fast) -> red (slow). Mirrors Python thresholds.
    public string AvgColor => HeatColor(_device.AvgMs);
    public string MinColor => HeatColor(_device.MinMs);
    public string MaxColor => HeatColor(_device.MaxMs);
    public string LastColor => HeatColor(_device.LastMs);

    private static string HeatColor(double? ms) => ms switch
    {
        null => Core.Palette.MidGray,
        <= 50 => Core.Palette.Green,    // excellent
        <= 100 => Core.Palette.Lime,    // good
        <= 200 => Core.Palette.Yellow,  // okay
        <= 400 => Core.Palette.Peach,   // bad
        _ => Core.Palette.Red,          // very bad
    };
    public string ProgressDisplay =>
        _device.TargetPings == ScanConfig.InfinitePingCount
            ? $"{Core.NumberFormat.Short(_device.CurrentPings)}/∞"
            : $"{Core.NumberFormat.Short(_device.CurrentPings)}/{Core.NumberFormat.Short(_device.TargetPings)}";

    private static string Fmt(double? v) => v is null ? "—" : v.Value.ToString("F1", De) + " ms";

    /// <summary>Push the underlying device's latest values to the UI.</summary>
    public void Refresh()
    {
        Raise(nameof(Status)); Raise(nameof(IsOnline)); Raise(nameof(Hostname));
        Raise(nameof(Mac)); Raise(nameof(GroupId)); Raise(nameof(GroupColor));
        Raise(nameof(AvgDisplay)); Raise(nameof(MinDisplay)); Raise(nameof(MaxDisplay));
        Raise(nameof(LastDisplay)); Raise(nameof(ProgressDisplay));
        Raise(nameof(AvgColor)); Raise(nameof(MinColor)); Raise(nameof(MaxColor)); Raise(nameof(LastColor));
    }
}
