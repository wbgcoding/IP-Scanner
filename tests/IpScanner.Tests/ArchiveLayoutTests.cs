using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using IpScanner.Core;
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
    public async Task SmallWindow_StacksRunsAboveDetailAndKeepsBothGridsReadable()
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
            // Stacked: the detail pane sits below the runs, not beside them, and the splitter is gone.
            var detail = (FrameworkElement)page.FindName("DetailPane");
            Assert.Equal(0, Grid.GetColumn(detail));
            Assert.Equal(1, Grid.GetRow(detail));
            Assert.Equal(Visibility.Collapsed, ((UIElement)page.FindName("MainSplitter")).Visibility);
            // Stacked panes use the full width, so the secondary columns stay; below that they drop out.
            Assert.Equal(Visibility.Visible, ((DataGridColumn)page.FindName("RunDurationColumn")).Visibility);
            var tiny = new ArchivePage { Width = 520, Height = 450 };
            tiny.Measure(new Size(520, 450));
            tiny.Arrange(new Rect(0, 0, 520, 450));
            tiny.UpdateLayout();
            Assert.Equal(Visibility.Collapsed, ((DataGridColumn)tiny.FindName("RunDurationColumn")).Visibility);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task WidePage_ShowsAllColumns_RunsLeftDetailRight_AndThreeTabs()
    {
        await WpfTestHost.RunAsync(() =>
        {
            Loc.SetLanguage("en");
            var page = new ArchivePage { Width = 1100, Height = 700 };
            page.Measure(new Size(1100, 700));
            page.Arrange(new Rect(0, 0, 1100, 700));
            page.UpdateLayout();

            Assert.Equal(Visibility.Visible, ((DataGridColumn)page.FindName("RunDurationColumn")).Visibility);
            var runs = (FrameworkElement)page.FindName("RunsPane");
            var detail = (FrameworkElement)page.FindName("DetailPane");
            Assert.Equal(2, Grid.GetColumn(detail));
            Assert.True(runs.ActualWidth >= 260, $"runs {runs.ActualWidth}");
            Assert.True(detail.ActualWidth >= 320, $"detail {detail.ActualWidth}");
            Assert.Equal(Visibility.Visible, ((UIElement)page.FindName("MainSplitter")).Visibility);

            var tabs = (TabControl)page.FindName("DetailTabs");
            Assert.Equal(3, tabs.Items.Count);
            tabs.SelectedIndex = 2;
            page.UpdateLayout();
            // The latency column keeps its own width in the history grid; the trend grid lets it take the rest.
            var history = (DataGrid)page.FindName("HistoryGrid");
            Assert.Contains(history.Columns, c => c.Header is string h && h == Loc.ArchiveLatencyTrend && c.Width.IsAuto);
            var trend = (DataGrid)page.FindName("TrendGrid");
            Assert.Contains(trend.Columns, c => c.Header is string h && h == Loc.ArchiveLatencyTrend && c.Width.IsStar);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Matches_FindsIpNameOrMacInAnyCase_AndMacWithoutSeparators()
    {
        Assert.True(ArchivePage.Matches("", "10.0.0.5", null, null));
        Assert.True(ArchivePage.Matches("  ", "10.0.0.5", null, null));
        Assert.True(ArchivePage.Matches("0.0.5", "10.0.0.5", null, null));
        Assert.True(ArchivePage.Matches("NAS", "10.0.0.5", "my-nas.local", null));
        Assert.True(ArchivePage.Matches("aa:bb", "10.0.0.5", null, "AA:BB:CC:00:11:22"));
        Assert.True(ArchivePage.Matches("aabbcc", "10.0.0.5", null, "AA:BB:CC:00:11:22"));
        Assert.True(ArchivePage.Matches("aa-bb-cc", "10.0.0.5", null, "AA:BB:CC:00:11:22"));
        Assert.False(ArchivePage.Matches("printer", "10.0.0.5", "nas", "AA:BB:CC:00:11:22"));
        Assert.False(ArchivePage.Matches("zz", "10.0.0.5", null, null));
    }

    [Fact]
    public void QuickSpan_MapsTheButtonsToLookBacks()
    {
        Assert.Equal(TimeSpan.FromDays(1), ArchivePage.QuickSpan(ArchivePage.QuickRange.Day));
        Assert.Equal(TimeSpan.FromDays(7), ArchivePage.QuickSpan(ArchivePage.QuickRange.Week));
        Assert.Equal(TimeSpan.FromDays(30), ArchivePage.QuickSpan(ArchivePage.QuickRange.Month));
        Assert.Null(ArchivePage.QuickSpan(ArchivePage.QuickRange.All));
    }

    [Fact]
    public void BuildPortRows_SkipsTheScannedMarkerAndLocalizesTheKind()
    {
        Loc.SetLanguage("en");
        var rows = ArchivePage.BuildPortRows(
        [
            new ArchivedFinding("10.0.0.10", ScanCompare.PortScannedKind, null),
            new ArchivedFinding("10.0.0.10", ScanCompare.OpenPortKind, "443/tcp"),
            new ArchivedFinding("10.0.0.2", "MacChanged", "AA -> BB"),
            new ArchivedFinding("10.0.0.2", ScanCompare.OpenPortKind, "22/tcp")
        ]);
        Assert.Equal(3, rows.Count);
        Assert.Equal(["10.0.0.2", "10.0.0.2", "10.0.0.10"], rows.Select(x => x.Ip));
        Assert.Equal(Loc.ArchiveKindOpenPort, rows[2].KindLabel);
        Assert.Equal("443/tcp", rows[2].Detail);
    }

    [Fact]
    public void BuildKpis_FormatsEveryCard_AndColorsHealth()
    {
        Loc.SetLanguage("en");
        var t = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
        var summary = ArchiveSummary.Empty with
        {
            RunCount = 5, CancelledCount = 1, FirstAt = t, LastAt = t.AddDays(3), DistinctDevices = 40, NewDevicesInRange = 4,
            AvgOnlinePerRun = 31.5, LastRunOnline = 33, LastRunDevices = 38, PingTotal = 1000, AvailabilityPercent = 97.5,
            LossPercent = 2.5, AvgLatencyMs = 12.34, MinLatencyMs = 1, MaxLatencyMs = 90, AvgJitterMs = 3.2,
            FindingCount = 2, OpenPortCount = 7, AvgDurationSeconds = 125
        };
        var cards = ArchivePage.BuildKpis(summary, 2 * 1048576);
        Assert.Equal(10, cards.Count);
        Assert.Equal("5", cards[0].Value);
        Assert.Equal("1 cancelled", cards[0].Sub);
        Assert.Equal("avg duration 2:05", cards[1].Sub);
        Assert.Equal("40", cards[2].Value);
        Assert.Equal("4 new in period", cards[2].Sub);
        Assert.Equal("33/38", cards[3].Value);
        Assert.Equal($"{97.5:0.0} %", cards[4].Value);
        Assert.Equal($"loss {2.5:0.0} %", cards[4].Sub);
        Assert.Equal(Palette.Yellow, cards[4].Accent);
        Assert.Contains("min 1 / max 90 ms", cards[5].Sub);
        Assert.Equal("7", cards[7].Value);
        Assert.Equal(Palette.Peach, cards[8].Accent);
        Assert.Equal($"{2.0:0.0} MB", cards[9].Value);

        var healthy = ArchivePage.BuildKpis(summary with { AvailabilityPercent = 99.9 }, 0);
        Assert.Equal(Palette.Green, healthy[4].Accent);
        var broken = ArchivePage.BuildKpis(summary with { AvailabilityPercent = 60 }, 0);
        Assert.Equal(Palette.Red, broken[4].Accent);
    }

    [Fact]
    public void BuildKpis_WithoutPingsShowsPlaceholdersInsteadOfZeros()
    {
        Loc.SetLanguage("en");
        var now = DateTimeOffset.Now;
        var cards = ArchivePage.BuildKpis(ArchiveSummary.Empty with { RunCount = 1, FirstAt = now, LastAt = now }, 0);
        Assert.Equal(Loc.NotAvailable, cards[4].Value);
        Assert.Null(cards[4].Accent);
        Assert.Equal(Loc.NotAvailable, cards[5].Value);
        Assert.Null(cards[5].Accent);
        Assert.Equal(Loc.NotAvailable, cards[6].Value);
    }

    [Fact]
    public void BuildFacts_ListsTheRunFields_AndSettingsOnlyWhenRecorded()
    {
        Loc.SetLanguage("en");
        var start = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
        var plain = new ArchivedScan(1, start, start.AddSeconds(95), "10.0.0.0/24", ["10.0.0.0/24"], "{}", 20, 12, 100, 20, 1, false);
        Assert.Equal(6, ArchivePage.BuildFacts(plain).Count);
        var withSettings = plain with { ConfigSnapshot = "{\"PingCount\":3,\"PingIntervalMs\":200,\"ScanThreads\":64}" };
        var facts = ArchivePage.BuildFacts(withSettings);
        Assert.Equal(7, facts.Count);
        Assert.Contains("3 pings", facts[^1].Value);
        Assert.Contains("64 threads", facts[^1].Value);
        Assert.Equal("12 of 20 online", facts.Single(x => x.Label == Loc.Devices).Value);
    }

    [Fact]
    public void LossColor_FollowsTheMainTableThresholds()
    {
        Assert.Equal(Palette.MidGray, ArchivePage.LossColor(null));
        Assert.Equal(Palette.Green, ArchivePage.LossColor(0));
        Assert.Equal(Palette.Yellow, ArchivePage.LossColor(2));
        Assert.Equal(Palette.Peach, ArchivePage.LossColor(10));
        Assert.Equal(Palette.Red, ArchivePage.LossColor(50));
    }
}
