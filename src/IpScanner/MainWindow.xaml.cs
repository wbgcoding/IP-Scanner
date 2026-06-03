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
    private const string ConfigPath = "ip_scanner.conf";

    public MainWindow()
    {
        InitializeComponent();
        WindowTheme.ApplyDark(this);

        // Seed a default config file on first run (acts as the template).
        if (!File.Exists(ConfigPath))
            ConfigManager.Save(ConfigPath, new ScanConfig());

        _config = ConfigManager.Load(ConfigPath);
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

        // On startup, immediately run a discovery sweep of the local network so
        // the sidebar network info and online devices show up without a manual scan.
        Loaded += async (_, _) =>
        {
            try { await _vm.RunInitScanAsync(); } catch { /* best-effort */ }
        };
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

    private async void OnScanClick(object sender, RoutedEventArgs e) => await StartScan();

    private async void OnSubnetKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) await StartScan();
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
                cidr = ParseCidr((CidrBox.SelectedItem as ComboBoxItem)?.Content?.ToString());
            }
            if (cidr < 24 && !ConfirmLargeRange(cidr)) return;
            _vm.ManualSubnet = $"{ipPart}/{cidr}";
        }

        ScanButton.IsEnabled = false;
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
        finally { ScanButton.IsEnabled = true; }
    }

    private void OnStopClick(object sender, RoutedEventArgs e) => _vm.Stop();

    // ── Embedded settings overlay ──
    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        LoadSettings(_config);
        SettingsOverlay.Visibility = Visibility.Visible;
    }

    private void OnSettingsCancel(object sender, RoutedEventArgs e)
        => SettingsOverlay.Visibility = Visibility.Collapsed;

    private void OnSettingsSave(object sender, RoutedEventArgs e)
    {
        _config = ReadSettings();
        ConfigManager.Save(ConfigPath, _config);
        _vm.Config = _config;
        SettingsOverlay.Visibility = Visibility.Collapsed;
    }

    private void LoadSettings(ScanConfig c)
    {
        SubnetsBox.Text = string.Join(Environment.NewLine, c.Subnets);
        PinnedBox.Text = string.Join(Environment.NewLine, c.PinnedIps);
        SetPingCountBox.Text = c.PingCount.ToString();
        IntervalBox.Text = c.PingIntervalMs.ToString();
        OfflineAfterBox.Text = c.OfflineAfterFailedPings.ToString();
        InitPingCountBox.Text = c.InitPingCount.ToString();
        HighPressureBox.IsChecked = c.HighPressureMode;
        EnableInternetBox.IsChecked = c.EnableInternetPing;
        InternetHostsBox.Text = string.Join(Environment.NewLine, c.InternetHosts);
        OutputDirBox.Text = c.OutputDirectory;
        FileOutputBox.IsChecked = c.FileOutput;
        ExportCsvBox.IsChecked = c.ExportCsv;
        KnownDbBox.IsChecked = c.KnownDevicesDb;
        PingThreadsBox.Text = c.PingThreads.ToString();
        InitThreadsBox.Text = c.InitPingThreads.ToString();
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
            PingCount = I(SetPingCountBox.Text, 10),
            PingIntervalMs = I(IntervalBox.Text, 100),
            OfflineAfterFailedPings = I(OfflineAfterBox.Text, 5),
            InitPingCount = I(InitPingCountBox.Text, 1),
            HighPressureMode = HighPressureBox.IsChecked == true,
            EnableInternetPing = EnableInternetBox.IsChecked == true,
            InternetHosts = Items(InternetHostsBox.Text),
            OutputDirectory = OutputDirBox.Text.Trim(),
            FileOutput = FileOutputBox.IsChecked == true,
            ExportCsv = ExportCsvBox.IsChecked == true,
            KnownDevicesDb = KnownDbBox.IsChecked == true,
            PingThreads = I(PingThreadsBox.Text, 100),
            InitPingThreads = I(InitThreadsBox.Text, 254),
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
            try { new KnownDevicesDb("scanner.db").Clear(); } catch { /* ignore */ }
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
