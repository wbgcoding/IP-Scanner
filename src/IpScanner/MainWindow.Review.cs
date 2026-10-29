#if DEBUG
using System.Windows;
using System.Windows.Controls;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;
using IpScanner.ViewModels;
using IpScanner.ViewModels.Tools;
using IpScanner.Views;

namespace IpScanner;

/// <summary>Hooks the surface tool uses to put the real main window into a known state. Compiled
/// out of Release builds.</summary>
public partial class MainWindow
{
    internal static void ReviewSetSealedHome(string dir)
    {
        lock (SealedGate) _sealedHome = dir;
    }

    internal FrameworkElement? ReviewElement(string name) => FindName(name) as FrameworkElement;

    internal IEnumerable<string> ReviewSettingsPages => SettingsPages;

    /// <summary>Fills the window with in-memory sample data: 40 devices, three networks, pinned IPs and
    /// internet hosts. Nothing is read from or written to the network, the database or the config.</summary>
    internal void ReviewLoadSample()
    {
        var rnd = new Random(7);
        string[] names =
        {
            "fritz.box", "nas-01", "printer-hp", "laptop-anna", "tv-livingroom", "switch-core", "camera-door",
            "pi-hole", "desktop-max", "phone-anna", "tablet-kids", "ap-upstairs", "printer-brother", "xbox",
            "thermostat", "sonos-kitchen", "server-lab", "nuc-media", "plug-office", "doorbell",
        };
        string[] macPrefix =
        {
            "B8:27:EB", "00:11:32", "3C:2A:F4", "F0:18:98", "A4:77:33", "00:0C:29", "DC:A6:32", "44:D9:E7",
        };
        for (int i = 0; i < 40; i++)
        {
            var ip = i == 0 ? "192.168.10.1" : $"192.168.10.{10 + i * 5}";
            var d = new Device(ip)
            {
                Mac = $"{macPrefix[i % macPrefix.Length]}:{rnd.Next(256):X2}:{rnd.Next(256):X2}:{rnd.Next(256):X2}",
                Hostname = i < names.Length ? names[i] : null,
                GroupId = i == 0 ? 2 : 3 + i % 4,
                TargetPings = 10,
                FromDb = i % 3 != 0,
            };
            bool online = i % 7 != 4;
            for (int k = 0; k < 10; k++)
            {
                if (online || k < 3) d.RecordPing(new PingResult(true, 1 + rnd.NextDouble() * (i == 8 ? 90 : 12), 64));
                else d.RecordPing(new PingResult(false, null, null));
            }
            if (!online) for (int k = 0; k < 6; k++) d.RecordPing(new PingResult(false, null, null));
            _vm.Devices.Add(new DeviceViewModel(d, isSelf: i == 8, isPinned: i == 1 || i == 2));
        }
        _vm.Progress.SetDevices(_vm.Devices.Count(x => x.IsOnline), _vm.Devices.Count(x => !x.IsOnline), _vm.Devices.Count);
        _vm.Progress.SetPings(380, 21, 0, 400);

        var primary = new NetworkInfo
        {
            Ip = "192.168.10.50", Mac = "DC:A6:32:11:22:33", Gateway = "192.168.10.1", SubnetMask = "255.255.255.0",
            DnsServers = new() { "192.168.10.1", "9.9.9.9" }, Interface = "Ethernet",
            Ipv6 = new()
            {
                new OwnIpv6Address("Ethernet", 12, Core.Net.Ipv6Address.Parse("2a02:8071:1234:5600:2d1:aff:fe42:7f3a"), 64),
                new OwnIpv6Address("Ethernet", 12, Core.Net.Ipv6Address.Parse("fd12:3456:789a:1::50"), 64),
                new OwnIpv6Address("Ethernet", 12, Core.Net.Ipv6Address.Parse("fe80::2d1:aff:fe42:7f3a").WithZone("12"), 64),
            },
        };
        _vm.Networks.Add(new NetworkInfoViewModel(1, primary, Core.Scanner.GroupColorPalette.ColorForIndex(0)));
        _vm.Networks.Add(new NetworkInfoViewModel(2,
            new NetworkInfo { Ip = "192.168.20.5", Gateway = "192.168.20.1", SubnetMask = "255.255.255.0", DnsServers = new() { "192.168.20.1" }, Interface = "Ethernet 2" },
            Core.Scanner.GroupColorPalette.ColorForIndex(1), primary: false, name: "Lab", sourceTarget: "192.168.20.0/24 Lab",
            prefix: "192.168.20"));
        _vm.Networks.Add(new NetworkInfoViewModel(3, new NetworkInfo { Ip = "10.0.5.0" },
            Core.Scanner.GroupColorPalette.ColorForIndex(2), primary: false, name: "Guest", sourceTarget: "10.0.5.0/24 Guest",
            pending: true, prefix: "10.0.5"));

        foreach (var (name, ip) in new[] { ("Google 1", "8.8.8.8"), ("Cloudflare", "1.1.1.1"), ("Quad9", "9.9.9.9") })
        {
            var host = new InternetHostViewModel(name, ip);
            for (int k = 0; k < 8; k++) host.RecordPing(8 + rnd.NextDouble() * 20);
            _vm.InternetHosts.Add(host);
        }
        _vm.RebuildPinnedEntries();
        _devicesView.Refresh();
    }

