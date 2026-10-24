namespace IpScanner.Core.Data;

/// <summary>Which latency view a device's detail panel is showing.</summary>
public enum LatencyRange { Minute, Day, Week }

/// <summary>Buckets raw per-scan latency_history rows into a fixed-length average
/// series for GraphSeries.Compute - 24 hourly buckets for Day, 7 daily buckets
/// for Week. Pure function, no SQLite dependency, so it's unit-testable on its
/// own.</summary>
public static class LatencyHistoryAggregator
{
    public sealed record Row(DateTime Ts, double? Avg, double? Min, double? Max);

    public const int DayBucketSeconds = 3600;      // 1 hour per bucket, 24 buckets
    public const int WeekBucketSeconds = 86400;     // 1 day per bucket, 7 buckets
    public const int DayTickSeconds = 21600;        // label every 6h
    public const int WeekTickSeconds = 172800;      // label every 2d

    public static int BucketCount(LatencyRange range) => range == LatencyRange.Day ? 24 : 7;
    public static int BucketSeconds(LatencyRange range) => range == LatencyRange.Day ? DayBucketSeconds : WeekBucketSeconds;
    public static int TickSeconds(LatencyRange range) => range == LatencyRange.Day ? DayTickSeconds : WeekTickSeconds;

    /// <summary>Averages every row's Avg value into its time bucket (buckets
    /// counted back from now, matching how GraphSeries renders "now" at the right
    /// edge). Empty buckets stay null - GraphSeries already skips those points.</summary>
    public static List<double?> Bucket(IReadOnlyList<Row> rows, LatencyRange range, DateTime? now = null)
    {
        int count = BucketCount(range), secondsPerBucket = BucketSeconds(range);
        var n = now ?? DateTime.Now;
        var start = n.AddSeconds(-(double)secondsPerBucket * count);
        var sums = new double[count];
        var counts = new int[count];
        foreach (var r in rows)
        {
            if (r.Avg is not { } v) continue;
            int idx = (int)((r.Ts - start).TotalSeconds / secondsPerBucket);
            if (idx < 0 || idx >= count) continue;
            sums[idx] += v;
            counts[idx]++;
        }
        var result = new List<double?>(count);
        for (int i = 0; i < count; i++)
            result.Add(counts[i] > 0 ? sums[i] / counts[i] : null);
        return result;
    }
}
