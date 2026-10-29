using System.Windows;
using System.Windows.Controls;
using System.IO;
using IpScanner.Core.Data;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;
using IpScanner.Views;

namespace IpScanner;

// An attached flag the TextBox style's ControlTemplate.Triggers reacts to,
// so an invalid path field's red border is a live DynamicResource lookup that follows
// a later theme switch instead of a one-off FindResource("Red") snapshot taken at
// validation time.
public static class PathFieldValidation
{
    public static readonly DependencyProperty IsInvalidProperty = DependencyProperty.RegisterAttached(
        "IsInvalid", typeof(bool), typeof(PathFieldValidation), new PropertyMetadata(false));

    public static bool GetIsInvalid(DependencyObject obj) => (bool)obj.GetValue(IsInvalidProperty);
    public static void SetIsInvalid(DependencyObject obj, bool value) => obj.SetValue(IsInvalidProperty, value);
}

public partial class MainWindow : Window
{
    // ── Live syntax validation for the path fields ──
    // Path fields: validate on every keystroke but only persist on LostFocus
    // or overlay close - otherwise Directory.CreateDirectory fires per character.
    private void OnOutputDirValidate(object sender, TextChangedEventArgs e)
        => ValidatePath(OutputDirBox, OutputDirError, toOwnedDirectory: t => t);

    private void OnDbPathValidate(object sender, TextChangedEventArgs e)
        => ValidatePath(DbPathBox, DbPathError, toOwnedDirectory: t => Path.GetDirectoryName(Path.GetFullPath(t)) is { Length: > 0 } d ? d : t);

    private void OnPathFieldCommit(object sender, RoutedEventArgs e) => ApplyInstant();

    private static bool IsValidPathText(string path) => ConfigImport.IsValidPathText(path);

