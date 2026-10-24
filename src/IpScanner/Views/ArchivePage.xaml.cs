using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using IpScanner.Core.Data;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;
using Microsoft.Win32;

namespace IpScanner.Views;

/// <summary>A result message for the window's notice banner; Folder, when set, gets an Open folder action.</summary>
public sealed record ArchiveBackupNotice(string Text, string? Folder);

public partial class ArchivePage : UserControl
{
    internal sealed class ArchiveKeyException : Exception;
    private sealed class ArchiveRangeException : ArgumentException;

    private ScanConfig? _config;
    private IReadOnlyList<ArchivedScan> _scans = [];
    private ScanArchiveStatistics? _statistics;
    private bool _busy;
    private bool _lastLoadOk;
    private int _selectionVersion;
    private int _trendVersion;

    public event EventHandler? CloseRequested;
    public event EventHandler? StartScanRequested;
    public event EventHandler<ArchiveBackupNotice>? BackupNoticeRequested;

    public ArchivePage() => InitializeComponent();

    public async Task OpenAsync(ScanConfig config)
    {
        _config = config;
        FromDate.SelectedDate = null;
        ToDate.SelectedDate = null;
        DaysBox.Text = config.ArchiveCompactionDays > 0
            ? config.ArchiveCompactionDays.ToString(CultureInfo.CurrentCulture) : "30";
        SetDetailTab(false);
        await ReloadAsync();
    }

    public async Task RefreshLanguageAsync()
    {
        if (Visibility == Visibility.Visible && _config is not null)
            await ReloadAsync();
    }

    internal static (DateTimeOffset? From, DateTimeOffset? Through) DateRange(DateTime? start, DateTime? end)
    {
        DateTimeOffset? from = start is { } startDate ? new DateTimeOffset(startDate.Date) : null;
        DateTimeOffset? through = end is { } endDate
            ? new DateTimeOffset(endDate.Date.AddDays(1).AddTicks(-1)) : null;
        if (from is not null && through is not null && through < from)
            throw new ArchiveRangeException();
        return (from, through);
    }

    private (DateTimeOffset? From, DateTimeOffset? Through) Range() =>
        DateRange(FromDate.SelectedDate, ToDate.SelectedDate);

    private async Task ReloadAsync()
    {
        if (_config is null || _busy) return;
        try
        {
            _busy = true;
            _lastLoadOk = false;
            ManagementPanel.IsEnabled = false;
            ++_selectionVersion;
            ++_trendVersion;
            StatusText.Text = Loc.ArchiveLoading;
            var path = _config.DatabasePath;
            var (from, through) = Range();
            var result = await Task.Run(() =>
            {
                var db = new KnownDevicesDb(path);
                if (!db.IsUsable) throw new ArchiveKeyException();
                return (Runs: db.ListScanArchive(from, through), Stats: db.GetScanArchiveStatistics(from, through));
            });
            _scans = result.Runs;
            _statistics = result.Stats;
            RunsGrid.ItemsSource = _scans.Select(x => new RunRow(x)).ToArray();
            RunsGrid.SelectedIndex = -1;
            HistoryGrid.ItemsSource = _statistics.Devices.OrderBy(x => Core.Net.Ipv4.SortKey(x.Ip))
                .Select(x => new HistoryRow(x)).ToArray();
            HistoryGrid.SelectedIndex = -1;
            HistoryHint.Text = _statistics.Devices.Count == 0 ? Loc.ArchiveEmpty : "";
            TrendGrid.ItemsSource = null;
            TrendHint.Visibility = Visibility.Visible;
            DevicesGrid.ItemsSource = null;
            DevicesHint.Text = "";
            DetailText.Text = Loc.ArchiveSelectRun;
            StatusText.Text = _scans.Count == 0 ? Loc.ArchiveEmpty :
                string.Format(CultureInfo.CurrentCulture, Loc.ArchiveRangeSummary,
                    _scans.Count, _statistics.SampleCount);
            StartScanButton.Visibility = _scans.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateSize();
            DeleteRangeButton.IsEnabled = from is not null && through is not null && _scans.Count > 0;
            DeleteRunButton.IsEnabled = false;
            _lastLoadOk = true;
            ManagementPanel.IsEnabled = true;
            if (_comparing) { FillRunPickers(); await RecomputeCompareAsync(); }
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _busy = false; }
    }

