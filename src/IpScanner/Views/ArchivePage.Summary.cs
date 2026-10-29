using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using IpScanner.Core.Data;
using IpScanner.Core.Localization;

namespace IpScanner.Views;

/// <summary>Key figures, chart, filters and layout fitting of the archive page.</summary>
public partial class ArchivePage
{
    internal enum QuickRange { All, Day, Week, Month }

    private QuickRange _quick = QuickRange.All;
    private bool _stacked;
    private const double StackBelow = 700;
    private const double CompactBelow = 880;
    private const double CompactStackedFrom = 560;
    private const double KpiMinWidth = 150;

    /// <summary>The look-back of a quick range; null for "all".</summary>
    internal static TimeSpan? QuickSpan(QuickRange range) => range switch
    {
        QuickRange.Day => TimeSpan.FromDays(1),
        QuickRange.Week => TimeSpan.FromDays(7),
        QuickRange.Month => TimeSpan.FromDays(30),
        _ => null
    };

    private void SyncQuickButtons()
    {
        var noDates = FromDate.SelectedDate is null && ToDate.SelectedDate is null;
        QuickDay.IsChecked = _quick == QuickRange.Day;
        QuickWeek.IsChecked = _quick == QuickRange.Week;
        QuickMonth.IsChecked = _quick == QuickRange.Month;
        QuickAll.IsChecked = _quick == QuickRange.All && noDates;
    }