    internal void ReviewOpenSettings(string page)
    {
        OnSettingsClick(this, new RoutedEventArgs());
        SettingsNavList.SelectedIndex = Math.Max(0, Array.IndexOf(SettingsPages, page));
    }

    internal void ReviewScrollToDriver()
    {
        UpdateLayout();
        DriverHelpPanel.BringIntoView();
    }

    /// <summary>Scan page scrolled to the preset section; <paramref name="detail"/> adds a saved preset
    /// with a port list and opens the name field with its error line.</summary>
    internal void ReviewShowPresets(bool detail, bool lower = false, bool hot = false)
    {
        ReviewOpenSettings("Scan");
        if (hot) ReviewApplyAggressive();
        if (detail)
        {
            if (Core.Cli.ScanPresets.TryParseFlags("-T4 -p 22,80,443 -sV", out var plan, out _))
            {
                plan.ApplyTo(_config);
                _config.ScanPresets.Add(Core.Cli.ScanPresets.ToEntry("Office scan", plan));
                LoadSettings(_config);
            }
            PresetNamePanel.Visibility = Visibility.Visible;
            PresetNameBox.Text = "Office scan";
            PresetNameError.Text = Core.Cli.ScanPresets.ValidateName("Office scan", ["Office scan"]) ?? "";
            PresetNameError.Visibility = Visibility.Visible;
        }
        UpdateLayout();
        if (lower) { PresetCommandBox.BringIntoView(); return; }
        for (DependencyObject? p = ScanPresetBox; p is not null; p = System.Windows.Media.VisualTreeHelper.GetParent(p))
        {
            if (p is not ScrollViewer sv) continue;
            sv.ScrollToVerticalOffset(sv.VerticalOffset + ScanPresetBox.TranslatePoint(new Point(0, 0), sv).Y - 60);
            break;
        }
    }

    internal void ReviewScrollToDiscovery()
    {
        UpdateLayout();
        TcpPingPortsBox.BringIntoView();
        UdpPingPortsBox.BringIntoView();
        OsFingerprintBox.BringIntoView();
    }

    /// <summary>Scan page with the advanced network section expanded and scrolled to its last rows.</summary>
    internal void ReviewOpenSettingsAdvanced(bool hot = false)
    {
        ReviewOpenSettings("Scan");
        if (hot) ReviewApplyAggressive();
        if (!_advancedNetworkExpanded) OnToggleAdvancedNetwork(this, new RoutedEventArgs());
        UpdateLayout();
        if (hot) PortScanTimingSlider.BringIntoView(); else CustomPortsBox.BringIntoView();
    }

    /// <summary>Loads the aggressive options (T5, all ports) so every warning mark of the settings shows.</summary>
    private void ReviewApplyAggressive()
    {
        if (!Core.Cli.ScanPresets.TryParseFlags("-T5 -p-", out var plan, out _)) return;
        plan.ApplyTo(_config);
        LoadSettings(_config);
    }

    internal void ReviewShowFirstRun() => ShowFirstRunCard();

    /// <summary>Adds IPv6 hosts (a global address, a long unique-local one and a link-local one with a zone) to the
    /// table so the screenshot shows the monospace column, the trimming and the numeric order next to IPv4 rows.</summary>
    internal void ReviewShowIpv6Rows()
    {
        var rnd = new Random(11);
        foreach (var (ip, name) in new[]
        {
            ("2001:db8::10", "nas-v6"), ("fd12:3456:789a:1:2b3c:4d5e:6f70:8192", "lab-printer"),
            ("fe80::1c2d:3e4f:5a6b:7c8d%12", "switch-core"), ("2001:db8:0:0:0:0:0:2", null),
        })
        {
            var d = new Device(ip) { Mac = "DC:A6:32:11:22:" + rnd.Next(256).ToString("X2"), Hostname = name, TargetPings = 10 };
            for (int k = 0; k < 10; k++) d.RecordPing(new PingResult(true, 1 + rnd.NextDouble() * 6, 64));
            _vm.Devices.Add(new DeviceViewModel(d, isSelf: false, isPinned: false));
        }
        _devicesView.Refresh();
        UpdateLayout();
        if (DeviceGrid.Items.Count > 0) DeviceGrid.ScrollIntoView(DeviceGrid.Items[DeviceGrid.Items.Count - 1]);
        UpdateLayout();
        IpColumn.Width = DataGridLength.SizeToCells;
        UpdateLayout();
    }

