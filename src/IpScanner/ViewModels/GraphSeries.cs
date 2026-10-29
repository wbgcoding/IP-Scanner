using System.Windows;
using System.Windows.Media;

namespace IpScanner.ViewModels;

/// <summary>Shared latency-graph math: maps a sample series to polyline points,
/// min/max labels and time tick markers.</summary>
public static class GraphSeries
{
    public sealed record Tick(string Label, double X);
    /// <summary>A run of lost samples, in graph x coordinates.</summary>
    public sealed record Band(double X, double Width);

    /// <param name="Points">Every present sample as one flat series.</param>
    /// <param name="Segments">The present samples split at lost ones (one segment when loss is not marked).</param>
    /// <param name="LossMarks">X of each lost sample (only when loss is marked).</param>
    /// <param name="Bands">Runs of <see cref="BandMinRun"/> or more lost samples.</param>
    /// <param name="Line">The segments as one frozen stroke geometry.</param>
    /// <param name="LossGeometry">The baseline ticks as one frozen stroke geometry.</param>
    /// <param name="BandGeometry">The bands as one frozen fill geometry.</param>
    public sealed record Result(PointCollection Points, string MaxText, string MinText, List<Tick> Ticks,
                                IReadOnlyList<IReadOnlyList<Point>> Segments, IReadOnlyList<double> LossMarks,
                                IReadOnlyList<Band> Bands, Geometry Line, Geometry LossGeometry, Geometry BandGeometry);

    /// <summary>Consecutive lost samples from which the run gets a band behind the line.</summary>
    public const int BandMinRun = 3;
    /// <summary>Height of a baseline loss tick.</summary>
    public const double LossTickHeight = 8;

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
    /// <param name="markLoss">True when a null sample is a lost ping: the line breaks there and the loss is
    /// marked. False when null just means no data (averages, empty history buckets): the line connects.</param>
    public static Result Compute(IReadOnlyList<double?> samples, double width, double height,
                                 int tickIntervalSeconds, double secondsPerSample = 1.0, bool markLoss = false)
    {
        var points = new PointCollection();
        var segments = new List<IReadOnlyList<Point>>();
        var lossMarks = new List<double>();
        var bands = new List<Band>();
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
            List<Point>? segment = null;
            for (int i = 0; i < samples.Count; i++)
            {
                if (samples[i] is not { } v)
                {
                    if (markLoss) segment = null;   // the line breaks at a lost sample
                    continue;
                }
                double y = height - PadBottom - (v - min) / (max - min) * (height - PadTop - PadBottom);
                var pt = new Point(i * stepX, y);
                points.Add(pt);
                if (segment is null) { segment = new List<Point>(); segments.Add(segment); }
                segment.Add(pt);
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
                    string label = secs switch
                    {
                        < 60 => $"{secs}s",
                        < 3600 => $"{secs / 60}m",
                        < 86400 => $"{secs / 3600}h",
                        _ => $"{secs / 86400}d",
                    };
                    ticks.Add(new Tick(label, x - TickLabelHalfWidth));
                }
        }
        if (markLoss && width > 0 && height > 0) FindLosses(samples, stepX, width, lossMarks, bands);
        points.Freeze();   // computed on a worker thread, consumed by UI bindings
        return new Result(points, maxText, minText, ticks, segments, lossMarks, bands,
                          LineGeometry(segments), LossTicks(lossMarks, height), BandRects(bands, height));
    }

    /// <summary>X of every lost sample and the bands behind runs of <see cref="BandMinRun"/> or more.</summary>
    internal static void FindLosses(IReadOnlyList<double?> samples, double stepX, double width,
                                    List<double> marks, List<Band> bands)
    {
        int runStart = -1;
        for (int i = 0; i <= samples.Count; i++)
        {
            bool lost = i < samples.Count && samples[i] is null;
            if (lost)
            {
                marks.Add(i * stepX);
                if (runStart < 0) runStart = i;
                continue;
            }
            if (runStart >= 0 && i - runStart >= BandMinRun)
            {
                double x0 = Math.Max(0, (runStart - 0.5) * stepX), x1 = Math.Min(width, (i - 0.5) * stepX);
                if (x1 > x0) bands.Add(new Band(x0, x1 - x0));
            }
            runStart = -1;
        }
    }

    private static Geometry LineGeometry(IReadOnlyList<IReadOnlyList<Point>> segments)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
            foreach (var seg in segments)
            {
                if (seg.Count == 1)   // an isolated sample between losses still shows as a dot
                {
                    ctx.BeginFigure(new Point(seg[0].X - 0.6, seg[0].Y), false, false);
                    ctx.LineTo(new Point(seg[0].X + 0.6, seg[0].Y), true, false);
                    continue;
                }
                ctx.BeginFigure(seg[0], false, false);
                for (int i = 1; i < seg.Count; i++) ctx.LineTo(seg[i], true, true);
            }
        g.Freeze();
        return g;
    }

    private static Geometry LossTicks(IReadOnlyList<double> marks, double height)
    {
        var g = new StreamGeometry();
        double baseline = height - PadBottom;
        using (var ctx = g.Open())
            foreach (var x in marks)
            {
                ctx.BeginFigure(new Point(x, baseline - LossTickHeight), false, false);
                ctx.LineTo(new Point(x, baseline), true, false);
            }
        g.Freeze();
        return g;
    }

    private static Geometry BandRects(IReadOnlyList<Band> bands, double height)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
            foreach (var b in bands)
            {
                ctx.BeginFigure(new Point(b.X, 0), true, true);
                ctx.LineTo(new Point(b.X + b.Width, 0), false, false);
                ctx.LineTo(new Point(b.X + b.Width, height), false, false);
                ctx.LineTo(new Point(b.X, height), false, false);
            }
        g.Freeze();
        return g;
    }
}