    private void UpdateSize()
    {
        if (_config is null) return;
        var path = ScanConfig.ResolvePath(_config.DatabasePath);
        long size = new[] { path, path + "-wal", path + "-shm" }
            .Where(File.Exists).Sum(x => new FileInfo(x).Length);
        SizeText.Text = string.Format(CultureInfo.CurrentCulture, Loc.ArchiveDatabaseSize,
            size / 1048576.0);
    }

    /// <summary>Below this page width the least important columns (run duration, first seen) are hidden
    /// so the two columns of the overview keep readable text instead of scrolling sideways.</summary>
    internal const double CompactWidth = 880;

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Width < CompactWidth;
        var visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        RunDurationColumn.Visibility = visibility;
        HistoryFirstSeenColumn.Visibility = visibility;
        // Narrow pages give the run list the larger share so its three columns fit without a scroll bar.
        MainArea.ColumnDefinitions[0].Width = new GridLength(compact ? 5 : 4, GridUnitType.Star);
        MainArea.ColumnDefinitions[2].Width = new GridLength(compact ? 4 : 5, GridUnitType.Star);
        MainArea.ColumnDefinitions[2].MinWidth = compact ? 300 : 330;
    }

    private void OnDetailTab(object sender, RoutedEventArgs e) => SetDetailTab(sender == HistoryTab);

    private void SetDetailTab(bool history)
    {
        HistoryTab.IsChecked = history;
        DevicesTab.IsChecked = !history;
        HistoryView.Visibility = history ? Visibility.Visible : Visibility.Collapsed;
        DevicesView.Visibility = history ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnRunSelected(object sender, SelectionChangedEventArgs e)
    {
        await UiGuard.Run(async () =>
        {
            DeleteRunButton.IsEnabled = RunsGrid.SelectedItem is RunRow;
            DevicesGrid.ItemsSource = null;
            DevicesHint.Text = "";
            if (RunsGrid.SelectedItem is not RunRow row || _config is null)
            {
                DetailText.Text = Loc.ArchiveSelectRun;
                return;
            }
            int version = ++_selectionVersion;
            try
            {
                DetailText.Text = Loc.ArchiveLoading;
                var path = _config.DatabasePath;
                var detail = await Task.Run(() => new KnownDevicesDb(path).LoadScanArchive(row.Scan.Id));
                if (version != _selectionVersion || detail is null) return;
                var scan = detail.Scan;
                DetailText.Text = string.Format(CultureInfo.CurrentCulture, Loc.ArchiveRunSummary,
                    scan.OnlineCount, scan.DeviceCount, scan.PingSuccess, scan.PingFailed,
                    scan.Subnets.Length == 0 ? Loc.NotAvailable : string.Join(", ", scan.Subnets));
                DevicesGrid.ItemsSource = detail.Devices.OrderBy(x => Core.Net.Ipv4.SortKey(x.Ip))
                    .Select(x => new DeviceRow(x)).ToArray();
                DevicesHint.Text = detail.Devices.Count == 0 ? Loc.ArchiveNoDevices : "";
            }
            catch (Exception ex) { ShowError(ex); }
        }, Loc.ArchiveTitle);
    }

    private void OnDeviceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (DevicesGrid.SelectedItem is not DeviceRow row || _statistics is null) return;
        var match = _statistics.Devices.FirstOrDefault(x =>
            string.Equals(x.DeviceKey, DeviceKey(row.Device), StringComparison.OrdinalIgnoreCase));
        if (match is null) return;
        var target = HistoryGrid.Items.Cast<HistoryRow>().FirstOrDefault(x => x.Stats.DeviceKey == match.DeviceKey);
        if (target is null) return;
        // The trend loads on the History tab, so the click has to land there to show anything.
        SetDetailTab(true);
        HistoryGrid.SelectedItem = target;
        HistoryGrid.ScrollIntoView(target);
    }

    private async void OnHistorySelected(object sender, SelectionChangedEventArgs e)
    {
        await UiGuard.Run(async () =>
        {
            TrendGrid.ItemsSource = null;
            TrendHint.Visibility = Visibility.Visible;
            if (HistoryGrid.SelectedItem is not HistoryRow row || _config is null) return;
            int version = ++_trendVersion;
            try
            {
                var path = _config.DatabasePath;
                var scans = _scans.Where(x => !x.Cancelled).ToArray();
                var key = row.Stats.DeviceKey;
                var trend = await Task.Run(() =>
                {
                    var db = new KnownDevicesDb(path);
                    return scans.Select(scan => (scan, detail: db.LoadScanArchive(scan.Id)))
                        .SelectMany(x => x.detail?.Devices
                            .Where(d => string.Equals(DeviceKey(d), key, StringComparison.OrdinalIgnoreCase))
                            .Select(d => new TrendRow(x.scan.StartedAt, d)) ?? [])
                        .ToArray();
                });
                if (version != _trendVersion) return;
                TrendGrid.ItemsSource = trend;
                TrendHint.Visibility = trend.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
            }
            catch (Exception ex) { ShowError(ex); }
        }, Loc.ArchiveTitle);
    }

    private static string DeviceKey(ArchivedDevice device) => string.IsNullOrWhiteSpace(device.Mac)
        ? "ip:" + device.Ip : "mac:" + device.Mac.ToUpperInvariant();

    private async void OnApplyRange(object sender, RoutedEventArgs e)
    {
        await UiGuard.Run(() => ReloadAsync(), Loc.ArchiveTitle);
    }
    private async void OnClearRange(object sender, RoutedEventArgs e)
    {
        await UiGuard.Run(async () =>
        {
            FromDate.SelectedDate = null;
            ToDate.SelectedDate = null;
            await ReloadAsync();
        }, Loc.ArchiveTitle);
    }

    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    private void OnStartScan(object sender, RoutedEventArgs e) => StartScanRequested?.Invoke(this, EventArgs.Empty);

    private bool Confirm(string message)
    {
        var owner = Window.GetWindow(this);
        return new ArchiveConfirmDialog(message) { Owner = owner }.ShowDialog() == true;
    }

    private Task MutateAsync(Func<KnownDevicesDb, int> action, string? success)
        => MutateAsync(db => new object[] { action(db) }, success);

    internal async Task MutateAsync(Func<KnownDevicesDb, object[]> action, string? success)
    {
        if (_config is null || _busy) return;
        try
        {
            _busy = true;
            ManagementPanel.IsEnabled = false;
            StatusText.Text = Loc.ArchiveWorking;
            var path = _config.DatabasePath;
            var args = await Task.Run(() => action(new KnownDevicesDb(path)));
            _busy = false;
            await ReloadAsync();
            if (_lastLoadOk && success is not null) StatusText.Text = string.Format(CultureInfo.CurrentCulture, success, args);
        }
        catch (Exception ex)
        {
            ManagementPanel.IsEnabled = _lastLoadOk;
            ShowError(ex);
        }
        finally { _busy = false; }
    }

    private async void OnDeleteRun(object sender, RoutedEventArgs e)
    {
        await UiGuard.Run(async () =>
        {
            if (RunsGrid.SelectedItem is not RunRow row || !Confirm(string.Format(
                CultureInfo.CurrentCulture, Loc.ArchiveConfirmDeleteRun, row.DateText, row.NetworkFull))) return;
            await MutateAsync(db => db.DeleteScanArchive(row.Scan.Id) ? 1 : 0, Loc.ArchiveDeleted);
        }, Loc.ArchiveTitle);
    }

    private async void OnDeleteRange(object sender, RoutedEventArgs e)
    {
        await UiGuard.Run(async () =>
        {
            try
            {
                var (from, through) = Range();
                if (from is null || through is null || _scans.Count == 0) return;
                if (!Confirm(string.Format(CultureInfo.CurrentCulture, Loc.ArchiveConfirmDeleteRange,
                    _scans.Count, FromDate.SelectedDate, ToDate.SelectedDate))) return;
                await MutateAsync(db => db.DeleteScanArchiveRange(from.Value, through.Value), Loc.ArchiveDeleted);
            }
            catch (Exception ex) { ShowError(ex); }
        }, Loc.ArchiveTitle);
    }

    internal static bool TryRetentionDays(string text, CultureInfo culture, out int days) =>
        int.TryParse(text, NumberStyles.None, culture, out days) && days is >= 1 and <= 36500;

    private bool TryDays(out int days)
    {
        if (!TryRetentionDays(DaysBox.Text, CultureInfo.CurrentCulture, out days))
        {
            StatusText.Text = Loc.ArchiveZeroDaysRefused;
            DaysBox.Focus();
            return false;
        }
        return true;
    }

    /// <summary>A finite archive retention must stay above the compaction age, otherwise
    /// runs would be deleted before they are ever compacted.</summary>
    internal static bool CompactionDaysAllowed(int days, int retentionDays) =>
        retentionDays <= 0 || days < retentionDays;

    private async void OnCompact(object sender, RoutedEventArgs e)
    {
        await UiGuard.Run(async () =>
        {
            if (!TryDays(out var days)) return;
            if (_config is null) return;
            if (!CompactionDaysAllowed(days, _config.ArchiveRetentionDays))
            {
                StatusText.Text = string.Format(CultureInfo.CurrentCulture, Loc.ArchiveDaysAboveRetention,
                    _config.ArchiveRetentionDays);
                DaysBox.Focus();
                return;
            }
            try
            {
                var path = _config.DatabasePath;
                var count = await Task.Run(() => new KnownDevicesDb(path).ListScanArchive()
                    .Count(x => x.StartedAt < DateTimeOffset.UtcNow.AddDays(-days)));
                if (!Confirm(string.Format(CultureInfo.CurrentCulture, Loc.ArchiveConfirmCompact, count, days))) return;
                await MutateAsync(db => db.CompactScanArchiveOlderThan(days), Loc.ArchiveCompacted);
            }
            catch (Exception ex) { ShowError(ex); }
        }, Loc.ArchiveTitle);
    }

    private async void OnPurge(object sender, RoutedEventArgs e)
    {
        await UiGuard.Run(async () =>
        {
            if (!TryDays(out var days)) return;
            if (_config is null) return;
            try
            {
                var path = _config.DatabasePath;
                var count = await Task.Run(() => new KnownDevicesDb(path).ListScanArchive()
                    .Count(x => x.StartedAt < DateTimeOffset.UtcNow.AddDays(-days)));
                if (!Confirm(string.Format(CultureInfo.CurrentCulture, Loc.ArchiveConfirmPurge, count, days))) return;
                await MutateAsync(db => db.PurgeScanArchiveOlderThan(days), Loc.ArchiveDeleted);
            }
            catch (Exception ex) { ShowError(ex); }
        }, Loc.ArchiveTitle);
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        await UiGuard.Run(async () =>
        {
            if (_config is null) return;
            var dialog = new OpenFileDialog { Filter = Loc.ArchiveFileFilter };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            var input = dialog.FileName;
            if (!Confirm(Loc.ArchiveConfirmImport)) return;
            await MutateAsync(db =>
            {
                var result = db.ImportScanArchive(input);
                return new object[] { result.Imported, result.Skipped };
            }, Loc.ArchiveImported);
        }, Loc.ArchiveTitle);
    }

    private async void OnBackup(object sender, RoutedEventArgs e)
    {
        await UiGuard.Run(async () =>
        {
            if (_config is null) return;
            var directory = Path.Combine(ScanConfig.ResolvePath(_config.OutputDirectory), "ArchiveBackups");
            string? created = null;
            var finished = false;
            await MutateAsync(db =>
            {
                created = BackupIfAny(db, directory);
                finished = true;
                return Array.Empty<object>();
            }, null);
            if (!finished) return;
            BackupNoticeRequested?.Invoke(this, created is null
                ? new ArchiveBackupNotice(Loc.ArchiveBackupNothing, null)
                : new ArchiveBackupNotice(string.Format(Loc.ArchiveBackupSavedFmt, created), Path.GetDirectoryName(created)));
        }, Loc.ArchiveTitle);
    }

    /// <summary>Writes a manual backup unless there is nothing to back up (null). A missing archive
    /// key is a failure of its own, never reported as an empty archive.</summary>
    internal static string? BackupIfAny(KnownDevicesDb db, string directory)
    {
        if (!db.IsUsable) throw new ArchiveKeyException();
        if (db.ListScanArchive().Count == 0) return null;
        return db.CreateManualScanArchiveBackup(directory) ?? throw new ArchiveKeyException();
    }

    private void ShowError(Exception ex)
    {
        Core.Log.Warn(Core.LogArea.Db, "Scan archive action failed", ex);
        StatusText.Text = ex switch
        {
            ArchiveKeyException => Loc.ArchiveKeyUnavailable,
            InvalidDataException => Loc.ArchiveInvalidFile,
            UnauthorizedAccessException => Loc.ArchiveAccessDenied,
            IOException => Loc.ArchiveIoError,
            ArchiveRangeException => Loc.ArchiveInvalidRange,
            _ => Loc.ArchiveUnexpectedError
        };
    }

    /// <summary>The first network of a run plus how many more it covered ("10.0.0.0/24 +2").</summary>
    internal static string NetworkSummary(ArchivedScan scan)
    {
        if (scan.Subnets is not { Length: > 0 } subnets) return scan.Network;
        return subnets.Length == 1 ? subnets[0] : $"{subnets[0]} +{subnets.Length - 1}";
    }

    internal static string NetworkFull(ArchivedScan scan) =>
        scan.Subnets is { Length: > 0 } subnets ? string.Join(", ", subnets) : scan.Network;

    /// <summary>Run time as hh:mm:ss, with the stopped marker folded in for cancelled runs.</summary>
    internal static string DurationText(ArchivedScan scan)
    {
        var text = (scan.FinishedAt - scan.StartedAt).ToString(@"hh\:mm\:ss", CultureInfo.CurrentCulture);
        return scan.Cancelled ? $"{text} ({Loc.ArchiveStopped})" : text;
    }

    internal sealed record RunRow(ArchivedScan Scan)
    {
        public string DateText => Scan.StartedAt.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
        /// <summary>Grouping key of the scan (kept for matching); the table shows <see cref="NetworkText"/>.</summary>
        public string Network => Scan.Network;
        public string NetworkText => NetworkSummary(Scan);
        public string NetworkFull => ArchivePage.NetworkFull(Scan);
        public int DeviceCount => Scan.DeviceCount;
        public string DurationText => ArchivePage.DurationText(Scan);
    }

    private sealed record DeviceRow(ArchivedDevice Device)
    {
        public string Ip => Device.Ip;
        public string? Hostname => Device.Hostname;
        public string Status => Device.Online ? Loc.Online : Loc.Offline;
        public string Latency => Device.AvgMs?.ToString("0.0", CultureInfo.CurrentCulture) ?? Loc.NotAvailable;
    }

    private sealed record HistoryRow(ArchivedDeviceStatistics Stats)
    {
        public string Ip => Stats.Ip;
        public string Availability => Stats.AvailabilityPercent.ToString("0.0'%'", CultureInfo.CurrentCulture);
        public string Latency => Stats.AvgMs?.ToString("0.0", CultureInfo.CurrentCulture) ?? Loc.NotAvailable;
        public string FirstSeen => Stats.FirstSeenAt.LocalDateTime.ToString("d", CultureInfo.CurrentCulture);
        public string LastSeen => Stats.LastSeenAt.LocalDateTime.ToString("d", CultureInfo.CurrentCulture);
    }

    private sealed record TrendRow(DateTimeOffset At, ArchivedDevice Device)
    {
        public string Date => At.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
        public string Availability => (100.0 * Device.OnlineCount / Math.Max(1, Device.ObservedCount))
            .ToString("0.0'%'", CultureInfo.CurrentCulture);
        public string Latency => Device.AvgMs?.ToString("0.0", CultureInfo.CurrentCulture) ?? Loc.NotAvailable;
    }
}
