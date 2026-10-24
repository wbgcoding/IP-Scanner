using System.Windows;
using System.Windows.Controls;
using System.IO;
using IpScanner.Core.Data;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using IpScanner.ViewModels;
using IpScanner.Views;

namespace IpScanner;

public partial class MainWindow : Window
{
    // ── Latency history graphs (device graphs at the bottom, internet mini graph) ──
    private readonly List<double?> _inetSamples = new();

    /// <summary>Visible time window in samples (one per second).</summary>
    private int GraphWindowSeconds => Math.Clamp(_config.GraphMaxSeconds, 10, 300);

    /// <summary>Show/hide the graphs per settings.</summary>
    private void ApplyGraphSettings()
    {
        var vis = _config.GraphsEnabled ? Visibility.Visible : Visibility.Collapsed;
        InetGraphSection.Visibility = vis;
        _vm.NetworkGraphsOn = _config.GraphsEnabled && _config.NetworkGraphsEnabled;
    }

    // Graph samples are computed on a worker thread (frozen geometry, bindings
    // marshal the property changes); only the internet canvas and the source
    // list are touched on the UI thread.
    private System.Threading.Timer? _graphTimer;
    private volatile bool _closing;
    private int _graphTickRunning;

    private void InitGraph()
    {
        _graphTimer = new System.Threading.Timer(_ => GraphTickWorker(), null, 1000, 1000);
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
        Closed += OnWindowClosed;
        // The window can close and the process end before the shutdown below finishes; make sure
        // the tray icon never outlives the app.
        Application.Current.Exit += (_, _) => DisposeTray();
    }

