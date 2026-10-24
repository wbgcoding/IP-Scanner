using System.Linq;
using IpScanner.ViewModels;
using Xunit;

namespace IpScanner.Tests.ViewModels;

public class RingBufferTests
{
    [Fact]
    public void Add_WithinCapacity_KeepsInsertionOrder()
    {
        var buf = new RingBuffer<int>();
        buf.Add(1, 3);
        buf.Add(2, 3);
        buf.Add(3, 3);

        Assert.Equal(new[] { 1, 2, 3 }, buf.ToArray());
    }

    [Fact]
    public void Add_PastCapacity_DropsOldestInsteadOfShifting()
    {
        var buf = new RingBuffer<int>();
        for (int i = 1; i <= 5; i++) buf.Add(i, 3);

        // Same end result List.RemoveAt(0) would have produced - only the FIFO
        // mechanics differ (overwrite vs. shift).
        Assert.Equal(new[] { 3, 4, 5 }, buf.ToArray());
        Assert.Equal(3, buf.Count);
    }

    [Fact]
    public void Add_CapacityShrinksThenGrows_TracksTheNewCapacityBothWays()
    {
        var buf = new RingBuffer<int>();
        for (int i = 1; i <= 5; i++) buf.Add(i, 5);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, buf.ToArray());

        buf.Add(6, 2);  // shrink: only the newest 2 survive
        Assert.Equal(new[] { 5, 6 }, buf.ToArray());

        buf.Add(7, 4);  // grow again: still just what's actually been kept
        Assert.Equal(new[] { 5, 6, 7 }, buf.ToArray());
    }

    [Fact]
    public void Add_NullableReferenceValues_RoundTripCorrectly()
    {
        // The real usage is RingBuffer<double?> - null samples (failed pings) must
        // survive the wraparound exactly like any other value.
        var buf = new RingBuffer<double?>();
        buf.Add(1.5, 3);
        buf.Add(null, 3);
        buf.Add(2.5, 3);
        buf.Add(null, 3);   // pushes 1.5 out

        Assert.Equal(new double?[] { null, 2.5, null }, buf.ToArray());
    }
}
