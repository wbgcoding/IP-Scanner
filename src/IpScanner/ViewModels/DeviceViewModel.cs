using System.Globalization;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;

namespace IpScanner.ViewModels;

public sealed class DeviceViewModel : ObservableObject
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
    private readonly Device _device;

    public DeviceViewModel(Device device, bool isSelf = false)
    {
        _device = device;
        IsSelf = isSelf;
        IpSortKey = ToSortKey(device.Ip);
    }

    /// <summary>True for this machine's own row.</summary>
    public bool IsSelf { get; }
    public string IpDisplay => IsSelf ? _device.Ip + "  ★" : _device.Ip;
    public string IpColor => IsSelf ? "#CBA6F7" : "#CDD6F4";   // mauve for self

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
    public string Hostname => _device.Hostname is null or "Unknown" ? "—" : _device.Hostname;
    public string Mac => _device.Mac is null or "Unknown" ? "—" : _device.Mac;
    public int GroupId => _device.GroupId;

    // 0/1 = none/unknown -> gray, 2 = gateway -> green, >=3 -> diverse palette.
    public string GroupColor => _device.GroupId switch
    {
        <= 1 => "#45475A",
        2 => "#A6E3A1",
        var g => GroupColorPalette.ColorForIndex(g - 3),
    };

    public string AvgDisplay => Fmt(_device.AvgMs);
    public string MinDisplay => Fmt(_device.MinMs);
    public string MaxDisplay => Fmt(_device.MaxMs);
    public string LastDisplay => Fmt(_device.LastMs);

    // Latency heatmap: green (fast) -> red (slow). Mirrors Python thresholds.
    public string AvgColor => HeatColor(_device.AvgMs);
    public string MinColor => HeatColor(_device.MinMs);
    public string MaxColor => HeatColor(_device.MaxMs);
    public string LastColor => HeatColor(_device.LastMs);

    private static string HeatColor(double? ms) => ms switch
    {
        null => "#585B70",   // no data -> muted
        <= 50 => "#A6E3A1",  // excellent (green)
        <= 100 => "#C9E88A", // good (lime)
        <= 200 => "#F9E2AF", // okay (yellow)
        <= 400 => "#FAB387", // bad (orange)
        _ => "#F38BA8",      // very bad (red)
    };
    public string ProgressDisplay =>
        _device.TargetPings == ScanConfig.InfinitePingCount
            ? $"{_device.CurrentPings}/∞"
            : $"{_device.CurrentPings}/{_device.TargetPings}";

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
