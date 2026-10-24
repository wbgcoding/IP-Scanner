using IpScanner.Core;
using IpScanner.ViewModels;
using Xunit;

namespace IpScanner.Tests.ViewModels;

public class GraphSeriesTests
{
    [Theory]
    [InlineData(10, 2)]
    [InlineData(12, 2)]
    [InlineData(13, 5)]
    [InlineData(30, 5)]
    [InlineData(31, 15)]
    [InlineData(60, 15)]
    [InlineData(90, 15)]
    [InlineData(91, 30)]
    [InlineData(120, 30)]
    [InlineData(240, 30)]
    [InlineData(241, 60)]
    [InlineData(3600, 60)]
    public void NiceTickStep_PicksExpectedStepAtEachBoundary(double span, int expected)
        => Assert.Equal(expected, GraphSeries.NiceTickStep(span));

    [Fact]
    public void Compute_FewerThanTwoPresentSamples_ReturnsEmptyResult()
    {
        // A single data point (or all-null) can't draw a line - must not throw
        // and must return an empty, harmless result instead of dividing by zero.
        var r = GraphSeries.Compute(new double?[] { 5.0, null, null }, 100, 40, 30);
        Assert.Empty(r.Points);
        Assert.Equal("", r.MaxText);
        Assert.Equal("", r.MinText);
        Assert.Empty(r.Ticks);
    }

    [Fact]
    public void Compute_ZeroWidthOrHeight_ReturnsEmptyResult()
    {
        var r = GraphSeries.Compute(new double?[] { 1.0, 2.0, 3.0 }, 0, 40, 30);
        Assert.Empty(r.Points);
    }

    [Fact]
    public void Compute_NormalSeries_ProducesOnePointPerSample_AndCorrectMinMax()
    {
        var samples = new double?[] { 1.0, 5.0, null, 3.0 };
        var r = GraphSeries.Compute(samples, 300, 60, 30);

        // null samples are skipped when plotting, not zero-filled. Compare
        // against NumberFormat.Ms itself rather than a hardcoded literal -
        // its decimal separator follows the current culture.
        Assert.Equal(3, r.Points.Count);
        Assert.Equal(NumberFormat.Ms(5.0), r.MaxText);
        Assert.Equal(NumberFormat.Ms(1.0), r.MinText);
    }

    [Fact]
    public void Compute_FlatLine_WidensRangeInsteadOfDividingByZero()
    {
        // All-equal samples would make (max - min) == 0 - the flat-line guard
        // must widen the range instead of producing NaN/Infinity coordinates.
        var samples = new double?[] { 4.0, 4.0, 4.0 };
        var r = GraphSeries.Compute(samples, 200, 50, 30);

        Assert.Equal(3, r.Points.Count);
        Assert.All(r.Points, p => Assert.False(double.IsNaN(p.Y) || double.IsInfinity(p.Y)));
    }

    [Fact]
    public void Compute_TicksStayWithinTheSampleWindow()
    {
        // 60 one-second samples, tick every 30s -> ticks at 30s and (barely) not
        // before the label would run off the left edge.
        var samples = new double?[60];
        for (int i = 0; i < samples.Length; i++) samples[i] = 10.0 + i;
        var r = GraphSeries.Compute(samples, 600, 80, 30);

        Assert.NotEmpty(r.Ticks);
        Assert.All(r.Ticks, t => Assert.True(t.X >= 0 && t.X <= 600));
    }
}
