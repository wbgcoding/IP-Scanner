#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using IpScanner.Core.Data;
using IpScanner.Core.Export;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using IpScanner.ViewModels.Tools;

namespace IpScanner.Views.Review;

internal sealed record ReviewContext(string Locale, string Theme, int Width, int Height, int Scale, string TempDir, bool OnScreen);

/// <summary>A surface that is open and ready to be captured.</summary>
internal sealed class Shown
{
    public required Window Primary { get; init; }
    /// <summary>True when the window was built but never shown (a dialog whose own code would act on show).</summary>
    public bool Detached { get; init; }
    public List<Window> Others { get; } = new();
    public FrameworkElement? CropTo { get; set; }
    /// <summary>Resolves the root element of an open popup or menu once it has been opened.</summary>
    public Func<FrameworkElement?>? Popup { get; set; }
}

internal sealed record Surface(string Id, string Group, bool Resizable, Func<ReviewContext, Task<Shown>> Open);

internal static class ReviewSurfaces
{
    internal static readonly string[] SettingsPages = { "Scan", "Display", "Internet", "Graphs", "Data", "App", "About", "Help" };
    internal static readonly string[] ToolPages = { "WakeOnLan", "PortCheck", "DnsLookup", "PingRoute", "Calculator", "MacTool", "Certificate", "HostDiscovery", "ServiceDetection", "OsEstimate", "Checks", "ScanExport" };

    internal static ScanConfig SampleConfig(ReviewContext c) => new()
    {
        Language = c.Locale,
        Theme = c.Theme,
        UiScalePercent = c.Scale,
        DatabasePath = Path.Combine(c.TempDir, "scanner.db"),
        OutputDirectory = Path.Combine(c.TempDir, "Scans"),
        ConfigDirectory = c.TempDir,
        Subnets = new() { "192.168.20.0/24 Lab", "10.0.5.0/24 Guest" },
        PinnedIps = new() { "192.168.10.5 NAS", "192.168.10.15 Printer" },
        CheckForUpdates = false,
    };

