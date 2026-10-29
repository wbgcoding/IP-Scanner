using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.IO;
using IpScanner.Core.Data;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;
using IpScanner.ViewModels;
using IpScanner.Views;

namespace IpScanner;

public partial class MainWindow : Window
{
    // ── Embedded settings overlay ──
    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        LoadSettings(_config);
        RefreshPresetCommand();
        SettingsOverlay.Visibility = Visibility.Visible;
    }

    private void OnSettingsCancel(object sender, RoutedEventArgs e)
    {
        DisarmReset();
        ApplyInstant();
        SettingsOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnOverlayBackgroundClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, SettingsOverlay))
        {
            ApplyInstant();
            SettingsOverlay.Visibility = Visibility.Collapsed;
        }
    }

    // First click arms the button (turns red and asks), second click resets.
    private ConfirmButtonGate? _resetGate;

    private ConfirmButtonGate ResetGate => _resetGate ??= new ConfirmButtonGate(ShowResetArmed, TimeSpan.FromSeconds(4));

    private void ShowResetArmed(bool armed)
    {
        SetResetButtonText(armed);
        ResetButton.Style = (Style)FindResource(armed ? "DangerButton" : "GhostButton");
    }

    private void SetResetButtonText(bool armed) =>
        ResetButton.Content = armed ? Loc.ResetConfirm : Loc.ResetDefaults;

    private void DisarmReset()
    {
        ResetGate.Reset();
        ShowResetArmed(false);
    }

    private void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        if (!ResetGate.Press()) return;
        // The sealed copies of the SNMP community and recent targets go back to defaults too.
        SealedSettings.Update(SealedHome, d => { d.SnmpCommunity = null; d.RecentWakeTargets = new(); });
        RememberSealedValues(new ScanConfig());
        // Only the settings go back to their defaults; the sidebar lists and the profiles stay.
        ApplyFullConfig(_config.ResetKeepingLists());
    }

    /// <summary>Replaces the live config and pushes every field into the whole
    /// UI - theme, language, sidebar lists (pinned IPs, subnet name/color
    /// overlays, internet hosts) included. LoadSettings alone only fills the
    /// Settings-dialog controls; theme/language additionally need their own
    /// apply step because their change handlers no-op while _loadingSettings
    /// is set. Shared by "reset to defaults" and "import config" so neither
    /// one leaves half the UI on the old values.</summary>
    private void ApplyFullConfig(ScanConfig newConfig)
    {
        _config = newConfig;
        _profilesCache = null;   // Config directory may have changed - reload profiles.json from there
        _vm.Config = _config;
        LoadSettings(_config);

        ThemeManager.Switch(Application.Current, _config.Theme);
        WindowTheme.Apply(this, dark: !ThemeManager.IsLight);
        Loc.SetLanguage(_config.Language);
        LocSource.Instance.Refresh();
        RefreshDynamicTexts();

        _vm.RefreshPinnedNames();   // pinned-IP sidebar chips + device pin state
        _vm.RefreshSubnetMeta();    // subnet name/color overlay on network cards
        _vm.RefreshTheme();         // C#-computed colors (theme may have changed)
        _vm.RestartInternetPinging(_config.PingCount == 0 ? 1 : _config.PingCount);

        ApplyUiScale();
        ApplySidebarWidth();
        ApplyExportsExpanded();
        ApplySidebarSectionsExpanded();
        ApplyBarColors();
        ApplyAccentColor();
        Core.NumberFormat.ApplyRegionOverride(_config.DateNumberFormat);
        ApplyRowDensity();
        ApplyColumnVisibility();
        ApplyDefaultPingCount();
        ApplyGraphSettings();
        ApplyHeatThresholds();
        ApplyRepeatScanTimer();
        UpdateTrayVisibility();
        ApplySkippedLegendVisibility();
        ApplyPortScanSettings();
        PersistConfigDebounced();
    }

    /// <summary>Paths next to the exe become ".\" relative paths.</summary>
    private static string MakeRelative(string path)
    {
        try
        {
            var full = ScanConfig.ResolvePath(path);
            var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), baseDir,
                              StringComparison.OrdinalIgnoreCase))
                return @".\";
            if (full.StartsWith(baseDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return @".\" + Path.GetRelativePath(baseDir, full);
            return path;
        }
        catch (Exception ex) { Core.Log.Warn(Core.LogArea.Config, "Shortening a path for display failed", ex); return path; }
    }

    /// <summary>Existing directory of a (possibly relative) path, or null.</summary>
    private static string? DirOf(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var full = ScanConfig.ResolvePath(path);
            var dir = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
            return dir is not null && Directory.Exists(dir) ? dir : null;
        }
        catch (Exception ex) { Core.Log.Warn(Core.LogArea.Config, "Reading a folder from a path failed", ex); return null; }
    }

    private async void OnMergeDb(object sender, RoutedEventArgs e)
    {
        await UiGuard.Run(async () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Loc.DbFileFilter };
            if (dlg.ShowDialog(this) != true) return;
            var trigger = sender as UIElement;
            if (trigger is not null) trigger.IsEnabled = false;
            try
            {
                var dbPath = _config.DatabasePath;
                var destination = await Task.Run(() => new KnownDevicesDb(dbPath));
                var progressDialog = new MergeProgressDialog(destination, dlg.FileName)
                    { Owner = this };
                if (progressDialog.ShowDialog() == true)
                    MessageDialog.Show(this, Loc.MergeDone(progressDialog.MergedCount), Loc.MergeDb);
                else if (progressDialog.Error is ForeignDatabaseException)
                    MessageDialog.Show(this, Loc.ForeignDatabaseMsg, Loc.MergeDb, MessageDialogButtons.Ok, MessageDialogKind.Warning);
                else if (progressDialog.Error is { } error)
                    ErrorDialog.Show(this, Loc.MergeFailedTitle, error, dlg.FileName);
            }
            catch (ForeignDatabaseException ex)
            {
                Core.Log.Warn(Core.LogArea.Db, "Merge refused: foreign database", ex);
                MessageDialog.Show(this, Loc.ForeignDatabaseMsg, Loc.MergeDb, MessageDialogButtons.Ok, MessageDialogKind.Warning);
            }
            catch (Exception ex)
            {
                Core.Log.Warn(Core.LogArea.Db, "Merging the database failed", ex);
                ErrorDialog.Show(this, Loc.MergeFailedTitle, ex, dlg.FileName);
            }
            finally
            {
                if (trigger is not null) trigger.IsEnabled = true;
            }
        }, Loc.MergeDb);
    }

    // The plain-CSV escape hatch for the encrypted database - the only
    // supported way to move device history to a different Windows account/machine.
    private async void OnExportDbCsv(object sender, RoutedEventArgs e)
    {
        await UiGuard.Run(async () =>
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { Filter = Loc.DbCsvFileFilter, FileName = Loc.DbCsvFileName };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                var dbPath = _config.DatabasePath;
                var target = dlg.FileName;
                int n = await BusyDialog.RunAsync(this, Loc.BusyExportingDevices,
                    () => new KnownDevicesDb(dbPath).ExportCsv(target));
                MessageDialog.Show(this, Loc.CsvRows(n), Loc.ExportDbCsv);
            }
            catch (Exception ex)
            {
                Core.Log.Warn(Core.LogArea.Db, "Exporting the database failed", ex);
                ErrorDialog.Show(this, Loc.DbExportFailedTitle, ex, dlg.FileName);
            }
        }, Loc.ExportDbCsv);
    }

    private async void OnImportDbCsv(object sender, RoutedEventArgs e)
    {
        await UiGuard.Run(async () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Loc.DbCsvFileFilter };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                var dbPath = _config.DatabasePath;
                var source = dlg.FileName;
                var (imported, skipped) = await BusyDialog.RunAsync(this, Loc.BusyImportingDevices,
                    () => new KnownDevicesDb(dbPath).ImportCsv(source));
                MessageDialog.Show(this, Loc.CsvImportResult(imported, skipped), Loc.ImportDbCsv);
            }
            catch (Exception ex)
            {
                Core.Log.Warn(Core.LogArea.Db, "Importing the database failed", ex);
                ErrorDialog.Show(this, Loc.DbImportFailedTitle, ex, dlg.FileName);
            }
        }, Loc.ImportDbCsv);
    }

    /// <summary>What went wrong reading the sealed settings at startup (shown once as a notice).</summary>
    internal static LoadProblem StartupSealedProblem { get; private set; }

    // Last values written to (or read from) the sealed settings, so a persist that changed neither
    // skips the encrypt-and-write round trip.
    private static readonly object SealedGate = new();
    private static string? _sealedSnmp;
    private static string? _sealedRecent;
    private static string? _sealedHome;

    /// <summary>Folder the sealed settings currently live in. It follows the database folder only
    /// once <see cref="RelocateSealedSettings"/> has carried the data over, so a changed database
    /// path never leaves the profiles, SNMP community and wake targets behind.</summary>
    internal static string SealedHome
    {
        get { lock (SealedGate) return _sealedHome ??= SealedSettings.DirFor(new ScanConfig().DatabasePath); }
    }

    /// <summary>Copies the sealed settings into the folder of the configured database when that
    /// folder changed. Call before the conf that names the new path is written; on failure the old
    /// folder stays in use and the next persist retries.</summary>
    private static void RelocateSealedSettings(ScanConfig c)
    {
        lock (SealedGate)
        {
            var target = SealedSettings.DirFor(c.DatabasePath);
            var home = _sealedHome ??= SealedSettings.DirFor(new ScanConfig().DatabasePath);
            if (string.Equals(Path.GetFullPath(home), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) return;
            if (SealedSettings.Relocate(home, target)) _sealedHome = target;
        }
    }

    private static void RememberSealedValues(ScanConfig c)
    {
        lock (SealedGate) { _sealedSnmp = c.SnmpCommunity; _sealedRecent = string.Join("\n", c.RecentWakeTargets); }
    }

    /// <summary>Writes the SNMP community and the recent Wake-on-LAN targets to the sealed settings
    /// when either changed. Safe to call from any thread; a failed write (key unavailable) is retried
    /// at the next persist.</summary>
    private static void SaveSealedSettings(ScanConfig c)
    {
        var recent = string.Join("\n", c.RecentWakeTargets);
        lock (SealedGate)
        {
            if (!c.LegacySealedPending && _sealedSnmp == c.SnmpCommunity && _sealedRecent == recent) return;
            var snmp = c.SnmpCommunity;
            var targets = c.RecentWakeTargets.ToList();
            if (!SealedSettings.Update(_sealedHome ?? SealedSettings.DirFor(c.DatabasePath), d => { d.SnmpCommunity = snmp; d.RecentWakeTargets = targets; })) return;
            _sealedSnmp = c.SnmpCommunity;
            _sealedRecent = recent;
            c.LegacySealedPending = false;
        }
    }

    /// <summary>Config file lives next to the database.</summary>
    private static string ConfPathFor(string dbPath)
        => Path.Combine(Path.GetDirectoryName(ScanConfig.ResolvePath(dbPath)) ?? AppContext.BaseDirectory, ScanConfig.ConfigFileName);

    /// <summary>Manual hostname/group overrides live next to the database too.</summary>
    private static string OverridesPathFor(string dbPath)
        => Path.Combine(Path.GetDirectoryName(ScanConfig.ResolvePath(dbPath)) ?? AppContext.BaseDirectory, ScanConfig.OverridesFileName);

    // True only when NONE of the candidate conf locations below
    // existed on disk at startup - a genuinely fresh install, not just an
    // existing install missing some newer config key. Set once, by whichever
    // of App.xaml.cs / the MainWindow constructor actually calls this method
    // first (App.xaml.cs always does, so it wins in the normal startup path).
    internal static bool IsFreshInstall { get; private set; }

    internal static ScanConfig LoadPersistedConfig()
    {
        try
        {
            bool foundExisting = false;

            // 1. Try the default location (next to the exe); fall back to the
            //    pre-2.6 location in the scans folder for existing installs.
            var def = ConfPathFor(new ScanConfig().DatabasePath);
            if (!File.Exists(def))
            {
                var legacy = Path.Combine(ScanConfig.ResolvePath(ScanConfig.DefaultOutputDirectory),
                                          ScanConfig.ConfigFileName);
                if (File.Exists(legacy)) def = legacy;
            }
            ConfigManager.MigrateIfNeeded(def);
            foundExisting |= File.Exists(def);
            var cfg = File.Exists(def) ? ConfigManager.Load(def) : new ScanConfig();

            // 2. If the loaded config has a non-default DB path, its sibling conf wins.
            var atDb = ConfPathFor(cfg.DatabasePath);
            if (!string.Equals(atDb, def, StringComparison.OrdinalIgnoreCase) && File.Exists(atDb))
            {
                ConfigManager.MigrateIfNeeded(atDb);
                cfg = ConfigManager.Load(atDb);
                foundExisting = true;
            }

            // 3. A configured conf directory always takes priority.
            var dir = cfg.ConfigDirectory.Trim();
            if (dir.Length > 0)
            {
                var custom = Path.Combine(ScanConfig.ResolvePath(dir), ScanConfig.ConfigFileName);
                if (File.Exists(custom))
                {
                    ConfigManager.MigrateIfNeeded(custom);
                    cfg = ConfigManager.Load(custom);
                    foundExisting = true;
                }
            }
            IsFreshInstall = !foundExisting;
            // Normalize to Windows-style relative paths (older confs used "./").
            cfg.DatabasePath = MakeRelative(cfg.DatabasePath);
            cfg.OutputDirectory = MakeRelative(cfg.OutputDirectory);
            cfg.ConfigDirectory = MakeRelative(cfg.ConfigDirectory);
            // SNMP community and recent Wake-on-LAN targets live in the sealed settings file.
            lock (SealedGate) _sealedHome = SealedSettings.DirFor(cfg.DatabasePath);
            var sealedSafe = SealedSettings.ApplyTo(cfg, SealedHome, out var sealedProblem);
            StartupSealedProblem = sealedProblem;
            cfg.LegacySealedPending = !sealedSafe;
            if (sealedSafe) RememberSealedValues(cfg);
            return cfg;
        }
        catch (Exception ex)
        {
            Core.Log.Warn(Core.LogArea.Config, "Loading the settings failed, defaults used", ex);
            IsFreshInstall = false; return new ScanConfig();
        }
    }

    private bool _loadingSettings;

    /// <summary>Instant save: every settings change applies and persists at once.</summary>
    private void ApplyInstant()
    {
        if (_loadingSettings) return;
        var prev = _config.PinnedIps;
        bool internetWas = _config.EnableInternetPing;
        _config = ReadSettings();
        _vm.Config = _config;
        RefreshPresetUi(_config);
        if (internetWas != _config.EnableInternetPing)
            _vm.RestartInternetPinging(_config.PingCount == 0 ? 1 : _config.PingCount);
        // Avoid ObservableCollection mutations (which cause a layout pass that
        // resets the TextBox caret) unless PinnedIps actually changed.
        if (!_config.PinnedIps.SequenceEqual(prev))
            _vm.RefreshPinnedNames();
        _vm.ApplyAutoGraph(_config.AutoGraphForPinned);
        ApplyDefaultPingCount();
        SyncExportToggles();
        ApplyGraphSettings();
        ApplyHeatThresholds();
        ApplyRowDensity();
        ApplyColumnVisibility();
        ApplyRepeatScanTimer();
        UpdateTrayVisibility();
        ApplySkippedLegendVisibility();
        ApplyPortScanSettings();
        PersistConfigDebounced();
        Core.Log.Info(Core.LogArea.Config, "Settings saved");
    }

    private void OnSettingChanged(object sender, RoutedEventArgs e) => ApplyInstant();

    /// <summary>Leaving a number box shows the clamped value it was saved with.</summary>
    private void OnSettingsFieldLostFocus(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings || e.OriginalSource is not System.Windows.Controls.TextBox) return;
        ReadSettings();
    }

    /// <summary>Moving the timing slider lays the template's numbers into the override boxes, so the
    /// user sees exactly what the template means and can still change any single value afterwards.</summary>
    private void OnPortScanTimingChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdatePortScanTimingSummary();
        if (_loadingSettings || !IsLoaded) return;
        LayTimingTemplateIntoBoxes();
        ApplyInstant();
    }

    private void LayTimingTemplateIntoBoxes()
    {
        var t = Core.Scanner.ScanTiming.For((int)Math.Round(PortScanTimingSlider.Value));
        var was = _loadingSettings;
        _loadingSettings = true;
        try
        {
            PortScanConnectTimeoutBox.Text = t.InitialRttMs.ToString();
            PortScanBannerTimeoutBox.Text = t.BannerTimeoutMs.ToString();
            PortScanParallelismBox.Text = t.Parallelism.ToString();
            PortScanRetriesBox.Text = t.Retries.ToString();
            PortScanDelayBox.Text = t.ScanDelayMs.ToString();
            PortScanMaxRateBox.Text = t.MaxRate.ToString();
        }
        finally { _loadingSettings = was; }
    }

    private void UpdatePortScanTimingSummary()
    {
        if (PortScanTimingSummary is null) return;
        var t = Core.Scanner.ScanTiming.For((int)Math.Round(PortScanTimingSlider.Value));
        PortScanTimingName.Text = Loc.PortScanTimingName(t.Template);
        PortScanTimingWarn.Visibility = t.Template >= 4 ? Visibility.Visible : Visibility.Collapsed;
        PortScanTimingSummary.Text = Loc.PortScanTimingSummary(t.Parallelism, t.InitialRttMs, t.Retries, t.ScanDelayMs, t.MaxRate);
    }

    /// <summary>The configured SNMP community is only sent to the user's own networks: the
    /// configured subnets plus the detected local network (with its real mask). Re-run whenever the subnet list,
    /// the adapter or the network changes, and before each scan.</summary>
    private void ApplySnmpTrust()
    {
        var trusted = _config.Subnets.Select(s => Core.Models.NetEntry.Parse(s).Target).ToList();
        var detected = Core.Scanner.NetworkDetector.DetectFast(Core.Scanner.NetworkDetector.SelectedSourceIp);
        if (detected.Ip is { Length: > 0 } local)
            trusted.Add($"{local}/{Core.Net.Ipv4.MaskToCidr(detected.SubnetMask) ?? 24}");
        Core.Scanner.PortScanner.SnmpTrustedNetworks = trusted;
    }

    /// <summary>Pushes the advanced network settings into PortScanner's own
    /// mutable statics - same pattern as ApplyHeatThresholds/Palette. The knock timeout
    /// covers both TCP connect and UDP wait; only the version-banner read gets its own.</summary>
    private void ApplyPortScanSettings()
    {
        Core.Scanner.Os.OsFingerprinter.Enabled = _config.OsFingerprint;
        Core.Scanner.PortScanner.ServiceDetection = _config.PortServiceDetection;
        Core.Scanner.PortScanner.ApplyTiming(Core.Scanner.ScanTiming.FromSettings(
            _config.PortScanTiming, _config.PortScanParallel, _config.PortScanConnectTimeoutMs,
            _config.PortScanBannerTimeoutMs, _config.PortScanRetries, _config.PortScanDelayMs, _config.PortScanMaxRate));
        Core.Scanner.PortScanner.ServiceProbeIntensity = _config.PortScanIntensity;
        Core.Scanner.PortScanner.SnmpCommunity = _config.SnmpCommunity;
        ApplySnmpTrust();
        Core.Scanner.PortScanner.CustomTcpPorts = _config.CustomPorts
            .Select(Core.Models.NetEntry.Parse)
            .Where(en => int.TryParse(en.Target, out var p) && p is > 0 and <= ushort.MaxValue)
            .Select(en => (int.Parse(en.Target), en.Name.Length > 0 ? en.Name : Loc.CustomPortDefaultName))
            .ToList();
    }

    /// <summary>"0 Threads" (max speed, one worker per device) becomes a
    /// checkbox instead of an explanation-needing 0 in the number field. Old
    /// configs with scan_threads=0 keep working unchanged - ScanConfig itself
    /// still stores 0 for "unlimited", only the settings UI changed.</summary>
    private void OnUnlimitedThreadsChanged(object sender, RoutedEventArgs e)
    {
        ScanThreadsBox.IsEnabled = UnlimitedThreadsBox.IsChecked != true;
        if (_loadingSettings) return;
        ApplyInstant();
    }

    /// <summary>Compact (22px) vs normal (28px) table rows, applied the
    /// same way as the accent color - set directly on Application.Resources so
    /// it survives theme switches untouched.</summary>
    private void ApplyRowDensity()
        => Application.Current.Resources["GridRowHeight"] = _config.CompactRows ? 22.0 : 28.0;

    /// <summary>Show/hide the eight optional table columns. IP, status
    /// and hostname have no checkbox and are never hidden.</summary>
    private bool IsColumnHidden(string key) => _config.HiddenColumns.Contains(key);

    private void ApplyColumnVisibility()
    {
        GroupColumn.Visibility = IsColumnHidden("group") ? Visibility.Collapsed : Visibility.Visible;
        AvgColumn.Visibility = IsColumnHidden("avg") ? Visibility.Collapsed : Visibility.Visible;
        MinColumn.Visibility = IsColumnHidden("min") ? Visibility.Collapsed : Visibility.Visible;
        MaxColumn.Visibility = IsColumnHidden("max") ? Visibility.Collapsed : Visibility.Visible;
        LastColumn.Visibility = IsColumnHidden("last") ? Visibility.Collapsed : Visibility.Visible;
        LossColumn.Visibility = IsColumnHidden("loss") ? Visibility.Collapsed : Visibility.Visible;
        JitterColumn.Visibility = IsColumnHidden("jitter") ? Visibility.Collapsed : Visibility.Visible;
        ProgressColumn.Visibility = IsColumnHidden("progress") ? Visibility.Collapsed : Visibility.Visible;
        MacColumn.Visibility = IsColumnHidden("mac") ? Visibility.Collapsed : Visibility.Visible;
        VendorColumn.Visibility = IsColumnHidden("vendor") ? Visibility.Collapsed : Visibility.Visible;
        PortsColumn.Visibility = IsColumnHidden("ports") ? Visibility.Collapsed : Visibility.Visible;
        MethodColumn.Visibility = IsColumnHidden("method") ? Visibility.Collapsed : Visibility.Visible;
        OsColumn.Visibility = IsColumnHidden("os") ? Visibility.Collapsed : Visibility.Visible;
        FirstSeenColumn.Visibility = IsColumnHidden("first_seen") ? Visibility.Collapsed : Visibility.Visible;
        LastSeenColumn.Visibility = IsColumnHidden("last_seen") ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Shows/hides the "skipped" pings legend square. Skipped
    /// pings are counted in Progress.SkippedText/SkippedFraction either way -
    /// this only toggles whether the legend row is visible.</summary>
    private void ApplySkippedLegendVisibility()
        => SkippedLegendPanel.Visibility = _config.ShowSkippedLegend ? Visibility.Visible : Visibility.Collapsed;

    private void OnColumnVisibilityChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        var hidden = new List<string>();
        if (GroupColBox.IsChecked != true) hidden.Add("group");
        if (AvgColBox.IsChecked != true) hidden.Add("avg");
        if (MinColBox.IsChecked != true) hidden.Add("min");
        if (MaxColBox.IsChecked != true) hidden.Add("max");
        if (LastColBox.IsChecked != true) hidden.Add("last");
        if (LossColBox.IsChecked != true) hidden.Add("loss");
        if (JitterColBox.IsChecked != true) hidden.Add("jitter");
        if (ProgressColBox.IsChecked != true) hidden.Add("progress");
        if (MacColBox.IsChecked != true) hidden.Add("mac");
        if (VendorColBox.IsChecked != true) hidden.Add("vendor");
        if (PortsColBox.IsChecked != true) hidden.Add("ports");
        if (MethodColBox.IsChecked != true) hidden.Add("method");
        if (OsColBox.IsChecked != true) hidden.Add("os");
        if (FirstSeenColBox.IsChecked != true) hidden.Add("first_seen");
        if (LastSeenColBox.IsChecked != true) hidden.Add("last_seen");
        _config.HiddenColumns = hidden;
        ApplyColumnVisibility();
        PersistConfigDebounced();
    }

    /// <summary>Left-hand Settings nav: show the matching page, hide the
    /// rest. A plain WPF ListBox already handles Up/Down to move the selection
    /// and sits before the page content in the visual tree, so Tab naturally
    /// walks into the current page's controls afterwards - no extra keyboard
    /// wiring needed.</summary>
    private void OnSettingsNavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SettingsNavList.SelectedItem is not ListBoxItem { Tag: string tag }) return;
        ScanPage.Visibility = tag == "Scan" ? Visibility.Visible : Visibility.Collapsed;
        DisplayPage.Visibility = tag == "Display" ? Visibility.Visible : Visibility.Collapsed;
        InternetPage.Visibility = tag == "Internet" ? Visibility.Visible : Visibility.Collapsed;
        GraphsPage.Visibility = tag == "Graphs" ? Visibility.Visible : Visibility.Collapsed;
        DataPage.Visibility = tag == "Data" ? Visibility.Visible : Visibility.Collapsed;
        AppPage.Visibility = tag == "App" ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = tag == "About" ? Visibility.Visible : Visibility.Collapsed;
        HelpPage.Visibility = tag == "Help" ? Visibility.Visible : Visibility.Collapsed;
        if (_loadingSettings) return;
        _config.SettingsPage = tag;
        PersistConfigDebounced();
    }

    // Text scale only commits when the field loses focus - applying it on every
    // keystroke would zoom wildly while typing.
    private void OnUiScaleCommit(object sender, RoutedEventArgs e) => ApplyUiScale();

    private void OnOfflineRecheckOffChanged(object sender, RoutedEventArgs e)
    {
        UpdateOfflineRecheckEnabled();
        if (_loadingSettings) return;
        ApplyInstant();
    }

    /// <summary>Preset the ping dropdown from the configured default.</summary>
    // Sentinel so the header ping dropdown is only synced when the configured
    // default actually changes - not on unrelated settings like export toggles.
    private int _lastSyncedDefaultPingCount = int.MinValue;

    private void ApplyDefaultPingCount()
    {
        if (_config.PingCount == _lastSyncedDefaultPingCount) return;
        _lastSyncedDefaultPingCount = _config.PingCount;
        PingCountBox.Text = _config.PingCount == ScanConfig.InfinitePingCount
            ? "∞" : _config.PingCount.ToString();
        UpdateOfflineRecheckEnabled();
    }

    /// <summary>The offline recheck only runs next to endless pings, so its field is inert otherwise.</summary>
    private void UpdateOfflineRecheckEnabled()
    {
        bool endless = _config.PingCount == ScanConfig.InfinitePingCount;
        OfflineRecheckOffBox.IsEnabled = endless;
        OfflineRecheckBox.IsEnabled = endless && OfflineRecheckOffBox.IsChecked != true;
    }

    private void SyncExportToggles()
    {
        TxtToggle.IsChecked = _config.FileOutput;
        CsvToggle.IsChecked = _config.ExportCsv;
    }

    // The sidebar TXT/CSV pills are the only place to toggle these -
    // the settings dialog's own checkboxes were removed to stop the two from
    // drifting out of sync. ReadSettings reads the pills directly.
    private void OnExportToggle(object sender, RoutedEventArgs e) => ApplyInstant();

    private string _filterText = "";

    // Quick filter pills - session-only (not persisted), ANDed with
    // each other and with the text filter.
    private bool _onlyOnline;
    private bool _onlyPinned;
    private bool _onlyUnknown;

    // A row shows when it passes the offline-DB toggle AND the quick-filter
    // pills AND matches the filter text.
    // Field-prefix parsing (ip:/mac:/name:/hersteller:/port:) lives in the
    // pure, unit-tested Core.Scanner.DeviceFilter so it needs no WPF plumbing here.
    private bool DeviceVisible(object o)
    {
        if (o is not DeviceViewModel vm) return true;
        if (!(_config.ShowOfflineFromDb || vm.IsOnline || vm.IsPinned || !vm.IsFromDb)) return false;
        if (!Core.Scanner.DeviceFilter.PassesQuickFilters(_onlyOnline, _onlyPinned, _onlyUnknown,
                vm.IsOnline, vm.IsPinned, vm.IsRogue)) return false;
        return Core.Scanner.DeviceFilter.Matches(_filterText, vm.Ip, vm.Hostname, vm.Mac, vm.Vendor,
            vm.Services.Select(s => s.Port));
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        _filterText = FilterBox.Text.Trim();
        _devicesView?.Refresh();   // TextChanged can fire before the view is wired
    }

    private void OnQuickFilterToggle(object sender, RoutedEventArgs e)
    {
        _onlyOnline = OnlinePill.IsChecked == true;
        _onlyPinned = PinnedPill.IsChecked == true;
        _onlyUnknown = UnknownPill.IsChecked == true;
        _devicesView?.Refresh();
    }

    // Remembered column sort and widths.
    // Keyed by a short, version-stable name (not the localized header text) so
    // an old or newer build's unrecognized keys are simply ignored on restore.
    private Dictionary<string, DataGridColumn> TableColumnsByKey() => new()
    {
        ["Ip"] = IpColumn, ["Status"] = StatusColumn, ["Group"] = GroupColumn,
        ["Hostname"] = HostnameColumn, ["Avg"] = AvgColumn, ["Min"] = MinColumn,
        ["Max"] = MaxColumn, ["Last"] = LastColumn, ["Loss"] = LossColumn,
        ["Jitter"] = JitterColumn, ["Progress"] = ProgressColumn, ["Mac"] = MacColumn,
        ["Vendor"] = VendorColumn, ["Ports"] = PortsColumn, ["Method"] = MethodColumn, ["Os"] = OsColumn,
        ["FirstSeen"] = FirstSeenColumn, ["LastSeen"] = LastSeenColumn,
    };

    private void RestoreTableSortAndWidths()
    {
        var columns = TableColumnsByKey();

        if (!string.IsNullOrEmpty(_config.TableWidths))
        {
            foreach (var part in _config.TableWidths.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split(':', 2);
                if (kv.Length != 2 || !columns.TryGetValue(kv[0], out var column)) continue;
                if (double.TryParse(kv[1], System.Globalization.CultureInfo.InvariantCulture, out var width) && width > 0)
                    column.Width = new DataGridLength(width);
            }
        }

        if (!string.IsNullOrEmpty(_config.TableSort))
        {
            var kv = _config.TableSort.Split(':', 2);
            if (kv.Length == 2 && columns.TryGetValue(kv[0], out var column) && !string.IsNullOrEmpty(column.SortMemberPath))
            {
                var direction = kv[1].Equals("desc", StringComparison.OrdinalIgnoreCase)
                    ? ListSortDirection.Descending : ListSortDirection.Ascending;
                column.SortDirection = direction;
                _devicesView.SortDescriptions.Add(new SortDescription(column.SortMemberPath, direction));
            }
        }
    }

    // DataGridColumn.Width has no CLR/routed change event of its own - a drag-
    // resize still updates this dependency property, so a property-changed
    // listener via DependencyPropertyDescriptor is the standard way to observe it.
    private readonly List<(DataGridColumn Column, EventHandler Handler)> _widthHooks = new();
    private DependencyPropertyDescriptor? _widthDescriptor;

    private void HookColumnWidthPersistence()
    {
        _widthDescriptor = DependencyPropertyDescriptor.FromProperty(DataGridColumn.WidthProperty, typeof(DataGridColumn));
        if (_widthDescriptor is null) return;
        foreach (var column in TableColumnsByKey().Values)
        {
            EventHandler handler = (_, _) => SaveTableWidths();
            _widthDescriptor.AddValueChanged(column, handler);
            _widthHooks.Add((column, handler));
        }
    }

    private void UnhookColumnWidthPersistence()
    {
        foreach (var (column, handler) in _widthHooks) _widthDescriptor?.RemoveValueChanged(column, handler);
        _widthHooks.Clear();
    }

    private void SaveTableWidths()
    {
        // The Hostname column is the one "*"-sized (fill remaining space) column -
        // saving a fixed pixel width for it would turn it non-flexible on restart.
        _config.TableWidths = string.Join(",", TableColumnsByKey()
            .Where(kv => !kv.Value.Width.IsStar)
            .Select(kv => $"{kv.Key}:{kv.Value.Width.DisplayValue:F0}"));
        PersistConfigDebounced();
    }

    private void OnColumnSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var path = e.Column.SortMemberPath;
        if (string.IsNullOrEmpty(path)) return;

        var newDirection = e.Column.SortDirection != ListSortDirection.Ascending
            ? ListSortDirection.Ascending : ListSortDirection.Descending;
        foreach (var col in DeviceGrid.Columns) col.SortDirection = null;
        e.Column.SortDirection = newDirection;

        _devicesView.SortDescriptions.Clear();
        _devicesView.SortDescriptions.Add(new SortDescription(path, newDirection));
        _devicesView.Refresh();

        var key = TableColumnsByKey().FirstOrDefault(kv => kv.Value == e.Column).Key;
        if (key is null) return;
        _config.TableSort = $"{key}:{(newDirection == ListSortDirection.Descending ? "desc" : "asc")}";
        PersistConfigDebounced();
    }

    /// <summary>Separate About window with the project-page link. The
    /// browser only launches when the user clicks the link inside it.</summary>
    private void OnOpenAboutWindow(object sender, RoutedEventArgs e)
        => new Views.AboutWindow { Owner = this }.ShowDialog();

    private void OnToggleOffline(object sender, RoutedEventArgs e)
    {
        // Direct flag flip + refresh - a full ApplyInstant()/ReadSettings() here
        // stalls the toggle during an active scan. ReadSettings reads OfflineToggle
        // back, so a later settings save stays consistent.
        if (_loadingSettings) return;
        _config.ShowOfflineFromDb = OfflineToggle.IsChecked == true;
        _devicesView.Refresh();
        PersistConfigDebounced();
    }

}
