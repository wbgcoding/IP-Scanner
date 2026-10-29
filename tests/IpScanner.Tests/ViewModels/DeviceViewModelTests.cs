using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System;
using System.IO;
using System.Threading;
using IpScanner.Core.Data;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using IpScanner.ViewModels;
using Xunit;

namespace IpScanner.Tests.ViewModels;

// Shares a collection with LocalizationTests: both read/write the process-wide
// Loc.German flag, so they must never interleave under xUnit's parallel
// test-collection execution.
[Collection("Localization")]
public class DeviceViewModelTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(Device.Unknown)]
    public void CopyableHostname_IsNullWhenHostIsUnknown_WhileHostnameShowsTheDash(string? raw)
    {
        var vm = new DeviceViewModel(new Device("192.0.2.9") { Hostname = raw });
        Assert.Null(vm.CopyableHostname);
        Assert.Equal(((char)0x2013).ToString(), vm.Hostname);
    }

    [Fact]
    public void CopyableHostname_EqualsHostnameForAKnownHost()
    {
        var vm = new DeviceViewModel(new Device("192.0.2.9") { Hostname = "printer" });
        Assert.Equal("printer", vm.CopyableHostname);
        Assert.Equal(vm.Hostname, vm.CopyableHostname);
    }

    [Fact]
    public async Task DetailOpen_RestoresPartialPortResultFromDatabase()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "scanner.db");
        var db = new KnownDevicesDb(path);
        var seenAt = DateTimeOffset.UtcNow.AddDays(-2);
        db.SavePortScan("network", "AA:BB:CC:00:00:01", "192.0.2.9",
            new[] { new NetService("HTTPS", 443, NetProtocol.Tcp, "stored") },
            seenAt, true);
        var finished = new TaskCompletionSource<(bool Loaded, string Error, string? Version, string Header)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
            var vm = new DeviceViewModel(new Device("192.0.2.9") { Mac = "AA:BB:CC:00:00:01" },
                portStore: () => ("network", path));
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            var until = DateTime.UtcNow.AddSeconds(5);
            timer.Tick += (_, _) =>
            {
                if (!vm.HasPortScanResult && !vm.HasPortScanError && DateTime.UtcNow < until) return;
                timer.Stop();
                finished.TrySetResult((vm.HasPortScanResult, vm.PortScanErrorText,
                    vm.Services.Count == 1 ? vm.Services[0].Version : null, vm.PortScanResultHeader));
                vm.ShowGraph = false;
                dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Normal);
            };
            vm.ShowGraph = true;
            timer.Start();
            System.Windows.Threading.Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var result = await finished.Task.WaitAsync(TimeSpan.FromSeconds(7));
        Assert.True(result.Loaded, result.Error);
        Assert.Equal("stored", result.Version);
        Assert.Contains(seenAt.ToLocalTime().ToString("d"), result.Header);
    }

    private static async Task WaitUntilIdle(DeviceViewModel vm)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(25);
            if (!vm.IsFullPortScanRunning) return;
        }
        throw new TimeoutException("full port scan did not finish");
    }

    [Fact]
    public async Task FullPortScan_PersistsTheMergedTcpAndUdpList()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "scanner.db");
        // The list is bound to a view that is tied to the UI thread, so the scan runs on the WPF test host.
        var hasError = await WpfTestHost.RunAsync(async () =>
        {
            var vm = new DeviceViewModel(new Device("192.0.2.9") { Mac = "AA:BB:CC:00:00:01" },
                portStore: () => ("network", path),
                fullScan: (ip, progress, ct, onFound) => Task.FromResult<IReadOnlyList<NetService>>(
                    new[] { new NetService("custom", 8080, NetProtocol.Tcp) }));
            vm.Services.Add(new NetService("ssh", 22, NetProtocol.Tcp));
            vm.Services.Add(new NetService("snmp", 161, NetProtocol.Udp));
            await vm.StartFullPortScan();
            return vm.HasPortScanError;
        });

        var stored = new KnownDevicesDb(path).LoadPortScan("network", "AA:BB:CC:00:00:01", "192.0.2.9");
        Assert.False(hasError);
        Assert.NotNull(stored);
        Assert.Equal(new[] { "161/Udp", "22/Tcp", "8080/Tcp" },
            stored!.Services.Select(s => $"{s.Port}/{s.Protocol}").ToArray());
    }

    [Fact]
    public async Task FullPortScan_ScanFailureShowsTheScanTextNotTheSaveText()
    {
        Loc.SetLanguage("en");
        try
        {
            var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "scanner.db");
            var vm = new DeviceViewModel(new Device("192.0.2.9") { Mac = "AA:BB:CC:00:00:01" },
                portStore: () => ("network", path),
                fullScan: (ip, progress, ct, onFound) => throw new InvalidOperationException("boom"));

            var scan = vm.StartFullPortScan();
            await WaitUntilIdle(vm);
            await scan;

            Assert.True(vm.HasPortScanError);
            Assert.Equal(Loc.PortCheckFailed, vm.PortScanErrorText);
            Assert.NotEqual(Loc.PortResultSaveFailed, vm.PortScanErrorText);
        }
        finally { Loc.SetLanguage("en"); }
    }

    [Fact]
    public async Task FullPortScan_CancellationIsSilent()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "scanner.db");
        var vm = new DeviceViewModel(new Device("192.0.2.9") { Mac = "AA:BB:CC:00:00:01" },
            portStore: () => ("network", path),
            fullScan: (ip, progress, ct, onFound) => throw new OperationCanceledException());

        var scan = vm.StartFullPortScan();
        await WaitUntilIdle(vm);
        await scan;

        Assert.False(vm.HasPortScanError);
    }

    [Fact]
    public void NoteDraft_OverLimitShowsValidationWithoutTruncatingInput()
    {
        Loc.SetLanguage("en");
        try
        {
            var vm = new DeviceViewModel(new Device("192.168.1.1"));
            vm.NoteDraft = new string('x', OverrideStore.MaxNoteLength + 1);

            Assert.True(vm.IsNoteTooLong);
            Assert.Equal(OverrideStore.MaxNoteLength + 1, vm.NoteDraft.Length);
            Assert.Contains($"{OverrideStore.MaxNoteLength + 1}/{OverrideStore.MaxNoteLength}", vm.NoteValidationText);
        }
        finally { Loc.SetLanguage("en"); }
    }

    [Fact]
    public void Refresh_LateLoadedNoteUpdatesUntouchedDraftButPreservesActiveEdit()
    {
        var device = new Device("192.168.1.1");
        var vm = new DeviceViewModel(device);

        device.Note = "stored note";
        vm.Refresh();
        Assert.Equal("stored note", vm.NoteDraft);

        vm.NoteDraft = "editing now";
        device.Note = "new stored note";
        vm.Refresh();

        Assert.Equal("new stored note", vm.Note);
        Assert.Equal("editing now", vm.NoteDraft);
    }

    [Fact]
    public void Update_ReflectsDeviceState()
    {
        // Loc.German defaults from the OS system language, so the expected decimal separator
        // would depend on the machine's locale and on whatever LocalizationTests left in the
        // shared static. Set the language this test needs and restore it afterwards.
        var wasGerman = Loc.German;
        Loc.SetLanguage("de");
        try
        {
            var d = new Device("192.168.1.1") { Hostname = "router" };
            d.RecordPing(new PingResult(true, 2.5, 64));
            var vm = new DeviceViewModel(d);
            vm.Refresh();
            Assert.Equal("192.168.1.1", vm.Ip);
            Assert.Equal("ONLINE", vm.Status);
            Assert.Equal("router", vm.Hostname);
            Assert.Contains("2,5", vm.AvgDisplay);  // de-DE comma decimal
        }
        finally
        {
            Loc.SetLanguage(wasGerman ? "de" : "en");
        }
    }

    [Fact]
    public void Refresh_RaisesPropertyChanged()
    {
        var d = new Device("192.168.1.1");
        var vm = new DeviceViewModel(d);
        bool raised = false;
        vm.PropertyChanged += (_, _) => raised = true;
        d.RecordPing(new PingResult(true, 1.0, 64));
        vm.Refresh();
        Assert.True(raised);
    }

    // A muted device never shows the offline-red highlight, the rogue
    // tint, or the latency heatmap - the whole row dims to Subtext instead.
    [Fact]
    public void Muted_SuppressesOfflineColorAndRogueHighlight()
    {
        var d = new Device("192.168.1.20") { Mac = "AA:BB:CC:00:00:20", IsMuted = true };
        d.RecordPing(new PingResult(false, null, null));   // never online -> would be red/offline
        var vm = new DeviceViewModel(d);
        vm.Refresh();

        Assert.True(vm.IsMuted);
        Assert.False(vm.IsRogue);
        Assert.Equal(IpScanner.Core.Palette.Subtext, vm.StatusColor);
        Assert.Equal(IpScanner.Core.Palette.Subtext, vm.IpColor);
    }

    [Fact]
    public void Muted_StillShowsRogueBeforeMuting()
    {
        var d = new Device("192.168.1.21") { Mac = "AA:BB:CC:00:00:21" };
        d.RecordPing(new PingResult(true, 1.0, 64));
        var vm = new DeviceViewModel(d);
        Assert.True(vm.IsRogue);   // online, unknown MAC, not self -> rogue

        d.IsMuted = true;
        vm.Refresh();
        Assert.False(vm.IsRogue);   // muting removes the highlight immediately
    }

    [Fact]
    public void Vendor_IsCachedPerMac_ButUpdatesWhenMacChanges()
    {
        var d = new Device("192.168.1.1");
        var vm = new DeviceViewModel(d);
        Assert.Equal("–", vm.Vendor);   // no MAC yet

        d.Mac = "00:00:00:11:22:33";    // OUI present in the embedded vendor DB (Xerox, historically stable)
        var resolved = vm.Vendor;
        Assert.NotEqual("–", resolved);
        Assert.Equal(resolved, vm.Vendor);   // second read hits the cache, same result

        d.Mac = null;                   // MAC lost/reset must invalidate the cache
        Assert.Equal("–", vm.Vendor);
    }

    [Fact]
    public void ShowGraph_OpenedWhileOffline_RetriesServiceScanOnceOnline()
    {
        // Opening the detail view while the device is offline must not leave the
        // services list empty for good: the scan is retried once the device answers.
        var d = new Device("203.0.113.5");   // TEST-NET-3 (RFC 5737): never online yet
        var vm = new DeviceViewModel(d);
        vm.ShowGraph = true;                 // opened while offline -> ScanServices no-ops
        Assert.False(vm.IsScanningServices);

        d.RecordPing(new PingResult(true, 1.0, 64));   // now online
        vm.Refresh();
        Assert.True(vm.IsScanningServices);  // retried automatically, not stuck empty
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ShowGraph_ClosedAndReopenedBeforeCancelUnwinds_StillEndsUpScanned()
    {
        // Closing the detail view cancels its in-flight scan, but that
        // cancellation is cooperative - reopening immediately sees
        // _servicesScanned still true (the cancelled scan hasn't reset it
        // yet). The reopen must still start a new scan, otherwise the view stays empty
        // once the cancelled scan's catch block resets the flag.
        var d = new Device("203.0.113.6");   // TEST-NET-3 (RFC 5737): real, all ports closed/filtered
        d.RecordPing(new PingResult(true, 1.0, 64));   // online, so ScanServices doesn't no-op
        var vm = new DeviceViewModel(d);

        vm.ShowGraph = true;    // starts a real (small) port scan
        vm.ShowGraph = false;   // cancels it
        vm.ShowGraph = true;    // reopens before the cancellation's catch/finally has run

        for (int i = 0; i < 100 && vm.IsScanningServices; i++) await Task.Delay(100);

        Assert.False(vm.IsScanningServices);   // settled, not left mid-scan
        // A settled scan either found something or correctly shows the
        // "scanned and empty" text - the bug's "limbo" state instead shows
        // "not checked yet" forever because _servicesScanned never got set
        // back to true.
        Assert.True(vm.Services.Count > 0 || vm.ServicesEmptyText == Loc.NoServices);
    }

    [Fact]
    public void Ipv6Row_ShowsTheShortFormSortsAfterIpv4AndTipsTheFullAddress()
    {
        Loc.SetLanguage("en");
        var v6 = new DeviceViewModel(new Device("2001:0db8:0:0:0:0:0:10"));
        var v4 = new DeviceViewModel(new Device("192.168.1.200"));
        Assert.Equal("2001:db8::10", v6.IpText);
        Assert.True(v4.IpSortKey.CompareTo(v6.IpSortKey) < 0);
        Assert.Contains("2001:0db8:0:0:0:0:0:10", v6.IpTooltip);
        Assert.Equal(Loc.TipPinIp, v4.IpTooltip);
        v6.IsPinned = true;
        Assert.Equal("2001:0db8:0:0:0:0:0:10", v6.IpTooltip);
    }
}