    // The shutdown below is asynchronous. With the default mode the app would end as soon as this
    // window is gone and the awaited work (engine disposal, tray) would never run, so from here on
    // the app ends only through the explicit Shutdown call at the end of OnWindowClosed.
    /// <summary>True once this window started closing for good; owned windows must not Show() or activate it then.</summary>
    internal bool IsClosingDown { get; private set; }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel) IsClosingDown = true;
        if (e.Cancel || Application.Current is not { } app) return;
        // Windows logoff and the updater start the shutdown themselves and then close this window;
        // the mode can no longer change then, and the app is ending anyway.
        try { app.ShutdownMode = ShutdownMode.OnExplicitShutdown; }
        catch (InvalidOperationException) { }
    }

    private async void OnWindowClosed(object? sender, EventArgs e)
    {
        await UiGuard.Run(async () =>
        {
            _closing = true;
            FlushPersistConfig();
            _graphTimer?.Dispose();
            _repeatScanTimer?.Stop();
            _persistDebounce?.Stop();
            UnhookColumnWidthPersistence();
            UnsubscribeViewModel();
            Logo.Shutdown();
            // SystemEvents holds a static reference - unhook or the window leaks.
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
            try { await _vm.Shutdown(); }
            finally { DisposeTray(); }
        }, Loc.UnexpectedError);
#if DEBUG
        if (Views.Review.ReviewTool.Active) return;   // the surface tool opens many windows in one process
#endif
        Application.Current?.Shutdown();
    }

    /// <summary>Theme "auto" follows the Windows light/dark setting live, not
    /// just at startup - re-apply whenever Windows reports a preference change
    /// while auto is active and the resolved theme actually flipped.</summary>
    private void OnSystemPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (_config.Theme != "auto") return;
        if (e.Category is not (Microsoft.Win32.UserPreferenceCategory.General or Microsoft.Win32.UserPreferenceCategory.Color)) return;
        Dispatcher.BeginInvoke(() =>
        {
            bool wasLight = ThemeManager.IsLight;
            ThemeManager.Switch(Application.Current, "auto");
            if (ThemeManager.IsLight == wasLight) return;   // no actual change
            WindowTheme.Apply(this, dark: !ThemeManager.IsLight);
            _vm.RefreshTheme();
        });
    }

    private void GraphTickWorker()
    {
        if (_closing || !_config.GraphsEnabled) return;
        if (Interlocked.Exchange(ref _graphTickRunning, 1) == 1) return;   // no overlap
        try
        {
            int capacity = GraphWindowSeconds, tick = GraphSeries.NiceTickStep(_config.GraphMaxSeconds);
            if (_config.NetworkGraphsEnabled)
                foreach (var net in _vm.NetworksSnapshot())
                    net.AddGraphSample(capacity, tick);
            _vm.SampleRowGraphs(capacity, tick);   // per-row table graphs

            Dispatcher.BeginInvoke(() =>
            {
                if (_closing) return;
                Sample(_inetSamples, _vm.InternetGraphValue());
                RenderInternetGraph();
            });
        }
        catch (Exception ex) { Core.Log.Debug(Core.LogArea.Ui, "Graph refresh tick failed: " + ex.GetType().Name); /* a failed tick must never kill the timer */ }
        finally { Interlocked.Exchange(ref _graphTickRunning, 0); }
    }

    private void Sample(List<double?> series, double? value)
    {
        series.Add(value);
        while (series.Count > GraphWindowSeconds) series.RemoveAt(0);
    }

    private void OnGraphSizeChanged(object sender, SizeChangedEventArgs e) => RenderInternetGraph();

    /// <summary>Switches a device's detail-panel latency graph between
    /// "last minute" (live, already buffered) and 24h/7d (loaded from the
    /// known-devices DB on demand, off the UI thread).</summary>
    private async void OnLatencyRangeClick(object sender, RoutedEventArgs e)
    {
        await UiGuard.Run(async () =>
        {
            if ((sender as FrameworkElement)?.DataContext is not DeviceViewModel vm) return;
            if ((sender as FrameworkElement)?.Tag is not string tag) return;
            var range = tag switch { "Day" => LatencyRange.Day, "Week" => LatencyRange.Week, _ => LatencyRange.Minute };
            vm.SetRange(range);
            if (range == LatencyRange.Minute) return;
            if (!_config.KnownDevicesDb || !vm.HasMac) return;
            var mac = vm.Mac;
            try
            {
                var since = DateTime.Now.AddSeconds(-(double)LatencyHistoryAggregator.BucketSeconds(range) * LatencyHistoryAggregator.BucketCount(range));
                var dbPath = _config.DatabasePath;
                var rows = await Task.Run(() => new KnownDevicesDb(dbPath).LoadLatencyHistory(mac, since));
                if (vm.SelectedRange != range) return;   // user switched again while loading
                var samples = LatencyHistoryAggregator.Bucket(rows, range);
                vm.SetHistoryGraph(samples, LatencyHistoryAggregator.BucketSeconds(range), LatencyHistoryAggregator.TickSeconds(range));
            }
            catch (Exception ex) { Core.Log.Warn(Core.LogArea.Db, "Reading the graph history failed", ex); /* history is best-effort */ }
        }, Loc.UnexpectedError);
    }

    /// <summary>Resize the per-row latency graph to the table width.</summary>
    private void OnRowGraphSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DeviceViewModel vm)
            vm.SetRowGraphWidth(e.NewSize.Width);
    }

    /// <summary>Resize the sidebar network graph to the sidebar's
    /// actual (resizable) width instead of a bare 272px literal.</summary>
    private void OnNetworkGraphSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is NetworkInfoViewModel vm)
            vm.SetGraphWidth(e.NewSize.Width);
    }

    private const int InetZoomWindowSeconds = 30;

    private void RenderInternetGraph()
    {
        IReadOnlyList<double?> samples = _inetSamples;
        int tick = GraphSeries.NiceTickStep(_config.GraphMaxSeconds);
        if (_vm.IsZoomed)
        {
            int n = Math.Min(_inetSamples.Count, InetZoomWindowSeconds);
            samples = _inetSamples.GetRange(_inetSamples.Count - n, n);
            tick = GraphSeries.NiceTickStep(InetZoomWindowSeconds);
        }
        RenderSeries(samples, InetGraphCanvas, InetGraphLine, InetGraphMaxText, InetGraphMinText, tick);
    }

    private void RenderSeries(IReadOnlyList<double?> samples, System.Windows.Controls.Canvas canvas,
                              System.Windows.Shapes.Polyline line,
                              TextBlock maxText, TextBlock minText, int tickIntervalSeconds)
    {
        var r = GraphSeries.Compute(samples, canvas.ActualWidth, canvas.ActualHeight,
                                    tickIntervalSeconds);
        line.Points = r.Points;
        maxText.Text = r.MaxText;
        minText.Text = r.MinText;

        for (int i = canvas.Children.Count - 1; i >= 0; i--)
            if (canvas.Children[i] is FrameworkElement fe && "tick".Equals(fe.Tag))
                canvas.Children.RemoveAt(i);

        var brush = BrushFor(Core.Palette.MidGray);
        foreach (var tick in r.Ticks)
        {
            var lbl = new TextBlock
            {
                Text = tick.Label, FontSize = 8, Foreground = brush,
                Width = 28, TextAlignment = TextAlignment.Center, Tag = "tick",
            };
            System.Windows.Controls.Canvas.SetLeft(lbl, tick.X);
            System.Windows.Controls.Canvas.SetBottom(lbl, 0);
            canvas.Children.Add(lbl);
        }
    }

    private string GetBarColor(string key) => key switch
    {
        "online" => _config.ColorOnline, "offline" => _config.ColorOffline,
        "success" => _config.ColorSuccess, "failed" => _config.ColorFailed,
        _ => _config.ColorSkipped,
    };

    private void SetBarColor(string key, string hex)
    {
        switch (key)
        {
            case "online": _config.ColorOnline = hex; break;
            case "offline": _config.ColorOffline = hex; break;
            case "success": _config.ColorSuccess = hex; break;
            case "failed": _config.ColorFailed = hex; break;
            default: _config.ColorSkipped = hex; break;
        }
    }

    private string ActiveConfPath()
    {
        var dir = _config.ConfigDirectory.Trim().Length > 0
            ? _config.ConfigDirectory.Trim()
            : ScanConfig.DefaultOutputDirectory;
        return Path.Combine(ScanConfig.ResolvePath(dir), ScanConfig.ConfigFileName);
    }

    private void PersistConfig()
    {
        try
        {
            var path = ActiveConfPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            RelocateSealedSettings(_config);
            ConfigManager.Save(path, _config, ConfirmConfigOverwrite);
            SaveSealedSettings(_config);
        }
        catch (Exception ex) { Core.Log.Warn(Core.LogArea.Config, "Saving the graph layout failed", ex); /* persisting is best-effort */ }
    }

    private bool ConfirmConfigOverwrite()
    {
        bool Ask() => new ConfigMergeDialog { Owner = this }.ShowDialog() == true;
        return Dispatcher.CheckAccess() ? Ask() : Dispatcher.Invoke(Ask);
    }

    // ── Debounced persistence ──
    // A settings change during an active scan (offline-DB toggle, a dragged slider) must not
    // write the config synchronously on the UI thread on every change. Coalesce
    // rapid changes into one write, off the UI thread, at most every 400 ms.
    private System.Windows.Threading.DispatcherTimer? _persistDebounce;
    private volatile bool _persistPending;
    private readonly Core.Backup.RestoreGate _restoreGate = new();

    private void PersistConfigDebounced()
    {
        _persistPending = true;
        if (_persistDebounce is null)
        {
            _persistDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _persistDebounce.Tick += OnPersistDebounceTick;
        }
        _persistDebounce.Stop();
        _persistDebounce.Start();
    }

    private void OnPersistDebounceTick(object? sender, EventArgs e)
    {
        _persistDebounce!.Stop();
        if (!_persistPending || _restoreGate.Active) return;   // a restore in progress re-arms this on leaving
        _persistPending = false;
        var path = ActiveConfPath();
        var snapshot = _config.Clone();   // taken here on the UI thread, the live config keeps changing
        _restoreGate.Track(Task.Run(() => SaveConfigSnapshot(path, snapshot)));
    }

    /// <summary>Immediate synchronous write, bypassing the debounce - window close and
    /// before export/import must never leave a pending change unwritten.</summary>
    private void FlushPersistConfig()
    {
        _persistDebounce?.Stop();
        _persistPending = false;
        SaveConfigSnapshot(ActiveConfPath(), _config.Clone());
    }

    /// <summary>Writes an already cloned config (and the sealed settings) to <paramref name="path"/>;
    /// touches no live state, so any thread may call it.</summary>
    private void SaveConfigSnapshot(string path, ScanConfig snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            RelocateSealedSettings(snapshot);
            ConfigManager.Save(path, snapshot, ConfirmConfigOverwrite);
            SaveSealedSettings(snapshot);
        }
        catch (Exception ex) { Core.Log.Warn(Core.LogArea.Config, "Saving the graph layout failed", ex); /* persisting is best-effort */ }
    }

    /// <summary>The field shows the full file path; only its folder is stored -
    /// the file name is always ip_scanner.conf.</summary>
    private static string ConfDirFromInput(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return ScanConfig.DefaultConfigDirectory;
        if (t.EndsWith(".conf", StringComparison.OrdinalIgnoreCase))
            return Path.GetDirectoryName(t) is { Length: > 0 } dir ? dir : ScanConfig.DefaultConfigDirectory;
        return t;
    }

    private void OnBrowseConfDir(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.ConfDirLabel,
            Filter = Loc.ConfFileFilter,
            FileName = ScanConfig.ConfigFileName,
            OverwritePrompt = false,
        };
        if (DirOf(ConfDirBox.Text.Length > 0 ? ConfDirBox.Text : ".") is { } dir)
            dlg.InitialDirectory = dir;
        if (dlg.ShowDialog(this) == true) ConfDirBox.Text = MakeRelative(dlg.FileName);
    }

    private void OnConfDirChanged(object sender, TextChangedEventArgs e)
        => ValidatePath(ConfDirBox, ConfDirError, allowEmpty: true, toOwnedDirectory: ConfDirFromInput);

    /// <summary>Scales header + main content (and the Settings overlay) but not the title
    /// bar row, which stays native size - and lets the new ScrollViewer around
    /// ScaledContent pick up the overflow at 150-200% instead of clipping it.</summary>
    private void ApplyUiScale()
    {
        double f = Math.Clamp(_config.UiScalePercent, 50, 200) / 100.0;
        var transform = f == 1.0 ? null : new System.Windows.Media.ScaleTransform(f, f);
        ScaledContent.LayoutTransform = transform;
        SettingsOverlay.LayoutTransform = transform;
        ArchiveOverlay.LayoutTransform = transform;
        LogOverlay.LayoutTransform = transform;
        _toolsWindow?.ApplyScaleAndFlow(f, Loc.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight);
    }

    /// <summary>The scroll viewer measures its content without a width limit, so the table's natural
    /// column widths would widen the whole page past the window and push the header toolbar out of
    /// view. The main area is capped to the viewport instead (the table scrolls itself). When the
    /// header does not fit in one row the progress block drops to its own row, and when the filter
    /// pills do not fit beside the filter box they wrap below it, so the page only grows past the
    /// viewport when even that is not enough.</summary>
    private void FitMainAreaToViewport()
    {
        double f = Math.Clamp(_config.UiScalePercent, 50, 200) / 100.0;
        double viewport = ScaledScroll.ViewportWidth > 0 ? ScaledScroll.ViewportWidth : ScaledScroll.ActualWidth;
        if (viewport <= 0) return;
        double avail = viewport / f;

        if (!FitHeaderAndPills(avail)) return;

        double pills = _pillsNeed >= 0 ? _pillsNeed : QuickPills.DesiredSize.Width;
        double filterRow = (_pillsOwnRow ? 240 + 8 + 20 : 240 + 8 + 20 + pills) + SidebarColumn.ActualWidth + 5;
        double cap = MainAreaCap(viewport, f, HeaderBar.DesiredSize.Width, filterRow);
        if (double.IsInfinity(MainArea.MaxWidth) || Math.Abs(MainArea.MaxWidth - cap) > 0.5)
            MainArea.MaxWidth = cap;
        // The packet reader notice wraps inside the viewport instead of widening the page.
        double barCap = avail;
        if (double.IsInfinity(DriverBar.MaxWidth) || Math.Abs(DriverBar.MaxWidth - barCap) > 0.5)
            DriverBar.MaxWidth = barCap;

        double need = SidebarFilterRowNeed(pills);
        if (avail >= need) _sidebarAutoSuspended = false;
        bool collapse = ShouldAutoCollapse(viewport, f, pills,
            SidebarColumn.Width.Value > 0 && !_sidebarAutoSuspended, _sidebarAutoCollapsed, out bool restore);
        if (collapse)
        {
            CollapseSidebar();
            _sidebarAutoCollapsed = true;
        }
        else if (restore)
        {
            OpenSidebar(avail - (need - ScanConfig.Bounds.SidebarWidthMin));
            _sidebarAutoCollapsed = false;
        }
    }

    // Natural widths measured while the header / pills sit on one line; kept while they wrap, because
    // a wrapped element no longer reports its natural width.
    private bool _headerTwoRows, _pillsOwnRow;
    private double _headerNeed = -1, _pillsNeed = -1;
    private string _headerNeedKey = "";

    /// <summary>Moves the progress block to its own header row and the filter pills below the filter
    /// box when the one-line layout would be wider than the window. False means a layout reset is
    /// pending and the caller waits for the next pass.</summary>
    private bool FitHeaderAndPills(double avail)
    {
        string key = string.Concat(ScanStopButton.Content, "|", ArchiveButton.Content, "|", LogButton.Content, "|",
            OnlinePill.Content, "|", PinnedPill.Content, "|", UnknownPill.Content, "|", OfflineToggle.Content);
        if (key != _headerNeedKey)
        {
            _headerNeedKey = key;
            _headerNeed = _pillsNeed = -1;
            if (_headerTwoRows || _pillsOwnRow)
            {
                SetHeaderRows(false);
                SetPillsRow(false);
                return false;
            }
        }

        if (!_headerTwoRows) _headerNeed = HeaderBar.DesiredSize.Width;
        if (!_pillsOwnRow) _pillsNeed = QuickPills.DesiredSize.Width;

        bool two = HeaderNeedsTwoRows(avail, _headerNeed, _headerTwoRows);
        if (two != _headerTwoRows) SetHeaderRows(two);
        if (two)
        {
            SetMaxWidth(HeaderControls, Math.Max(120, avail - 24 - HeaderLogoPanel.DesiredSize.Width));
            SetMaxWidth(HeaderProgress, Math.Max(120, avail - 24));
        }

        double rowNeed = 240 + 8 + 20 + _pillsNeed + SidebarColumn.ActualWidth + 5;
        bool own = HeaderNeedsTwoRows(avail, rowNeed, _pillsOwnRow);
        if (own != _pillsOwnRow) SetPillsRow(own);
        if (own) SetMaxWidth(QuickPills, Math.Max(120, avail - 20));
        return true;
    }

    private static void SetMaxWidth(FrameworkElement e, double width)
    {
        if (double.IsInfinity(e.MaxWidth) || Math.Abs(e.MaxWidth - width) > 0.5) e.MaxWidth = width;
    }

    private void SetHeaderRows(bool two)
    {
        _headerTwoRows = two;
        System.Windows.Controls.Grid.SetRow(HeaderProgress, two ? 1 : 0);
        System.Windows.Controls.Grid.SetColumn(HeaderProgress, two ? 0 : 1);
        System.Windows.Controls.Grid.SetColumnSpan(HeaderProgress, two ? 3 : 1);
        HeaderProgress.Margin = two ? new Thickness(0, 8, 0, 0) : new Thickness(18, 0, 18, 0);
        if (!two)
        {
            HeaderControls.MaxWidth = double.PositiveInfinity;
            HeaderProgress.MaxWidth = double.PositiveInfinity;
        }
    }

    private void SetPillsRow(bool own)
    {
        _pillsOwnRow = own;
        System.Windows.Controls.Grid.SetRow(QuickPills, own ? 1 : 0);
        System.Windows.Controls.Grid.SetColumn(QuickPills, own ? 0 : 1);
        System.Windows.Controls.Grid.SetColumnSpan(QuickPills, own ? 2 : 1);
        QuickPills.Margin = own ? new Thickness(0, 6, 0, 0) : new Thickness(8, 0, 0, 0);
        QuickPills.HorizontalAlignment = own ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        if (!own) QuickPills.MaxWidth = double.PositiveInfinity;
    }

    /// <summary>True when a row of <paramref name="need"/> DIPs does not fit in <paramref name="avail"/>
    /// and must wrap onto a second row; once wrapped it needs 40 more DIPs than the row to go back,
    /// so a width near the limit does not flicker.</summary>
    internal static bool HeaderNeedsTwoRows(double avail, double need, bool twoRows)
        => need >= 0 && (twoRows ? avail < need + 40 : avail < need);

    /// <summary>Width the main area is capped to: the viewport, grown only by what the header and the
    /// filter row cannot shrink below.</summary>
    internal static double MainAreaCap(double viewport, double f, double headerWidth, double filterRow)
        => Math.Max(viewport / f, Math.Max(headerWidth, filterRow));

    /// <summary>Width the filter row needs next to the narrowest sidebar: filter box minimum (MinWidth
    /// in the XAML) + pills + the row margins + the sidebar at its minimum and its handle.</summary>
    internal static double SidebarFilterRowNeed(double pills)
        => 240 + 8 + 20 + pills + ScanConfig.Bounds.SidebarWidthMin + 5;

    /// <summary>Decides whether the sidebar folds away on its own: it collapses when the window is too
    /// narrow for the filter row beside it, and comes back (<paramref name="restore"/>) once there is
    /// clearly room again; the extra 40 px stop a width near the limit from flickering.</summary>
    internal static bool ShouldAutoCollapse(double viewport, double f, double pills, bool open, bool autoCollapsed, out bool restore)
    {
        double avail = viewport / f;
        double need = SidebarFilterRowNeed(pills);
        restore = !open && autoCollapsed && avail >= need + 40;
        return open && avail < need;
    }

    /// <summary>Applies the persisted sidebar width at startup and after a
    /// config reset/import - the GridSplitter itself enforces the
    /// 240-560 drag range via the column's Min/MaxWidth.</summary>
    private void ApplySidebarWidth()
        => SidebarColumn.Width = new GridLength(
            Math.Clamp(_config.SidebarWidth, ScanConfig.Bounds.SidebarWidthMin, ScanConfig.Bounds.SidebarWidthMax));

    private void OnSidebarResized(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _config.SidebarWidth = (int)SidebarColumn.ActualWidth;
        PersistConfigDebounced();
    }

    // Ctrl+B / the header button collapse the sidebar to width 0 and
    // back to its last width - session-only, like the quick-filter
    // pills, not persisted across restarts.
    private double _sidebarWidthBeforeCollapse = -1;

    // Set while the window is too narrow and the sidebar folded away by itself; a manual toggle
    // clears it, and a sidebar the user opened in a narrow window stays open until room returns.
    private bool _sidebarAutoCollapsed;
    private bool _sidebarAutoSuspended;

    private void OnToggleSidebarClick(object sender, RoutedEventArgs e)
    {
        _sidebarAutoCollapsed = false;
        if (SidebarColumn.Width.Value > 0)
        {
            CollapseSidebar();
        }
        else
        {
            OpenSidebar(double.MaxValue);
            _sidebarAutoSuspended = true;
        }
    }

    private void CollapseSidebar()
    {
        _sidebarWidthBeforeCollapse = SidebarColumn.Width.Value;
        SidebarColumn.MinWidth = 0;
        SidebarColumn.Width = new GridLength(0);
    }

    private void OpenSidebar(double maxWidth)
    {
        SidebarColumn.MinWidth = ScanConfig.Bounds.SidebarWidthMin;
        var restore = _sidebarWidthBeforeCollapse > 0 ? _sidebarWidthBeforeCollapse : _config.SidebarWidth;
        restore = Math.Min(restore, Math.Max(maxWidth, ScanConfig.Bounds.SidebarWidthMin));
        SidebarColumn.Width = new GridLength(
            Math.Clamp(restore, ScanConfig.Bounds.SidebarWidthMin, ScanConfig.Bounds.SidebarWidthMax));
    }

    /// <summary>Pushes the configured latency-heatmap thresholds into
    /// Core.Palette, then re-raises every already-rendered latency color the same
    /// way a theme switch does - so a changed setting recolors the table
    /// immediately instead of only on the next ping result.</summary>
    private void ApplyHeatThresholds()
    {
        Core.Palette.HeatMs1 = _config.HeatMs1;
        Core.Palette.HeatMs2 = _config.HeatMs2;
        Core.Palette.HeatMs3 = _config.HeatMs3;
        Core.Palette.HeatMs4 = _config.HeatMs4;
        _vm.RefreshTheme();
    }

    private void OnResetHeatDefaults(object sender, RoutedEventArgs e)
    {
        HeatMs1Box.Text = ScanConfig.Bounds.HeatMs1Default.ToString("0.#");
        HeatMs2Box.Text = ScanConfig.Bounds.HeatMs2Default.ToString("0.#");
        HeatMs3Box.Text = ScanConfig.Bounds.HeatMs3Default.ToString("0.#");
        HeatMs4Box.Text = ScanConfig.Bounds.HeatMs4Default.ToString("0.#");
        ApplyInstant();
    }

    // MAC/hostname techniques, best-first (index = rank). The engine runs all
    // of them in parallel per device, shows the FIRST result immediately and
    // swaps in a better-ranked one when it arrives later.
    //   MAC:      ARP (rank 0) > NetBIOS (rank 3)
    //   Hostname: reverse DNS (1) > mDNS (2) > NetBIOS (3)
    // Factored into a builder so the "resolve name again" retry path
    // (manual context-menu entry, background retry pass) can ask for a doubled
    // mDNS window instead of duplicating the whole chain.
    private static Func<string, CancellationToken, Task<(string? mac, string? host)>>[] BuildEnrichers(int mdnsTimeoutMs)
        => SystemScan.Enrichers(mdnsTimeoutMs);

    private static readonly Func<string, CancellationToken, Task<(string? mac, string? host)>>[] Enrichers = BuildEnrichers(1500);
    private static readonly Func<string, CancellationToken, Task<(string? mac, string? host)>>[] RetryEnrichers = BuildEnrichers(3000);

    private async void OnScanStopClick(object sender, RoutedEventArgs e)
    {
        await UiGuard.Run(async () =>
        {
            if (_vm.IsScanning) _vm.Stop();
            else await StartScan();
        }, Loc.ScanError);
    }

    private async void OnSubnetKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        await UiGuard.Run(async () =>
        {
            if (e.Key == System.Windows.Input.Key.Enter && !_vm.IsScanning) await StartScan();
        }, Loc.ScanError);
    }

    /// <summary>The mask list only applies to a typed address; with the box empty the detected
    /// network and its real mask are used, so the list is greyed out instead of silently ignored.</summary>
    private void OnSubnetTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (CidrBox is not null) CidrBox.IsEnabled = SubnetBox.Text.Trim().Length > 0;
        if (_config is not null && PresetCommandBox is not null) RefreshPresetCommand();
    }

    private System.Threading.CancellationTokenSource? _networkChangeDebounce;

    private bool? _networkUp;

    /// <summary>The log line for an online/offline flip; null while nothing changed or on the first reading.</summary>
    internal static string? NetworkTransitionMessage(bool? before, bool now)
        => before is null || before == now ? null
            : now ? "Network connection restored" : "Network connection lost";

    private void LogNetworkTransition()
    {
        var now = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();
        var message = NetworkTransitionMessage(_networkUp, now);
        _networkUp = now;
        if (message is not null) Core.Log.Warn(Core.LogArea.Scan, message);
    }

    private async void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        await UiGuard.Run(async () =>
        {
            // Several adapters report one change in a burst; look up once things settle.
            _networkChangeDebounce?.Cancel();
            var cts = _networkChangeDebounce = new System.Threading.CancellationTokenSource();
            try
            {
                await Task.Delay(1500, cts.Token);
                LogNetworkTransition();
                Core.Log.Info(Core.LogArea.Scan, "Network change detected");
                await _vm.RefreshDetectedNetworkAsync();
            }
            catch (OperationCanceledException) { /* superseded by a newer change */ }
        }, Loc.UnexpectedError);
    }

    /// <summary>Editable dropdown: free text wins, otherwise the selected item.</summary>
    private string? ManualCidrText()
        => string.IsNullOrWhiteSpace(CidrBox.Text)
            ? (CidrBox.SelectedItem as ComboBoxItem)?.Content?.ToString()
            : CidrBox.Text;

    /// <summary>True when the typed target spans more than one /24 and a scan start would ask for confirmation.</summary>
    internal static bool IsLargeManualTarget(string? raw, string? cidrText)
    {
        raw = (raw ?? "").Trim();
        if (raw.Length == 0) return false;
        string ipPart; int cidr;
        if (raw.Contains('/'))
        {
            var parts = raw.Split('/');
            ipPart = parts[0].Trim();
            cidr = ParseCidr(parts[1]);
        }
        else
        {
            ipPart = raw;
            cidr = ParseCidr(cidrText);
        }
        return cidr < 24 && Core.Net.Ipv4.IsValid(ipPart);
    }

    private async Task StartScan()
    {
        if (_restoreGate.Active) return;
        // Build manual subnet "ip/cidr" from the box + dropdown (empty = auto-detect).
        var raw = SubnetBox.Text.Trim();
        if (raw.Length == 0)
        {
            _vm.ManualSubnet = null;
        }
        else
        {
            string ipPart; int cidr;
            if (raw.Contains('/'))                       // custom mask typed in the box
            {
                var parts = raw.Split('/');
                ipPart = parts[0].Trim();
                cidr = ParseCidr(parts[1]);
            }
            else
            {
                ipPart = raw;
                cidr = ParseCidr(ManualCidrText());
            }
            if (!Core.Net.Ipv4.IsValid(ipPart))
            {
                ErrorDialog.Show(this, Loc.ScanError, "", Loc.InvalidSubnet, "");
                return;
            }
            var planned = Core.Net.Ipv4.PrefixesForCidr(ipPart, cidr);
            if (planned is null)
            {
                ErrorDialog.Show(this, Loc.ScanError, "", Loc.RangeTooLargeMsg(cidr), "");
                return;
            }
            if (cidr < 24 && !ConfirmLargeRange(cidr, planned.Count)) return;
            _vm.ManualSubnet = $"{ipPart}/{cidr}";
        }

        try
        {
            ApplySelectedPingCount();
            ApplySnmpTrust();
            _vm.Config = _config;
            await _vm.RunScanAsync();
        }
        catch (Exception ex)
        {
            Core.Log.Warn(Core.LogArea.Scan, "Starting the scan failed", ex);
            ErrorDialog.Show(this, Loc.ScanError, ex, null);
        }
    }

}
