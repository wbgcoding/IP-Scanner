namespace IpScanner.ViewModels;

public sealed class InternetHostViewModel : ObservableObject
{
    public InternetHostViewModel(string name, string ip) { Name = name; Ip = ip; }

    public string Name { get; }
    public string Ip   { get; }

    private double? _last, _min, _max;
    private double _sum;
    private int _count, _pings;

    private double? Avg => _count == 0 ? null : _sum / _count;

    /// <summary>Record one ping result (null = failed).</summary>
    public void RecordPing(double? ms)
    {
        _pings++;
        _last = ms;
        if (ms is { } v)
        {
            _count++;
            _sum += v;
            _min = _min is null ? v : Math.Min(_min.Value, v);
            _max = _max is null ? v : Math.Max(_max.Value, v);
        }
        Raise(nameof(LastDisplay)); Raise(nameof(AvgDisplay));
        Raise(nameof(MinDisplay)); Raise(nameof(MaxDisplay));
        Raise(nameof(LastColor)); Raise(nameof(AvgColor));
        Raise(nameof(MinColor)); Raise(nameof(MaxColor));
    }

    private static string Fmt(double? v) => v is null ? "—" : v.Value.ToString("F0") + " ms";

    public string LastDisplay => _pings == 0 ? "…" : Fmt(_last);
    public string AvgDisplay  => Fmt(Avg);
    public string MinDisplay  => Fmt(_min);
    public string MaxDisplay  => Fmt(_max);

    public string LastColor => Core.Palette.Heat(_last);
    public string AvgColor  => Core.Palette.Heat(Avg);
    public string MinColor  => Core.Palette.Heat(_min);
    public string MaxColor  => Core.Palette.Heat(_max);
}
