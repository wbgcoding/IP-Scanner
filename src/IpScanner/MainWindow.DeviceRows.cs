using System.Windows;
using System.Windows.Controls;
using System.IO;
using System.Linq;
using IpScanner.Core.Data;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;
using IpScanner.ViewModels;
using IpScanner.Views;

namespace IpScanner;

public partial class MainWindow : Window
{
    // ── Row context menu + detail-view service buttons ──
    private static DeviceViewModel? MenuVm(object sender) => (sender as FrameworkElement)?.DataContext as DeviceViewModel;

    // A context action targets the selection only when the clicked row belongs to it.
    // Right-clicking outside the selection keeps that unrelated selection untouched.
    private System.Collections.Generic.List<DeviceViewModel> TargetDevices(object sender)
    {
        var vm = MenuVm(sender);
        if (vm is null) return new();
        var selected = DeviceGrid.SelectedItems.OfType<DeviceViewModel>().ToList();
        return selected.Count > 1 && selected.Contains(vm) ? selected : new System.Collections.Generic.List<DeviceViewModel> { vm };
    }

    private bool Copy(string? text, string? successNotice = null)
    {
        if (string.IsNullOrEmpty(text)) { ShowNotice(Loc.NothingToCopy, null, null); return false; }
        var ok = ClipboardHelper.TrySetText(text);
        ShowNotice(ok ? successNotice ?? Loc.CopiedOne : Loc.CopyFailedMsg, null, null);
        return ok;
    }

    private void OnCopyIp(object sender, RoutedEventArgs e) => Copy(string.Join("\r\n", TargetDevices(sender).Select(v => v.Ip)));
    private void OnCopyMac(object sender, RoutedEventArgs e)
        => Copy(string.Join("\r\n", TargetDevices(sender).Select(v => v.Model.Mac).Where(m => !string.IsNullOrEmpty(m))));
    private void OnCopyHostname(object sender, RoutedEventArgs e)
        => Copy(string.Join("\r\n", TargetDevices(sender).Select(v => v.CopyableHostname).Where(h => !string.IsNullOrEmpty(h))));

    // A device's open ports as text, one line per service - same
    // "port label padded, then name" shape TxtExporter already uses, so the
    // clipboard/file output reads the same as a full TXT report.
    private static string? FormatPortsText(DeviceViewModel? vm)
        => vm is null || vm.Services.Count == 0 ? null : Core.Export.TxtExporter.FormatPortsText(vm.Ip, vm.CopyableHostname, vm.Services);

    // Separate each selected device's port block with a blank line.
    private void OnCopyOpenPorts(object sender, RoutedEventArgs e)
    {
        var text = string.Join("\r\n", TargetDevices(sender).Select(FormatPortsText).Where(t => !string.IsNullOrEmpty(t)));
        if (text.Length == 0) { ShowNotice(Loc.NothingToCopy, null, null); return; }
        Copy(text);
    }

    private void OnCopyRowAsText(object sender, RoutedEventArgs e) => CopyRowsAsText(TargetDevices(sender));

    // Ctrl+C on the focused table copies the whole selection the same way.
    internal void CopySelectedRowAsText() => CopyRowsAsText(DeviceGrid.SelectedItems.OfType<DeviceViewModel>().ToList());

    private void CopyRowsAsText(System.Collections.Generic.IReadOnlyCollection<DeviceViewModel> vms)
    {
        if (vms.Count == 0) { ShowNotice(Loc.NothingToCopy, null, null); return; }
        Copy(Core.Export.TxtExporter.FormatRowsAsText(vms.Select(vm => new Core.Export.TxtExporter.RowFields(
            vm.Ip, vm.Status, vm.Hostname, vm.AvgDisplay, vm.MinDisplay, vm.MaxDisplay, vm.LastDisplay,
            vm.LossDisplay, vm.JitterDisplay, vm.ProgressDisplay, vm.Mac, vm.Vendor, vm.Note))),
            Loc.RowsCopied(vms.Count));
    }

    // On-demand hostname re-resolution + per-device opt-out.
    private async void OnResolveHostnameAgain(object sender, RoutedEventArgs e)
    {
        await UiGuard.Run(async () =>
        {
            var vm = MenuVm(sender);
            if (vm is null) return;
            await _vm.ResolveHostnameAgainAsync(vm);
        }, Loc.ResolveHostnameAgain);
    }

