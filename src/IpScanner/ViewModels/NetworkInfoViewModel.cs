using IpScanner.Core.Models;

namespace IpScanner.ViewModels;

public sealed class NetworkInfoViewModel : ObservableObject
{
    public NetworkInfoViewModel(int index, NetworkInfo info, string badgeColor,
                                bool primary = true, string name = "")
    {
        Index = index; BadgeColor = badgeColor; _name = name;
        Cidr       = info.Cidr       ?? "—";
        Ip         = info.Ip         ?? "—";
        Mac        = info.Mac        ?? "—";
        Gateway    = info.Gateway    ?? "—";
        SubnetMask = info.SubnetMask ?? "—";
        Dns        = info.DnsServers.Count > 0 ? string.Join(", ", info.DnsServers) : "—";
        Interface  = info.Interface;

        // Rows with no detected value are hidden in the sidebar. "Own IP" only
        // makes sense for the primary (auto-detected) network.
        HasIp        = primary && info.Ip is not null;
        HasMac       = info.Mac is not null;
        HasGateway   = info.Gateway is not null;
        HasMask      = info.SubnetMask is not null;
        HasDns       = info.DnsServers.Count > 0;
        HasInterface = primary && !string.IsNullOrEmpty(info.Interface);
    }

    private readonly string _name;
    public int    Index      { get; }
    public string BadgeColor { get; }
    public string Title      => _name.Length > 0 ? _name : $"{Core.Localization.Loc.Network} {Index}";
    public string Cidr       { get; }
    public string Ip         { get; }
    public string Mac        { get; }
    public string Gateway    { get; }
    public string SubnetMask { get; }
    public string Dns        { get; }
    public string Interface  { get; }

    public bool HasIp        { get; }
    public bool HasMac       { get; }
    public bool HasGateway   { get; }
    public bool HasMask      { get; }
    public bool HasDns       { get; }
    public bool HasInterface { get; }

    private int     _online, _offline;
    private double? _avg;

    public int OnlineCount  { get => _online;  set => SetProperty(ref _online,  value); }
    public int OfflineCount { get => _offline; set => SetProperty(ref _offline, value); }

    public string AvgLatency => Core.NumberFormat.Ms(_avg);
    public string AvgLatencyColor => Core.Palette.Heat(_avg);
    public void SetAvg(double? v) { _avg = v; Raise(nameof(AvgLatency)); Raise(nameof(AvgLatencyColor)); }
}
