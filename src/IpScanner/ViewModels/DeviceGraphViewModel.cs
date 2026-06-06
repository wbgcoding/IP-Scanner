using System.Windows.Media;

namespace IpScanner.ViewModels;

/// <summary>Selectable graph source: overall average (Ip = null) or one device.</summary>
public sealed class GraphSource : ObservableObject
{
    public GraphSource(string label, string? ip) { _label = label; Ip = ip; }

    public string? Ip { get; }

    private string _label;
    public string Label { get => _label; set { if (_label != value) { _label = value; Raise(nameof(Label)); } } }

    public override string ToString() => Label;
}

/// <summary>One latency-history graph in the sidebar with its own source.</summary>
public sealed class DeviceGraphViewModel : ObservableObject
{
    public const double Width = 272, Height = 64;

    private readonly List<double?> _samples = new();

    private GraphSource? _source;
    public GraphSource? SelectedSource
    {
        get => _source;
        set
        {
            if (ReferenceEquals(_source, value)) return;
            _source = value;
            _samples.Clear();   // fresh line for the new source
            Raise(nameof(SelectedSource));
        }
    }

    public PointCollection GraphPoints { get; private set; } = new();
    public string GraphMaxText { get; private set; } = "";
    public string GraphMinText { get; private set; } = "";
    public IReadOnlyList<GraphSeries.Tick> GraphTicks { get; private set; } = Array.Empty<GraphSeries.Tick>();
    public string ValueText { get; private set; } = "—";
    public string ValueColor { get; private set; } = Core.Palette.MidGray;

    public void AddSample(double? value, int capacity, int tickIntervalSeconds)
    {
        _samples.Add(value);
        while (_samples.Count > capacity) _samples.RemoveAt(0);
        var r = GraphSeries.Compute(_samples, Width, Height, tickIntervalSeconds);
        GraphPoints = r.Points;
        GraphMaxText = r.MaxText;
        GraphMinText = r.MinText;
        GraphTicks = r.Ticks;
        ValueText = Core.NumberFormat.Ms(value);
        ValueColor = Core.Palette.Heat(value);
        Raise(nameof(GraphPoints)); Raise(nameof(GraphMaxText)); Raise(nameof(GraphMinText));
        Raise(nameof(GraphTicks)); Raise(nameof(ValueText)); Raise(nameof(ValueColor));
    }
}
