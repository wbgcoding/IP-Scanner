using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using IpScanner.Core.Data;
using IpScanner.Core.Localization;
using IpScanner.Views;
using Xunit;

namespace IpScanner.Tests;

/// <summary>The archive page keeps its grids readable at the smallest window and folds run facts into few columns.</summary>
public class ArchiveLayoutTests
{
    private static ArchivedScan Scan(string[] subnets, bool cancelled, TimeSpan length)
    {
        var start = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
        return new ArchivedScan(1, start, start + length, subnets.Length > 0 ? subnets[0] : "10.0.0.0/24",
            subnets, "{}", 20, 12, 100, 20, 120, cancelled);
    }

    [Fact]
    public void NetworkSummary_ShowsFirstSubnetAndCount()
    {
        Assert.Equal("10.0.0.0/24", ArchivePage.NetworkSummary(Scan(["10.0.0.0/24"], false, TimeSpan.FromMinutes(1))));
        Assert.Equal("10.0.0.0/24 +2", ArchivePage.NetworkSummary(Scan(["10.0.0.0/24", "10.0.1.0/24", "10.0.2.0/24"], false, TimeSpan.FromMinutes(1))));
        Assert.Equal("10.0.0.0/24, 10.0.1.0/24", ArchivePage.NetworkFull(Scan(["10.0.0.0/24", "10.0.1.0/24"], false, TimeSpan.FromMinutes(1))));
    }

    [Fact]
    public void DurationText_MarksStoppedRuns()
    {
        Loc.SetLanguage("en");
        Assert.Equal("00:03:12", ArchivePage.DurationText(Scan(["10.0.0.0/24"], false, TimeSpan.FromSeconds(192))));
        Assert.Equal("00:03:12 (stopped)", ArchivePage.DurationText(Scan(["10.0.0.0/24"], true, TimeSpan.FromSeconds(192))));
    }

    [Fact]
    public async Task SmallWindow_KeepsBothGridsReadableWithoutNestedScrolling()
    {
        await WpfTestHost.RunAsync(() =>
        {
            Loc.SetLanguage("en");
            var page = new ArchivePage { Width = 650, Height = 450 };
            page.Measure(new Size(650, 450));
            page.Arrange(new Rect(0, 0, 650, 450));
            page.UpdateLayout();

            foreach (var name in new[] { "RunsGrid", "DevicesGrid" })
            {
                var grid = (DataGrid)page.FindName(name);
                Assert.True(grid.ActualHeight >= 150, $"{name} is {grid.ActualHeight}");
                Assert.True(grid.ActualWidth >= 250, $"{name} is {grid.ActualWidth} wide");
            }
            // The detail column has no scroll viewer of its own around its grids.
            Assert.Null(page.FindName("DetailScroll"));
            Assert.Null(page.FindName("DetailPane"));
            // Narrow pages drop the secondary columns.
            Assert.Equal(Visibility.Collapsed, ((DataGridColumn)page.FindName("RunDurationColumn")).Visibility);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task WidePage_ShowsAllColumns_AndHistoryTabSwitchesView()
    {
        await WpfTestHost.RunAsync(() =>
        {
            Loc.SetLanguage("en");
            var page = new ArchivePage { Width = 1100, Height = 700 };
            page.Measure(new Size(1100, 700));
            page.Arrange(new Rect(0, 0, 1100, 700));
            page.UpdateLayout();

            Assert.Equal(Visibility.Visible, ((DataGridColumn)page.FindName("RunDurationColumn")).Visibility);
            var devices = (UIElement)page.FindName("DevicesView");
            var history = (UIElement)page.FindName("HistoryView");
            Assert.Equal(Visibility.Visible, devices.Visibility);
            Assert.Equal(Visibility.Collapsed, history.Visibility);

            var tab = (System.Windows.Controls.Primitives.ToggleButton)page.FindName("HistoryTab");
            tab.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal(Visibility.Visible, history.Visibility);
            Assert.Equal(Visibility.Collapsed, devices.Visibility);
            foreach (var name in new[] { "HistoryGrid", "TrendGrid" })
            {
                var grid = (DataGrid)page.FindName(name);
                Assert.Contains(grid.Columns, c => c.Header is string h && h == Loc.ArchiveLatencyTrend && c.Width.IsAuto);
            }
            return Task.CompletedTask;
        });
    }
}
