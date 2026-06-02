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

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow(_config) { Owner = this };
        if (win.ShowDialog() == true)
        {
            _config = win.Result;
            ConfigManager.Save(ConfigPath, _config);
            _vm.Config = _config;
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