    private void OnToggleHostnameRetry(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item) return;
        var vm = MenuVm(sender);
        if (vm is null) return;
        _vm.SetNoHostnameRetry(vm, item.IsChecked);
    }

    // Mute a device expected to go offline routinely (e.g. a printer
    // switched off at night) - no offline color, no rogue highlight, no fast
    // offline-recheck burst; still pinged normally. The same state is applied to
    // every selected device.
    private void OnToggleMuteDevice(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item) return;
        foreach (var vm in TargetDevices(sender)) _vm.SetMuted(vm, item.IsChecked);
    }

    // Accept a rogue device into the known-devices DB - removes the
    // orange highlight immediately (menu entry itself only shows for rogue rows).
    // Mark every rogue device in the selection and leave known devices unchanged.
    private async void OnMarkAsKnown(object sender, RoutedEventArgs e)
    {
        await UiGuard.Run(async () =>
        {
            var targets = TargetDevices(sender).Where(v => v.IsRogue).ToList();
            var trigger = sender as UIElement;
            if (trigger is not null) trigger.IsEnabled = false;
            try
            {
                Exception? failure = null;
                foreach (var vm in targets)
                    failure ??= await _vm.MarkAsKnownAsync(vm);
                if (failure is not null)
                    ErrorDialog.Show(this, Loc.MarkAsKnown, failure, ScanConfig.ResolvePath(_config.DatabasePath));
            }
            finally
            {
                if (trigger is not null) trigger.IsEnabled = true;
            }
        }, Loc.MarkAsKnown);
    }

    private void OnSavePortsClick(object sender, RoutedEventArgs e)
    {
        var vm = MenuVm(sender);
        var text = FormatPortsText(vm);
        if (text is null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.SavePortsTitle,
            Filter = Loc.PortsFileFilter,
            FileName = $"{vm!.Ip}_ports.txt",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, text);
            var folder = Path.GetDirectoryName(dlg.FileName);
            ShowNotice(string.Format(Loc.SavedFmt, dlg.FileName), folder is null ? null : Loc.OpenFolder,
                folder is null ? null : () => OpenFolderInExplorer(folder));
        }
        catch (Exception ex) { Core.Log.Warn(Core.LogArea.Tools, "Saving the port list failed", ex); ErrorDialog.Show(this, Loc.SavePortsFailedTitle, ex, dlg.FileName); }
    }

    // Routes through the same stronger send as the Tools-window page
    // (directed broadcast + general broadcast, ports 7 and 9, repeated) instead of
    // a single-packet send. The result is reported in a themed message dialog.
    private async void OnWake(object sender, RoutedEventArgs e)
    {
        await UiGuard.Run(async () =>
        {
            var vm = MenuVm(sender);
            var mac = vm?.Model.Mac;
            System.Net.IPAddress? directed = null;
            var info = Core.Scanner.NetworkDetector.DetectFast();
            if (info.Ip is { } ip && Core.Net.Ipv4.DirectedBroadcast(ip, info.SubnetMask) is { } b)
                System.Net.IPAddress.TryParse(b, out directed);

            if (mac is null)
            {
                MessageDialog.Show(this, Loc.WakeNoMacMsg, Loc.WakeDevice, MessageDialogButtons.Ok, MessageDialogKind.Warning);
                return;
            }
            bool sent = await Core.Scanner.WakeOnLan.SendAsync(mac, directed);
            MessageDialog.Show(this, sent ? Loc.WakeSentMsg(vm?.Ip ?? "") : Loc.WakeFailedMsg,
                Loc.WakeDevice, MessageDialogButtons.Ok, sent ? MessageDialogKind.Info : MessageDialogKind.Warning);
        }, Loc.WakeDevice);
    }

    /// <summary>Wake-on-LAN only works within the same broadcast domain, and
    /// needs a known MAC - gray the item out and explain why when neither holds.</summary>
    private void OnDeviceRowMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        if (menu.Items.OfType<MenuItem>().LastOrDefault() is not { } wakeItem) return;
        if ((menu.PlacementTarget as FrameworkElement)?.DataContext is not DeviceViewModel vm) return;
        var targets = TargetDevices(menu.PlacementTarget);
        bool hasMac = vm.HasMac;
        wakeItem.IsEnabled = hasMac;
        wakeItem.ToolTip = hasMac ? Loc.WakeTooltip : Loc.WakeNoMacTooltip;

        // Hostname retry is a per-row action. Mute is checked only when every
        // targeted device is already muted.
        if (menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "StopHostnameRetryItem") is { } stopItem)
            stopItem.IsChecked = _vm.IsNoHostnameRetry(vm);
        if (menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "MuteDeviceItem") is { } muteItem)
            muteItem.IsChecked = targets.All(_vm.IsMuted);

        // Mark-as-known is offered when at least one targeted device is rogue.
        if (menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "MarkAsKnownItem") is { } markItem)
            markItem.Visibility = targets.Any(v => v.IsRogue) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnDeviceGridSelectionChanged(object sender, SelectionChangedEventArgs e)
        => RefreshSelectionCount();

    private void RefreshSelectionCount()
    {
        int n = DeviceGrid.SelectedItems.Count;
        SelectionCountText.Visibility = n > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (n > 1) SelectionCountText.Text = Loc.SelectedCount(n);
    }

    private void OnPinSelected(object sender, RoutedEventArgs e)
    {
        foreach (var vm in TargetDevices(sender).Where(v => !v.IsPinned))
            Sidebar.Pin(vm.Ip);
    }

    private void OnScanSelectedPorts(object sender, RoutedEventArgs e)
    {
        var targets = TargetDevices(sender);
        if (targets.Count == 0 || !ConfirmFullPortScan(targets)) return;
        foreach (var vm in targets) Core.BackgroundTask.Observe(vm.StartFullPortScan(), "Full port scan");
        ShowSelectedPortScanProgress(targets);
    }

    private void OnServiceClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Core.Scanner.NetService svc })
            Core.Scanner.PortScanner.Open(svc.Ip, svc);
    }

    // Opt-in full 1-65535 TCP port scan from the device detail view.
    private void OnScanAllPortsClick(object sender, RoutedEventArgs e)
    {
        var vm = MenuVm(sender);
        if (vm is not null && ConfirmFullPortScan(new[] { vm })) Core.BackgroundTask.Observe(vm.StartFullPortScan(), "Full port scan");
    }

    private void OnDeviceNoteLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: DeviceViewModel vm } && !vm.IsNoteTooLong && vm.HasNoteEdit)
            _vm.SetNote(vm, vm.NoteDraft);
    }

    /// <summary>A target outside the configured networks (a pinned IP, say) is always asked about,
    /// with the stronger wording, exactly like the port check tool.</summary>
    private bool ConfirmFullPortScan(IEnumerable<DeviceViewModel> targets)
    {
        // Network cards are enumerated once for the whole action, not once per selected row.
        var own4 = IpScanner.Core.Net.OwnNetworks.Build(_config.Subnets, _config.PinnedIps);
        var own6 = IpScanner.Core.Scanner.NetworkDetector.ListOwnIpv6();
        var outside = targets.Any(t => ViewModels.Tools.PortCheckToolViewModel.IsOutsideConfiguredSubnets(t.Ip, _config, own6, own4));
        return Core.Scanner.FullPortScanSafetyGate.Shared.Confirm(outside, () =>
        {
            var dialog = new FullPortScanWarningDialog(outside) { Owner = this };
            return dialog.ShowDialog() == true;
        });
    }
    private void OnCancelFullPortScanClick(object sender, RoutedEventArgs e) => MenuVm(sender)?.CancelFullPortScan();

    private System.Windows.Threading.DispatcherTimer? _selectedPortScanTimer;
    private IReadOnlyCollection<DeviceViewModel> _selectedPortScanTargets = Array.Empty<DeviceViewModel>();

    private void ShowSelectedPortScanProgress(IReadOnlyCollection<DeviceViewModel> targets)
    {
        if (targets.Count < 2) return;
        _selectedPortScanTargets = targets;
        SelectedPortScanProgress.Visibility = Visibility.Visible;
        if (_selectedPortScanTimer is null)
        {
            _selectedPortScanTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _selectedPortScanTimer.Tick += (_, _) => RefreshSelectedPortScanProgress();
        }
        _selectedPortScanTimer.Start();
        RefreshSelectedPortScanProgress();
    }

    private void RefreshSelectedPortScanProgress()
    {
        int complete = _selectedPortScanTargets.Count(vm => !vm.IsFullPortScanRunning);
        SelectedPortScanText.Text = string.Format(Loc.ScanningSelectedPortsFmt, complete, _selectedPortScanTargets.Count);
        if (complete != _selectedPortScanTargets.Count) return;
        _selectedPortScanTimer?.Stop();
        SelectedPortScanProgress.Visibility = Visibility.Collapsed;
    }

    private void OnCancelSelectedPortScanClick(object sender, RoutedEventArgs e)
    {
        foreach (var vm in _selectedPortScanTargets) vm.CancelFullPortScan();
    }

    private static readonly string[] LanguageModes = ModeLists.LanguageCodes();

    /// <summary>"Automatic (detected language)" + endonyms; rebuilt on switch so "Automatic" re-localizes.</summary>
    private void SetLanguageItems() => LanguageBox.ItemsSource = ModeLists.LanguageLabels(nameDetected: true);

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || LanguageBox.SelectedIndex < 0) return;
        var mode = LanguageModes[LanguageBox.SelectedIndex];
        if (mode == _config.Language) return;
        _config.Language = mode;
        Loc.SetLanguage(mode);
        PersistConfigDebounced();
        LocSource.Instance.Refresh();   // re-evaluates all {loc:L} bindings
        RefreshDynamicTexts();
    }

    // Date/number format, independent of the UI language above.
    private static readonly string[] DateFormatModes = ModeLists.LanguageCodes();

    /// <summary>"Automatic" + endonyms, same list as the language box.</summary>
    private void SetDateFormatItems() => DateFormatBox.ItemsSource = ModeLists.LanguageLabels();

    // 0 = off, otherwise minutes between automatic repeat scans.
    private static readonly int[] RepeatScanModes = ScanConfig.Bounds.RepeatScanMinutesOptions;

    private void SetRepeatScanItems()
        => RepeatScanBox.ItemsSource = RepeatScanModes
            .Select(n => n == 0 ? Loc.RepeatScanOff : Loc.RepeatScanMinutes(n)).ToArray();

    // 0 = unbegrenzt (never purged), otherwise retention in days.
    private static readonly int[] DbRetentionModes = ScanConfig.Bounds.DbRetentionDaysOptions;

    private void SetDbRetentionItems()
    {
        DbRetentionBox.ItemsSource = DbRetentionModes
            .Select(n => n == 0 ? Loc.DbRetentionUnlimited : Loc.DbRetentionDaysText(n)).ToArray();
        ArchiveRetentionBox.ItemsSource = ScanConfig.Bounds.ArchiveRetentionDaysOptions
            .Select(n => n == 0 ? Loc.DbRetentionUnlimited : Loc.DbRetentionDaysText(n)).ToArray();
        ArchiveCompactionBox.ItemsSource = _compactionDayItems
            .Select(n => n == 0 ? Loc.ArchiveCompactionOff : Loc.DbRetentionDaysText(n)).ToArray();
        ArchiveBackupRetentionBox.ItemsSource = _backupDayItems
            .Select(n => n == 0 ? Loc.DbRetentionUnlimited : Loc.DbRetentionDaysText(n)).ToArray();
    }

    // The two day lists below carry the loaded conf value as an extra entry when it is not a preset,
    // so a hand-edited value is shown and survives a save.
    private int[] _compactionDayItems = ScanConfig.Bounds.ArchiveCompactionDaysOptions;
    private int[] _backupDayItems = ScanConfig.Bounds.ArchiveBackupRetentionDaysOptions;

    // Import target/mode combos are transient UI choices, not config
    // fields - SetImportListsItems (MainWindow.Import.cs) preserves the current
    // selection across re-localization instead of reading it from LoadSettings.

    private void OnDateFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || DateFormatBox.SelectedIndex < 0) return;
        var mode = DateFormatModes[DateFormatBox.SelectedIndex];
        if (mode == _config.DateNumberFormat) return;
        _config.DateNumberFormat = mode;
        Core.NumberFormat.ApplyRegionOverride(mode);
        PersistConfigDebounced();
    }

    // "auto" follows the Windows light/dark setting.
    private static readonly string[] ThemeModes = { "auto", "dark", "light" };

    // The Settings overlay's left-hand nav categories, in the same
    // order as the ListBoxItems in MainWindow.xaml.
    internal static readonly string[] SettingsPages = { "Scan", "Display", "Internet", "Graphs", "Data", "App", "About", "Help" };

    /// <summary>Rebuilt on language switch so the labels re-localize.</summary>
    private void SetThemeItems()
        => ThemeBox.ItemsSource = new[] { Loc.LangAuto, Loc.ThemeDark, Loc.ThemeLight };

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || ThemeBox.SelectedIndex < 0) return;
        var mode = ThemeModes[ThemeBox.SelectedIndex];
        if (mode == _config.Theme) return;
        _config.Theme = mode;
        ThemeManager.Switch(Application.Current, mode);
        WindowTheme.Apply(this, dark: !ThemeManager.IsLight);
        // The tools window's page content already follows via DynamicResource
        // (app-wide resources), only its own native DWM title-bar frame needs a nudge.
        _toolsWindow?.RefreshTheme();
        _vm.RefreshTheme();
        PersistConfigDebounced();
    }

    /// <summary>Re-applies texts that are set from code (not via bindings).</summary>
    private void RefreshDynamicTexts()
    {
        _loadingSettings = true;
        try
        {
            SetLanguageItems();      // re-localize the "Automatic" entry
            SetThemeItems();         // re-localize the theme labels
            SetDateFormatItems();    // re-localize the date/number format list
            SetRepeatScanItems();    // re-localize the repeat-scan interval list
            SetDbRetentionItems();   // re-localize the DB retention list
            SetImportListsItems();   // re-localize the import target/mode combos
            SetProfileItems();       // re-localize the "Default" entry, re-sync selection
            ApplyFlowDirection();    // mirror for RTL languages
            LoadSettings(_config);   // language combo selection, chip tooltips
        }
        finally { _loadingSettings = false; }
        UpdateScanButton();
        _vm.RefreshAllRows();                // ONLINE/OFFLINE status texts
        _vm.RefreshLanguage();               // progress legend + "Network N" fallback titles
        RefreshStatusText();                 // update check status line
        SetResetButtonText(_resetGate?.IsArmed ?? false);
        RefreshSelectionCount();             // selection count is set from code
        RefreshTrayLanguage();               // tray tooltip + context menu
        RefreshDriverBar();                  // packet reader notice + capability matrix
        _toolsWindow?.RefreshLanguage();     // Vendor-lookup footnote text
        if (ArchiveScroll.Visibility == Visibility.Visible) _ = ArchiveOverlay.RefreshLanguageAsync();
        if (LogScroll.Visibility == Visibility.Visible) LogOverlay.RefreshLanguage();
    }

    private void OnExportConf(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = Loc.ConfFileFilter,
            FileName = ScanConfig.ConfigFileName,
        };
        if (DirOf(_config.DatabasePath) is { } dir) dlg.InitialDirectory = dir;
        if (dlg.ShowDialog(this) != true) return;
        FlushPersistConfig();
        try { ConfigManager.Save(dlg.FileName, ReadSettings(), ConfirmConfigOverwrite); }
        catch (Exception ex) { Core.Log.Warn(Core.LogArea.Config, "Exporting the settings failed", ex); ErrorDialog.Show(this, Loc.ConfExportFailedTitle, ex, dlg.FileName); }
    }

    private void OnImportConf(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Loc.ConfFileFilter };
        if (DirOf(_config.DatabasePath) is { } dir) dlg.InitialDirectory = dir;
        if (dlg.ShowDialog(this) != true) return;
        FlushPersistConfig();
        try
        {
            // Full apply, not just the Settings-dialog fields - an imported file's subnets,
            // pinned IPs, internet hosts and bar colors have no dialog control of their own
            // and would otherwise be silently discarded. Sealed values the file does not carry
            // stay; paths and profile names from another machine are vetted first, and
            // whatever fails keeps the current value.
            var imported = ConfigManager.Load(dlg.FileName);
            SealedImport.KeepCurrentUnlessImported(imported, _config, dlg.FileName);
            var (result, kept) = ConfigImport.Sanitize(_config, imported);
            ApplyFullConfig(result);
            if (kept.Count > 0) ShowNotice(Loc.ImportKeptValues(string.Join(", ", kept)), null, null);
        }
        catch (Exception ex) { Core.Log.Warn(Core.LogArea.Config, "Importing the settings failed", ex); ErrorDialog.Show(this, Loc.ConfImportFailedTitle, ex, dlg.FileName); }
    }

    private void LoadSettings(ScanConfig c)
    {
        _loadingSettings = true;
        try
        {
        OfflineToggle.IsChecked = c.ShowOfflineFromDb;
        MinimizeTrayBox.IsChecked = c.MinimizeToTray;
        CheckForUpdatesBox.IsChecked = c.CheckForUpdates;
        GraphsEnabledBox.IsChecked = c.GraphsEnabled;
        NetworkGraphsBox.IsChecked = c.NetworkGraphsEnabled;
        AutoGraphPinnedBox.IsChecked = c.AutoGraphForPinned;
        GraphMaxBox.Text = c.GraphMaxSeconds.ToString();
        // 0 = "Unbegrenzt" checkbox, checked and the number box disabled;
        // the box itself always shows a real, editable number to start from.
        UnlimitedThreadsBox.IsChecked = c.ScanThreads == 0;
        ScanThreadsBox.IsEnabled = c.ScanThreads != 0;
        ScanThreadsBox.Text = (c.ScanThreads == 0 ? ScanConfig.Bounds.ScanThreadsDefault : c.ScanThreads).ToString();
        UiScaleBox.Text = c.UiScalePercent.ToString();
        IntervalBox.Text = c.PingIntervalMs.ToString();
        OfflineAfterBox.Text = c.OfflineAfterFailedPings.ToString();
        // Off is a checkbox; the number box always shows a real value.
        OfflineRecheckOffBox.IsChecked = c.OfflineRecheckSeconds == 0;
        OfflineRecheckBox.Text = (c.OfflineRecheckSeconds > 0 ? c.OfflineRecheckSeconds : ScanConfig.Bounds.OfflineRecheckSecondsDefault).ToString();
        UpdateOfflineRecheckEnabled();
        EnableInternetBox.IsChecked = c.EnableInternetPing;
        InternetTimeoutBox.Text = c.InternetTimeoutMs.ToString();
        OutputDirBox.Text = MakeRelative(c.OutputDirectory);
        DbPathBox.Text = MakeRelative(c.DatabasePath);
        ConfDirBox.Text = MakeRelative(Path.Combine(
            c.ConfigDirectory.Length > 0 ? c.ConfigDirectory : ScanConfig.DefaultConfigDirectory,
            ScanConfig.ConfigFileName));
        KnownDbBox.IsChecked = c.KnownDevicesDb;
        ScanHistoryBox.IsChecked = c.ScanHistory;
        LanguageBox.SelectedIndex = Math.Max(0, Array.IndexOf(LanguageModes, c.Language));
        ThemeBox.SelectedIndex = Math.Max(0, Array.IndexOf(ThemeModes, c.Theme));
        DateFormatBox.SelectedIndex = Math.Max(0, Array.IndexOf(DateFormatModes, c.DateNumberFormat));
        CompactRowsBox.IsChecked = c.CompactRows;
        RepeatScanBox.SelectedIndex = Math.Max(0, Array.IndexOf(RepeatScanModes, c.RepeatScanMinutes));
        NotifyScanDoneBox.IsChecked = c.NotifyScanDone;
        NotifyNewDeviceBox.IsChecked = c.NotifyNewDevice;
        StartMinimizedBox.IsChecked = c.StartMinimized;
        TcpPingPortsBox.Text = c.TcpPingPorts;
        UdpPingPortsBox.Text = c.UdpPingPorts;
        OsFingerprintBox.IsChecked = c.OsFingerprint;
        RefreshDiscoveryRows(c.DiscoveryModes);
        HtmlReportBox.IsChecked = c.HtmlReport;
        ExportPortsBox.IsChecked = c.ExportPorts;
        DbRetentionBox.SelectedIndex = Math.Max(0, Array.IndexOf(DbRetentionModes, c.DbRetentionDays));
        ArchiveRetentionBox.SelectedIndex = Math.Max(0,
            Array.IndexOf(ScanConfig.Bounds.ArchiveRetentionDaysOptions, c.ArchiveRetentionDays));
        _compactionDayItems = ScanConfig.Bounds.WithCustom(ScanConfig.Bounds.ArchiveCompactionDaysOptions, c.ArchiveCompactionDays);
        _backupDayItems = ScanConfig.Bounds.WithCustom(ScanConfig.Bounds.ArchiveBackupRetentionDaysOptions, c.ArchiveBackupRetentionDays);
        SetDbRetentionItems();
        ArchiveCompactionBox.SelectedIndex = Math.Max(0, Array.IndexOf(_compactionDayItems, c.ArchiveCompactionDays));
        ArchiveBackupRetentionBox.SelectedIndex = Math.Max(0, Array.IndexOf(_backupDayItems, c.ArchiveBackupRetentionDays));
        ShowSkippedLegendBox.IsChecked = c.ShowSkippedLegend;
        GroupColBox.IsChecked = !c.HiddenColumns.Contains("group");
        AvgColBox.IsChecked = !c.HiddenColumns.Contains("avg");
        MinColBox.IsChecked = !c.HiddenColumns.Contains("min");
        MaxColBox.IsChecked = !c.HiddenColumns.Contains("max");
        LastColBox.IsChecked = !c.HiddenColumns.Contains("last");
        LossColBox.IsChecked = !c.HiddenColumns.Contains("loss");
        JitterColBox.IsChecked = !c.HiddenColumns.Contains("jitter");
        ProgressColBox.IsChecked = !c.HiddenColumns.Contains("progress");
        MacColBox.IsChecked = !c.HiddenColumns.Contains("mac");
        VendorColBox.IsChecked = !c.HiddenColumns.Contains("vendor");
        PortsColBox.IsChecked = !c.HiddenColumns.Contains("ports");
        MethodColBox.IsChecked = !c.HiddenColumns.Contains("method");
        OsColBox.IsChecked = !c.HiddenColumns.Contains("os");
        FirstSeenColBox.IsChecked = !c.HiddenColumns.Contains("first_seen");
        LastSeenColBox.IsChecked = !c.HiddenColumns.Contains("last_seen");
        TxtToggle.IsChecked = c.FileOutput;
        CsvToggle.IsChecked = c.ExportCsv;
        HeatMs1Box.Text = c.HeatMs1.ToString("0.#");
        HeatMs2Box.Text = c.HeatMs2.ToString("0.#");
        HeatMs3Box.Text = c.HeatMs3.ToString("0.#");
        HeatMs4Box.Text = c.HeatMs4.ToString("0.#");
        HeatOrderHint.Visibility = Visibility.Collapsed;
        PortScanConnectTimeoutBox.Text = c.PortScanConnectTimeoutMs.ToString();
        PortScanBannerTimeoutBox.Text = c.PortScanBannerTimeoutMs.ToString();
        PortScanParallelismBox.Text = c.PortScanParallel.ToString();
        PortScanTimingSlider.Value = c.PortScanTiming;
        PortScanRetriesBox.Text = c.PortScanRetries.ToString();
        PortScanDelayBox.Text = c.PortScanDelayMs.ToString();
        PortScanMaxRateBox.Text = c.PortScanMaxRate.ToString();
        PortScanIntensityBox.Text = c.PortScanIntensity.ToString();
        UpdatePortScanTimingSummary();
        ServiceDetectionBox.IsChecked = c.PortServiceDetection;
        RefreshPresetUi(c);
        SetPortsUi(c.ScanPortRange);
        CustomPortsBox.Text = string.Join(", ", c.CustomPorts);
        SnmpCommunityBox.Text = c.SnmpCommunity;
        AboutVersionText.Text = System.Reflection.Assembly.GetExecutingAssembly()
            .GetName().Version?.ToString(3) ?? "?";
        AboutCopyrightText.Text = Views.AboutWindow.CopyrightLine;
        SettingsNavList.SelectedIndex = Math.Max(0, Array.IndexOf(SettingsPages, c.SettingsPage));
        }
        finally { _loadingSettings = false; }
    }

}
