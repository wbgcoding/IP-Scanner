using IpScanner.Core.Models;

namespace IpScanner.ViewModels;

public sealed class NetworkInfoViewModel : ObservableObject
{
    public NetworkInfoViewModel(int index, NetworkInfo info, string badgeColor)
    {
        Index = index; BadgeColor = badgeColor;
        Cidr       = info.Cidr       ?? "—";
        Ip         = info.Ip         ?? "—";
        Mac        = info.Mac        ?? "—";
        Gateway    = info.Gateway    ?? "—";
        SubnetMask = info.SubnetMask ?? "—";
        Dns        = info.DnsServers.Count > 0 ? string.Join(", ", info.DnsServers) : "—";
        Interface  = info.Interface;
    }

    public int    Index      { get; }
    public string BadgeColor { get; }
    public string Title      => $"Netzwerk {Index}";
    public string Cidr       { get; }
    public string Ip         { get; }
    public string Mac        { get; }
    public string Gateway    { get; }
    public string SubnetMask { get; }
    public string Dns        { get; }
    public string Interface  { get; }

    private int     _online, _offline;
    private double? _avg;

    public int OnlineCount  { get => _online;  set => SetProperty(ref _online,  value); }
    public int OfflineCount { get => _offline; set => SetProperty(ref _offline, value); }

    public string AvgLatency => _avg is null ? "—" : _avg.Value.ToString("F1") + " ms";
    public void SetAvg(double? v) { _avg = v; Raise(nameof(AvgLatency)); }
}