    internal void ReviewShowOsColumn()
    {
        OsColumn.Visibility = Visibility.Visible;
        UpdateLayout();
    }

    /// <summary>Pins the packet reader state (null = real detection) and rebuilds the notice and the matrix.</summary>
    internal void ReviewSetDriver(Core.Scanner.PacketDriverStatus? status, bool elevated = false)
    {
        Core.Scanner.PacketDriver.OverrideForTests(status, elevated);
        _driverBarDismissed = false;
        RefreshDriverBar();
    }

    /// <summary>Expands the first row's detail with a fixed service list (a few common ports, or a long
    /// sweep result) and a short note, without touching the network.</summary>
    internal void ReviewShowDeviceDetail(bool manyPorts, string note)
    {
        if (ReviewElement("DeviceGrid") is not DataGrid grid || grid.Items.Count == 0
            || grid.Items[0] is not DeviceViewModel dvm) return;
        var services = new List<Core.Scanner.NetService>();
        if (manyPorts)
        {
            string[] names = { "HTTP", "SSH", "SMB", "RDP", "MQTT", "Telnet", "FTP", "DNS", "IPP", "VNC" };
            for (int i = 0; i < 60; i++)
                services.Add(new Core.Scanner.NetService(i < names.Length ? names[i] : "Unknown", 1000 + i * 37, Core.Scanner.NetProtocol.Tcp));
        }
        else
        {
            services.Add(new Core.Scanner.NetService("HTTP", 80, Core.Scanner.NetProtocol.Tcp));
            services.Add(new Core.Scanner.NetService("HTTPS", 443, Core.Scanner.NetProtocol.Tcp));
            services.Add(new Core.Scanner.NetService("SSH", 22, Core.Scanner.NetProtocol.Tcp));
            services.Add(new Core.Scanner.NetService("SMB", 445, Core.Scanner.NetProtocol.Tcp));
            services.Add(new Core.Scanner.NetService("DNS", 53, Core.Scanner.NetProtocol.Udp));
        }
        dvm.ReviewOpenDetail(services, note);
        grid.UpdateLayout();
    }

    internal void ReviewShowUndo()
        => ShowNotice(Loc.EntryRemoved("Lab"), Loc.UndoLabel, null, sticky: true);

    /// <summary>Opens the log page over a few sample rows so the screenshot shows both levels and an expanded row.</summary>
    internal Task ReviewOpenLogAsync()
    {
        Core.Log.ClearBuffer();
        Core.Log.Info(Core.LogArea.App, "Session started");
        Core.Log.Warn(Core.LogArea.Db, "Scan archive write failed",
            new InvalidOperationException("SQLite Error 5: 'database is locked'."));
        Core.Log.Error(Core.LogArea.Scan, "Starting the scan failed",
            new InvalidOperationException("The subnet could not be read."));
        Core.Log.Warn(Core.LogArea.Ui, "Opening the browser failed");
        OnLogClick(this, new RoutedEventArgs());
        return Task.CompletedTask;
    }

    internal Task ReviewOpenArchiveAsync()
    {
        OnArchiveClick(this, new RoutedEventArgs());
        return Task.CompletedTask;
    }

    internal Task ReviewOpenArchiveCompareAsync() => ArchiveOverlay.ReviewShowCompareAsync();

    internal Task ReviewSelectArchiveRunAsync(int run, int device) => ArchiveOverlay.ReviewSelectAsync(run, device);

    internal ToolsWindow ReviewOpenTools(string page, Action<ToolsViewModel>? setup = null)
    {
        _config.ToolsLastPage = page;
        var win = new ToolsWindow(this);
        win.Show();
        setup?.Invoke(win.ReviewVm);
        return win;
    }

    internal void ReviewSetProfiles(params string[] names)
    {
        _config.ProfileNames = names.ToList();
        SetProfileItems();
    }
}
#endif
