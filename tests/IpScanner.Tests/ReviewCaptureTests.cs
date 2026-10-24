#if DEBUG
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using IpScanner.Views.Review;
using Xunit;

namespace IpScanner.Tests;

/// <summary>The surface tool's clip detector must not flag what is drawn on purpose: scroll bar
/// parts and content that can be scrolled into view.</summary>
public class ReviewCaptureTests
{
    [Fact]
    public void Overflow_CountsAnOverhangInADirectionThatCannotScroll()
    {
        var r = new Rect(0, 0, 120, 20);
        Assert.Equal(20, ReviewCapture.Overflow(r, 100, 30, scrollsH: false, scrollsV: false));
    }

    [Fact]
    public void Overflow_IgnoresADirectionThatCanScroll()
    {
        var r = new Rect(0, 0, 120, 20);
        Assert.Equal(0, ReviewCapture.Overflow(r, 100, 30, scrollsH: true, scrollsV: false));
        // The other direction still counts.
        var tall = new Rect(0, 0, 120, 50);
        Assert.Equal(20, ReviewCapture.Overflow(tall, 100, 30, scrollsH: true, scrollsV: false));
        Assert.Equal(0, ReviewCapture.Overflow(tall, 100, 30, scrollsH: true, scrollsV: true));
    }

    [Fact]
    public void Overflow_ContentInsideItsParentIsNotAnOverflow()
    {
        Assert.True(ReviewCapture.Overflow(new Rect(5, 5, 50, 10), 100, 30, false, false) <= 0);
    }

    [Fact]
    public async Task Analyze_NeverReportsPartsOfAScrollBar()
    {
        await WpfTestHost.RunAsync(() =>
        {
            var bar = new ScrollBar { Orientation = Orientation.Vertical, Width = 12, Height = 40, Maximum = 100, ViewportSize = 10 };
            var host = new Grid { Width = 40, Height = 40 };
            host.Children.Add(bar);
            host.Measure(new Size(40, 40));
            host.Arrange(new Rect(0, 0, 40, 40));
            host.UpdateLayout();

            Assert.Empty(ReviewCapture.Analyze("test", "en", "dark", host));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Analyze_StillFlagsANarrowedLabel()
    {
        await WpfTestHost.RunAsync(() =>
        {
            var label = new TextBlock { Text = "A label that is far too long for its slot", Width = 30, TextWrapping = TextWrapping.NoWrap };
            var host = new Grid { Width = 30, Height = 30 };
            host.Children.Add(label);
            host.Measure(new Size(30, 30));
            host.Arrange(new Rect(0, 0, 30, 30));
            host.UpdateLayout();

            Assert.Contains(ReviewCapture.Analyze("test", "en", "dark", host), f => f.Kind == "text-width");
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Analyze_SkipsTheSubtreeOfAZeroWidthElement()
    {
        await WpfTestHost.RunAsync(() =>
        {
            var label = new TextBlock { Text = "A label that is far too long for its slot", Width = 30, TextWrapping = TextWrapping.NoWrap };
            var collapsed = new Grid { Width = 0 };
            collapsed.Children.Add(label);
            var host = new Grid { Width = 100, Height = 30 };
            host.Children.Add(collapsed);
            host.Measure(new Size(100, 30));
            host.Arrange(new Rect(0, 0, 100, 30));
            host.UpdateLayout();

            Assert.Empty(ReviewCapture.Analyze("test", "en", "dark", host));
            // The same label in a normal container is still flagged.
            collapsed.Width = 30;
            host.UpdateLayout();
            Assert.Contains(ReviewCapture.Analyze("test", "en", "dark", host), f => f.Kind == "text-width");
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Analyze_AnEmptyGridWithAutoScrollingIsNotClipped()
    {
        await WpfTestHost.RunAsync(() =>
        {
            var grid = new DataGrid { AutoGenerateColumns = false, Width = 120, Height = 80 };
            grid.Columns.Add(new DataGridTextColumn { Header = "Port", Width = 90 });
            grid.Columns.Add(new DataGridTextColumn { Header = "Service", Width = 200 });
            var host = new Grid { Width = 120, Height = 80 };
            host.Children.Add(grid);
            host.Measure(new Size(120, 80));
            host.Arrange(new Rect(0, 0, 120, 80));
            host.UpdateLayout();

            Assert.Empty(ReviewCapture.Analyze("test", "en", "dark", host));
            return Task.CompletedTask;
        });
    }
}
#endif
