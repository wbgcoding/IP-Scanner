using System.Threading.Tasks;
using System.Windows;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using IpScanner.ViewModels;
using IpScanner.Views;

namespace IpScanner;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private ScanConfig _config;
    private System.ComponentModel.ICollectionView _devicesView = null!;
    private System.Windows.Threading.DispatcherTimer? _visibilityRefreshDebounce;

    /// <summary>Fires on nearly every 100 ms aggregate tick during a scan - coalesced to at
    /// most one grid Refresh() per 250 ms, and skipped entirely while every device is shown
    /// anyway.</summary>
    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs ev)
    {
        if (ev.PropertyName == nameof(MainViewModel.DetectedCidr)) { Dispatcher.BeginInvoke(RefreshPresetCommand); return; }
        if (ev.PropertyName != nameof(MainViewModel.IsScanning)) return;
        Dispatcher.BeginInvoke(UpdateScanButton);
        if (!_vm.IsScanning) NotifyScanFinishedIfEnabled();
    }

    /// <summary>Detaches everything that lets the view model call back into this window.</summary>
    private void UnsubscribeViewModel()
    {
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.DeviceVisibilityChanged -= OnDeviceVisibilityChanged;
        _vm.ListsRebuilt -= DisarmSidebarDeletes;
        _vm.InterfaceSelectionChanged -= OnInterfaceSelectionChanged;
        _vm.NewUnknownDeviceFound -= OnNewUnknownDevice;
    }

    private void OnDeviceVisibilityChanged()
    {
        if (_config.ShowOfflineFromDb) return;
        if (_visibilityRefreshDebounce is null)
        {
            _visibilityRefreshDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _visibilityRefreshDebounce.Tick += (_, _) => { _visibilityRefreshDebounce!.Stop(); _devicesView.Refresh(); };
        }
        if (!_visibilityRefreshDebounce.IsEnabled) _visibilityRefreshDebounce.Start();
    }

    public MainWindow()
    {
        // Settings persist as ip_scanner.conf next to the database and are
        // loaded again on every start. Language must be set before any XAML
        // loads because x:Static bindings cache their values.
        // App.OnStartup already loaded the config (needed for the theme); reuse it.
        _config = App.BootConfig ?? LoadPersistedConfig();
        Core.Log.VerboseProvider = () => _config.VerboseLog;
        Core.Log.Info(Core.LogArea.App, $"App started: version {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}, language {_config.Language}");
        Loc.SetLanguage(_config.Language);
        // Restore the persisted adapter choice before anything below
        // detects the network or pings - both read this live.
        NetworkDetector.SelectedSourceIp = _config.NetworkInterfaceIp.Length > 0 ? _config.NetworkInterfaceIp : null;

        InitializeComponent();
        SettingsOverlay.AddHandler(LostFocusEvent, new RoutedEventHandler(OnSettingsFieldLostFocus));
        LayoutUpdated += (_, _) => FitMainAreaToViewport();
        WindowTheme.Apply(this, dark: !ThemeManager.IsLight);
        _vm = new MainViewModel(
            // Both read the live selection at call time, not just once
            // at construction, so switching adapters mid-session takes effect.
            pingFunc: (ip, timeout) => SystemScan.Ping(ip, timeout, NetworkDetector.SelectedSourceIp),
            detectNetwork: () => NetworkDetector.DetectFast(NetworkDetector.SelectedSourceIp),
            dispatch: a => Dispatcher.BeginInvoke(a),
            enrichers: Enrichers,
            retryEnrichers: RetryEnrichers)
        { Config = _config, ConfirmLargeScan = ConfirmLargeScan };
        DataContext = _vm;
        ApplyFlowDirection();
        RefreshDriverBar();

        // Hide/show offline known-devices via the grid's default view filter.
        _devicesView = System.Windows.Data.CollectionViewSource.GetDefaultView(_vm.Devices);
        _devicesView.Filter = DeviceVisible;
        _vm.DeviceVisibilityChanged += OnDeviceVisibilityChanged;

        // Remembered sort + column widths.
        RestoreTableSortAndWidths();
        HookColumnWidthPersistence();

        // The detected network is only the subnet box's placeholder; the box itself
        // stays empty (= auto-detect) until the user types a subnet.
        try
        {
            _vm.SetDetectedNetwork(NetworkDetector.DetectFast(NetworkDetector.SelectedSourceIp));
        }
        catch (Exception ex) { Core.Log.Warn(Core.LogArea.Scan, "Detecting the network failed", ex); /* detection best-effort */ }
        _networkUp = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        Closed += (_, _) => System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;

        // Center horizontally on the primary monitor; fill its full work-area height.
        var wa = SystemParameters.WorkArea;
        Width = Math.Min(Width, wa.Width);
        Height = wa.Height;
        Top = wa.Top;
        Left = wa.Left + (wa.Width - Width) / 2;

        // One button toggles between Scan and Stop depending on scan state.
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.ListsRebuilt += DisarmSidebarDeletes;
        ApplyUiScale();          // persisted text-scale takes effect at startup
        ApplySidebarWidth();     // persisted sidebar width takes effect at startup
        ApplyExportsExpanded();  // persisted collapsed/expanded exports section
        ApplySidebarSectionsExpanded();  // Subnets/pinned/internet sections
        ApplyBarColors();        // persisted bar colors
        ApplyAccentColor();      // persisted accent color override
        ApplyDefaultPingCount(); // persisted default ping count into the dropdown
        ApplyPortScanSettings(); // persisted advanced network settings into PortScanner
        SetLanguageItems();
        SetThemeItems();
        SetDateFormatItems();
        SetRepeatScanItems();
        SetDbRetentionItems();
        SetImportListsItems();
        SetProfileItems();
        Core.NumberFormat.ApplyRegionOverride(_config.DateNumberFormat);
        ApplyRowDensity();
        ApplyColumnVisibility();
        ApplySkippedLegendVisibility();
        InitColorPicker();
        _vm.RebuildPinnedEntries();
        _vm.RefreshAvailableInterfaces();           // Adapter picker choices
        _vm.InterfaceSelectionChanged += OnInterfaceSelectionChanged;
        _vm.InitOverrides(OverridesPathFor(_config.DatabasePath));   // manual hostname/group edits
        InitGraph();
        InitTray();
        ApplyGraphSettings();
        ApplyRepeatScanTimer();  // Auto-repeat scan, if configured
        LoadSettings(_config);   // fills all fields incl. export toggles once

        // Start straight into the tray, skipping the normal window flash.
        if (_config.StartMinimized)
        {
            WindowState = WindowState.Minimized;
            if (_config.MinimizeToTray)
                Loaded += (_, _) => { Hide(); UpdateTrayVisibility(); };
        }

        // On startup, immediately run a discovery sweep of the local network so
        // the sidebar network info and online devices show up without a manual scan.
        // On a genuinely fresh install, the sweep, the update check and the internet
        // pings wait behind the first-run card until one of its actions is chosen.
        _vm.InternetPingingHeld = Core.FirstRunChoice.HoldInternetPinging(IsFreshInstall);
        Loaded += async (_, _) =>
        {
#if DEBUG
            if (Views.Review.ReviewTool.Active) return;   // the surface tool seeds its own state
#endif
            if (Core.FirstRunChoice.StartUpdateCheckAtLaunch(IsFreshInstall, _config))
                _ = UiGuard.Run(() => CheckForUpdatesAsync(), Loc.UpdateTitle);   // runs alongside the sweep
            ShowStartupLoadProblems();
            if (IsFreshInstall) ShowFirstRunCard();
            else await RunStartupSweepAsync();
        };
    }

    /// <summary>The actual startup sweep body, shared by the normal startup path
    /// and every first-run-card exit (any of its buttons).</summary>
    private async Task RunStartupSweepAsync()
    {
        try
        {
            ApplySelectedPingCount();   // the startup sweep uses the header ping count
            await _vm.RunInitScanAsync();
        }
        catch (Exception ex) { Core.Log.Warn(Core.LogArea.Ui, "Startup scan failed", ex); /* best-effort */ }
    }

    /// <summary>Switching the adapter re-detects the network (already live
    /// via NetworkDetector.SelectedSourceIp, read by detectNetwork/pingFunc at call
    /// time). If nothing is running, start scanning on the new adapter right away -
    /// same as the Scan/Stop button's own if/else, except for a typed range that needs the
    /// large-range confirmation: that one waits for the Scan button. A scan already in flight is only
    /// cancelled here, not auto-restarted: RunScanInternal's start gate only frees
    /// once the cancelled run has actually unwound, so chaining stop+start back to
    /// back could silently no-op; the user presses Scan again instead, same as
    /// cancelling any other running scan.</summary>
    private void OnInterfaceSelectionChanged()
        => _ = UiGuard.Run(InterfaceSelectionChangedAsync, Loc.UnexpectedError);

    private async Task InterfaceSelectionChangedAsync()
    {
        PersistConfigDebounced();
        ApplySnmpTrust();
        if (_vm.IsScanning) _vm.Stop();
        else if (!IsLargeManualTarget(SubnetBox.Text, ManualCidrText())) await StartScan();
    }

    // First-run welcome card.
    private bool _firstRunSweepStarted;

    private void ShowFirstRunCard()
    {
        var info = Core.Scanner.NetworkDetector.DetectFast();
        FirstRunNetworkText.Text = string.IsNullOrEmpty(info.Cidr)
            ? Loc.FirstRunNoNetwork
            : string.Format(Loc.FirstRunNetworkFmt, info.Cidr);
        FirstRunConfigText.Text = string.Format(Loc.FirstRunConfigFmt, ActiveConfPath());
        FirstRunDatabaseText.Text = string.Format(Loc.FirstRunDatabaseFmt, ScanConfig.ResolvePath(_config.DatabasePath));
        FirstRunReportsText.Text = string.Format(Loc.FirstRunReportsFmt, ScanConfig.ResolvePath(
            string.IsNullOrWhiteSpace(_config.OutputDirectory) ? ScanConfig.DefaultOutputDirectory : _config.OutputDirectory));
        FirstRunCard.Visibility = Visibility.Visible;
    }

    /// <summary>Guards against two card actions racing each other. Starts only what the
    /// ticked boxes enabled: the update check, then the sweep (which starts the internet
    /// loop when internet pings are enabled).</summary>
    private async Task StartFirstRunSweepAsync()
    {
        if (_firstRunSweepStarted) return;
        _firstRunSweepStarted = true;
        FirstRunCard.Visibility = Visibility.Collapsed;
        _vm.InternetPingingHeld = false;
        if (_firstRunStart.CheckUpdates) _ = UiGuard.Run(() => CheckForUpdatesAsync(), Loc.UpdateTitle);
        await RunStartupSweepAsync();
    }

    private (bool CheckUpdates, bool InternetPing) _firstRunStart;   // InternetPing: the sweep's scan starts the loop

    /// <summary>Any of the three card actions first stores the two ticked boxes, then
    /// marks the install as no-longer-fresh - an immediate, non-debounced write so the conf
    /// file exists on disk right away (its mere existence is what IsFreshInstall checks on
    /// the next launch).</summary>
    private void PersistFirstRunSeen()
    {
        if (_firstRunSweepStarted) return;
        _firstRunStart = Core.FirstRunChoice.Apply(_config,
            FirstRunUpdatesBox.IsChecked == true, FirstRunInternetBox.IsChecked == true);
        // Keep the settings page in step so its change handlers never write the old values back.
        _loadingSettings = true;
        try
        {
            CheckForUpdatesBox.IsChecked = _config.CheckForUpdates;
            EnableInternetBox.IsChecked = _config.EnableInternetPing;
        }
        finally { _loadingSettings = false; }
        PersistConfig();
    }

    private async void OnFirstRunScanNow(object sender, RoutedEventArgs e)
    {
        await UiGuard.Run(async () =>
        {
            PersistFirstRunSeen();
            await StartFirstRunSweepAsync();
        }, Loc.ScanError);
    }

    private async void OnFirstRunOpenSettings(object sender, RoutedEventArgs e)
    {
        await UiGuard.Run(async () =>
        {
            PersistFirstRunSeen();
            OnSettingsClick(this, e);
            await StartFirstRunSweepAsync();
        }, Loc.ScanError);
    }

    private async void OnFirstRunDismiss(object sender, RoutedEventArgs e)
    {
        await UiGuard.Run(async () =>
        {
            PersistFirstRunSeen();
            await StartFirstRunSweepAsync();
        }, Loc.ScanError);
    }

    private enum UpdateStatusKind { None, Checking, Failed, UpToDate }

    private UpdateStatusKind _updateStatus;

    private void SetUpdateStatus(UpdateStatusKind kind)
    {
        _updateStatus = kind;
        RefreshStatusText();
    }

    /// <summary>Re-renders the update status line from the stored kind (language switch).</summary>
    private void RefreshStatusText()
    {
        UpdateStatusText.Text = _updateStatus switch
        {
            UpdateStatusKind.Checking => Loc.Checking,
            UpdateStatusKind.Failed => Loc.UpdateCheckFailed,
            UpdateStatusKind.UpToDate => Loc.UpToDate,
            _ => "",
        };
    }

    /// <summary>Ask GitHub for a newer release; if found, offer to download it
    /// and swap the exe. A manual check also reports "up to date".</summary>
    private async Task CheckForUpdatesAsync(bool manual = false)
    {
        if (manual) SetUpdateStatus(UpdateStatusKind.Checking);
        var result = await Core.UpdateService.CheckAsync();
        Core.Log.Info(Core.LogArea.Update, $"Update check result: {result.Status}");
        if (_closing) return;
        if (result.Status == Core.UpdateService.CheckStatus.Failed)
        {
            if (manual) SetUpdateStatus(UpdateStatusKind.Failed);
            return;
        }
        if (result.Release is not { } release)
        {
            if (manual) SetUpdateStatus(UpdateStatusKind.UpToDate);
            return;
        }
        if (manual) SetUpdateStatus(UpdateStatusKind.None);

        // A release without a signature is announced but never installed from inside the app.
        if (result.Status == Core.UpdateService.CheckStatus.UpdateAvailableUnsigned)
        {
            ShowNotice(Loc.UpdateUnsigned(release.Version.ToString(3)), Loc.OpenDownloadPage,
                () => OpenReleasePage(release), sticky: true);
            return;
        }

        var dlg = new Views.UpdateDialog(release.Version.ToString(3),
                                         Core.UpdateService.CurrentVersion.ToString(3)) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var applied = await Core.UpdateService.DownloadAndApplyAsync(release);
        if (applied.Success)
            Application.Current.Shutdown();   // updater takes over once we exit
        else
        {
            var reason = applied.Failure switch
            {
                Core.UpdateService.ApplyFailure.Download => Loc.UpdateDownloadFailed,
                Core.UpdateService.ApplyFailure.Folder => Loc.UpdateFolderReadOnly,
                Core.UpdateService.ApplyFailure.Signature => Loc.UpdateSignatureFailed,
                _ => Loc.UpdateFailed,
            };
            ShowNotice(reason, Loc.OpenDownloadPage, () => OpenReleasePage(release), sticky: true);
        }
    }

    /// <summary>Opens the release page in the default browser; only a github.com https address is ever launched.</summary>
    private static void OpenReleasePage(Core.UpdateService.Release release)
    {
        if (!Core.UpdateService.IsTrustedAssetUrl(release.PageUrl)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(release.PageUrl) { UseShellExecute = true }); }
        catch (Exception ex) { Core.Log.Warn(Core.LogArea.Ui, "Opening the browser failed", ex); /* no browser available: nothing more to do */ }
    }

    private async void OnCheckUpdatesNow(object sender, RoutedEventArgs e)
    {
        await UiGuard.Run(() => CheckForUpdatesAsync(manual: true), Loc.UpdateTitle);
    }

    /// <summary>Mirror the whole UI for right-to-left languages (Arabic).</summary>
    private void ApplyFlowDirection()
        => FlowDirection = Loc.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    // F5 = (re)scan, Ctrl+F = focus filter, Esc = close settings. More shortcuts
    // exist; every one also has a visible control, and the Help settings page lists them.
    private void OnWindowKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        bool ctrl = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
        switch (e.Key)
        {
            case System.Windows.Input.Key.F5:
                OnScanStopClick(this, e); e.Handled = true; break;
            case System.Windows.Input.Key.F when ctrl:
                FilterBox.Focus(); FilterBox.SelectAll(); e.Handled = true; break;
            // Ctrl+T opens (or refocuses) the tools window.
            case System.Windows.Input.Key.T when ctrl:
                OnToolsClick(this, e); e.Handled = true; break;
            case System.Windows.Input.Key.Escape when EntryEditPopup.IsOpen:
                EntryEditPopup.IsOpen = false; e.Handled = true; break;
            case System.Windows.Input.Key.Escape when ProfileEditPopup.IsOpen:
                ProfileEditPopup.IsOpen = false; e.Handled = true; break;
            case System.Windows.Input.Key.Escape when SettingsOverlay.Visibility == Visibility.Visible:
                OnSettingsCancel(this, e); e.Handled = true; break;
            case System.Windows.Input.Key.Escape when ArchiveScroll.Visibility == Visibility.Visible:
                OnArchiveCloseRequested(this, EventArgs.Empty); e.Handled = true; break;
            case System.Windows.Input.Key.Escape when LogScroll.Visibility == Visibility.Visible:
                OnLogCloseRequested(this, EventArgs.Empty); e.Handled = true; break;
            // Esc while the filter box has focus clears the text only,
            // the quick-filter pills stay as they were.
            case System.Windows.Input.Key.Escape when FilterBox.IsFocused:
                FilterBox.Clear(); e.Handled = true; break;
            case System.Windows.Input.Key.OemComma when ctrl:
                OnSettingsClick(this, e); e.Handled = true; break;
            case System.Windows.Input.Key.B when ctrl:
                OnToggleSidebarClick(this, e); e.Handled = true; break;
            case System.Windows.Input.Key.E when ctrl:
                OnOpenScansFolder(this, e); e.Handled = true; break;
            case System.Windows.Input.Key.D1 when ctrl:
                OnlinePill.IsChecked = !(OnlinePill.IsChecked == true); OnQuickFilterToggle(this, e); e.Handled = true; break;
            case System.Windows.Input.Key.D2 when ctrl:
                PinnedPill.IsChecked = !(PinnedPill.IsChecked == true); OnQuickFilterToggle(this, e); e.Handled = true; break;
            case System.Windows.Input.Key.D3 when ctrl:
                UnknownPill.IsChecked = !(UnknownPill.IsChecked == true); OnQuickFilterToggle(this, e); e.Handled = true; break;
            // Only while the device table itself has focus - never steals a
            // normal Ctrl+C out of a text field elsewhere in the window.
            case System.Windows.Input.Key.C when ctrl && ShouldCopySelectedRow(DeviceGrid.IsKeyboardFocusWithin, System.Windows.Input.Keyboard.FocusedElement?.GetType()):
                CopySelectedRowAsText(); e.Handled = true; break;
            case System.Windows.Input.Key.F1:
                OnSettingsClick(this, e);
                SettingsNavList.SelectedIndex = System.Array.IndexOf(SettingsPages, "Help");
                e.Handled = true; break;
        }
    }

    internal static bool ShouldCopySelectedRow(bool gridHasKeyboardFocus, Type? focusedElementType) =>
        gridHasKeyboardFocus && (focusedElementType is null ||
            !typeof(System.Windows.Controls.Primitives.TextBoxBase).IsAssignableFrom(focusedElementType));

}
