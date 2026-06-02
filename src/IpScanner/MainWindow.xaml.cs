using System.IO;
using System.Windows;
using System.Windows.Controls;
using IpScanner.Core.Data;
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
            if (ni.Cidr is not null) SubnetBox.Text = ni.Cidr;
        }
        catch { /* detection best-effort */ }
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

    private async void OnApplySubnet(object sender, RoutedEventArgs e) => await StartScan();

    private async Task StartScan()
    {
        ScanButton.IsEnabled = false;
        try
        {
            ApplySelectedPingCount();
            _vm.ManualSubnet = string.IsNullOrWhiteSpace(SubnetBox.Text) ? null : SubnetBox.Text.Trim();
            _vm.Config = _config;
            await _vm.RunScanAsync();
            if (_vm.LastExportPath is not null) ExportPathText.Text = _vm.LastExportPath;
            // Prefill the field with the detected subnet so it's visible/editable.
            if (string.IsNullOrWhiteSpace(SubnetBox.Text) && _vm.Networks.Count > 0)
                SubnetBox.Text = _vm.Networks[0].Cidr;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Scan-Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
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

    private void ApplySelectedPingCount()
    {
        var text = (PingCountBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
        _config.PingCount = text == "∞" ? ScanConfig.InfinitePingCount
            : int.TryParse(text, out var n) ? n : 10;
    }
}
