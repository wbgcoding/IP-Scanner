using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core.Data;
using IpScanner.Core.Export;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using IpScanner.ViewModels;
using Xunit;

namespace IpScanner.Tests.ViewModels;

public class MainViewModelTests
{
    private static PingResult OnlyDotOne(string ip, int _) =>
        ip.EndsWith(".1") ? new PingResult(true, 1.0, 64) : new PingResult(false, null, null);

    [Fact]
    public async Task RunScan_PopulatesDevices()
    {
        var vm = new MainViewModel(
            pingFunc: OnlyDotOne,
            detectNetwork: () => new NetworkInfo { Ip = "10.0.0.5", Gateway = "10.0.0.1" },
            dispatch: a => a());      // synchronous dispatch for tests

        vm.Config = new ScanConfig { PingCount = 1, PingIntervalMs = 0, FileOutput = false, KnownDevicesDb = false, EnableInternetPing = false };

        await vm.RunScanAsync(new[] { "10.0.0" });

        Assert.Contains(vm.Devices, d => d.Ip == "10.0.0.1" && d.IsOnline);
        Assert.True(vm.Progress.OnlineCount >= 1);
    }

    private static ScanConfig ArchiveConfig(string dir, bool knownDb) => new()
    {
        PingCount = 1, PingIntervalMs = 0, FileOutput = false, EnableInternetPing = false,
        KnownDevicesDb = knownDb, ScanHistory = true, ScanThreads = 4,
        DatabasePath = Path.Combine(dir, "scanner.db"), OutputDirectory = Path.Combine(dir, "Scans"),
    };

    [Fact]
    public async Task RunScan_WithKnownDevicesDbOff_WritesNoArchiveRow()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var vm = new MainViewModel(OnlyDotOne, () => new NetworkInfo { Ip = "10.0.0.5", Gateway = "10.0.0.1" }, a => a());
        vm.Config = ArchiveConfig(dir, knownDb: false);

        await vm.RunScanAsync(new[] { "10.0.0" });

