using System.IO;
using System.Windows;
using System.Windows.Controls;
using IpScanner.Core.Data;
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

    public MainWindow()
    {
        // Settings persist as ip_scanner.conf next to the database and are
        // loaded again on every start. Language must be set before any XAML
        // loads because x:Static bindings cache their values.
        _config = LoadPersistedConfig();
        Loc.SetLanguage(_config.Language);

        InitializeComponent();
        WindowTheme.ApplyDark(this);
        _vm = new MainViewModel(
            pingFunc: IcmpPinger.Ping,
            detectNetwork: NetworkDetector.DetectFast,
            dispatch: a => Dispatcher.BeginInvoke(a),
            enrichers: Enrichers)
        { Config = _config };
        DataContext = _vm;

        // Prefill the subnet field with the currently detected network so the
        // value is visible immediately (user can edit it before scanning).
        try
        {
            var ni = NetworkDetector.DetectFast();
            if (ni.Cidr is not null) SubnetBox.Text = ni.Cidr.Split('/')[0];   // base IP, no /24
        }
        catch { /* detection best-effort */ }

        // Center horizontally on the primary monitor; fill its full work-area height.
        var wa = SystemParameters.WorkArea;
        Width = Math.Min(Width, wa.Width);
        Height = wa.Height;
        Top = wa.Top;
        Left = wa.Left + (wa.Width - Width) / 2;

        // One button toggles between Scan and Stop depending on scan state.
        _vm.PropertyChanged += (_, ev) =>
        {
            if (ev.PropertyName == nameof(MainViewModel.IsScanning))
                Dispatcher.BeginInvoke(UpdateScanButton);
        };
        LogoImage.RenderTransform = _logoSpin;
        System.Windows.Media.CompositionTarget.Rendering += OnSpinTick;
        ApplyUiScale();          // persisted text-scale takes effect at startup
        ApplyBarColors();        // persisted bar colors
        ApplyDefaultPingCount(); // persisted default ping count into the dropdown
        LanguageBox.ItemsSource = new[] { Loc.LangAuto, "Deutsch", "English" };
        LoadSettings(_config);   // fills all fields incl. export toggles once

        // On startup, immediately run a discovery sweep of the local network so
        // the sidebar network info and online devices show up without a manual scan.
        Loaded += async (_, _) =>
        {
            try
            {
                await _vm.RunInitScanAsync();
                // Auto-detect: fill the subnet field from the discovered network
                // if the user hasn't typed anything yet.
                if (SubnetBox.Text.Trim().Length == 0 && _vm.Networks.Count > 0)
                    SubnetBox.Text = _vm.Networks[0].Cidr.Split('/')[0];
            }
            catch { /* best-effort */ }
        };
    }

    // Logo spin: speed eases toward a target so starting/stopping never jumps.
    private readonly System.Windows.Media.RotateTransform _logoSpin = new();
    private double _spinAngle, _spinSpeed, _spinTarget;   // deg, deg/s
    private double _spinRef = 1;                          // last nonzero target (glow scale)
    private long _spinLastTick = System.Diagnostics.Stopwatch.GetTimestamp();

    private void OnSpinTick(object? sender, EventArgs e)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double dt = Math.Min(0.1, (now - _spinLastTick) / (double)System.Diagnostics.Stopwatch.Frequency);
        _spinLastTick = now;
        if (_spinSpeed == 0 && _spinTarget == 0) return;

        _spinSpeed += (_spinTarget - _spinSpeed) * Math.Min(1.0, dt * 2.5);   // ~1s ramp
        if (_spinTarget == 0 && Math.Abs(_spinSpeed) < 3) _spinSpeed = 0;
        _spinAngle = (_spinAngle + _spinSpeed * dt) % 360;
        _logoSpin.Angle = _spinAngle;

        // Centre dot glow: brightness follows the eased spin speed, pulsing
        // twice per revolution so it breathes in sync with the rotation.
        double norm = Math.Clamp(_spinSpeed / _spinRef, 0, 1);
        double pulse = 0.45 + 0.55 * (0.5 + 0.5 * Math.Sin(_spinAngle * Math.PI / 90));
        LogoGlow.Opacity = norm * pulse;
    }

    private void UpdateScanButton()
    {
        bool scanning = _vm.IsScanning;
        ScanStopButton.Content = scanning ? Loc.Stop : Loc.Scan;
        ScanStopButton.Style = (Style)FindResource(scanning ? "DangerButton" : "AccentButton");

        // Spin while scanning — faster with more threads, eased in/out.
        int threads = _config.ScanThreads <= 0 ? 254 : _config.ScanThreads;
        double seconds = Math.Clamp(120.0 / threads, 0.6, 6.0);
        _spinTarget = scanning ? 360.0 / seconds : 0.0;
        if (_spinTarget > 0) _spinRef = _spinTarget;   // glow scales against full speed
    }

    /// <summary>Scale the whole UI (text included) by the configured percent.</summary>
    // ── Progress-bar colors (legend squares act as color pickers) ──
    private void ApplyBarColors()
    {
        static System.Windows.Media.SolidColorBrush B(string hex) =>
            new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        DevBar.Color1 = B(_config.ColorOnline);  LegOnline.Background = B(_config.ColorOnline);
        DevBar.Color2 = B(_config.ColorOffline); LegOffline.Background = B(_config.ColorOffline);
        DevBar.Color3 = B(_config.ColorUnknown); LegUnknown.Background = B(_config.ColorUnknown);
        PingBar.Color1 = B(_config.ColorSuccess); LegSuccess.Background = B(_config.ColorSuccess);
        PingBar.Color2 = B(_config.ColorFailed);  LegFailed.Background = B(_config.ColorFailed);
        PingBar.Color3 = B(_config.ColorSkipped); LegSkipped.Background = B(_config.ColorSkipped);
    }

    private void OnLegendColorClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.Border { Tag: string key }) return;
        string current = GetBarColor(key);
        var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(current);
        using var dlg = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B),
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        SetBarColor(key, $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}");
        ApplyBarColors();
        PersistConfig();
    }

    private string GetBarColor(string key) => key switch
    {
        "online" => _config.ColorOnline, "offline" => _config.ColorOffline,
        "unknown" => _config.ColorUnknown, "success" => _config.ColorSuccess,
        "failed" => _config.ColorFailed, _ => _config.ColorSkipped,
    };

    private void SetBarColor(string key, string hex)
    {
        switch (key)
        {
            case "online": _config.ColorOnline = hex; break;
            case "offline": _config.ColorOffline = hex; break;
            case "unknown": _config.ColorUnknown = hex; break;
            case "success": _config.ColorSuccess = hex; break;
            case "failed": _config.ColorFailed = hex; break;
            default: _config.ColorSkipped = hex; break;
        }
    }

    private void PersistConfig()
    {
        try
        {
            var path = ConfPathFor(_config.DatabasePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            ConfigManager.Save(path, _config);
        }
        catch { /* persisting is best-effort */ }
    }

    private void ApplyUiScale()
    {
        double f = Math.Clamp(_config.UiScalePercent, 50, 200) / 100.0;
        RootLayout.LayoutTransform = f == 1.0 ? null
            : new System.Windows.Media.ScaleTransform(f, f);
    }

    // MAC/hostname techniques, best-first (index = rank). The engine runs all
    // of them in parallel per device, shows the FIRST result immediately and
    // swaps in a better-ranked one when it arrives later.
    //   MAC:      ARP (rank 0) > NetBIOS (rank 3)
    //   Hostname: reverse DNS (1) > mDNS (2) > NetBIOS (3)
    private static readonly Func<string, (string? mac, string? host)>[] Enrichers =
    {
        ip => (ArpHelper.Resolve(ip), null),
        ip => (null, HostnameResolver.Resolve(ip)),
        ip => (null, MdnsHelper.Resolve(ip, 1500)),
        ip => { var (name, mac) = NetBiosHelper.Lookup(ip); return (mac, name); },
    };

    private async void OnScanStopClick(object sender, RoutedEventArgs e)
    {
        if (_vm.IsScanning) _vm.Stop();
        else await StartScan();
    }

    private async void OnSubnetKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && !_vm.IsScanning) await StartScan();
    }

    private async Task StartScan()
    {
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
                // Editable dropdown: free text wins, otherwise the selected item.
                var cidrText = string.IsNullOrWhiteSpace(CidrBox.Text)
                    ? (CidrBox.SelectedItem as ComboBoxItem)?.Content?.ToString()
                    : CidrBox.Text;
                cidr = ParseCidr(cidrText);
            }
            if (cidr < 24 && !ConfirmLargeRange(cidr)) return;
            _vm.ManualSubnet = $"{ipPart}/{cidr}";
        }

        try
        {
            ApplySelectedPingCount();
            _vm.Config = _config;
            await _vm.RunScanAsync();
            if (raw.Length == 0 && _vm.Networks.Count > 0)
                SubnetBox.Text = _vm.Networks[0].Cidr.Split('/')[0];
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.ScanError, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Embedded settings overlay ──
    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        LoadSettings(_config);
        SettingsOverlay.Visibility = Visibility.Visible;
    }

    private void OnSettingsCancel(object sender, RoutedEventArgs e)
        => SettingsOverlay.Visibility = Visibility.Collapsed;

    // Clicking the dimmed background (not the panel) closes the overlay.
    private void OnOverlayBackgroundClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, SettingsOverlay))
            SettingsOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        // Reset removes the persisted config files and applies the defaults
        // without writing a new file.
        foreach (var p in new[] { ConfPathFor(_config.DatabasePath), ConfPathFor(new ScanConfig().DatabasePath) })
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best-effort */ }
        _config = new ScanConfig();
        _vm.Config = _config;
        LoadSettings(_config);
        ApplyUiScale();
        ApplyBarColors();
        ApplyDefaultPingCount();
    }

    /// <summary>Existing directory of a (possibly relative) path, or null.</summary>
    private static string? DirOf(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var full = Path.GetFullPath(path);
            var dir = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
            return dir is not null && Directory.Exists(dir) ? dir : null;
        }
        catch { return null; }
    }

    private void OnMergeDb(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Datenbank (*.db)|*.db|*.*|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            int n = new KnownDevicesDb(_config.DatabasePath).MergeFrom(dlg.FileName);
            MessageBox.Show(this, Loc.MergeDone(n), Loc.MergeDb, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.ScanError, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Config file lives next to the database.</summary>
    private static string ConfPathFor(string dbPath)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath)) ?? ".", "ip_scanner.conf");

    private static ScanConfig LoadPersistedConfig()
    {
        try
        {
            var def = ConfPathFor(new ScanConfig().DatabasePath);
            var cfg = File.Exists(def) ? ConfigManager.Load(def) : new ScanConfig();
            // The loaded config may point to a different db folder — its conf wins.
            var at = ConfPathFor(cfg.DatabasePath);
            if (!string.Equals(at, def, StringComparison.OrdinalIgnoreCase) && File.Exists(at))
                cfg = ConfigManager.Load(at);
            return cfg;
        }
        catch { return new ScanConfig(); }
    }

    private bool _loadingSettings;

    /// <summary>Instant save: every settings change applies and persists at once.</summary>
    private void ApplyInstant()
    {
        if (_loadingSettings) return;
        _config = ReadSettings();
        _vm.Config = _config;
        ApplyDefaultPingCount();
        SyncExportToggles();
        PersistConfig();
    }

    private void OnSettingChanged(object sender, RoutedEventArgs e) => ApplyInstant();

    // Text scale only commits when the field loses focus — applying it on every
    // keystroke would zoom wildly while typing.
    private void OnUiScaleCommit(object sender, RoutedEventArgs e) => ApplyUiScale();

    /// <summary>Preset the ping dropdown from the configured default.</summary>
    private void ApplyDefaultPingCount()
        => PingCountBox.Text = _config.PingCount == ScanConfig.InfinitePingCount
            ? "∞" : _config.PingCount.ToString();

    private void SyncExportToggles()
    {
        TxtToggle.IsChecked = _config.FileOutput;
        CsvToggle.IsChecked = _config.ExportCsv;
    }

    // Sidebar TXT/CSV pills drive the settings checkboxes (single source of truth).
    private void OnExportToggle(object sender, RoutedEventArgs e)
    {
        FileOutputBox.IsChecked = TxtToggle.IsChecked == true;
        ExportCsvBox.IsChecked = CsvToggle.IsChecked == true;
        ApplyInstant();
    }

    private static readonly string[] LanguageModes = { "auto", "de", "en" };

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || LanguageBox.SelectedIndex < 0) return;
        var mode = LanguageModes[LanguageBox.SelectedIndex];
        if (mode == _config.Language) return;
        _config.Language = mode;
        PersistConfig();
        // x:Static strings are cached — restart so the new language shows everywhere.
        if (Environment.ProcessPath is { } exe)
            System.Diagnostics.Process.Start(exe);
        Application.Current.Shutdown();
    }

    private void OnExportConf(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Config (*.conf)|*.conf",
            FileName = "ip_scanner.conf",
        };
        if (DirOf(_config.DatabasePath) is { } dir) dlg.InitialDirectory = dir;
        if (dlg.ShowDialog(this) != true) return;
        try { ConfigManager.Save(dlg.FileName, ReadSettings()); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Loc.ScanError, MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void OnImportConf(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Config (*.conf)|*.conf|*.*|*.*" };
        if (DirOf(_config.DatabasePath) is { } dir) dlg.InitialDirectory = dir;
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            LoadSettings(ConfigManager.Load(dlg.FileName));
            ApplyInstant();   // imported values take effect + persist immediately
            ApplyUiScale();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Loc.ScanError, MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void LoadSettings(ScanConfig c)
    {
        _loadingSettings = true;
        try
        {
        SubnetsBox.Text = string.Join(Environment.NewLine, c.Subnets);
        PinnedBox.Text = string.Join(Environment.NewLine, c.PinnedIps);
        ScanThreadsBox.Text = c.ScanThreads.ToString();
        UiScaleBox.Text = c.UiScalePercent.ToString();
        IntervalBox.Text = c.PingIntervalMs.ToString();
        OfflineAfterBox.Text = c.OfflineAfterFailedPings.ToString();
        InitPingCountBox.Text = c.InitPingCount.ToString();
        StartupPingsBox.Text = c.StartupPingCount.ToString();
        DefaultPingsBox.Text = c.PingCount.ToString();
        OfflineRecheckBox.Text = c.OfflineRecheckSeconds.ToString();
        EnableInternetBox.IsChecked = c.EnableInternetPing;
        InternetTimeoutBox.Text = c.InternetTimeoutMs.ToString();
        InternetHostsBox.Text = string.Join(Environment.NewLine, c.InternetHosts);
        OutputDirBox.Text = c.OutputDirectory;
        DbPathBox.Text = c.DatabasePath;
        FileOutputBox.IsChecked = c.FileOutput;
        ExportCsvBox.IsChecked = c.ExportCsv;
        KnownDbBox.IsChecked = c.KnownDevicesDb;
        LanguageBox.SelectedIndex = Math.Max(0, Array.IndexOf(LanguageModes, c.Language));
        TxtToggle.IsChecked = c.FileOutput;
        CsvToggle.IsChecked = c.ExportCsv;
        }
        finally { _loadingSettings = false; }
    }

    // Split on newlines, commas and semicolons so values can be comma-separated.
    private static List<string> Items(string t) => t
        .Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    // ── Live syntax validation for the network/IP lists ──
    private void OnSubnetsValidate(object sender, TextChangedEventArgs e)
    { ValidateList(SubnetsBox, SubnetsError, IsValidSubnetEntry); ApplyInstant(); }

    private void OnPinnedValidate(object sender, TextChangedEventArgs e)
    { ValidateList(PinnedBox, PinnedError, Core.Net.Ipv4.IsValid); ApplyInstant(); }

    private void OnHostsValidate(object sender, TextChangedEventArgs e)
    { ValidateList(InternetHostsBox, InternetHostsError, IsValidHostEntry); ApplyInstant(); }

    private void OnOutputDirValidate(object sender, TextChangedEventArgs e)
    { ValidatePath(OutputDirBox, OutputDirError); ApplyInstant(); }

    private void OnDbPathValidate(object sender, TextChangedEventArgs e)
    { ValidatePath(DbPathBox, DbPathError); ApplyInstant(); }

    /// <summary>"ip" or "ip name" — the first token must be a valid IPv4.</summary>
    private static bool IsValidHostEntry(string entry)
        => Core.Net.Ipv4.IsValid(entry.Split(' ', 2)[0].Trim());

    private static bool IsValidPathText(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
        try { Path.GetFullPath(path); return true; }
        catch { return false; }
    }

    private void ValidatePath(TextBox box, TextBlock error)
    {
        if (IsValidPathText(box.Text))
        {
            box.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
            error.Visibility = Visibility.Collapsed;
        }
        else
        {
            box.BorderBrush = (System.Windows.Media.Brush)FindResource("Red");
            error.Text = Loc.InvalidPath;
            error.Visibility = Visibility.Visible;
        }
    }

    /// <summary>"a.b.c.d" or "a.b.c.d/1..32".</summary>
    private static bool IsValidSubnetEntry(string entry)
    {
        var parts = entry.Split('/');
        if (parts.Length > 2 || !Core.Net.Ipv4.IsValid(parts[0].Trim())) return false;
        return parts.Length == 1 ||
               (int.TryParse(parts[1].Trim(), out var cidr) && cidr is >= 1 and <= 32);
    }

    private void ValidateList(TextBox box, TextBlock error, Func<string, bool> isValid)
    {
        var bad = Items(box.Text).FirstOrDefault(x => !isValid(x));
        if (bad is null)
        {
            box.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
            error.Visibility = Visibility.Collapsed;
        }
        else
        {
            box.BorderBrush = (System.Windows.Media.Brush)FindResource("Red");
            error.Text = Loc.InvalidEntry(bad);
            error.Visibility = Visibility.Visible;
        }
    }

    private ScanConfig ReadSettings()
    {
        int I(string s, int d) => int.TryParse(s.Trim(), out var v) ? v : d;

        int defaultPings = I(DefaultPingsBox.Text, 10);
        return new ScanConfig
        {
            Subnets = Items(SubnetsBox.Text),
            PinnedIps = Items(PinnedBox.Text),
            PingCount = defaultPings is ScanConfig.InfinitePingCount or > 0 ? defaultPings : 10,
            ScanThreads = I(ScanThreadsBox.Text, 50),
            UiScalePercent = Math.Clamp(I(UiScaleBox.Text, 100), 50, 200),
            PingIntervalMs = I(IntervalBox.Text, 100),
            OfflineAfterFailedPings = I(OfflineAfterBox.Text, 5),
            InitPingCount = I(InitPingCountBox.Text, 1),
            StartupPingCount = Math.Clamp(I(StartupPingsBox.Text, 5), 0, 10_000),
            OfflineRecheckSeconds = Math.Clamp(I(OfflineRecheckBox.Text, 2), 0, 3600),
            EnableInternetPing = EnableInternetBox.IsChecked == true,
            InternetTimeoutMs = Math.Clamp(I(InternetTimeoutBox.Text, 1000), 100, 10_000),
            InternetHosts = Items(InternetHostsBox.Text),
            OutputDirectory = OutputDirBox.Text.Trim(),
            DatabasePath = DbPathBox.Text.Trim().Length > 0 ? DbPathBox.Text.Trim() : "./Scans/scanner.db",
            FileOutput = FileOutputBox.IsChecked == true,
            ExportCsv = ExportCsvBox.IsChecked == true,
            KnownDevicesDb = KnownDbBox.IsChecked == true,
            Language = LanguageModes[Math.Max(0, LanguageBox.SelectedIndex)],
            // Colors have no settings UI — carry them over from the live config.
            ColorOnline = _config.ColorOnline, ColorOffline = _config.ColorOffline,
            ColorUnknown = _config.ColorUnknown, ColorSuccess = _config.ColorSuccess,
            ColorFailed = _config.ColorFailed, ColorSkipped = _config.ColorSkipped,
        };
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.OutputDir };
        if (DirOf(OutputDirBox.Text) is { } dir) dlg.InitialDirectory = dir;
        if (dlg.ShowDialog(this) == true) OutputDirBox.Text = dlg.FolderName;
    }

    private void OnClearDb(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(Loc.ClearDbConfirm, Loc.Confirm, MessageBoxButton.YesNo) == MessageBoxResult.Yes)
        {
            try { new KnownDevicesDb(_config.DatabasePath).Clear(); } catch { /* ignore */ }
        }
    }

    private void OnBrowseDb(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.DbFile,
            Filter = "Datenbank (*.db)|*.db|*.*|*.*",
            FileName = "scanner.db",
            OverwritePrompt = false,    // existing db is opened, not replaced
        };
        if (DirOf(DbPathBox.Text) is { } dir) dlg.InitialDirectory = dir;
        if (dlg.ShowDialog(this) == true) DbPathBox.Text = dlg.FileName;
    }

    // Extract a CIDR number from the dropdown text ("/24", "24", "/16" ...).
    private static int ParseCidr(string? text)
    {
        var digits = new string((text ?? "").Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) && n is >= 1 and <= 32 ? n : 24;
    }

    // Warn before scanning a large range (/16 = 256 subnets, /8 = 65536).
    private bool ConfirmLargeRange(int cidr)
    {
        int subnets = cidr >= 16 ? 256 : 65536;
        var r = MessageBox.Show(this, Loc.LargeRangeMsg(cidr, subnets, (long)subnets * 254),
            Loc.LargeRange, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        return r == MessageBoxResult.Yes;
    }

    private void ApplySelectedPingCount()
    {
        // Editable dropdown: free text wins, fall back to the selected item.
        var text = string.IsNullOrWhiteSpace(PingCountBox.Text)
            ? PingCountBox.SelectedItem?.ToString()
            : PingCountBox.Text.Trim();
        _config.PingCount = text == "∞" || text == "-1" ? ScanConfig.InfinitePingCount
            : int.TryParse(text, out var n) && n > 0 ? n : 10;
    }
}
