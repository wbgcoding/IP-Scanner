namespace IpScanner.ViewModels;

public sealed class InternetHostViewModel : ObservableObject
{
    public InternetHostViewModel(string name, string ip) { Name = name; Ip = ip; }

    public string Name { get; }
    public string Ip   { get; }

    private double? _min, _max, _last;
    private double _sum;
    private int _count;

    private double? Avg => _count == 0 ? null : _sum / _count;

    /// <summary>Record one ping result (null = failed).</summary>
    public void RecordPing(double? ms)
    {
        _last = ms;
        if (ms is { } v)
        {
            _count++;
            _sum += v;
            _min = _min is null ? v : Math.Min(_min.Value, v);
            _max = _max is null ? v : Math.Max(_max.Value, v);
        }
        Raise(nameof(AvgDisplay)); Raise(nameof(MinDisplay)); Raise(nameof(MaxDisplay)); Raise(nameof(LastDisplay));
        Raise(nameof(AvgColor)); Raise(nameof(MinColor)); Raise(nameof(MaxColor)); Raise(nameof(LastColor));
    }

    public string AvgDisplay => Core.NumberFormat.Ms(Avg);
    public string MinDisplay => Core.NumberFormat.Ms(_min);
    public string MaxDisplay => Core.NumberFormat.Ms(_max);
    public string LastDisplay => Core.NumberFormat.Ms(_last);

    public string AvgColor => Core.Palette.Heat(Avg);
    public string MinColor => Core.Palette.Heat(_min);
    public string MaxColor => Core.Palette.Heat(_max);
    public string LastColor => Core.Palette.Heat(_last);
}
