namespace IpScanner.ViewModels;

public sealed class InternetHostViewModel : ObservableObject
{
    public InternetHostViewModel(string name, string ip) { Name = name; Ip = ip; }

    public string Name { get; }
    public string Ip   { get; }

    private double? _latency;

    public string LatencyDisplay => _latency is null ? "…" : _latency.Value.ToString("F0") + " ms";
    public void SetLatency(double? ms) { _latency = ms; Raise(nameof(LatencyDisplay)); }
}
