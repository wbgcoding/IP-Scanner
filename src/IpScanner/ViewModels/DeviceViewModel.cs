using System.Globalization;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;

namespace IpScanner.ViewModels;

public sealed class DeviceViewModel : ObservableObject
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
    private readonly Device _device;

    public DeviceViewModel(Device device) => _device = device;

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
    }
}
