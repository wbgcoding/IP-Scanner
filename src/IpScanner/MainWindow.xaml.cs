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
        InitializeComponent();
        WindowTheme.ApplyDark(this);

        // Settings live in memory only; .conf is written/read solely via the
        // explicit export/import buttons in the settings panel.
        _config = new ScanConfig();
        _vm = new MainViewModel(
            pingFunc: IcmpPinger.Ping,
            detectNetwork: NetworkDetector.DetectFast,
            dispatch: a => Dispatcher.BeginInvoke(a),
            enrich: Enrich)
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

        // On startup, immediately run a discovery sweep of the local network so
        // the sidebar network info and online devices show up without a manual scan.
        Loaded += async (_, _) =>
        {
            try { await _vm.RunInitScanAsync(); } catch { /* best-effort */ }
        };
    }

    private readonly System.Windows.Media.RotateTransform _logoSpin = new();

    private void UpdateScanButton()
    {
        bool scanning = _vm.IsScanning;
        ScanStopButton.Content = scanning ? Loc.Stop : Loc.Scan;
        ScanStopButton.Style = (Style)FindResource(scanning ? "DangerButton" : "AccentButton");

        // Logo spins while a scan is running — faster with more threads.
        LogoImage.RenderTransform = _logoSpin;
        if (scanning)
        {
            int threads = _config.ScanThreads <= 0 ? 254 : _config.ScanThreads;
            double seconds = Math.Clamp(120.0 / threads, 0.6, 6.0);
            var spin = new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(seconds))
            { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever };
            _logoSpin.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, spin);
        }
        else
        {
            _logoSpin.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
            _logoSpin.Angle = 0;
        }
    }

    /// <summary>Scale the whole UI (text included) by the configured percent.</summary>
    private void ApplyUiScale()
    {
        double f = Math.Clamp(_config.UiScalePercent, 50, 200) / 100.0;
        RootLayout.LayoutTransform = f == 1.0 ? null
            : new System.Windows.Media.ScaleTransform(f, f);
    }

    // Resolve MAC + hostname for an online device: ARP + reverse DNS, with a
    // NetBIOS fallback when DNS/ARP miss (works across subnets).
    private static (string? mac, string? host) Enrich(string ip)
    {
        var host = HostnameResolver.Resolve(ip);
        var mac = ArpHelper.Resolve(ip);
        if (host is null || mac is null)
        {
            var (nbName, nbMac) = NetBiosHelper.Lookup(ip);
            host ??= nbName;
            mac ??= nbMac;
        }
        return (mac, host);
    }

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
        => LoadSettings(new ScanConfig());

    private void OnMergeDb(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Datenbank (*.db)|*.db|*.*|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            int n = new KnownDevicesDb(MainViewModel.DbPath).MergeFrom(dlg.FileName);
            MessageBox.Show(this, Loc.MergeDone(n), Loc.MergeDb, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.ScanError, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnSettingsSave(object sender, RoutedEventArgs e)
    {
        _config = ReadSettings();
        _vm.Config = _config;
        ApplyUiScale();
        SettingsOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnExportConf(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Config (*.conf)|*.conf",
            FileName = "ip_scanner.conf",
        };
        if (dlg.ShowDialog(this) != true) return;
        try { ConfigManager.Save(dlg.FileName, ReadSettings()); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Loc.ScanError, MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void OnImportConf(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Config (*.conf)|*.conf|*.*|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        try { LoadSettings(ConfigManager.Load(dlg.FileName)); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Loc.ScanError, MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void LoadSettings(ScanConfig c)
    {
        SubnetsBox.Text = string.Join(Environment.NewLine, c.Subnets);
        PinnedBox.Text = string.Join(Environment.NewLine, c.PinnedIps);
        ScanThreadsBox.Text = c.ScanThreads.ToString();
        UiScaleBox.Text = c.UiScalePercent.ToString();
        IntervalBox.Text = c.PingIntervalMs.ToString();
        OfflineAfterBox.Text = c.OfflineAfterFailedPings.ToString();
        InitPingCountBox.Text = c.InitPingCount.ToString();
        OfflineRecheckBox.Text = c.OfflineRecheckSeconds.ToString();
        EnableInternetBox.IsChecked = c.EnableInternetPing;
        InternetHostsBox.Text = string.Join(Environment.NewLine, c.InternetHosts);
        OutputDirBox.Text = c.OutputDirectory;
        FileOutputBox.IsChecked = c.FileOutput;
        ExportCsvBox.IsChecked = c.ExportCsv;
        KnownDbBox.IsChecked = c.KnownDevicesDb;
    }

    private ScanConfig ReadSettings()
    {
        // Split on newlines, commas and semicolons so values can be comma-separated.
        static List<string> Items(string t) => t
            .Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        int I(string s, int d) => int.TryParse(s.Trim(), out var v) ? v : d;

        return new ScanConfig
        {
            Subnets = Items(SubnetsBox.Text),
            PinnedIps = Items(PinnedBox.Text),
            PingCount = _config.PingCount,        // chosen in the main header
            ScanThreads = I(ScanThreadsBox.Text, 50),
            UiScalePercent = Math.Clamp(I(UiScaleBox.Text, 100), 50, 200),
            PingIntervalMs = I(IntervalBox.Text, 100),
            OfflineAfterFailedPings = I(OfflineAfterBox.Text, 5),
            InitPingCount = I(InitPingCountBox.Text, 1),
            OfflineRecheckSeconds = Math.Clamp(I(OfflineRecheckBox.Text, 2), 0, 3600),
            EnableInternetPing = EnableInternetBox.IsChecked == true,
            InternetHosts = Items(InternetHostsBox.Text),
            OutputDirectory = OutputDirBox.Text.Trim(),
            FileOutput = FileOutputBox.IsChecked == true,
            ExportCsv = ExportCsvBox.IsChecked == true,
            KnownDevicesDb = KnownDbBox.IsChecked == true,
        };
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.OutputDir };
        if (dlg.ShowDialog(this) == true) OutputDirBox.Text = dlg.FolderName;
    }

    private void OnClearDb(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(Loc.ClearDbConfirm, Loc.Confirm, MessageBoxButton.YesNo) == MessageBoxResult.Yes)
        {
            try { new KnownDevicesDb(MainViewModel.DbPath).Clear(); } catch { /* ignore */ }
        }
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
        var text = (PingCountBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
        _config.PingCount = text == "∞" ? ScanConfig.InfinitePingCount
            : int.TryParse(text, out var n) ? n : 10;
    }
}