    private static async Task Settle(int ms = 160)
    {
        await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        await Task.Delay(ms);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    internal static async Task SettleAsync(int ms = 160) => await Settle(ms);

    // Windows caps a window at the size of the screen; the tool needs sizes above it (the default
    // window on a scaled display), so the cap is lifted for every window it opens.
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MinMaxInfo { public Point Reserved, MaxSize, MaxPosition, MinTrack, MaxTrack; }

    private static IntPtr LiftSizeCap(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != 0x0024) return IntPtr.Zero;
        var mmi = System.Runtime.InteropServices.Marshal.PtrToStructure<MinMaxInfo>(lParam);
        mmi.MaxTrack = new Point { X = 8000, Y = 6000 };
        mmi.MaxSize = new Point { X = 8000, Y = 6000 };
        System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, false);
        return IntPtr.Zero;
    }

    private static void Place(Window w, ReviewContext c, bool resizable)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(w).EnsureHandle();
        System.Windows.Interop.HwndSource.FromHwnd(handle)?.AddHook(LiftSizeCap);
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = c.OnScreen ? 0 : -20000;
        w.Top = c.OnScreen ? 0 : 0;
        w.ShowActivated = false;
        w.ShowInTaskbar = false;
        if (resizable)
        {
            w.Width = c.Width;
            w.Height = c.Height;
        }
    }

    internal static async Task<MainWindow> NewMainAsync(ReviewContext c, bool sample = true, params string[] profiles)
    {
        MainWindow.ReviewSetSealedHome(c.TempDir);
        var cfg = SampleConfig(c);
        if (profiles.Length > 0) cfg.ProfileNames = profiles.ToList();
        App.ReviewSetBootConfig(cfg);
        var w = new MainWindow();
        Place(w, c, resizable: true);
        w.Show();
        if (sample) w.ReviewLoadSample();
        await Settle();
        return w;
    }

    private static async Task<Shown> MainState(ReviewContext c, Action<MainWindow> setup, bool sample = true,
        string[]? profiles = null, string? cropName = null)
    {
        var w = await NewMainAsync(c, sample, profiles ?? Array.Empty<string>());
        setup(w);
        await Settle();
        var shown = new Shown { Primary = w };
        if (cropName is not null) shown.CropTo = w.ReviewElement(cropName);
        return shown;
    }

    private static async Task<Shown> Dialog(ReviewContext c, Func<Window> create, bool detached = false)
    {
        MainWindow.ReviewSetSealedHome(c.TempDir);
        App.ReviewSetBootConfig(SampleConfig(c));
        var w = create();
        if (!detached)
        {
            Place(w, c, resizable: false);
            w.Show();
            await Settle();
        }
        return new Shown { Primary = w, Detached = detached };
    }

    /// <summary>Two fabricated runs that cover every comparison change type.</summary>
    private static void SeedCompareArchive(string dbPath)
    {
        static Device Host(string ip, string mac, string name, double ms)
        {
            var d = new Device(ip) { Mac = mac, Hostname = name };
            d.RecordPing(new PingResult(true, ms, 64));
            return d;
        }
        var db = new KnownDevicesDb(dbPath);
        if (db.ListScanArchive().Count > 0) return;
        var t = DateTimeOffset.Now.AddDays(-1);
        var portsA = new Dictionary<string, IReadOnlyList<NetService>>
        {
            ["192.168.20.10"] = new[] { new NetService("HTTP", 80, NetProtocol.Tcp), new NetService("SSH", 22, NetProtocol.Tcp) },
            ["192.168.20.11"] = new[] { new NetService("HTTPS", 443, NetProtocol.Tcp) }
        };
        var portsB = new Dictionary<string, IReadOnlyList<NetService>>
        {
            ["192.168.20.10"] = new[] { new NetService("HTTP", 80, NetProtocol.Tcp), new NetService("HTTPS", 443, NetProtocol.Tcp) },
            ["192.168.20.11"] = new[] { new NetService("HTTPS", 443, NetProtocol.Tcp) }
        };
        db.SaveScanArchive(t.AddHours(-24), t.AddHours(-24).AddMinutes(2), "192.168.20.0/24", ["192.168.20.0/24"], "{}",
            [Host("192.168.20.10", "AA:00:00:00:00:10", "nas", 2), Host("192.168.20.11", "AA:00:00:00:00:11", "printer", 4),
             Host("192.168.20.12", "AA:00:00:00:00:12", "laptop", 3), Host("192.168.20.13", "AA:00:00:00:00:13", "camera", 5),
             Host("192.168.20.14", "AA:00:00:00:00:14", "tv", 10), Host("192.168.20.15", "AA:00:00:00:00:15", "old-pc", 6)],
            [], false, portsA);
        db.SaveScanArchive(t, t.AddMinutes(2), "192.168.20.0/24", ["192.168.20.0/24"], "{}",
            [Host("192.168.20.10", "AA:00:00:00:00:10", "nas", 2), Host("192.168.20.11", "AA:00:00:00:00:11", "printer", 4),
             Host("192.168.20.22", "AA:00:00:00:00:12", "laptop", 3), Host("192.168.20.13", "BB:00:00:00:00:99", "camera", 5),
             Host("192.168.20.14", "AA:00:00:00:00:14", "tv", 40), Host("192.168.20.30", "AA:00:00:00:00:30", "new-phone", 8)],
            [], false, portsB);
    }

    /// <summary>A lived-in archive: seven runs of the home net (the newest two feed the comparison), a wide
    /// multi-subnet run with long names, and a stopped run, so list, detail, trend and compare show real shapes.</summary>
    private static void SeedRealisticArchive(string dbPath)
    {
        var db = new KnownDevicesDb(dbPath);
        if (db.ListScanArchive().Count > 0) return;
        string[] names =
        [
            "fritz.box", "nas-wohnzimmer.fritz.box", "DESKTOP-4F8KQ2L", "Brother-HL-L2350DW-Buero-Obergeschoss.local", "iPhone-von-Annika",
            "", "printer", "hue-bridge", "sonos-kueche", "pixel-7-pro", "esp32-garten-sensor-04", "macbook-air-m2",
            "tv-schlafzimmer", "raspberrypi", "switch-keller", "ap-dachgeschoss", "", "ring-doorbell", "camera-einfahrt", "kindle"
        ];
        Device Host(string net, int i, int run, int seed)
        {
            var d = new Device($"{net}.{i + 1}") { Mac = $"AA:BB:CC:{seed:X2}:{i:X2}:{(i * 7) % 256:X2}" };
            var name = names[i % names.Length];
            if (name.Length > 0) d.Hostname = i < names.Length ? name : $"{name}-{i / names.Length}";
            // Every few hosts are away in some runs, so availability and the trend differ per device.
            var online = (i + run) % 6 != 0 && (i * 3 + run) % 11 != 0;
            var ms = 1 + (i * 7 + run * 3) % 38 + (i % 9 == 0 ? 70 : 0);
            d.RecordPing(online ? new PingResult(true, ms, 64) : new PingResult(false, null, null));
            return d;
        }
        Device[] Net(string net, int count, int run, int seed) => Enumerable.Range(0, count).Select(i => Host(net, i, run, seed)).ToArray();
        var now = DateTimeOffset.Now;
        const string settings = "{\"PingCount\":3,\"PingIntervalMs\":200,\"ScanThreads\":64}";
        void Save(TimeSpan ago, TimeSpan took, string network, string[] subnets, Device[] devices, bool stopped = false,
            IReadOnlyDictionary<string, IReadOnlyList<NetService>>? ports = null) =>
            db.SaveScanArchive(now - ago, now - ago + took, network, subnets, settings, devices, [], stopped, ports);
        // A few hosts expose services, the set shifting a little from run to run.
        IReadOnlyDictionary<string, IReadOnlyList<NetService>> Ports(int run) => new Dictionary<string, IReadOnlyList<NetService>>
        {
            ["192.168.20.2"] = new[] { new NetService("HTTP", 80, NetProtocol.Tcp), new NetService("HTTPS", 443, NetProtocol.Tcp) },
            ["192.168.20.3"] = new[] { new NetService("SMB", 445, NetProtocol.Tcp), new NetService("SSH", 22, NetProtocol.Tcp) },
            ["192.168.20.8"] = run % 2 == 0 ? new[] { new NetService("HTTP", 80, NetProtocol.Tcp) } : new[] { new NetService("HTTP", 80, NetProtocol.Tcp), new NetService("RTSP", 554, NetProtocol.Tcp) }
        };

        var home = new[] { "192.168.20.0/24" };
        int[] homeCounts = [38, 37, 36, 38, 35, 34, 30];
        TimeSpan[] homeAgo = [TimeSpan.FromHours(2), TimeSpan.FromHours(26), TimeSpan.FromDays(3), TimeSpan.FromDays(5), TimeSpan.FromDays(8), TimeSpan.FromDays(13), TimeSpan.FromDays(21)];
        for (var r = 0; r < homeCounts.Length; r++)
            Save(homeAgo[r], TimeSpan.FromSeconds(95 + r * 11), "192.168.20.0/24", home, Net("192.168.20", homeCounts[r], r, 0x20), ports: Ports(r));
        var wide = new[] { "10.20.0.0/16", "172.16.8.0/24", "192.168.178.0/24" };
        Save(TimeSpan.FromDays(2) + TimeSpan.FromHours(3), TimeSpan.FromMinutes(14) + TimeSpan.FromSeconds(8), string.Join(", ", wide), wide, Net("172.16.8", 52, 1, 0x30));
        Save(TimeSpan.FromDays(6), TimeSpan.FromMinutes(12) + TimeSpan.FromSeconds(41), string.Join(", ", wide), wide, Net("172.16.8", 49, 2, 0x30));
        Save(TimeSpan.FromDays(4), TimeSpan.FromSeconds(23), "192.168.178.0/24", ["192.168.178.0/24"], Net("192.168.178", 11, 3, 0x40), stopped: true);
        Save(TimeSpan.FromDays(9), TimeSpan.FromSeconds(71), "192.168.178.0/24", ["192.168.178.0/24"], Net("192.168.178", 14, 4, 0x40));
    }

    private static string TempDb(ReviewContext c, string name) => Path.Combine(c.TempDir, name);

    internal static List<Surface> Fixed()
    {
        var list = new List<Surface>
        {
            new("main", "g.main", true, c => MainState(c, _ => { })),
            new("main.sidebar", "g.main", true, c => MainState(c, _ => { }, cropName: "SidebarPanel")),
            new("main.sidebar.profiles", "g.main", true, c => MainState(c, w => w.ReviewSetProfiles("Home", "Lab"),
                profiles: new[] { "Home", "Lab" }, cropName: "SidebarPanel")),
            new("main.firstrun", "g.main", true, c => MainState(c, w => w.ReviewShowFirstRun(), sample: false)),
            new("main.driver.admin", "g.main", true, c => MainState(c, w => w.ReviewSetDriver(new PacketDriverStatus(false, PacketDriver.ReasonAdmin, "1.79")), cropName: "DriverBar")),
            new("main.driver", "g.main", true, c => MainState(c, w => w.ReviewSetDriver(PacketDriverStatus.Missing), cropName: "DriverBar")),
            new("main.presets", "g.settings", true, c => MainState(c, w => w.ReviewShowPresets(false))),
            new("main.presets.command", "g.settings", true, c => MainState(c, w => w.ReviewShowPresets(true, true))),
            new("main.presets.saved", "g.settings", true, c => MainState(c, w => w.ReviewShowPresets(true))),
            new("main.presets.marks", "g.settings", true, c => MainState(c, w => w.ReviewShowPresets(false, false, true))),
            new("main.discovery", "g.settings", true, c => MainState(c, w => { w.ReviewSetDriver(PacketDriverStatus.Missing); w.ReviewOpenSettings("Scan"); w.ReviewScrollToDiscovery(); })),
            new("main.discovery.driver", "g.settings", true, c => MainState(c, w => { w.ReviewSetDriver(new PacketDriverStatus(true, null, "1.0"), true); w.ReviewOpenSettings("Scan"); w.ReviewScrollToDiscovery(); })),
            new("main.ipv6", "g.main", true, c => MainState(c, w => w.ReviewShowIpv6Rows())),
            new("main.oscolumn", "g.main", true, c => MainState(c, w => w.ReviewShowOsColumn())),
            new("main.seencolumns", "g.main", true, c => MainState(c, w => w.ReviewShowSeenColumns())),
            new("main.driver.help", "g.settings", true, c => MainState(c, w => { w.ReviewSetDriver(PacketDriverStatus.Missing); w.ReviewOpenSettings("Help"); w.ReviewScrollToDriver(); })),
            new("main.help.reference", "g.settings", true, c => MainState(c, w => { w.ReviewOpenSettings("Help"); w.ReviewScrollToReference(); })),
            new("main.help.options", "g.settings", true, c => MainState(c, w => { w.ReviewOpenSettings("Help"); w.ReviewScrollToReference("options"); })),
            new("main.help.examples", "g.settings", true, c => MainState(c, w => { w.ReviewOpenSettings("Help"); w.ReviewScrollToReference("examples"); })),
            new("main.help.exit", "g.settings", true, c => MainState(c, w => { w.ReviewOpenSettings("Help"); w.ReviewScrollToReference("exit"); })),
            new("main.devicedetail", "g.main", true, c => MainState(c, w => w.ReviewShowDeviceDetail(false, "Rack 2, top shelf"))),
            new("main.devicedetail.ports", "g.main", true, c => MainState(c, w => w.ReviewShowDeviceDetail(true, ""))),
            new("main.devicedetail.outage", "g.main", true, c => MainState(c, w => w.ReviewShowGraphOutage())),
            new("main.undo", "g.main", true, c => MainState(c, w => w.ReviewShowUndo())),
            new("archive.empty", "g.archive", true, async c =>
            {
                var w = await NewMainAsync(c);
                await w.ReviewOpenArchiveAsync();
                await Settle(500);
                return new Shown { Primary = w };
            }),
            new("archive", "g.archive", true, async c =>
            {
                var w = await NewMainAsync(c);
                SeedRealisticArchive(Path.Combine(c.TempDir, "scanner.db"));
                await w.ReviewOpenArchiveAsync();
                await Settle(500);
                return new Shown { Primary = w };
            }),
            new("archive.detail", "g.archive", true, async c =>
            {
                var w = await NewMainAsync(c);
                SeedRealisticArchive(Path.Combine(c.TempDir, "scanner.db"));
                await w.ReviewOpenArchiveAsync();
                await Settle(500);
                await w.ReviewSelectArchiveRunAsync(0, 3, 0);
                await Settle(500);
                return new Shown { Primary = w };
            }),
            new("archive.runs", "g.archive", true, async c =>
            {
                var w = await NewMainAsync(c);
                SeedRealisticArchive(Path.Combine(c.TempDir, "scanner.db"));
                await w.ReviewOpenArchiveAsync();
                await Settle(500);
                await w.ReviewSelectArchiveRunAsync(-1, -1);
                await Settle(500);
                return new Shown { Primary = w };
            }),
            new("archive.ports", "g.archive", true, async c =>
            {
                var w = await NewMainAsync(c);
                SeedRealisticArchive(Path.Combine(c.TempDir, "scanner.db"));
                await w.ReviewOpenArchiveAsync();
                await Settle(500);
                await w.ReviewSelectArchiveRunAsync(0, -1, 1);
                await Settle(500);
                return new Shown { Primary = w };
            }),
            new("archive.history", "g.archive", true, async c =>
            {
                var w = await NewMainAsync(c);
                SeedRealisticArchive(Path.Combine(c.TempDir, "scanner.db"));
                await w.ReviewOpenArchiveAsync();
                await Settle(500);
                await w.ReviewSelectArchiveRunAsync(0, 3);
                await Settle(500);
                return new Shown { Primary = w };
            }),
            new("archive.compare", "g.archive", true, async c =>
            {
                var w = await NewMainAsync(c);
                SeedRealisticArchive(Path.Combine(c.TempDir, "scanner.db"));
                await w.ReviewOpenArchiveAsync();
                await Settle(500);
                await w.ReviewOpenArchiveCompareAsync();
                await Settle(500);
                return new Shown { Primary = w };
            }),
            new("archive.compare.small", "g.archive", true, async c =>
            {
                var w = await NewMainAsync(c);
                SeedCompareArchive(Path.Combine(c.TempDir, "scanner.db"));
                await w.ReviewOpenArchiveAsync();
                await Settle(500);
                await w.ReviewOpenArchiveCompareAsync();
                await Settle(500);
                return new Shown { Primary = w };
            }),
            new("log", "g.log", true, async c =>
            {
                var w = await NewMainAsync(c);
                await w.ReviewOpenLogAsync();
                await Settle(500);
                return new Shown { Primary = w };
            }),
            new("about", "g.dialog", false, c => Dialog(c, () => new AboutWindow())),
            new("dialog.error", "g.dialog", false, c => Dialog(c, () => new ErrorDialog(Loc.DbExportFailedTitle,
                "System.IO.IOException: The process cannot access the file because it is being used by another process.\n   at IpScanner.Core.Data.KnownDevicesDb.ExportCsv(String path)",
                null, null))),
            new("dialog.message", "g.dialog", false, c => Dialog(c, () => new MessageDialog(
                Loc.CsvRows(1250), Loc.ExportDbCsv, MessageDialogButtons.Ok, MessageDialogKind.Info))),
            new("dialog.message.yesno", "g.dialog", false, c => Dialog(c, () => new MessageDialog(
                Loc.LargeRangeMsg(16, 256, 256L * Core.Net.Ipv4.HostsPerSubnet), Loc.LargeRange,
                MessageDialogButtons.YesNo, MessageDialogKind.Warning))),
            new("dialog.update", "g.dialog", false, c => Dialog(c, () => new UpdateDialog("2.11.0", "2.10.0"))),
            new("dialog.merge", "g.dialog", false, c => Dialog(c, () =>
            {
                Directory.CreateDirectory(c.TempDir);
                var source = TempDb(c, "merge-source.db");
                _ = new KnownDevicesDb(source);
                var w = new MergeProgressDialog(new KnownDevicesDb(TempDb(c, "merge-dest.db")), source);
                if (w.FindName("ProgressBar") is ProgressBar bar) bar.Value = 42;
                if (w.FindName("RecordsText") is TextBlock rec) rec.Text = Loc.MergeRecords(21000, 50000);
                if (w.FindName("ProgressText") is TextBlock pct) pct.Text = "42%";
                return w;
            }, detached: true)),
            new("dialog.fullscan", "g.dialog", false, c => Dialog(c, () => new FullPortScanWarningDialog(true))),
            new("dialog.archiveconfirm", "g.dialog", false, c => Dialog(c, () => new ArchiveConfirmDialog(
                Loc.ArchiveKeyUnavailable, Loc.Ok, true, Loc.ArchiveTitle))),
            new("dialog.manualexport", "g.dialog", false, c => Dialog(c, () => new ManualExportDialog(
                ExportScope.Filtered, ExportFormat.Csv, true))),
            new("dialog.configmerge", "g.dialog", false, c => Dialog(c, () => new ConfigMergeDialog())),
            new("dialog.backuppassword.set", "g.dialog", false, c => Dialog(c, () => new BackupPasswordDialog(true))),
            new("dialog.backuppassword.ask", "g.dialog", false, c => Dialog(c, () => new BackupPasswordDialog(false, true))),
            new("selftest.clip", "g.selftest", false, c => Dialog(c, SelfTestWindow)),
        };
        foreach (var page in SettingsPages)
        {
            var p = page;
            list.Add(new Surface($"main.settings.{p}", "g.settings", true, c => MainState(c, w => w.ReviewOpenSettings(p))));
        }
        list.Add(new Surface("main.settings.ScanAdvanced", "g.settings", true, c => MainState(c, w => w.ReviewOpenSettingsAdvanced())));
        list.Add(new Surface("main.settings.ScanAdvancedMarks", "g.settings", true, c => MainState(c, w => w.ReviewOpenSettingsAdvanced(true))));
        foreach (var page in ToolPages)
        {
            var p = page;
            list.Add(new Surface($"tools.{p}", "g.tools", true, async c =>
            {
                var main = await NewMainAsync(c);
                var tools = main.ReviewOpenTools(p);
                Place(tools, c, resizable: true);
                await Settle();
                var shown = new Shown { Primary = tools };
                shown.Others.Add(main);
                return shown;
            }));
        }
        // The subnet calculator with an IPv6 prefix and a containment test.
        AddMarks(list, "Calculator", v =>
        {
            v.Calculator.SubnetAddress = "2001:db8:1234:5600::";
            v.Calculator.SubnetMask = "/56";
            v.Calculator.ContainsAddress = "2001:db8:1234:5611::42";
        }, null, "tools.CalculatorIpv6");
        // The subnet split with a capped table, and the MAC page with a vendor and a table result.
        AddMarks(list, "Calculator", v =>
        {
            v.Calculator.SubnetAddress = "10.20.0.0";
            v.Calculator.SubnetMask = "/16";
            v.Calculator.SplitPrefix = "/24";
        }, null, "tools.CalculatorSplit");
        AddMarks(list, "MacTool", v =>
        {
            v.Mac.VendorMac = "00:00:00:11:22:33";
            v.Mac.ArpIp = "192.168.1.1";
        }, null, "tools.MacToolMarks");
        // The route table with hops: header and values must line up column by column.
        AddMarks(list, "PingRoute", v =>
        {
            var hop = new RouteToolViewModel.Hop { Number = 1, Address = "192.168.1.1", Name = "router.lan" };
            foreach (var ms in new double?[] { 1, 2, 1 }) hop.Record(ms);
            v.PingRoute.Route.Hops.Add(hop);
            var far = new RouteToolViewModel.Hop { Number = 12, Address = "93.184.216.34", Name = "a-long-host-name.example.net" };
            foreach (var ms in new double?[] { 118, null, 121 }) far.Record(ms);
            v.PingRoute.Route.Hops.Add(far);
        });
        // Pages in their aggressive state: every shared warning mark must show.
        AddMarks(list, "ServiceDetection", v => { v.ServiceDetection.Timing = 5; v.ServiceDetection.Scope = ScanToolViewModel.ScopeAll; });
        AddMarks(list, "Checks", v => { v.Checks.Timing = 4; v.Checks.Scope = ScanToolViewModel.ScopeAll; v.Checks.CheckIntrusive = true; });
        AddMarks(list, "PortCheck", v =>
        {
            v.PortCheck.SelectedScanType = v.PortCheck.ScanTypeOptions.First(o => ScanTypes.IsAggressive(o.Type));
            v.PortCheck.CheckIntrusive = true;
        }, tools => CheckRadio(tools, "Top1000"));
        return list;
    }

    // The scope radios of the port page are code driven: tick one the way a click would.
    private static void CheckRadio(DependencyObject node, string tag)
    {
        if (node is RadioButton { Tag: string t } r && t == tag) { r.IsChecked = true; return; }
        int n = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < n; i++) CheckRadio(VisualTreeHelper.GetChild(node, i), tag);
    }

    private static void AddMarks(List<Surface> list, string page, Action<ToolsViewModel> setup, Action<Window>? afterShow = null, string? id = null)
        => list.Add(new Surface(id ?? $"tools.{page}Marks", "g.tools", true, async c =>
        {
            var main = await NewMainAsync(c);
            var tools = main.ReviewOpenTools(page, setup);
            Place(tools, c, resizable: true);
            await Settle();
            if (afterShow is not null) { afterShow(tools); await Settle(); }
            var shown = new Shown { Primary = tools };
            shown.Others.Add(main);
            return shown;
        }));

    // A window with one label that fits and one deliberately narrowed label: the clip report must
    // flag only the second.
    private static Window SelfTestWindow()
    {
        var grid = new Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        var fits = new TextBlock { Name = "FitsLabel", Text = "Fits fine", VerticalAlignment = VerticalAlignment.Center };
        var narrow = new Border { Width = 40, HorizontalAlignment = HorizontalAlignment.Left, Child = new TextBlock { Name = "NarrowLabel", Text = "A deliberately narrowed label" } };
        Grid.SetRow(narrow, 1);
        grid.Children.Add(fits);
        grid.Children.Add(narrow);
        var w = new Window { Width = 280, Height = 120, Content = grid, Title = "selftest" };
        w.SetResourceReference(Control.BackgroundProperty, "Bg");
        w.SetResourceReference(Control.ForegroundProperty, "Text");
        return w;
    }

    // Names of the visible combo boxes of a laid-out window (x:Name, else a running index).
    internal static List<(string Name, ComboBox Combo)> FindCombos(DependencyObject root)
    {
        var found = new List<(string, ComboBox)>();
        int index = 0;
        void Walk(DependencyObject d)
        {
            if (d is UIElement { Visibility: not Visibility.Visible }) return;
            if (d is ComboBox cb && cb.ActualWidth > 0)
                found.Add((string.IsNullOrEmpty(cb.Name) ? $"combo{index}" : cb.Name, cb));
            if (d is ComboBox) index++;
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(d, i));
        }
        Walk(root);
        return found;
    }

    /// <summary>Builds the complete surface list: the fixed surfaces plus one open state for every
    /// combo box and the device-row menu, found by probing each host surface once.</summary>
    internal static async Task<List<Surface>> BuildAllAsync(string tempDir)
    {
        var all = Fixed();
        var probe = new ReviewContext("en", "dark", 1320, 720, 100, tempDir, false);
        var seen = new HashSet<string>();
        foreach (var host in all.ToList())
        {
            if (host.Group is "g.selftest" or "g.archive" or "g.log" || host.Id is "main.sidebar" or "main.sidebar.profiles" or "main.firstrun" or "main.undo")
                continue;
            Shown s;
            try { s = await host.Open(probe); }
            catch { continue; }
            await Settle(60);
            foreach (var (name, _) in FindCombos(s.Primary))
            {
                if (!seen.Add(name)) continue;
                var hostCopy = host; var comboName = name;
                all.Add(new Surface($"{host.Id}.open.{name}", "g.popups", host.Resizable, async c =>
                {
                    var shown = await hostCopy.Open(c);
                    var combo = FindCombos(shown.Primary).FirstOrDefault(x => x.Name == comboName).Combo;
                    if (combo is not null)
                    {
                        combo.IsDropDownOpen = true;
                        await Settle();
                        shown.Popup = () => ((combo.Template.FindName("PART_Popup", combo) ?? combo.Template.FindName("Popup", combo)) as Popup)?.Child as FrameworkElement;
                    }
                    return shown;
                }));
            }
            CloseAll(s);
        }
        all.Add(new Surface("main.menu.devicerow", "g.popups", true, async c =>
        {
            var w = await NewMainAsync(c);
            var grid = w.ReviewElement("DeviceGrid") as DataGrid;
            var row = grid is null ? null : FirstRow(grid);
            ContextMenu? menu = row?.ContextMenu;
            if (menu is not null && row is not null)
            {
                menu.PlacementTarget = row;
                menu.IsOpen = true;
                await Settle();
            }
            var shown = new Shown { Primary = w };
            shown.Popup = () => menu;
            return shown;
        }));
        all.Add(new Surface("main.menu.devicerow.openwith", "g.popups", true, async c =>
        {
            var w = await NewMainAsync(c);
            var grid = w.ReviewElement("DeviceGrid") as DataGrid;
            var row = grid is null ? null : FirstRow(grid);
            ContextMenu? menu = row?.ContextMenu;
            MenuItem? openWith = null;
            if (menu is not null && row is not null)
            {
                menu.PlacementTarget = row;
                menu.IsOpen = true;
                await Settle();
                openWith = menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "OpenWithItem");
                if (openWith is not null) { openWith.IsSubmenuOpen = true; await Settle(); }
            }
            var shown = new Shown { Primary = w };
            shown.Popup = () => (openWith?.Template.FindName("PART_Popup", openWith) as Popup)?.Child as FrameworkElement;
            return shown;
        }));
        all.Add(new Surface("main.tip.ports", "g.popups", true, async c =>
        {
            var w = await NewMainAsync(c);
            w.ReviewShowDeviceDetail(false, "");
            var grid = w.ReviewElement("DeviceGrid") as DataGrid;
            var row = grid is null ? null : FirstRow(grid);
            var col = grid?.Columns.FirstOrDefault(x => x.SortMemberPath == "PortsSortKey");
            var cell = row is null || col is null ? null : col.GetCellContent(row);
            ToolTip? tip = null;
            if (cell is not null && ToolTipService.GetToolTip(cell) is { } content)
            {
                // The cell's own tooltip text in a real ToolTip, placed the way a hover would place it.
                tip = new ToolTip { Content = content, PlacementTarget = cell, Placement = PlacementMode.Bottom };
                tip.IsOpen = true;
                await Settle();
            }
            var shown = new Shown { Primary = w };
            shown.Popup = () => tip;
            return shown;
        }));
        return all;
    }

    private static DataGridRow? FirstRow(DataGrid grid)
    {
        grid.UpdateLayout();
        return grid.ItemContainerGenerator.ContainerFromIndex(0) as DataGridRow;
    }

    internal static void CloseAll(Shown s)
    {
        foreach (var o in s.Others) SafeClose(o);
        SafeClose(s.Primary);
    }

    private static void SafeClose(Window w)
    {
        try
        {
            if (w is MergeProgressDialog) return;
            w.Close();
        }
        catch { /* a window that refuses to close is dropped at process exit */ }
    }
}
#endif
