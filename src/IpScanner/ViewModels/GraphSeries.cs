using System.Windows;
using System.Windows.Media;

namespace IpScanner.ViewModels;

/// <summary>Shared latency-graph math: maps a sample series to polyline points,
/// min/max labels and time tick markers.</summary>
public static class GraphSeries
{
    public sealed record Tick(string Label, double X);
    public sealed record Result(PointCollection Points, string MaxText, string MinText, List<Tick> Ticks);

    public const double PadTop = 13, PadBottom = 14;   // line stays clear of both labels
    private const double TickLabelHalfWidth = 14;

    /// <summary>Pick a readable tick step (seconds) for a given visible time span.</summary>
    public static int NiceTickStep(double spanSeconds) => spanSeconds switch
    {
        <= 12 => 2,
        <= 30 => 5,
        <= 90 => 15,
        <= 240 => 30,
        _ => 60,
    };

    /// <param name="secondsPerSample">Real time between two samples (1 for the
    /// per-second graphs; the ping interval for the per-ping row graph).</param>
    public static Result Compute(IReadOnlyList<double?> samples, double width, double height,
                                 int tickIntervalSeconds, double secondsPerSample = 1.0)
    {
        var points = new PointCollection();
        var ticks = new List<Tick>();
        string maxText = "", minText = "";

        // Single pass: count present samples and their min/max (no LINQ allocs).
        int present = 0;
        double min = double.MaxValue, max = double.MinValue;
        foreach (var s in samples)
        {
            if (s is not { } v) continue;
            present++;
            if (v < min) min = v;
            if (v > max) max = v;
        }
        double stepX = samples.Count > 1 ? width / (samples.Count - 1) : 0;

        if (width > 0 && height > 0 && present > 1)
        {
            if (max - min < 0.5) { max += 0.5; min = Math.Max(0, min - 0.5); }   // flat-line guard
            for (int i = 0; i < samples.Count; i++)
            {
                if (samples[i] is not { } v) continue;
                double y = height - PadBottom - (v - min) / (max - min) * (height - PadTop - PadBottom);
                points.Add(new Point(i * stepX, y));
            }
            maxText = Core.NumberFormat.Ms(max);
            minText = Core.NumberFormat.Ms(min);

            // Time ticks counted back from "now" (the right edge).
            if (tickIntervalSeconds > 0 && secondsPerSample > 0)
                for (int secs = tickIntervalSeconds; ; secs += tickIntervalSeconds)
                {
                    double samplesAgo = secs / secondsPerSample;
                    if (samplesAgo > samples.Count - 1) break;
                    double x = (samples.Count - 1 - samplesAgo) * stepX;
                    if (x < TickLabelHalfWidth) break;
                    ticks.Add(new Tick(secs < 60 ? $"{secs}s" : $"{secs / 60}m", x - TickLabelHalfWidth));
                }
        }
        points.Freeze();   // computed on a worker thread, consumed by UI bindings
        return new Result(points, maxText, minText, ticks);
    }
}
