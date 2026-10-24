using System;
using System.Collections.Generic;
using IpScanner.Core.Data;
using Xunit;

namespace IpScanner.Tests.Core;

public class LatencyHistoryAggregatorTests
{
    [Fact]
    public void Bucket_Day_AveragesTwoRowsInSameHour()
    {
        var now = new DateTime(2026, 6, 1, 12, 0, 0);
        var rows = new List<LatencyHistoryAggregator.Row>
        {
            new(now.AddMinutes(-10), 10, 8, 12),
            new(now.AddMinutes(-5), 20, 18, 22),
        };
        var samples = LatencyHistoryAggregator.Bucket(rows, LatencyRange.Day, now);

        Assert.Equal(24, samples.Count);
        Assert.Equal(15, samples[^1]);   // last bucket = current hour, avg of 10 and 20
    }

    [Fact]
    public void Bucket_Day_RowOutsideWindow_Excluded()
    {
        var now = new DateTime(2026, 6, 1, 12, 0, 0);
        var rows = new List<LatencyHistoryAggregator.Row> { new(now.AddDays(-2), 999, 999, 999) };
        var samples = LatencyHistoryAggregator.Bucket(rows, LatencyRange.Day, now);

        Assert.All(samples, s => Assert.Null(s));
    }

    [Fact]
    public void Bucket_Week_PlacesRowInCorrectDayBucket()
    {
        // start = now - 7d = 2026-05-31 00:00; row at 2026-06-04 01:00 is
        // 4 days 1 hour after start → bucket index 4 (0-based, oldest first).
        var now = new DateTime(2026, 6, 7, 0, 0, 0);
        var rows = new List<LatencyHistoryAggregator.Row> { new(now.AddDays(-3).AddHours(1), 42, 40, 44) };
        var samples = LatencyHistoryAggregator.Bucket(rows, LatencyRange.Week, now);

        Assert.Equal(7, samples.Count);
        Assert.Equal(42, samples[4]);
        Assert.All(new[] { 0, 1, 2, 3, 5, 6 }, i => Assert.Null(samples[i]));
    }

    [Fact]
    public void Bucket_EmptyRows_AllBucketsNull()
    {
        var samples = LatencyHistoryAggregator.Bucket(new List<LatencyHistoryAggregator.Row>(), LatencyRange.Week);
        Assert.Equal(7, samples.Count);
        Assert.All(samples, Assert.Null);
    }

    [Theory]
    [InlineData(LatencyRange.Day, 24, LatencyHistoryAggregator.DayBucketSeconds)]
    [InlineData(LatencyRange.Week, 7, LatencyHistoryAggregator.WeekBucketSeconds)]
    public void BucketCount_AndSeconds_MatchRange(LatencyRange range, int count, int seconds)
    {
        Assert.Equal(count, LatencyHistoryAggregator.BucketCount(range));
        Assert.Equal(seconds, LatencyHistoryAggregator.BucketSeconds(range));
    }
}
