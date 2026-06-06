using System.Windows;
using System.Windows.Media;

namespace IpScanner.ViewModels;

/// <summary>Shared latency-graph math: maps a sample series (one per second)
/// to polyline points, min/max labels and time tick markers.</summary>
public static class GraphSeries
{
    public sealed record Tick(string Label, double X);
    public sealed record Result(PointCollection Points, string MaxText, string MinText, List<Tick> Ticks);

    public const double PadTop = 4, PadBottom = 14;
    private const double TickLabelHalfWidth = 14;

    public static Result Compute(IReadOnlyList<double?> samples, double width, double height,
                                 int tickIntervalSeconds)
    {
        var points = new PointCollection();
        var ticks = new List<Tick>();
        string maxText = "", minText = "";
        var present = samples.Where(v => v is not null).Select(v => v!.Value).ToList();
        double stepX = samples.Count > 1 ? width / (samples.Count - 1) : 0;

        if (width > 0 && height > 0 && present.Count > 1)
        {
            double min = present.Min(), max = present.Max();
            if (max - min < 0.5) { max += 0.5; min = Math.Max(0, min - 0.5); }   // flat-line guard
            for (int i = 0; i < samples.Count; i++)
            {
                if (samples[i] is not { } v) continue;
                double y = height - PadBottom - (v - min) / (max - min) * (height - PadTop - PadBottom);
                points.Add(new Point(i * stepX, y));
            }
            maxText = Core.NumberFormat.Ms(max);
            minText = Core.NumberFormat.Ms(min);

            for (int secs = tickIntervalSeconds; secs < samples.Count; secs += tickIntervalSeconds)
            {
                double x = (samples.Count - 1 - secs) * stepX;
                if (x < TickLabelHalfWidth) continue;
                ticks.Add(new Tick(secs < 60 ? $"{secs}s" : $"{secs / 60}m", x - TickLabelHalfWidth));
            }
        }
        points.Freeze();   // computed on a worker thread, consumed by UI bindings
        return new Result(points, maxText, minText, ticks);
    }
}