    private async void OnQuickRange(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string tag } || !Enum.TryParse(tag, out QuickRange range)) return;
        _quick = range;
        _suppressRange = true;
        try
        {
            FromDate.SelectedDate = null;
            ToDate.SelectedDate = null;
        }
        finally { _suppressRange = false; }
        SyncQuickButtons();
        await UiGuard.Run(() => ReloadAsync(), Loc.ArchiveTitle);
    }

    private async void OnDateChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressRange || _config is null) return;
        _quick = QuickRange.All;
        SyncQuickButtons();
        await UiGuard.Run(() => ReloadAsync(), Loc.ArchiveTitle);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (IsInitialized) ApplyFilter();
    }

    /// <summary>True when the device matches the search text: IP, name or MAC, any case;
    /// a MAC matches with or without separators.</summary>
    internal static bool Matches(string? query, string? ip, string? name, string? mac)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var q = query.Trim();
        static bool Has(string? value, string text) =>
            value is not null && value.Contains(text, StringComparison.OrdinalIgnoreCase);
        if (Has(ip, q) || Has(name, q) || Has(mac, q)) return true;
        static string Strip(string text) => text.Replace(":", "").Replace("-", "");
        var bare = Strip(q);
        return bare.Length > 0 && mac is not null && Strip(mac).Contains(bare, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Sets the rows of a grid and keeps its current sort order.</summary>
    private static void SetItems(DataGrid grid, System.Collections.IEnumerable? items)
    {
        var sort = grid.Items.SortDescriptions.ToArray();
        grid.ItemsSource = items;
        foreach (var description in sort) grid.Items.SortDescriptions.Add(description);
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text;
        var runLoaded = RunCard.Visibility == Visibility.Visible;

        var devices = _deviceRows.Where(x => Matches(query, x.Ip, x.Hostname, x.Mac)).ToArray();
        SetItems(DevicesGrid, devices);
        DevicesHint.Text = !runLoaded ? "" : _deviceRows.Count == 0 ? Loc.ArchiveNoDevices
            : devices.Length == 0 ? Loc.ArchiveNoMatch : "";

        var ports = _portRows.Where(x => Matches(query, x.Ip, null, null)).ToArray();
        SetItems(PortsGrid, ports);
        PortsHint.Text = !runLoaded ? "" : _portRows.Count == 0 ? Loc.ArchiveNoPorts
            : ports.Length == 0 ? Loc.ArchiveNoMatch : "";

        var history = _historyRows.Where(x => Matches(query, x.Ip, x.Stats.Hostname, x.Stats.Mac)).ToArray();
        SetItems(HistoryGrid, history);
        HistoryHint.Text = _historyRows.Count == 0 ? Loc.ArchiveEmpty : history.Length == 0 ? Loc.ArchiveNoMatch : "";
    }

    // Key figures

    internal sealed record KpiItem(string Label, string Value, string Sub, string? Accent = null, double ValueSize = 20)
    {
        public bool HasAccent => Accent is not null;
    }

    internal sealed record FactItem(string Label, string Value);

    private static string Number(double value, string format) => value.ToString(format, CultureInfo.CurrentCulture);

    private static string Clock(double seconds)
    {
        var total = (int)Math.Round(seconds);
        return string.Create(CultureInfo.InvariantCulture, $"{total / 60}:{total % 60:00}");
    }

    private static string Size(long bytes) => bytes >= 1024 * 1024
        ? Number(bytes / 1048576.0, "0.0") + " MB"
        : Number(Math.Max(0, bytes) / 1024.0, "0") + " KB";

    internal static IReadOnlyList<KpiItem> BuildKpis(ArchiveSummary s, long dbBytes)
    {
        var na = Loc.NotAvailable;
        string Fmt(string format, params object[] args) => string.Format(CultureInfo.CurrentCulture, format, args);

        string period;
        if (s.FirstAt is { } first && s.LastAt is { } last)
        {
            var a = first.LocalDateTime.ToString("d", CultureInfo.CurrentCulture);
            var b = last.LocalDateTime.ToString("d", CultureInfo.CurrentCulture);
            period = a == b ? a : Fmt(Loc.ArchiveKpiPeriodFmt, a, b);
        }
        else period = na;

        var hasPings = s.PingTotal > 0;
        var availability = hasPings ? Number(s.AvailabilityPercent, "0.0") + " %" : na;
        var availabilityAccent = !hasPings ? null
            : s.AvailabilityPercent >= 99 ? Core.Palette.Green
            : s.AvailabilityPercent >= 90 ? Core.Palette.Yellow : Core.Palette.Red;

        return
        [
            new(Loc.ArchiveKpiRuns, s.RunCount.ToString(CultureInfo.CurrentCulture),
                s.CancelledCount > 0 ? Fmt(Loc.ArchiveKpiCancelledFmt, s.CancelledCount) : ""),
            new(Loc.ArchiveKpiPeriod, period,
                Fmt(Loc.ArchiveKpiAvgDurationFmt, s.AvgDurationSeconds > 0 ? Clock(s.AvgDurationSeconds) : na),
                null, 15),
            new(Loc.Devices, s.DistinctDevices.ToString(CultureInfo.CurrentCulture),
                Fmt(Loc.ArchiveKpiNewDevicesFmt, s.NewDevicesInRange)),
            new(Loc.Online, $"{s.LastRunOnline}/{s.LastRunDevices}",
                Fmt(Loc.ArchiveKpiAvgOnlineFmt, Number(s.AvgOnlinePerRun, "0.#"))),
            new(Loc.ArchiveAvailability, availability,
                hasPings ? Fmt(Loc.ArchiveKpiLossFmt, Number(s.LossPercent, "0.0") + " %") : "", availabilityAccent),
            new(Loc.ArchiveKpiLatency,
                s.AvgLatencyMs is { } ms ? Number(ms, "0.0") + " ms" : na,
                s.MinLatencyMs is { } min && s.MaxLatencyMs is { } max
                    ? Fmt(Loc.ArchiveKpiLatencyRangeFmt, Number(min, "0.#"), Number(max, "0.#")) : "",
                s.AvgLatencyMs is null ? null : Core.Palette.Heat(s.AvgLatencyMs)),
            new(Loc.Jitter, s.AvgJitterMs is { } jitter ? Number(jitter, "0.0") + " ms" : na, Loc.ArchiveKpiJitterNote),
            new(Loc.OpenPortsHeader, s.OpenPortCount.ToString(CultureInfo.CurrentCulture), Loc.ArchiveKpiOpenPortsNote),
            new(Loc.ArchiveKpiFindings, s.FindingCount.ToString(CultureInfo.CurrentCulture), Loc.ArchiveKpiFindingsNote,
                s.FindingCount > 0 ? Core.Palette.Peach : null),
            new(Loc.ArchiveKpiStorage, Size(dbBytes), Loc.ArchiveKpiStorageNote)
        ];
    }

    private void RenderOverview()
    {
        var any = _summary.RunCount > 0;
        KpiList.ItemsSource = any ? BuildKpis(_summary, _dbBytes) : null;
        KpiList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        UpdateKpiColumns();
        DrawChart();
    }

    /// <summary>Cards share one width; the row count follows from how many fit side by side.</summary>
    private void UpdateKpiColumns()
    {
        var count = KpiList.Items.Count;
        if (count == 0) return;
        var fit = Math.Max(1, (int)Math.Floor((TopBlock.ActualWidth + 8) / KpiMinWidth));
        var rows = (int)Math.Ceiling(count / (double)fit);
        var columns = (int)Math.Ceiling(count / (double)rows);
        if (KpiList.Tag is not int current || current != columns) KpiList.Tag = columns;
    }

    // Chart of online devices per run

    private void OnChartSizeChanged(object sender, SizeChangedEventArgs e) => DrawChart();

    private void DrawChart()
    {
        var points = _summary.Runs.Where(x => !x.Cancelled).ToArray();
        var show = points.Length >= 2;
        ChartCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        var maxOnline = Math.Max(1, points.Max(x => x.OnlineCount));
        ChartMaxText.Text = $"{Loc.Max} {points.Max(x => x.OnlineCount)}";
        var first = points[0].StartedAt;
        var last = points[^1].StartedAt;
        var span = last - first;
        var format = span <= TimeSpan.FromDays(2) ? "g" : "d";
        string Tick(DateTimeOffset at) => at.LocalDateTime.ToString(format, CultureInfo.CurrentCulture);
        ChartTickFirst.Text = Tick(first);
        ChartTickMid.Text = Tick(first + span / 2);
        ChartTickLast.Text = Tick(last);

        double width = ChartCanvas.ActualWidth, height = ChartCanvas.ActualHeight > 0 ? ChartCanvas.ActualHeight : ChartCanvas.Height;
        if (width <= 2 || height <= 4) return;
        var line = new PointCollection(points.Length);
        for (var i = 0; i < points.Length; i++)
        {
            var fraction = span <= TimeSpan.Zero ? i / (double)(points.Length - 1)
                : (points[i].StartedAt - first).TotalSeconds / span.TotalSeconds;
            var x = 1 + fraction * (width - 2);
            var y = height - 2 - points[i].OnlineCount / (double)maxOnline * (height - 4);
            line.Add(new Point(x, y));
        }
        ChartLine.Points = line;
        var area = new PointCollection(line) { new Point(line[^1].X, height), new Point(line[0].X, height) };
        ChartFill.Points = area;
    }

    // Detail card, ports

    private static string? SettingsText(string? snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot)) return null;
        try
        {
            using var doc = JsonDocument.Parse(snapshot);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            long? Read(string name)
            {
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
                        && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt64(out var v))
                        return v;
                return null;
            }
            if (Read("PingCount") is not { } pings || Read("PingIntervalMs") is not { } interval
                || Read("ScanThreads") is not { } threads) return null;
            return string.Format(CultureInfo.CurrentCulture, Loc.ArchiveCardSettingsFmt, pings, interval, threads);
        }
        catch (JsonException) { return null; }
    }

    internal static IReadOnlyList<FactItem> BuildFacts(ArchivedScan scan)
    {
        string Stamp(DateTimeOffset at) => at.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
        string Fmt(string format, params object[] args) => string.Format(CultureInfo.CurrentCulture, format, args);
        var facts = new List<FactItem>
        {
            new(Loc.ArchiveCardStarted, Stamp(scan.StartedAt)),
            new(Loc.ArchiveCardFinished, Stamp(scan.FinishedAt)),
            new(Loc.ArchiveDuration, scan.SampleCount > 1 ? Loc.NotAvailable : DurationText(scan)),
            new(Loc.Subnets, NetworkFull(scan)),
            new(Loc.Devices, Fmt(Loc.ArchiveCardOnlineFmt, scan.OnlineCount, scan.DeviceCount)),
            new(Loc.Pings, Fmt(Loc.ArchiveCardPingsFmt, scan.PingSuccess, scan.PingFailed))
        };
        if (SettingsText(scan.ConfigSnapshot) is { } settings) facts.Add(new(Loc.ArchiveCardSettings, settings));
        return facts;
    }

    internal static string KindText(string kind) => kind switch
    {
        ScanCompare.OpenPortKind => Loc.ArchiveKindOpenPort,
        "MacChanged" => Loc.CompareTypeMacChanged,
        "MacOnMultipleIps" => Loc.ArchiveKindMacOnIps,
        "MultipleMacsForOneIp" => Loc.ArchiveKindMultiMacs,
        _ => kind
    };

    internal static IReadOnlyList<PortRow> BuildPortRows(IEnumerable<ArchivedFinding> findings) =>
        findings.Where(x => x.Kind != ScanCompare.PortScannedKind)
            .Select(x => new PortRow(x.Ip, x.Kind, x.Detail))
            .OrderBy(x => Core.Net.Ipv4.SortKey(x.Ip)).ThenBy(x => x.Kind, StringComparer.Ordinal)
            .ThenBy(x => x.Detail, StringComparer.Ordinal).ToArray();

    // Layout fitting

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        _stacked = width < StackBelow;
        var compact = width < CompactBelow && !(_stacked && width >= CompactStackedFrom);
        var visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        RunDurationColumn.Visibility = visibility;
        RunLossColumn.Visibility = visibility;
        RunFindingsColumn.Visibility = visibility;
        HistoryFirstSeenColumn.Visibility = visibility;
        FitLayout();
        UpdateKpiColumns();
    }

    private void OnHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateKpiColumns();
        FitLayout();
    }

    /// <summary>Wide: runs and detail side by side, filling what is left of the page (never below a
    /// usable height, the page scrolls). Narrow: stacked, each with a fixed height.</summary>
    private void FitLayout()
    {
        if (_stacked)
        {
            Grid.SetColumn(RunsPane, 0);
            Grid.SetColumnSpan(RunsPane, 3);
            Grid.SetRow(RunsPane, 0);
            Grid.SetColumn(DetailPane, 0);
            Grid.SetColumnSpan(DetailPane, 3);
            Grid.SetRow(DetailPane, 1);
            RunsColumn.MinWidth = 0;
            DetailColumn.MinWidth = 0;
            MainSplitter.Visibility = Visibility.Collapsed;
            MainArea.RowDefinitions[0].Height = GridLength.Auto;
            MainArea.RowDefinitions[1].Height = GridLength.Auto;
            MainArea.Height = double.NaN;
            RunsPane.Height = 380;
            DetailPane.Height = 520;
            DetailPane.Margin = new Thickness(0, 12, 0, 0);
            return;
        }
        Grid.SetColumn(RunsPane, 0);
        Grid.SetColumnSpan(RunsPane, 1);
        Grid.SetRow(RunsPane, 0);
        Grid.SetColumn(DetailPane, 2);
        Grid.SetColumnSpan(DetailPane, 1);
        Grid.SetRow(DetailPane, 0);
        RunsColumn.MinWidth = 260;
        DetailColumn.MinWidth = 320;
        MainSplitter.Visibility = Visibility.Visible;
        MainArea.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
        MainArea.RowDefinitions[1].Height = new GridLength(0);
        RunsPane.Height = double.NaN;
        DetailPane.Height = double.NaN;
        DetailPane.Margin = new Thickness(0);
        var height = Math.Max(440, ContentHost.ActualHeight - TopBlock.ActualHeight - 12);
        if (double.IsNaN(MainArea.Height) || Math.Abs(MainArea.Height - height) >= 0.5) MainArea.Height = height;
    }

    /// <summary>A grid at the end of its own scroll range hands the wheel to the page, so the page never feels stuck.</summary>
    private void OnGridWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject grid || OverviewScroll.ScrollableHeight <= 0) return;
        var inner = FindScrollViewer(grid);
        var atLimit = inner is null || inner.ScrollableHeight <= 0
            || (e.Delta > 0 && inner.VerticalOffset <= 0)
            || (e.Delta < 0 && inner.VerticalOffset >= inner.ScrollableHeight);
        if (!atLimit) return;
        OverviewScroll.ScrollToVerticalOffset(OverviewScroll.VerticalOffset - e.Delta / 2.5);
        e.Handled = true;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScrollViewer(child) is { } found) return found;
        }
        return null;
    }
}