    // toOwnedDirectory, when given, maps the typed text to the folder that would hold the
    // app's files - flagged in red (with an explanation) if that folder is a drive root, the
    // user's profile or a shared well-known folder such as Documents or Desktop, matching the
    // refusal FileProtection.IsProtectableDirectory enforces. Subfolders are accepted.
    private void ValidatePath(TextBox box, TextBlock error, bool allowEmpty = false, Func<string, string>? toOwnedDirectory = null)
    {
        var text = box.Text;
        bool empty = allowEmpty && text.Trim().Length == 0;
        bool syntaxOk = empty || IsValidPathText(text);
        bool unsafeDir = syntaxOk && !empty && toOwnedDirectory is not null
            && !FileProtection.IsProtectableDirectory(toOwnedDirectory(text.Trim()));
        bool ok = syntaxOk && !unsafeDir;
        PathFieldValidation.SetIsInvalid(box, !ok);
        if (ok)
        {
            error.Visibility = Visibility.Collapsed;
        }
        else
        {
            error.Text = unsafeDir ? Loc.UnsafeDataPath : Loc.InvalidPath;
            error.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Holds a typed number to its range; unparsable text yields the fallback and counts as unchanged.</summary>
    internal static (int value, bool changed) Clamp(string text, int min, int max, int fallback = 0)
    {
        if (!int.TryParse(text.Trim(), out var parsed)) return (Math.Clamp(fallback, min, max), false);
        var clamped = Math.Clamp(parsed, min, max);
        return (clamped, clamped != parsed);
    }

    private ScanConfig ReadSettings()
    {
        // Bounds and fallback defaults come from the single shared source
        // (ScanConfig.Bounds) - the same one ConfigManager.Load uses - so a
        // typed value can't land un-clamped just because it came from the live
        // dialog instead of the .conf file, and the two never disagree.
        // A corrected value is shown in the box once the user leaves it; while the caret is inside,
        // the typed text stays so a half-typed number is not rewritten mid-keystroke.
        int Bounded(System.Windows.Controls.TextBox box, int lo, int hi, int fallback)
        {
            var (value, changed) = Clamp(box.Text, lo, hi, fallback);
            if (changed && !box.IsKeyboardFocused)
            {
                var was = _loadingSettings;
                _loadingSettings = true;
                try { box.Text = value.ToString(System.Globalization.CultureInfo.InvariantCulture); }
                finally { _loadingSettings = was; }
            }
            return value;
        }

        double D(string s, double d) => double.TryParse(s.Trim(),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : d;
        double BoundedD(System.Windows.Controls.TextBox box, double fallback)
            => Math.Clamp(D(box.Text, fallback), ScanConfig.Bounds.HeatMsMin, ScanConfig.Bounds.HeatMsMax);

        // Palette.Heat assumes ascending thresholds - an out-of-order edit is
        // silently corrected by sorting, with a hint shown so it isn't mistaken
        // for the value simply not having saved.
        var heat = new[]
        {
            BoundedD(HeatMs1Box, ScanConfig.Bounds.HeatMs1Default), BoundedD(HeatMs2Box, ScanConfig.Bounds.HeatMs2Default),
            BoundedD(HeatMs3Box, ScanConfig.Bounds.HeatMs3Default), BoundedD(HeatMs4Box, ScanConfig.Bounds.HeatMs4Default),
        };
        var sortedHeat = heat.OrderBy(v => v).ToArray();
        HeatOrderHint.Visibility = sortedHeat.SequenceEqual(heat) ? Visibility.Collapsed : Visibility.Visible;

        // Start from a copy of the live config so every setting without a control in this
        // overlay (sidebar state, colors, sort, widths, profiles, ...) survives an edit.
        var c = _config.Clone();
        c.ConfigDirectory = MakeRelative(ConfDirFromInput(ConfDirBox.Text));
        c.GraphsEnabled = GraphsEnabledBox.IsChecked == true;
        c.NetworkGraphsEnabled = NetworkGraphsBox.IsChecked == true;
        c.AutoGraphForPinned = AutoGraphPinnedBox.IsChecked == true;
        c.ShowOfflineFromDb = OfflineToggle.IsChecked == true;
        c.MinimizeToTray = MinimizeTrayBox.IsChecked == true;
        c.CheckForUpdates = CheckForUpdatesBox.IsChecked == true;
        c.GraphMaxSeconds = Bounded(GraphMaxBox, ScanConfig.Bounds.GraphMaxSecondsMin, ScanConfig.Bounds.GraphMaxSecondsMax, ScanConfig.Bounds.GraphMaxSecondsDefault);
        // The "unlimited" checkbox forces 0 (one thread per device) regardless of whatever
        // is left in the (now-disabled) number field.
        c.ScanThreads = UnlimitedThreadsBox.IsChecked == true ? 0
            : Bounded(ScanThreadsBox, Math.Max(1, ScanConfig.Bounds.ScanThreadsMin), ScanConfig.Bounds.ScanThreadsMax, ScanConfig.Bounds.ScanThreadsDefault);
        c.UiScalePercent = Bounded(UiScaleBox, ScanConfig.Bounds.UiScalePercentMin, ScanConfig.Bounds.UiScalePercentMax, ScanConfig.Bounds.UiScalePercentDefault);
        c.PingIntervalMs = Bounded(IntervalBox, ScanConfig.Bounds.PingIntervalMsMin, ScanConfig.Bounds.PingIntervalMsMax, ScanConfig.Bounds.PingIntervalMsDefault);
        c.OfflineAfterFailedPings = Bounded(OfflineAfterBox, ScanConfig.Bounds.OfflineAfterFailedPingsMin, ScanConfig.Bounds.OfflineAfterFailedPingsMax, ScanConfig.Bounds.OfflineAfterFailedPingsDefault);
        c.OfflineRecheckSeconds = OfflineRecheckOffBox.IsChecked == true ? 0
            : Bounded(OfflineRecheckBox, Math.Max(1, ScanConfig.Bounds.OfflineRecheckSecondsMin), ScanConfig.Bounds.OfflineRecheckSecondsMax, ScanConfig.Bounds.OfflineRecheckSecondsDefault);
        c.EnableInternetPing = EnableInternetBox.IsChecked == true;
        c.InternetTimeoutMs = Bounded(InternetTimeoutBox, ScanConfig.Bounds.InternetTimeoutMsMin, ScanConfig.Bounds.InternetTimeoutMsMax, ScanConfig.Bounds.InternetTimeoutMsDefault);
        // A path flagged red by ValidatePath must not overwrite the last known-good value
        // (the clone already holds it): otherwise it saves silently and only fails later,
        // when the export actually runs. That includes a protected folder such as a drive
        // root or the user's profile, same check ValidatePath uses to flag it red.
        if (IsValidPathText(OutputDirBox.Text) && FileProtection.IsProtectableDirectory(OutputDirBox.Text.Trim()))
            c.OutputDirectory = MakeRelative(OutputDirBox.Text.Trim());
        if (DbPathBox.Text.Trim().Length == 0)
            c.DatabasePath = ScanConfig.DefaultDatabasePath;
        else if (IsValidPathText(DbPathBox.Text) && FileProtection.IsProtectableDirectory(
            Path.GetDirectoryName(Path.GetFullPath(DbPathBox.Text.Trim())) is { Length: > 0 } dbDir ? dbDir : DbPathBox.Text.Trim()))
            c.DatabasePath = MakeRelative(DbPathBox.Text.Trim());
        // TXT/CSV toggles live only in the sidebar pills; the settings dialog has no checkboxes for them.
        c.FileOutput = TxtToggle.IsChecked == true;
        c.ExportCsv = CsvToggle.IsChecked == true;
        c.HtmlReport = HtmlReportBox.IsChecked == true;
        c.ExportPorts = ExportPortsBox.IsChecked == true;
        c.ShowSkippedLegend = ShowSkippedLegendBox.IsChecked == true;
        c.DbRetentionDays = ScanConfig.Bounds.DbRetentionDaysOptions[
            Math.Clamp(DbRetentionBox.SelectedIndex, 0, ScanConfig.Bounds.DbRetentionDaysOptions.Length - 1)];
        c.ArchiveRetentionDays = ScanConfig.Bounds.ArchiveRetentionDaysOptions[
            Math.Clamp(ArchiveRetentionBox.SelectedIndex, 0, ScanConfig.Bounds.ArchiveRetentionDaysOptions.Length - 1)];
        c.ArchiveCompactionDays = _compactionDayItems[
            Math.Clamp(ArchiveCompactionBox.SelectedIndex, 0, _compactionDayItems.Length - 1)];
        c.ArchiveBackupRetentionDays = _backupDayItems[
            Math.Clamp(ArchiveBackupRetentionBox.SelectedIndex, 0, _backupDayItems.Length - 1)];
        c.KnownDevicesDb = KnownDbBox.IsChecked == true;
        c.ScanHistory = ScanHistoryBox.IsChecked == true;
        c.Language = LanguageModes[Math.Max(0, LanguageBox.SelectedIndex)];
        c.Theme = ThemeModes[Math.Max(0, ThemeBox.SelectedIndex)];
        c.CompactRows = CompactRowsBox.IsChecked == true;
        // The repeat-scan interval maps 1:1 onto a fixed option list.
        c.RepeatScanMinutes = ScanConfig.Bounds.RepeatScanMinutesOptions[
            Math.Clamp(RepeatScanBox.SelectedIndex, 0, ScanConfig.Bounds.RepeatScanMinutesOptions.Length - 1)];
        c.NotifyScanDone = NotifyScanDoneBox.IsChecked == true;
        c.NotifyNewDevice = NotifyNewDeviceBox.IsChecked == true;
        c.StartMinimized = StartMinimizedBox.IsChecked == true;
        c.DiscoveryModes = ReadDiscoveryModes(c.DiscoveryModes);
        c.TcpPingPorts = Core.Scanner.Discovery.DiscoveryModes.NormalizePorts(TcpPingPortsBox.Text, Core.Scanner.Discovery.DiscoveryModes.DefaultTcpPorts);
        c.UdpPingPorts = Core.Scanner.Discovery.DiscoveryModes.NormalizePorts(UdpPingPortsBox.Text, Core.Scanner.Discovery.DiscoveryModes.DefaultUdpPorts);
        c.OsFingerprint = OsFingerprintBox.IsChecked == true;
        c.PortServiceDetection = ServiceDetectionBox.IsChecked == true;
        c.ScanPortRange = ReadPortRange(c.ScanPortRange);
        c.HeatMs1 = sortedHeat[0]; c.HeatMs2 = sortedHeat[1]; c.HeatMs3 = sortedHeat[2]; c.HeatMs4 = sortedHeat[3];
        c.PortScanConnectTimeoutMs = Bounded(PortScanConnectTimeoutBox, ScanConfig.Bounds.PortScanConnectTimeoutMsMin, ScanConfig.Bounds.PortScanConnectTimeoutMsMax, ScanConfig.Bounds.PortScanConnectTimeoutMsDefault);
        c.PortScanBannerTimeoutMs = Bounded(PortScanBannerTimeoutBox, ScanConfig.Bounds.PortScanBannerTimeoutMsMin, ScanConfig.Bounds.PortScanBannerTimeoutMsMax, ScanConfig.Bounds.PortScanBannerTimeoutMsDefault);
        c.PortScanParallel = Bounded(PortScanParallelismBox, ScanConfig.Bounds.PortScanParallelMin, ScanConfig.Bounds.PortScanParallelMax, ScanConfig.Bounds.PortScanParallelDefault);
        c.PortScanTiming = (int)Math.Round(PortScanTimingSlider.Value);
        c.PortScanRetries = Bounded(PortScanRetriesBox, ScanConfig.Bounds.PortScanRetriesMin, ScanConfig.Bounds.PortScanRetriesMax, ScanConfig.Bounds.PortScanRetriesDefault);
        c.PortScanDelayMs = Bounded(PortScanDelayBox, ScanConfig.Bounds.PortScanDelayMsMin, ScanConfig.Bounds.PortScanDelayMsMax, ScanConfig.Bounds.PortScanDelayMsDefault);
        c.PortScanMaxRate = Bounded(PortScanMaxRateBox, ScanConfig.Bounds.PortScanMaxRateMin, ScanConfig.Bounds.PortScanMaxRateMax, ScanConfig.Bounds.PortScanMaxRateDefault);
        c.PortScanIntensity = Bounded(PortScanIntensityBox, ScanConfig.Bounds.PortScanIntensityMin, ScanConfig.Bounds.PortScanIntensityMax, ScanConfig.Bounds.PortScanIntensityDefault);
        c.CustomPorts = CustomPortsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        c.SnmpCommunity = SnmpCommunityBox.Text.Trim().Length > 0
            ? SnmpCommunityBox.Text.Trim()[..Math.Min(SnmpCommunityBox.Text.Trim().Length, ScanConfig.Bounds.SnmpCommunityMaxLength)]
            : "public";
        return c;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.OutputDir };
        if (DirOf(OutputDirBox.Text) is { } dir) dlg.InitialDirectory = dir;
        if (dlg.ShowDialog(this) == true) OutputDirBox.Text = MakeRelative(dlg.FolderName);
    }

    private async void OnClearDb(object sender, RoutedEventArgs e)
    {
        await UiGuard.Run(async () =>
        {
            var dialog = new Views.ArchiveConfirmDialog(Loc.ClearDbConfirm, Loc.ClearDbIncludeArchive,
                checkboxDefault: true, title: Loc.ClearDb) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            bool includeArchive = dialog.IsChecked;
            var dbPath = _config.DatabasePath;
            var backups = System.IO.Path.Combine(ScanConfig.ResolvePath(_config.OutputDirectory), "ArchiveBackups");
            var trigger = sender as UIElement;
            if (trigger is not null) trigger.IsEnabled = false;
            try
            {
                var deleted = await Task.Run(() =>
                {
                    var count = new KnownDevicesDb(dbPath).Clear(includeArchive);
                    if (includeArchive) KnownDevicesDb.DeleteArchiveBackups(backups);
                    return count;
                });
                ShowNotice(Loc.ClearDbDone(deleted), null, null);
            }
            catch (Exception ex)
            {
                Core.Log.Warn(Core.LogArea.Db, "Clearing the database failed", ex);
                Views.ErrorDialog.Show(this, Loc.ClearDbFailedTitle, ex, ScanConfig.ResolvePath(dbPath));
            }
            finally
            {
                if (trigger is not null) trigger.IsEnabled = true;
            }
        }, Loc.ClearDb);
    }

    private void OnBrowseDb(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.DbFile,
            Filter = Loc.DbFileFilter,
            FileName = ScanConfig.DefaultDatabaseFileName,
            OverwritePrompt = false,    // existing db is opened, not replaced
        };
        if (DirOf(DbPathBox.Text) is { } dir) dlg.InitialDirectory = dir;
        if (dlg.ShowDialog(this) == true) DbPathBox.Text = MakeRelative(dlg.FileName);
    }

    // Extract a CIDR number from the dropdown text ("/24", "24", "/16" ...).
    private static int ParseCidr(string? text)
    {
        var digits = new string((text ?? "").Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) && n is >= 1 and <= 32 ? n : 24;
    }

    // Warn before scanning a range spanning more than one /24 (up to the /16 cap).
    private bool ConfirmLargeRange(int cidr, int subnets)
    {
        var r = MessageDialog.Show(this, Loc.LargeRangeMsg(cidr, subnets, (long)subnets * Core.Net.Ipv4.HostsPerSubnet),
            Loc.LargeRange, MessageDialogButtons.YesNo, MessageDialogKind.Warning);
        return r == MessageDialogResult.Yes;
    }

    /// <summary>Takes the header ping count (the only ping-count setting) into the config and
    /// saves it when it changed, so the next start begins with the same value.</summary>
    private void ApplySelectedPingCount()
    {
        // Editable dropdown: free text wins, fall back to the selected item.
        var text = string.IsNullOrWhiteSpace(PingCountBox.Text)
            ? PingCountBox.SelectedItem?.ToString()
            : PingCountBox.Text.Trim();
        var before = _config.PingCount;
        _config.PingCount = text == "∞" || text == "-1" ? ScanConfig.InfinitePingCount
            : int.TryParse(text, out var n) && n > 0 ? n : 10;
        UpdateOfflineRecheckEnabled();
        if (_config.PingCount != before) PersistConfigDebounced();
    }
}