        Assert.Empty(new KnownDevicesDb(vm.Config.DatabasePath).ListScanArchive());
    }

    [Fact]
    public async Task RunScan_WithKnownDevicesDbOn_ArchivesTheRun()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var vm = new MainViewModel(OnlyDotOne, () => new NetworkInfo { Ip = "10.0.0.5", Gateway = "10.0.0.1" }, a => a());
        vm.Config = ArchiveConfig(dir, knownDb: true);

        await vm.RunScanAsync(new[] { "10.0.0" });

        var run = Assert.Single(new KnownDevicesDb(vm.Config.DatabasePath).ListScanArchive());
        Assert.False(run.Cancelled);
    }

    [Fact]
    public async Task Stop_DuringARunningScan_ArchivesTheRunAsCancelled()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var vm = new MainViewModel((ip, count) => { Thread.Sleep(20); return OnlyDotOne(ip, count); },
            () => new NetworkInfo { Ip = "10.0.0.5", Gateway = "10.0.0.1" }, a => a());
        vm.Config = ArchiveConfig(dir, knownDb: true);

        var scan = vm.RunScanAsync(new[] { "10.0.0" });
        await Task.Delay(50);
        vm.Stop();
        await scan;

        var run = Assert.Single(new KnownDevicesDb(vm.Config.DatabasePath).ListScanArchive());
        Assert.True(run.Cancelled);
    }

    [Fact]
    public async Task Stop_DuringARunningScan_CancelsItAndAllowsAnImmediateRestart()
    {
        // The scan button calls Stop() while a scan is running instead of trying to start
        // a second one - Stop() must actually cancel the run and release the scan-start
        // gate so the very next click can start a fresh scan right away, not no-op.
        // Slow pings only while the first scan has to be caught mid-flight; the restart runs at full speed.
        var slowPings = true;
        var vm = new MainViewModel(
            pingFunc: (ip, count) => { if (Volatile.Read(ref slowPings)) Thread.Sleep(20); return OnlyDotOne(ip, count); },
            detectNetwork: () => new NetworkInfo { Ip = "10.0.0.5" },
            dispatch: a => a());
        vm.Config = new ScanConfig
        {
            PingCount = 1, PingIntervalMs = 0, FileOutput = false, KnownDevicesDb = false,
            EnableInternetPing = false, ScanThreads = 4,
        };

        var firstScan = vm.RunScanAsync(new[] { "10.0.0" });
        await Task.Delay(50); // let it actually start pinging before stopping it mid-flight
        Assert.True(vm.IsScanning);

        vm.Stop();
        await firstScan;
        Assert.False(vm.IsScanning);

        Volatile.Write(ref slowPings, false);
        await vm.RunScanAsync(new[] { "10.0.0" });
        Assert.Contains(vm.Devices, d => d.Ip == "10.0.0.1" && d.IsOnline);
    }

    // "Resolve name again" reruns the (doubled-timeout) enrich chain
    // for one device and applies the result immediately, without a full scan.
    [Fact]
    public async Task ResolveHostnameAgainAsync_AppliesHostnameFromRetryEnrichers()
    {
        var vm = new MainViewModel(
            pingFunc: OnlyDotOne,
            detectNetwork: () => new NetworkInfo { Ip = "10.0.0.5", Gateway = "10.0.0.1" },
            dispatch: a => a(),
            retryEnrichers: new[] { EnrichTechnique.FromSync(ip => (null, "resolved-name")) });
        vm.Config = new ScanConfig { PingCount = 1, PingIntervalMs = 0, FileOutput = false, KnownDevicesDb = false, EnableInternetPing = false };
        await vm.RunScanAsync(new[] { "10.0.0" });

        var device = vm.Devices.First(d => d.Ip == "10.0.0.1");
        Assert.True(string.IsNullOrEmpty(device.Model.Hostname));

        await vm.ResolveHostnameAgainAsync(device);

        Assert.Equal("resolved-name", device.Model.Hostname);
    }

    [Fact]
    public void SetNoHostnameRetry_RoundTripsThroughIsNoHostnameRetry()
    {
        var vm = new MainViewModel(
            pingFunc: OnlyDotOne,
            detectNetwork: () => new NetworkInfo { Ip = "10.0.0.5" },
            dispatch: a => a());
        vm.Config = new ScanConfig { PingCount = 1, PingIntervalMs = 0, FileOutput = false, KnownDevicesDb = false, EnableInternetPing = false };

        var device = new DeviceViewModel(new Device("10.0.0.9"));
        Assert.False(vm.IsNoHostnameRetry(device));

        vm.SetNoHostnameRetry(device, true);
        Assert.True(vm.IsNoHostnameRetry(device));

        vm.SetNoHostnameRetry(device, false);
        Assert.False(vm.IsNoHostnameRetry(device));
    }

    // "Mute device" toggle round-trips through the overrides store and
    // is mirrored onto the underlying Device (read by ScanEngine directly).
    [Fact]
    public void SetMuted_RoundTripsThroughIsMuted()
    {
        var vm = new MainViewModel(
            pingFunc: OnlyDotOne,
            detectNetwork: () => new NetworkInfo { Ip = "10.0.0.5" },
            dispatch: a => a());
        vm.Config = new ScanConfig { PingCount = 1, PingIntervalMs = 0, FileOutput = false, KnownDevicesDb = false, EnableInternetPing = false };

        var device = new DeviceViewModel(new Device("10.0.0.10"));
        Assert.False(vm.IsMuted(device));

        vm.SetMuted(device, true);
        Assert.True(vm.IsMuted(device));
        Assert.True(device.Model.IsMuted);

        vm.SetMuted(device, false);
        Assert.False(vm.IsMuted(device));
    }

    [Fact]
    public void SetNote_SanitizesPersistsAndRejectsOverLimit()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "overrides.dat");
        var vm = new MainViewModel(
            pingFunc: OnlyDotOne,
            detectNetwork: () => new NetworkInfo { Ip = "10.0.0.5" },
            dispatch: a => a());
        vm.InitOverrides(path);
        var device = new DeviceViewModel(new Device("10.0.0.9") { Mac = "AA:BB:CC:00:00:09" });

        Assert.True(vm.SetNote(device, "line one\r\nline" + (char)0 + " two"));
        Assert.Equal("line one\nline two", device.Note);
        Assert.False(vm.SetNote(device, new string('x', OverrideStore.MaxNoteLength + 1)));

        var reloaded = new OverrideStore();
        reloaded.Load(path);
        Assert.Equal("line one\nline two", reloaded.Get("AA:BB:CC:00:00:09", "10.0.0.99")?.Note);
    }

    [Fact]
    public void LateMac_ReappliesStoredNoteBeforeExport_AndUntouchedDraftDoesNotEraseIt()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(dir, "overrides.dat");
        const string mac = "AA:BB:CC:00:11:99";
        var stored = new OverrideStore();
        stored.Load(path);
        stored.SetNote(mac, "10.0.0.50", "switch closet");

        var vm = new MainViewModel(OnlyDotOne, () => new NetworkInfo(), a => a());
        vm.InitOverrides(path);
        var model = new Device("10.0.0.9");
        model.RecordPing(new PingResult(true, 1, 64));
        var update = typeof(MainViewModel).GetMethod("OnDeviceUpdated", BindingFlags.Instance | BindingFlags.NonPublic)!;
        update.Invoke(vm, new object[] { model });
        var row = Assert.Single(vm.Devices);
        Assert.Equal("", row.NoteDraft);

        model.Mac = mac;
        update.Invoke(vm, new object[] { model });

        Assert.Equal("switch closet", model.Note);
        var csv = File.ReadAllText(CsvExporter.Write(new[] { model }, dir, "20260919_120000", ""));
        Assert.Contains("switch closet", csv);
        Assert.Equal("switch closet", row.NoteDraft);
        Assert.False(row.HasNoteEdit);
        if (row.HasNoteEdit) vm.SetNote(row, row.NoteDraft);
        var reloaded = new OverrideStore();
        reloaded.Load(path);
        Assert.Equal("switch closet", reloaded.Get(mac, model.Ip)?.Note);

        row.NoteDraft = "edited note";
        Assert.True(row.HasNoteEdit);
        Assert.True(vm.SetNote(row, row.NoteDraft));
        reloaded.Load(path);
        Assert.Equal("edited note", reloaded.Get(mac, model.Ip)?.Note);
    }

    [Fact]
    public async Task RunScan_LateMacNoteIsPresentInAutomaticCsvExport()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(dir, "overrides.dat");
        const string mac = "AA:BB:CC:00:11:98";
        var stored = new OverrideStore();
        stored.Load(path);
        stored.SetNote(mac, "10.0.0.50", "rack label");

        var vm = new MainViewModel(OnlyDotOne,
            () => new NetworkInfo { Ip = "10.0.0.5", Gateway = "10.0.0.1" }, a => a(),
            enrichers: new[]
            {
                EnrichTechnique.FromSync(ip => ip.EndsWith(".1") ? (mac, null) : (null, null)),
            });
        vm.InitOverrides(path);
        vm.Config = new ScanConfig
        {
            PingCount = 1, PingIntervalMs = 0, FileOutput = false, ExportCsv = true,
            OutputDirectory = dir, KnownDevicesDb = false, EnableInternetPing = false,
        };

        await vm.RunScanAsync(new[] { "10.0.0" });

        var csv = File.ReadAllText(Assert.Single(Directory.GetFiles(dir, "*.csv")));
        Assert.Contains("rack label", csv);
    }

    // "Mark as known" writes a rogue device into the known-devices DB
    // under the gateway's MAC, the same key a normal scan-end save uses, and
    // clears the rogue highlight immediately.
    [Fact]
    public async Task MarkAsKnown_ClearsRogueHighlight_AndPersistsToDatabase()
    {
        var dbPath = Path.Combine(Directory.CreateTempSubdirectory().FullName, "scanner.db");
        var vm = new MainViewModel(
            pingFunc: (ip, _) => ip.EndsWith(".1") || ip.EndsWith(".9") ? new PingResult(true, 1.0, 64) : new PingResult(false, null, null),
            detectNetwork: () => new NetworkInfo { Ip = "10.0.0.5", Gateway = "10.0.0.1" },
            dispatch: a => a(),
            enrichers: new[]
            {
                EnrichTechnique.FromSync(ip => ip.EndsWith(".1") ? ("AA:BB:CC:00:00:01", null)
                    : ip.EndsWith(".9") ? ("AA:BB:CC:00:00:09", null) : (null, null)),
            });
        vm.Config = new ScanConfig
        {
            PingCount = 1, PingIntervalMs = 0, FileOutput = false, KnownDevicesDb = false,
            EnableInternetPing = false, DatabasePath = dbPath,
        };

        await vm.RunScanAsync(new[] { "10.0.0" });

        var rogue = vm.Devices.First(d => d.Ip == "10.0.0.9");
        // The MAC lookup runs beside the scan; on a loaded machine it can land a moment later.
        for (int i = 0; i < 100 && !rogue.IsRogue; i++) await Task.Delay(50);
        Assert.True(rogue.IsRogue);

        await vm.MarkAsKnownAsync(rogue);

        Assert.False(rogue.IsRogue);
        Assert.True(rogue.Model.FromDb);

        var db = new IpScanner.Core.Data.KnownDevicesDb(dbPath);
        var loaded = db.Load("AA:BB:CC:00:00:01");
        Assert.Contains(loaded, d => d.Ip == "10.0.0.9");
    }

    [Fact]
    public async Task MarkAsKnown_WhenTheDatabaseCannotBeWritten_ReturnsTheFailureAndKeepsTheDeviceUnknown()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var blocker = Path.Combine(dir, "file.txt");
        File.WriteAllText(blocker, "x");
        var vm = new MainViewModel(
            pingFunc: (ip, _) => ip.EndsWith(".1") || ip.EndsWith(".9") ? new PingResult(true, 1.0, 64) : new PingResult(false, null, null),
            detectNetwork: () => new NetworkInfo { Ip = "10.0.0.5", Gateway = "10.0.0.1" },
            dispatch: a => a(),
            enrichers: new[]
            {
                EnrichTechnique.FromSync(ip => ip.EndsWith(".1") ? ("AA:BB:CC:00:00:01", null)
                    : ip.EndsWith(".9") ? ("AA:BB:CC:00:00:09", null) : (null, null)),
            });
        vm.Config = new ScanConfig
        {
            PingCount = 1, PingIntervalMs = 0, FileOutput = false, KnownDevicesDb = false,
            EnableInternetPing = false, DatabasePath = Path.Combine(dir, "scanner.db"),
        };
        await vm.RunScanAsync(new[] { "10.0.0" });
        var rogue = vm.Devices.First(d => d.Ip == "10.0.0.9");

        vm.Config = new ScanConfig { DatabasePath = Path.Combine(blocker, "sub", "scanner.db") };
        var failure = await vm.MarkAsKnownAsync(rogue);

        Assert.NotNull(failure);
        Assert.False(rogue.Model.FromDb);
        Assert.True(rogue.IsRogue);
    }

    [Fact]
    public async Task Stop_DuringExportPortProbing_ReturnsWithinOneSecond()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var probing = new TaskCompletionSource();
        var vm = new MainViewModel(OnlyDotOne, () => new NetworkInfo { Ip = "10.0.0.5", Gateway = "10.0.0.1" }, a => a(),
            portScan: async (ip, ct) =>
            {
                probing.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return new List<NetService>();
            });
        vm.Config = new ScanConfig
        {
            PingCount = 1, PingIntervalMs = 0, FileOutput = true, ExportPorts = true, KnownDevicesDb = false,
            EnableInternetPing = false, OutputDirectory = Path.Combine(dir, "Scans"), ScanThreads = 4,
        };

        var scan = vm.RunScanAsync(new[] { "10.0.0" });
        await probing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        vm.Stop();
        await scan.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), watch.Elapsed.ToString());
        Assert.False(vm.IsScanning);
    }

    [Fact]
    public void StoredPortResultsOlderThanTheScan_AreNotReusedForReports()
    {
        var started = new DateTimeOffset(2030, 5, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.False(MainViewModel.IsFreshForReport(started.AddDays(-3), started));
        Assert.True(MainViewModel.IsFreshForReport(started.AddSeconds(5), started));
        Assert.True(MainViewModel.IsFreshForReport(null, started));   // found in this session
    }

    [Fact]
    public async Task CollectPortsForExport_ProbesAgainWhenTheStoredResultIsTooOld()
    {
        var probed = new List<string>();
        var vm = new MainViewModel(OnlyDotOne, () => new NetworkInfo { Ip = "10.0.0.5" }, a => a(),
            portScan: (ip, ct) =>
            {
                lock (probed) probed.Add(ip);
                return Task.FromResult<IReadOnlyList<NetService>>(new[] { new NetService("ssh", 22, NetProtocol.Tcp) });
            });
        var d = new Device("10.0.0.1"); d.RecordPing(new PingResult(true, 1, 64));
        var started = DateTimeOffset.UtcNow;
        IReadOnlyList<NetService>? Known(string ip) =>
            MainViewModel.IsFreshForReport(started.AddDays(-2), started) ? new[] { new NetService("http", 80, NetProtocol.Tcp) } : null;

        var ports = await vm.CollectPortsForExport(new[] { d }, Known, probeMissing: true, CancellationToken.None);

        Assert.Equal(22, ports["10.0.0.1"][0].Port);
        Assert.Equal(new[] { "10.0.0.1" }, probed);
    }

    [Fact]
    public async Task CollectPortsForExport_ReusesKnownServicesAndProbesOnlyWhenAsked()
    {
        var probed = new List<string>();
        var vm = new MainViewModel(OnlyDotOne, () => new NetworkInfo { Ip = "10.0.0.5" }, a => a(),
            portScan: (ip, ct) =>
            {
                lock (probed) probed.Add(ip);
                return Task.FromResult<IReadOnlyList<NetService>>(new[] { new NetService("ssh", 22, NetProtocol.Tcp) });
            });
        var known = new Device("10.0.0.1"); known.RecordPing(new PingResult(true, 1, 64));
        var fresh = new Device("10.0.0.2"); fresh.RecordPing(new PingResult(true, 1, 64));
        var offline = new Device("10.0.0.3"); offline.RecordPing(new PingResult(false, null, null));
        var devices = new[] { known, fresh, offline };
        IReadOnlyList<NetService>? Known(string ip) =>
            ip == "10.0.0.1" ? new[] { new NetService("http", 80, NetProtocol.Tcp) } : null;

        var without = await vm.CollectPortsForExport(devices, Known, probeMissing: false, CancellationToken.None);
        Assert.Equal(new[] { "10.0.0.1" }, without.Keys.ToArray());
        Assert.Empty(probed);

        var with = await vm.CollectPortsForExport(devices, Known, probeMissing: true, CancellationToken.None);
        Assert.Equal(new[] { "10.0.0.1", "10.0.0.2" }, with.Keys.OrderBy(k => k).ToArray());
        Assert.Equal(80, with["10.0.0.1"][0].Port);
        Assert.Equal(new[] { "10.0.0.2" }, probed);
    }

    [Fact]
    public async Task RepeatScan_WithoutAnyTarget_DoesNothing()
    {
        var pings = 0;
        var vm = new MainViewModel((ip, _) => { Interlocked.Increment(ref pings); return new PingResult(false, null, null); },
            () => new NetworkInfo(), a => a());
        vm.Config = new ScanConfig { PingCount = 1, PingIntervalMs = 0, EnableInternetPing = false };

        await vm.RunRepeatScanAsync();

        Assert.Equal(0, pings);
        Assert.False(vm.IsScanning);
        Assert.Empty(vm.Devices);
    }

    [Fact]
    public async Task RepeatScan_UsesTheDetectedNetworkWithoutAsking()
    {
        var vm = new MainViewModel(OnlyDotOne, () => new NetworkInfo { Ip = "10.0.0.5", Gateway = "10.0.0.1" }, a => a());
        vm.Config = new ScanConfig { PingCount = 1, PingIntervalMs = 0, FileOutput = false, KnownDevicesDb = false, EnableInternetPing = false };

        await vm.RunRepeatScanAsync();

        Assert.Contains(vm.Devices, d => d.Ip == "10.0.0.1" && d.IsOnline);
    }

    [Fact]
    public async Task DisablingInternetPing_StopsTheRunningLoop()
    {
        var pings = 0;
        var vm = new MainViewModel((ip, _) => { Interlocked.Increment(ref pings); return new PingResult(true, 1.0, 64); },
            () => new NetworkInfo(), a => a());
        var cfg = new ScanConfig
        {
            EnableInternetPing = true, InternetHosts = new List<string> { "203.0.113.9" }, PingIntervalMs = 0,
        };
        vm.Config = cfg;
        await vm.RestartInternetPingingAsync(ScanConfig.InfinitePingCount);
        var until = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref pings) < 2 && DateTime.UtcNow < until) await Task.Delay(50);
        Assert.True(pings >= 2);

        cfg.EnableInternetPing = false;
        await vm.RestartInternetPingingAsync(ScanConfig.InfinitePingCount);
        var atStop = Volatile.Read(ref pings);
        await Task.Delay(400);

        Assert.Equal(atStop, Volatile.Read(ref pings));
    }

    [Fact]
    public async Task StopAndClear_EndsTheRunningScanAndEmptiesTheRows()
    {
        var vm = new MainViewModel((ip, count) => { Thread.Sleep(20); return OnlyDotOne(ip, count); },
            () => new NetworkInfo { Ip = "10.0.0.5", Gateway = "10.0.0.1" }, a => a());
        vm.Config = new ScanConfig
        {
            PingCount = 1, PingIntervalMs = 0, FileOutput = false, KnownDevicesDb = false,
            EnableInternetPing = false, ScanThreads = 4,
        };
        var scan = vm.RunScanAsync(new[] { "10.0.0" });
        await Task.Delay(100);

        await vm.StopAndClearAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await scan;

        Assert.False(vm.IsScanning);
        Assert.Empty(vm.Devices);
    }

    [Fact]
    public async Task StopAndClear_AlsoRemovesTheOldNetworkCards()
    {
        var vm = new MainViewModel(OnlyDotOne, () => new NetworkInfo { Ip = "10.0.0.5", Gateway = "10.0.0.1" }, a => a());
        vm.Config = new ScanConfig { PingCount = 1, PingIntervalMs = 0, FileOutput = false, KnownDevicesDb = false, EnableInternetPing = false };
        vm.ReconcileNetworks(new[] { "10.0.0" }, new NetworkInfo { Ip = "10.0.0.5", Gateway = "10.0.0.1" });
        Assert.NotEmpty(vm.Networks);

        await vm.StopAndClearAsync();

        Assert.Empty(vm.Networks);
    }

    [Fact]
    public async Task StopAndClear_ResetsCountersProgressAndLastScanWithTheEmptiedTable()
    {
        var vm = new MainViewModel(OnlyDotOne, () => new NetworkInfo { Ip = "10.0.0.5", Gateway = "10.0.0.1" }, a => a());
        vm.Config = new ScanConfig { PingCount = 1, PingIntervalMs = 0, FileOutput = false, KnownDevicesDb = false, EnableInternetPing = false };
        await vm.RunScanAsync(new[] { "10.0.0" });
        Assert.True(vm.HasLastScan);
        Assert.NotEqual("0 / 0", vm.Progress.PingCountText);
        Assert.NotEqual("0 / 0", vm.Progress.DeviceCountText);

        await vm.StopAndClearAsync();

        Assert.Empty(vm.Devices);
        Assert.False(vm.HasLastScan);
        Assert.Equal("", vm.LastScanText);
        Assert.False(vm.ValuesAreStale);
        Assert.Equal("0 / 0", vm.Progress.DeviceCountText);
        Assert.Equal("0 / 0", vm.Progress.PingCountText);
        Assert.Equal(0, vm.Progress.OnlineCount);
    }

    [Fact]
    public async Task ScanStartedWhileStopAndClearWaits_RunsAfterTheClear()
    {
        var slow = true;
        var vm = new MainViewModel((ip, count) => { if (Volatile.Read(ref slow)) Thread.Sleep(20); return OnlyDotOne(ip, count); },
            () => new NetworkInfo { Ip = "10.0.0.5", Gateway = "10.0.0.1" }, a => a());
        vm.Config = new ScanConfig
        {
            PingCount = 1, PingIntervalMs = 0, FileOutput = false, KnownDevicesDb = false,
            EnableInternetPing = false, ScanThreads = 4,
        };
        var first = vm.RunScanAsync(new[] { "10.0.0" });
        await Task.Delay(100);

        var clear = vm.StopAndClearAsync();
        Volatile.Write(ref slow, false);
        var second = vm.RunScanAsync(new[] { "10.0.0" });
        await Task.WhenAll(clear, first, second).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.False(vm.IsScanning);
        Assert.Contains(vm.Devices, d => d.Ip == "10.0.0.1" && d.IsOnline);
    }

    [Fact]
    public async Task ScanStartedBetweenTwoQuickClears_StillRuns()
    {
        var slow = true;
        var slowDispatch = false;
        // While set, applying a clear takes a moment, so the second clear still holds the gate when
        // the queued scan start gets its turn.
        var vm = new MainViewModel((ip, count) => { if (Volatile.Read(ref slow)) Thread.Sleep(20); return OnlyDotOne(ip, count); },
            () => new NetworkInfo { Ip = "10.0.0.5", Gateway = "10.0.0.1" },
            a => { if (Volatile.Read(ref slowDispatch)) Thread.Sleep(200); a(); });
        vm.Config = new ScanConfig
        {
            PingCount = 1, PingIntervalMs = 0, FileOutput = false, KnownDevicesDb = false,
            EnableInternetPing = false, ScanThreads = 4,
        };
        var first = vm.RunScanAsync(new[] { "10.0.0" });
        await Task.Delay(100);

        // Two profile switches around the scan request: the second clear queues on the gate
        // before the scan gets to it.
        Volatile.Write(ref slowDispatch, true);
        var clearOne = vm.StopAndClearAsync();
        var second = vm.RunScanAsync(new[] { "10.0.0" });
        var clearTwo = vm.StopAndClearAsync();
        Volatile.Write(ref slow, false);
        await Task.WhenAll(clearOne, clearTwo, first, second).WaitAsync(TimeSpan.FromSeconds(20));
        Volatile.Write(ref slowDispatch, false);

        Assert.False(vm.IsScanning);
        Assert.Contains(vm.Devices, d => d.Ip == "10.0.0.1" && d.IsOnline);
    }

    [Fact]
    public async Task ScanQueuedBehindAClear_KeepsItsRowsAndCardsWhenTheDispatcherRunsLater()
    {
        // The UI thread applies dispatched work later and in order, so the clear's own wipe must
        // be ordered before the queued scan's rows and cards, never after them.
        var queued = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        using var firstPingRelease = new ManualResetEventSlim();
        var holdFirstScan = true;
        var vm = new MainViewModel((ip, count) =>
            {
                if (Volatile.Read(ref holdFirstScan)) firstPingRelease.Wait(TimeSpan.FromSeconds(10));
                return OnlyDotOne(ip, count);
            },
            () => new NetworkInfo { Ip = "10.0.0.5", Gateway = "10.0.0.1" },
            a => queued.Enqueue(a));
        vm.Config = new ScanConfig
        {
            PingCount = 1, PingIntervalMs = 0, FileOutput = false, KnownDevicesDb = false,
            EnableInternetPing = false, ScanThreads = 4,
        };
        var first = vm.RunScanAsync(new[] { "10.0.0" });
        await Task.Delay(100);

        var clear = vm.StopAndClearAsync();
        var second = vm.RunScanAsync(new[] { "10.0.0" });
        Volatile.Write(ref holdFirstScan, false);
        firstPingRelease.Set();
        await Task.WhenAll(first, clear, second).WaitAsync(TimeSpan.FromSeconds(20));

        while (queued.TryDequeue(out var work)) work();

        Assert.Contains(vm.Devices, d => d.Ip == "10.0.0.1" && d.IsOnline);
        Assert.Contains(vm.Networks, n => n.Prefix == "10.0.0");
    }

    [Fact]
    public async Task StartingASecondScan_DoesNotWaitForTheFirstEnginesShutdown()
    {
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var vm = new MainViewModel(OnlyDotOne, () => new NetworkInfo { Ip = "10.0.0.5", Gateway = "10.0.0.1" }, a => a(),
            enrichers: new Func<string, CancellationToken, Task<(string? mac, string? host)>>[]
            {
                async (ip, ct) =>
                {
                    entered.TrySetResult();
                    await release.Task;   // ignores the token, so the engine shutdown has to wait it out
                    return (null, null);
                },
            });
        vm.Config = new ScanConfig
        {
            PingCount = 1, PingIntervalMs = 0, FileOutput = false, KnownDevicesDb = false,
            EnableInternetPing = false, ScanThreads = 4,
        };
        await vm.RunScanAsync(new[] { "10.0.0" });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await vm.RunScanAsync(new[] { "10.0.0" });
        var elapsed = watch.Elapsed;
        release.SetResult();

        Assert.True(elapsed < TimeSpan.FromMilliseconds(600), $"second start took {elapsed.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public async Task MarkAsKnown_NoMac_DoesNothing()
    {
        var vm = new MainViewModel(
            pingFunc: OnlyDotOne,
            detectNetwork: () => new NetworkInfo { Ip = "10.0.0.5", Gateway = "10.0.0.1" },
            dispatch: a => a());
        vm.Config = new ScanConfig { PingCount = 1, PingIntervalMs = 0, FileOutput = false, KnownDevicesDb = false, EnableInternetPing = false };

        var device = new DeviceViewModel(new Device("10.0.0.30"));   // no MAC
        await vm.MarkAsKnownAsync(device);

        Assert.False(device.Model.FromDb);
    }

    [Fact]
    public async Task RestartInternetPinging_QuicklyTwice_NeverRunsTwoLoopsConcurrently()
    {
        // A quick second edit must wait for the old loop's in-flight ping to finish before
        // the new loop starts, so the old loop's last sample and the new loop's first
        // sample never land at the same time. The old ping is held open by the test (not by
        // a sleep) and the restart's wait bound is raised to its maximum, so no scheduler
        // delay on a loaded machine can end the wait early.
        object gate = new();
        int concurrent = 0, maxConcurrent = 0, calls = 0;
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseFirst = new ManualResetEventSlim(false);
        var vm = new MainViewModel(
            pingFunc: (ip, timeout) =>
            {
                int call = Interlocked.Increment(ref calls);
                lock (gate) { concurrent++; maxConcurrent = Math.Max(maxConcurrent, concurrent); }
                if (call == 1)
                {
                    firstEntered.TrySetResult();
                    releaseFirst.Wait(TimeSpan.FromSeconds(30));
                }
                else secondEntered.TrySetResult();
                lock (gate) { concurrent--; }
                return new PingResult(true, 5.0, 64);
            },
            detectNetwork: () => new NetworkInfo { Ip = "10.0.0.5" },
            dispatch: a => a());
        vm.Config = new ScanConfig
        {
            InternetHosts = new() { "203.0.113.1" }, EnableInternetPing = true,
            PingIntervalMs = 0, InternetTimeoutMs = 10_000,
        };

        try
        {
            await vm.RestartInternetPingingAsync(5);
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));   // first loop's ping is in flight

            var second = vm.RestartInternetPingingAsync(5);
            await Task.Delay(200);     // far below the wait bound: a correct restart is still waiting
            Assert.False(second.IsCompleted, "the restart must wait for the old loop's in-flight ping");
            Assert.Equal(1, Volatile.Read(ref calls));

            releaseFirst.Set();
            await second.WaitAsync(TimeSpan.FromSeconds(30));
            await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));  // new loop has pinged

            Assert.Equal(1, maxConcurrent);
        }
        finally
        {
            releaseFirst.Set();
            vm.Stop();
        }
    }

    [Fact]
    public async Task PingInternet_FillsLatencies()
    {
        var vm = new MainViewModel(
            pingFunc: (ip, _) => new PingResult(true, ip == "1.1.1.1" ? 8.0 : 12.0, 64),
            detectNetwork: () => new NetworkInfo { Ip = "10.0.0.5" },
            dispatch: a => a());
        vm.Config = new ScanConfig { InternetHosts = new() { "1.1.1.1", "8.8.8.8" }, EnableInternetPing = true };

        await vm.PingInternetAsync(CancellationToken.None);

        Assert.Equal(2, vm.InternetHosts.Count);
        Assert.Contains(vm.InternetHosts, h => h.Ip == "1.1.1.1");
    }

    [Fact]
    public async Task RunScan_AddingASubnet_KeepsExistingNetworkCardInstances()
    {
        // Rebuilding Networks on every scan would replace every card and lose its graph
        // history, scroll position and armed delete state even for an unchanged subnet. The
        // diff-based ReconcileNetworks must keep the same instance for a
        // subnet that's still present and only add a new card for the new one.
        var vm = new MainViewModel(
            pingFunc: OnlyDotOne,
            detectNetwork: () => new NetworkInfo { Ip = "10.0.0.5" },
            dispatch: a => a());
        vm.Config = new ScanConfig { PingCount = 1, PingIntervalMs = 0, FileOutput = false, KnownDevicesDb = false, EnableInternetPing = false };

        await vm.RunScanAsync(new[] { "10.0.0", "192.168.1" });
        Assert.Equal(2, vm.Networks.Count);
        var first = vm.Networks.First(n => n.Prefix == "10.0.0");
        var second = vm.Networks.First(n => n.Prefix == "192.168.1");

        await vm.RunScanAsync(new[] { "10.0.0", "192.168.1", "192.168.2" });

        Assert.Equal(3, vm.Networks.Count);
        Assert.Same(first, vm.Networks.First(n => n.Prefix == "10.0.0"));
        Assert.Same(second, vm.Networks.First(n => n.Prefix == "192.168.1"));
        Assert.Contains(vm.Networks, n => n.Prefix == "192.168.2");
    }

    [Fact]
    public async Task RunScan_WiderTarget_KeepsTheDefaultNameOfAnExistingCardAndAddsAllSlices()
    {
        var vm = new MainViewModel(
            pingFunc: OnlyDotOne,
            detectNetwork: () => new NetworkInfo { Ip = "192.168.2.5" },
            dispatch: a => a());
        vm.Config = new ScanConfig { PingCount = 1, PingIntervalMs = 0, FileOutput = false, KnownDevicesDb = false, EnableInternetPing = false };

        await vm.RunScanAsync(new[] { "192.168.2" });
        var card = vm.Networks.Single();
        Assert.Equal(1, card.Index);

        await vm.RunScanAsync(new[] { "192.168.0", "192.168.1", "192.168.2", "192.168.3" });

        Assert.Equal(4, vm.Networks.Count);
        Assert.Same(card, vm.Networks.First(n => n.Prefix == "192.168.2"));
        Assert.Equal(1, card.Index);
        Assert.Equal(new[] { 1, 2, 3, 4 }, vm.Networks.Select(n => n.Index).OrderBy(i => i));
    }

    [Theory]
    [InlineData(new int[0], 1)]
    [InlineData(new[] { 1, 2, 3 }, 4)]
    [InlineData(new[] { 1, 3 }, 2)]
    [InlineData(new[] { 2 }, 1)]
    public void NextFreeIndex_IsTheSmallestUnusedOrdinal(int[] used, int expected)
        => Assert.Equal(expected, NetworkInfoViewModel.NextFreeIndex(used));

    [Fact]
    public async Task NetworkCards_ShowTheRangeThatIsReallyScanned()
    {
        var vm = new MainViewModel(
            pingFunc: OnlyDotOne,
            detectNetwork: () => new NetworkInfo { Ip = "10.0.0.5", SubnetMask = "255.255.255.0" },
            dispatch: a => a());
        vm.Config = new ScanConfig
        {
            PingCount = 1, PingIntervalMs = 0, FileOutput = false, KnownDevicesDb = false, EnableInternetPing = false,
            Subnets = new List<string> { "192.168.5.128/25", "172.16.4.0/23" },
        };
        vm.ManualSubnet = "10.9.8.64/26";

        await vm.RunScanAsync();

        Assert.Equal("10.9.8.64/26", vm.Networks.First(n => n.Prefix == "10.9.8").Cidr);
        Assert.Equal("192.168.5.128/25", vm.Networks.First(n => n.Prefix == "192.168.5").Cidr);
        Assert.Equal("172.16.4.0/23", vm.Networks.First(n => n.Prefix == "172.16.4").Cidr);
        Assert.Equal("172.16.4.0/23", vm.Networks.First(n => n.Prefix == "172.16.5").Cidr);
    }

    [Fact]
    public async Task RefreshDetectedNetwork_FollowsTheMachineToAnotherNetwork()
    {
        var current = new NetworkInfo { Ip = "10.0.0.5", SubnetMask = "255.255.255.0" };
        var vm = new MainViewModel(OnlyDotOne, () => current, a => a());
        vm.SetDetectedNetwork(current);
        Assert.Equal("10.0.0.0/24", vm.DetectedCidr);

        current = new NetworkInfo { Ip = "192.168.7.20", SubnetMask = "255.255.254.0" };
        await vm.RefreshDetectedNetworkAsync();

        Assert.Equal("192.168.6.0/23", vm.DetectedCidr);
    }

    [Fact]
    public void ListsRebuilt_FiresOnRemoveOrReset_NotOnPlainAdd()
    {
        // The sidebar's arm-then-confirm delete state (ArmThenConfirm) holds a
        // reference to a Networks/PinnedEntries/InternetHosts view model - a
        // rebuild that removes or replaces that instance must disarm it, or
        // the confirm button keeps pointing at an orphaned object. A plain Add
        // never orphans anything, so it must not trigger a disarm on every
        // routine append either.
        var vm = new MainViewModel(
            pingFunc: OnlyDotOne,
            detectNetwork: () => new NetworkInfo(),
            dispatch: a => a());
        int fired = 0;
        vm.ListsRebuilt += () => fired++;

        vm.Networks.Add(new NetworkInfoViewModel(1, new NetworkInfo(), "#000000"));
        Assert.Equal(0, fired);
        vm.Networks.Clear();
        Assert.Equal(1, fired);

        vm.PinnedEntries.Add(new PinnedIpViewModel("192.168.0.5", "", ""));
        Assert.Equal(1, fired);
        vm.PinnedEntries.RemoveAt(0);
        Assert.Equal(2, fired);
    }

    [Fact]
    public async Task PingInternet_ReconcilesInsteadOfRebuilding_KeepsHistoryForUnchangedIps()
    {
        var vm = new MainViewModel(
            pingFunc: (ip, _) => new PingResult(true, 8.0, 64),
            detectNetwork: () => new NetworkInfo { Ip = "10.0.0.5" },
            dispatch: a => a());
        vm.Config = new ScanConfig { InternetHosts = new() { "1.1.1.1", "8.8.8.8" }, EnableInternetPing = true };

        await vm.PingInternetAsync(CancellationToken.None);
        var instanceBefore = vm.InternetHosts.First(h => h.Ip == "1.1.1.1");
        Assert.Equal(8.0, instanceBefore.MinRaw);

        // A second "scan" with the same configured hosts (e.g. next scan start,
        // or an unrelated sidebar edit) must reuse the same instance - not
        // reset its accumulated min/max/avg back to empty.
        await vm.PingInternetAsync(CancellationToken.None);
        var instanceAfter = vm.InternetHosts.First(h => h.Ip == "1.1.1.1");
        Assert.Same(instanceBefore, instanceAfter);
        Assert.Equal(2, vm.InternetHosts.Count);

        // Dropping a host from config removes it; a genuinely new one starts fresh.
        vm.Config = new ScanConfig { InternetHosts = new() { "1.1.1.1", "9.9.9.9" }, EnableInternetPing = true };
        await vm.PingInternetAsync(CancellationToken.None);
        Assert.DoesNotContain(vm.InternetHosts, h => h.Ip == "8.8.8.8");
        Assert.Same(instanceBefore, vm.InternetHosts.First(h => h.Ip == "1.1.1.1"));
    }

    [Fact]
    public async Task RunScan_WithFileOutput_WritesReport()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var vm = new MainViewModel(
            pingFunc: OnlyDotOne,
            detectNetwork: () => new NetworkInfo { Ip = "10.0.0.5", Gateway = "10.0.0.1" },
            dispatch: a => a());
        vm.Config = new ScanConfig { PingCount = 1, FileOutput = true, OutputDirectory = dir, KnownDevicesDb = false, EnableInternetPing = false };

        await vm.RunScanAsync(new[] { "10.0.0" });

        Assert.NotNull(vm.LastExportPath);
        Assert.True(File.Exists(vm.LastExportPath));
    }

    [Fact]
    public async Task RunScan_ConfigSubnets_AreScannedInAddition()
    {
        var vm = new MainViewModel(
            pingFunc: OnlyDotOne,
            detectNetwork: () => new NetworkInfo { Ip = "10.0.2.5", Gateway = "10.0.2.1" },
            dispatch: a => a());
        vm.Config = new ScanConfig
        {
            PingCount = 1, PingIntervalMs = 0, FileOutput = false,
            KnownDevicesDb = false, EnableInternetPing = false,
            Subnets = new() { "10.0.1.0/24" },
        };
        vm.ManualSubnet = "10.0.0.0/24";

        await vm.RunScanAsync();

        // Manual subnet replaces auto-detect; configured subnet is scanned too.
        Assert.Contains(vm.Devices, d => d.Ip == "10.0.0.1");
        Assert.Contains(vm.Devices, d => d.Ip == "10.0.1.1");
        Assert.DoesNotContain(vm.Devices, d => d.Ip == "10.0.2.1");
    }

    [Fact]
    public async Task RunScan_UpdatesNetworkCardStats()
    {
        var vm = new MainViewModel(
            pingFunc: (ip, _) => ip.EndsWith(".1") ? new PingResult(true, 2.0, 64) : new PingResult(false, null, null),
            detectNetwork: () => new NetworkInfo { Ip = "10.0.0.5", Gateway = "10.0.0.1" },
            dispatch: a => a());
        vm.Config = new ScanConfig { PingCount = 1, KnownDevicesDb = false, FileOutput = false, EnableInternetPing = false };

        await vm.RunScanAsync(new[] { "10.0.0" });

        Assert.Single(vm.Networks);
        Assert.Equal(1, vm.Networks[0].OnlineCount);
        Assert.Equal(253, vm.Networks[0].OfflineCount);
    }

    [Fact]
    public async Task RunScan_MultipleSlash24Cards_EachCountsOnlyItsOwnPrefix()
    {
        // A mask wider than /24 (e.g. /20) scans several /24-sized cards at once -
        // each card must count only devices in its own prefix, not the whole range.
        // Card 0 is built from the detected NetworkInfo, cards 1+ from a
        // synthetic one, and the primary-card special case must not corrupt keying.
        var vm = new MainViewModel(
            pingFunc: (ip, _) => (ip == "10.0.32.5" || ip == "10.0.34.9")
                ? new PingResult(true, 1.0, 64) : new PingResult(false, null, null),
            detectNetwork: () => new NetworkInfo { Ip = "10.0.32.1", Gateway = "10.0.32.1" },
            dispatch: a => a());
        vm.Config = new ScanConfig { PingCount = 1, PingIntervalMs = 0, FileOutput = false, KnownDevicesDb = false, EnableInternetPing = false };

        await vm.RunScanAsync(new[] { "10.0.32", "10.0.33", "10.0.34" });

        Assert.Equal(3, vm.Networks.Count);
        var card0 = vm.Networks.Single(n => n.Prefix == "10.0.32");
        var card1 = vm.Networks.Single(n => n.Prefix == "10.0.33");
        var card2 = vm.Networks.Single(n => n.Prefix == "10.0.34");
        Assert.Equal(1, card0.OnlineCount);
        Assert.Equal(0, card1.OnlineCount);
        Assert.Equal(1, card2.OnlineCount);
    }

    // Hostnames as pinned entries.
    [Fact]
    public async Task RunScan_PinnedHostname_ResolvesAndPings()
    {
        var vm = new MainViewModel(
            pingFunc: (ip, _) => ip == "10.0.0.50" ? new PingResult(true, 1.0, 64) : new PingResult(false, null, null),
            detectNetwork: () => new NetworkInfo { Ip = "10.0.0.5", Gateway = "10.0.0.1" },
            dispatch: a => a(),
            resolveHostname: host => host == "nas.local"
                ? Task.FromResult(new[] { IPAddress.Parse("10.0.0.50") })
                : throw new Exception("unexpected host"));

        vm.Config = new ScanConfig { PingCount = 1, PingIntervalMs = 0, FileOutput = false, KnownDevicesDb = false, EnableInternetPing = false, PinnedIps = new List<string> { "nas.local" } };
        vm.RebuildPinnedEntries();

        await vm.RunScanAsync(new[] { "10.0.0" });

        Assert.Contains(vm.Devices, d => d.Ip == "10.0.0.50" && d.IsOnline);
        var pinned = Assert.Single(vm.PinnedEntries, p => p.Ip == "nas.local");
        Assert.False(pinned.IsUnresolved);
    }

    [Fact]
    public async Task RunScan_UnresolvablePinnedHostname_DoesNotCrash_AndMarksSidebarEntry()
    {
        var vm = new MainViewModel(
            pingFunc: OnlyDotOne,
            detectNetwork: () => new NetworkInfo { Ip = "10.0.0.5", Gateway = "10.0.0.1" },
            dispatch: a => a(),
            resolveHostname: _ => throw new Exception("simulated DNS failure"));

        vm.Config = new ScanConfig { PingCount = 1, PingIntervalMs = 0, FileOutput = false, KnownDevicesDb = false, EnableInternetPing = false, PinnedIps = new List<string> { "doesnotexist.invalid" } };
        vm.RebuildPinnedEntries();

        await vm.RunScanAsync(new[] { "10.0.0" });

        var pinned = Assert.Single(vm.PinnedEntries, p => p.Ip == "doesnotexist.invalid");
        Assert.True(pinned.IsUnresolved);
    }

    [Fact]
    public async Task Shutdown_WithBothBackgroundLoopsRunning_CompletesWithinTwoSeconds()
    {
        var vm = new MainViewModel(
            pingFunc: (ip, _) => new PingResult(true, 5.0, 64),
            detectNetwork: () => new NetworkInfo { Ip = "10.0.0.5" },
            dispatch: a => a());
        vm.Config = new ScanConfig
        {
            InternetHosts = new() { "203.0.113.1" }, EnableInternetPing = true,
            PingIntervalMs = 0, InternetTimeoutMs = 500,
        };
        vm.RestartInternetPinging(ScanConfig.InfinitePingCount);
        await Task.Delay(200);   // the internet loop is mid-cycle, the hostname loop waits on its minute tick

        var started = System.Diagnostics.Stopwatch.StartNew();
        await vm.Shutdown().WaitAsync(TimeSpan.FromSeconds(5));
        started.Stop();

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2), "Shutdown took " + started.Elapsed);
    }

    [Fact]
    public async Task Shutdown_WaitsForTheRunningScanToFinish_SoNothingIsLeftWhenItReturns()
    {
        var vm = new MainViewModel((ip, count) => { Thread.Sleep(20); return OnlyDotOne(ip, count); },
            () => new NetworkInfo { Ip = "10.0.0.5", Gateway = "10.0.0.1" }, a => a());
        vm.Config = new ScanConfig
        {
            PingCount = 1, PingIntervalMs = 0, FileOutput = false, KnownDevicesDb = false,
            EnableInternetPing = false, ScanThreads = 4,
        };
        var scan = vm.RunScanAsync(new[] { "10.0.0" });
        await Task.Delay(100);
        Assert.True(vm.IsScanning);

        await vm.Shutdown().WaitAsync(TimeSpan.FromSeconds(5));

        // The caller may end the process right after Shutdown returns, so the scan and its tail must be done.
        Assert.False(vm.IsScanning);
        Assert.True(scan.IsCompleted);
    }

    [Fact]
    public async Task NewScan_CancelsTheFullPortScansOfTheRowsItRemoves()
    {
        CancellationToken rowToken = default;
        var scanStarted = new ManualResetEventSlim();
        var vm = new MainViewModel(
            pingFunc: OnlyDotOne,
            detectNetwork: () => new NetworkInfo { Ip = "10.0.0.5", Gateway = "10.0.0.1" },
            dispatch: a => a(),
            fullPortScan: async (ip, progress, ct, onFound) =>
            {
                rowToken = ct;
                scanStarted.Set();
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { }
                return Array.Empty<NetService>();
            });
        vm.Config = new ScanConfig { PingCount = 1, PingIntervalMs = 0, FileOutput = false, KnownDevicesDb = false, EnableInternetPing = false };
        await vm.RunScanAsync(new[] { "10.0.0" });
        var row = vm.Devices.First(d => d.Ip == "10.0.0.1");
        var fullScan = row.StartFullPortScan();
        Assert.True(scanStarted.Wait(5000));
        Assert.False(rowToken.IsCancellationRequested);

        await vm.RunScanAsync(new[] { "10.0.0" });

        Assert.True(rowToken.IsCancellationRequested);
        await fullScan.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(row.IsFullPortScanRunning);
    }

    [Fact]
    public void RefreshSubnetMeta_DropsPendingCardWithoutMeta_AndKeepsOneWithMeta()
    {
        var vm = new MainViewModel(OnlyDotOne, () => new NetworkInfo { Ip = "10.0.0.5", Gateway = "10.0.0.1" }, a => a());
        vm.Config = new ScanConfig { Subnets = new List<string> { "10.51.0.0/24", "10.52.0.0/24" }, KnownDevicesDb = false };
        vm.RefreshSubnetMeta();
        Assert.Contains(vm.Networks, n => n.Prefix == "10.51.0" && n.IsPending);
        Assert.Contains(vm.Networks, n => n.Prefix == "10.52.0" && n.IsPending);

        // The first subnet is deleted again in the sidebar: its placeholder must go, not linger blank.
        vm.Config.Subnets.RemoveAt(0);
        vm.RefreshSubnetMeta();
        Assert.DoesNotContain(vm.Networks, n => n.Prefix == "10.51.0");
        Assert.Contains(vm.Networks, n => n.Prefix == "10.52.0" && n.IsPending);
    }

    [Fact]
    public void RefreshLanguage_RelocalizesTheThreadsLabel()
    {
        try
        {
            var vm = new MainViewModel(OnlyDotOne, () => new NetworkInfo { Ip = "10.0.0.5", Gateway = "10.0.0.1" }, a => a());
            Assert.Equal("", vm.ThreadsText);
            Loc.SetLanguage("ja");
            vm.SetActiveWorkers(7);
            Assert.Equal(Loc.Threads + ": 7", vm.ThreadsText);
            var german = vm.ThreadsText;

            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            Loc.SetLanguage("en");
            vm.RefreshLanguage();
            Assert.Contains("ThreadsText", raised);
            Assert.Equal(Loc.Threads + ": 7", vm.ThreadsText);
            Assert.NotEqual(german, vm.ThreadsText);
        }
        finally { Loc.SetLanguage("en"); }
    }

    [Fact]
    public void RefreshLanguage_RaisesLastScanTextAndRelabelsAutoInterface()
    {
        try
        {
            var vm = new MainViewModel(OnlyDotOne, () => new NetworkInfo { Ip = "10.0.0.5", Gateway = "10.0.0.1" }, a => a());
            vm.RefreshAvailableInterfaces();
            var selected = vm.SelectedInterfaceIp;
            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            Loc.SetLanguage("de");
            vm.RefreshLanguage();
            Assert.Contains("LastScanText", raised);
            Assert.Equal(Loc.NetworkInterfaceAuto, vm.AvailableInterfaces[0].Label);
            var german = vm.AvailableInterfaces[0].Label;

            raised.Clear();
            Loc.SetLanguage("en");
            vm.RefreshLanguage();
            Assert.Contains("LastScanText", raised);
            Assert.Equal(Loc.NetworkInterfaceAuto, vm.AvailableInterfaces[0].Label);
            Assert.NotEqual(german, vm.AvailableInterfaces[0].Label);
            Assert.Equal("", vm.AvailableInterfaces[0].Ip);
            Assert.Equal(selected, vm.SelectedInterfaceIp);
        }
        finally { Loc.SetLanguage("en"); }
    }
}
