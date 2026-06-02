using System.Globalization;
using System.Windows;
using IpScanner.Core.Data;
using IpScanner.Core.Models;

namespace IpScanner.Views;

public partial class SettingsWindow : Window
{
    public ScanConfig Result { get; private set; }

    public SettingsWindow(ScanConfig cfg)
    {
        InitializeComponent();
        Result = cfg;
        Load(cfg);
    }

    private void Load(ScanConfig c)
    {
        SubnetsBox.Text = string.Join(Environment.NewLine, c.Subnets);
        PinnedBox.Text = string.Join(Environment.NewLine, c.PinnedIps);
        PingCountBox.Text = c.PingCount.ToString();
        IntervalBox.Text = c.PingIntervalMs.ToString();
        OfflineAfterBox.Text = c.OfflineAfterFailedPings.ToString();
        InitPingCountBox.Text = c.InitPingCount.ToString();
        HighPressureBox.IsChecked = c.HighPressureMode;
        EnableInternetBox.IsChecked = c.EnableInternetPing;
        InternetHostsBox.Text = string.Join(Environment.NewLine, c.InternetHosts);
        OutputDirBox.Text = c.OutputDirectory;
        FileOutputBox.IsChecked = c.FileOutput;
        ExportCsvBox.IsChecked = c.ExportCsv;
        PingThreadsBox.Text = c.PingThreads.ToString();
        InitThreadsBox.Text = c.InitPingThreads.ToString();
        RefreshRateBox.Text = c.RefreshRate.ToString(CultureInfo.InvariantCulture);
        KnownDbBox.IsChecked = c.KnownDevicesDb;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        static List<string> Lines(string t) => t
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        int I(string s, int d) => int.TryParse(s.Trim(), out var v) ? v : d;
        double D(string s, double d) => double.TryParse(s.Trim().Replace(',', '.'),
            CultureInfo.InvariantCulture, out var v) ? v : d;

        Result = new ScanConfig
        {
            Subnets = Lines(SubnetsBox.Text),
            PinnedIps = Lines(PinnedBox.Text),
            PingCount = I(PingCountBox.Text, 10),
            PingIntervalMs = I(IntervalBox.Text, 100),
            OfflineAfterFailedPings = I(OfflineAfterBox.Text, 5),
            InitPingCount = I(InitPingCountBox.Text, 1),
            HighPressureMode = HighPressureBox.IsChecked == true,
            EnableInternetPing = EnableInternetBox.IsChecked == true,
            InternetHosts = Lines(InternetHostsBox.Text),
            OutputDirectory = OutputDirBox.Text.Trim(),
            FileOutput = FileOutputBox.IsChecked == true,
            ExportCsv = ExportCsvBox.IsChecked == true,
            PingThreads = I(PingThreadsBox.Text, 100),
            InitPingThreads = I(InitThreadsBox.Text, 254),
            RefreshRate = D(RefreshRateBox.Text, 1.0),
            KnownDevicesDb = KnownDbBox.IsChecked == true,
        };
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnClearDb(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Known-Devices-Datenbank wirklich leeren?", "Bestätigen",
                MessageBoxButton.YesNo) == MessageBoxResult.Yes)
        {
            try { new KnownDevicesDb("scanner.db").Clear(); } catch { /* ignore */ }
        }
    }
}
