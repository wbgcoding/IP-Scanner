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

    [Fact]
    public void Compute_MarkLoss_BreaksTheLineAtLostSamples()
    {
        var r = GraphSeries.Compute(new double?[] { 1, 2, null, 3, 4, 5 }, 500, 60, 30, markLoss: true);
        Assert.Equal(2, r.Segments.Count);
        Assert.Equal(2, r.Segments[0].Count);
        Assert.Equal(3, r.Segments[1].Count);
        Assert.Equal(5, r.Points.Count);
    }

    [Fact]
    public void Compute_WithoutMarkLoss_KeepsOneConnectedLineAndNoMarks()
    {
        // Averages and history buckets use null for "no data": they stay connected and unmarked.
        var r = GraphSeries.Compute(new double?[] { 1, null, null, null, 3, 4 }, 500, 60, 30);
        Assert.Single(r.Segments);
        Assert.Empty(r.LossMarks);
        Assert.Empty(r.Bands);
    }

    [Fact]
    public void Compute_MarkLoss_PutsOneTickAtEachLostSampleX()
    {
        // 6 samples over 500 px: 100 px apart.
        var r = GraphSeries.Compute(new double?[] { 1, null, 3, 4, null, 6 }, 500, 60, 30, markLoss: true);
        Assert.Equal(new[] { 100.0, 400.0 }, r.LossMarks);
        Assert.Empty(r.Bands);   // single losses get a tick only
    }

    [Fact]
    public void Compute_MarkLoss_TwoLostInARowGetNoBand_ThreeDo()
    {
        var two = GraphSeries.Compute(new double?[] { 1, 2, null, null, 5, 6, 7 }, 600, 60, 30, markLoss: true);
        Assert.Empty(two.Bands);
        Assert.Equal(2, two.LossMarks.Count);

        // 7 samples over 600 px: 100 px apart; samples 2..4 lost -> band from 150 to 450.
        var three = GraphSeries.Compute(new double?[] { 1, 2, null, null, null, 6, 7 }, 600, 60, 30, markLoss: true);
        Assert.Equal(3, three.LossMarks.Count);
        var band = Assert.Single(three.Bands);
        Assert.Equal(150, band.X, 6);
        Assert.Equal(300, band.Width, 6);
    }

    [Fact]
    public void Compute_MarkLoss_BandsAtTheEdgesAreClippedToTheGraph()
    {
        // 8 samples over 700 px: 100 px apart; samples 0..2 and 5..7 lost.
        var r = GraphSeries.Compute(new double?[] { null, null, null, 4, 5, null, null, null }, 700, 60, 30, markLoss: true);
        Assert.Equal(2, r.Bands.Count);
        Assert.Equal(0, r.Bands[0].X, 6);
        Assert.Equal(250, r.Bands[0].Width, 6);
        Assert.Equal(450, r.Bands[1].X, 6);
        Assert.Equal(250, r.Bands[1].Width, 6);
    }

    [Fact]
    public void Compute_MarkLoss_AllLostStillMarksTheOutageWithoutALine()
    {
        var r = GraphSeries.Compute(new double?[] { null, null, null, null }, 300, 60, 30, markLoss: true);
        Assert.Empty(r.Segments);
        Assert.Equal(4, r.LossMarks.Count);
        Assert.Single(r.Bands);
        Assert.Equal("", r.MaxText);
    }

    [Fact]
    public void Compute_MarkLoss_AnIsolatedSampleBetweenLossesStaysAsOwnSegment()
    {
        var r = GraphSeries.Compute(new double?[] { 1, null, 5, null, 2, 3 }, 500, 60, 30, markLoss: true);
        Assert.Equal(3, r.Segments.Count);
        Assert.Single(r.Segments[1]);
        Assert.False(r.Line.Bounds.IsEmpty);
    }

    [Fact]
    public void Compute_Geometries_AreFrozenForCrossThreadUse()
    {
        var r = GraphSeries.Compute(new double?[] { 1, null, null, null, 3, 4 }, 500, 60, 30, markLoss: true);
        Assert.True(r.Line.IsFrozen);
        Assert.True(r.LossGeometry.IsFrozen);
        Assert.True(r.BandGeometry.IsFrozen);
        Assert.False(r.LossGeometry.Bounds.IsEmpty);
        Assert.False(r.BandGeometry.Bounds.IsEmpty);
    }

    [Fact]
    public void Compute_LossTicksSitOnTheBaseline()
    {
        var r = GraphSeries.Compute(new double?[] { 1, null, 3 }, 200, 60, 30, markLoss: true);
        double baseline = 60 - GraphSeries.PadBottom;
        Assert.Equal(baseline, r.LossGeometry.Bounds.Bottom, 6);
        Assert.Equal(baseline - GraphSeries.LossTickHeight, r.LossGeometry.Bounds.Top, 6);
    }
}
