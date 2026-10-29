using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core.Cli;
using IpScanner.Core.Export;
using IpScanner.Core.Localization;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using IpScanner.Core.Scanner.Checks;
using IpScanner.ViewModels.Tools;
using Xunit;

namespace IpScanner.Tests.ViewModels;

[Collection("ToolScan")]
public sealed class ScanToolViewModelTests
{
    private static readonly HashSet<string> Up = ["192.168.1.1", "192.168.1.2"];

    private static CliServices Fake(CliPortRunner? ports = null, CliCheckRunner? checks = null) => new()
    {
        Ping = (ip, _) => Up.Contains(ip) ? new PingResult(true, 1.5, 64) : new PingResult(false, null, null),
        NameResolver = (_, _) => Task.FromResult<string?>(null),
        PortRunner = ports ?? ((ip, spec, _, _, _, onResult) =>
        {
            var list = new List<PortResult>();
            foreach (var p in spec.Tcp)
            {
                var r = p == 80
                    ? new PortResult(80, NetProtocol.Tcp, PortState.Open, PortReason.SynAck, new NetService("HTTP", 80, NetProtocol.Tcp))
                    : new PortResult(p, NetProtocol.Tcp, PortState.Closed, PortReason.Rst, null);
                list.Add(r);
                onResult?.Invoke(r);
            }
            return Task.FromResult<IReadOnlyList<PortResult>>(list);
        }),
        CheckRunner = checks ?? ((_, _, _, _) => Task.FromResult<IReadOnlyList<CheckResult>>([])),
        Network = _ => new NetworkInfo { Ip = "192.168.1.5", SubnetMask = "255.255.255.0", Gateway = "192.168.1.1", Interface = "Test" },
        Cards = () => [],
        Now = () => new DateTime(2026, 1, 2, 3, 4, 5),
    };

    private static T Page<T>(string target = "192.168.1.1-3", CliServices? services = null) where T : ScanToolViewModel, new()
    {
        // No dispatcher: a pool thread that once built a WPF object keeps one nobody pumps, and rows posted to it never arrive.
        var vm = new T { Services = services ?? Fake(), OutsideTest = _ => false, UiDispatcherSource = () => null };
        vm.Initialize(new ScanConfig());
        vm.Target = target;
        return vm;
    }

    private static Task<IReadOnlyList<PortResult>> NoPorts() => Task.FromResult<IReadOnlyList<PortResult>>([]);

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("not an address!", false)]
    [InlineData("192.168.1.300", false)]
    [InlineData("10.0.0.0/8", false)]
    [InlineData("192.168.1.1", true)]
    [InlineData("192.168.1.0/24", true)]
    [InlineData("192.168.1.1-3, 192.168.2.5", true)]
    public void Target_IsValidatedInline(string target, bool ok)
    {
        var vm = Page<HostDiscoveryToolViewModel>(target);
        Assert.Equal(ok, vm.TargetOk);
        Assert.Equal(ok, vm.CanStart);
        Assert.False(string.IsNullOrEmpty(vm.TargetMessage));
    }

    [Fact]
    public async Task TargetMessage_HiddenOnFreshPage_ShownAfterEditOrStart()
    {
        var fresh = new HostDiscoveryToolViewModel { Services = Fake(), OutsideTest = _ => false };
        fresh.Initialize(new ScanConfig());
        Assert.False(fresh.ShowTargetMessage);
        Assert.False(fresh.CanStart);

        fresh.Target = "1.2.3.4";
        fresh.Target = "";
        Assert.True(fresh.ShowTargetMessage);
        Assert.Equal(Loc.ScanToolTargetEmpty, fresh.TargetMessage);

        var other = new HostDiscoveryToolViewModel { Services = Fake(), OutsideTest = _ => false };
        other.Initialize(new ScanConfig());
        await other.StartAsync(_ => true);
        Assert.True(other.ShowTargetMessage);
    }

    [Fact]
    public async Task Discovery_ListsLiveHostsAndSkipsPorts()
    {
        int portCalls = 0;
        var vm = Page<HostDiscoveryToolViewModel>(services: Fake((_, _, _, _, _, _) =>
        {
            Interlocked.Increment(ref portCalls);
            return NoPorts();
        }));

        await vm.StartAsync(_ => true);

        Assert.Equal(new[] { "192.168.1.1", "192.168.1.2" }, vm.Rows.Select(r => r.Ip).ToArray());
        Assert.Equal(0, portCalls);
        Assert.False(vm.IsBusy);
        Assert.NotNull(vm.LastReport);
        Assert.True(vm.CanSave);
    }

    [Fact]
    public async Task Services_ListsOpenPortsWithServiceName()
    {
        var vm = Page<ServiceDetectionToolViewModel>();

        await vm.StartAsync(_ => true);

        Assert.Equal(2, vm.Rows.Count);
        Assert.All(vm.Rows, r => Assert.Contains("80", r.C1));
        Assert.All(vm.Rows, r => Assert.Contains("HTTP", r.C2, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Os_ListsEveryLiveHostWithItsOpenPorts()
    {
        var vm = Page<OsEstimateToolViewModel>();

        await vm.StartAsync(_ => true);

        Assert.Equal(2, vm.Rows.Count);
        Assert.All(vm.Rows, r => Assert.Contains("80", r.C3));
    }

    [Fact]
    public async Task Checks_KeepsTheFindingsOfTheCheckRunner()
    {
        var vm = Page<ChecksToolViewModel>(services: Fake(checks: (ip, list, _, _) =>
        {
            IReadOnlyList<CheckResult> results =
                [new CheckResult("test.check", CheckCategory.Safe, list[0], CheckOutcome.Found, [new CheckFinding("banner", "hello")])];
            return Task.FromResult(results);
        }));

        await vm.StartAsync(_ => true);

        Assert.NotEmpty(vm.Rows);
        Assert.Contains(vm.Rows, r => r.Ip == "192.168.1.1");
        Assert.Contains(vm.Rows, r => r.Ip == "192.168.1.2");
    }

    [Fact]
    public async Task Checks_RowsArriveEvenWhenTheStartingThreadOwnsAnUnpumpedDispatcher()
    {
        var vm = Page<ChecksToolViewModel>(services: Fake(checks: (ip, list, _, _) =>
        {
            IReadOnlyList<CheckResult> results =
                [new CheckResult("test.check", CheckCategory.Safe, list[0], CheckOutcome.Found, [new CheckFinding("banner", "hello")])];
            return Task.FromResult(results);
        }));
        // The same state a test thread is left in after any test built a WPF object on it.
        // The dispatcher is held strongly: WPF tracks it only weakly once its thread is gone, so a
        // collection under load would otherwise make Dispatcher.FromThread return null.
        System.Windows.Threading.Dispatcher? unpumped = null;
        var poisoned = new Thread(() => unpumped = System.Windows.Threading.Dispatcher.CurrentDispatcher);
        poisoned.Start();
        poisoned.Join();
        vm.UiDispatcherSource = () => unpumped;
        Assert.NotNull(vm.UiDispatcherSource());

        await vm.StartAsync(_ => true);

        Assert.Empty(vm.Rows);

        vm.UiDispatcherSource = () => null;
        await vm.StartAsync(_ => true);

        Assert.NotEmpty(vm.Rows);
        GC.KeepAlive(unpumped);
    }

    [Fact]
    public void Checks_CannotStartWithoutAnyCheckSelected()
    {
        var vm = Page<ChecksToolViewModel>();
        Assert.True(vm.CanStart);
        vm.CheckSafe = false;
        vm.CheckDiscovery = false;
        Assert.False(vm.CanStart);
        vm.CheckIntrusive = true;
        Assert.True(vm.CanStart);
    }

    [Fact]
    public async Task Gate_DeclinedStartScansNothing()
    {
        int portCalls = 0;
        var vm = Page<ServiceDetectionToolViewModel>(services: Fake((_, _, _, _, _, _) =>
        {
            Interlocked.Increment(ref portCalls);
            return NoPorts();
        }));
        vm.Scope = ScanToolViewModel.ScopeAll;
        ScanToolGate? asked = null;

        await vm.StartAsync(g => { asked = g; return false; });

        Assert.NotNull(asked);
        Assert.True(asked!.FullRange);
        Assert.Equal(0, portCalls);
        Assert.Empty(vm.Rows);
        Assert.Null(vm.LastReport);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Gate_CarefulDefaultsNeverAsk()
    {
        var vm = Page<ServiceDetectionToolViewModel>();
        bool asked = false;

        await vm.StartAsync(_ => { asked = true; return true; });

        Assert.False(asked);
        Assert.NotNull(vm.LastReport);
    }

    [Fact]
    public async Task Gate_LargeTargetListAsks()
    {
        var vm = Page<HostDiscoveryToolViewModel>("192.168.0.0/21");
        ScanToolGate? asked = null;

        await vm.StartAsync(g => { asked = g; return false; });

        Assert.True(asked!.Large);
        Assert.True(asked.Count >= 1024);
    }

    [Fact]
    public async Task Gate_OutsideTheSubnetAsksAndDropsIntrusiveChecks()
    {
        CheckSelection? seen = null;
        var vm = Page<ChecksToolViewModel>(services: Fake(checks: (_, _, sel, _) =>
        {
            seen = sel;
            return Task.FromResult<IReadOnlyList<CheckResult>>([]);
        }));
        vm.OutsideTest = _ => true;
        vm.CheckIntrusive = true;
        ScanToolGate? asked = null;

        await vm.StartAsync(g => { asked = g; return true; });

        Assert.True(asked!.Outside);
        Assert.True(asked.Intrusive);
        Assert.NotNull(seen);
        Assert.False(seen!.Value.Intrusive);
    }

    [Fact]
    public async Task Gate_InsideTheSubnetKeepsIntrusiveChecksAfterConfirmation()
    {
        CheckSelection? seen = null;
        var vm = Page<ChecksToolViewModel>(services: Fake(checks: (_, _, sel, _) =>
        {
            seen = sel;
            return Task.FromResult<IReadOnlyList<CheckResult>>([]);
        }));
        vm.CheckIntrusive = true;

        await vm.StartAsync(_ => true);

        Assert.True(seen!.Value.Intrusive);
    }

    [Fact]
    public async Task Cancel_StopsTheRunAndKeepsAPartialReport()
    {
        var entered = new SemaphoreSlim(0);
        var vm = Page<ServiceDetectionToolViewModel>(services: Fake(async (_, _, _, _, ct, _) =>
        {
            entered.Release();
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        }));

        var run = vm.StartAsync(_ => true);
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.True(vm.IsBusy);
        vm.Stop();
        await run.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.False(vm.IsBusy);
        Assert.True(vm.LastReport!.Meta.Partial);
    }

    [Fact]
    public async Task Start_WhileAnotherScanRunsDoesNothing()
    {
        var vm = Page<HostDiscoveryToolViewModel>();
        using (var held = ToolScan.TryEnter())
        {
            Assert.NotNull(held);
            await vm.StartAsync(_ => true);
        }

        Assert.Empty(vm.Rows);
        Assert.Null(vm.LastReport);
        Assert.False(vm.IsBusy);
        await vm.StartAsync(_ => true);
        Assert.Equal(2, vm.Rows.Count);
    }

    [Fact]
    public async Task Save_WritesEveryReportFormat()
    {
        var vm = Page<ServiceDetectionToolViewModel>();
        await vm.StartAsync(_ => true);
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            foreach (var format in ReportFormats.All)
            {
                var path = Path.Combine(dir, "report." + ReportFormats.Extension(format));
                Assert.True(await vm.SaveAsync(path, format), format.ToString());
                Assert.Contains("192.168.1.1", await File.ReadAllTextAsync(path));
            }
            Assert.Equal(ReportFormats.All.Count, Directory.GetFiles(dir).Length);
            Assert.Equal(4, ReportFormats.All.Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Save_FailureIsReportedNotThrown()
    {
        var vm = Page<HostDiscoveryToolViewModel>();
        await vm.StartAsync(_ => true);
        var dir = Directory.CreateTempSubdirectory().FullName;
        try { Assert.False(await vm.SaveAsync(dir, ReportFormat.Text)); }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Copy_IsNullBeforeTheFirstRunAndTextAfterwards()
    {
        var vm = Page<HostDiscoveryToolViewModel>();
        Assert.Null(vm.CopyText());
        await vm.StartAsync(_ => true);
        Assert.Contains("192.168.1.2", vm.CopyText());
    }

    [Fact]
    public async Task Export_RunsTheChosenPresetAndWritesTheFile()
    {
        var vm = Page<ScanExportToolViewModel>();
        Assert.True(vm.Presets.Count >= 4);
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            vm.PresetIndex = Math.Max(0, vm.Presets.ToList().FindIndex(p => p.Plan.Ports == ScanPlan.PortsFast));
            vm.Format = ReportFormat.Json;
            vm.SavePath = Path.Combine(dir, "scan.json");

            await vm.StartAsync(_ => true);

            Assert.True(File.Exists(vm.SavePath));
            Assert.Contains("192.168.1.1", await File.ReadAllTextAsync(vm.SavePath));
            Assert.Equal(2, vm.Rows.Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Export_FullRangePresetAsksBeforeScanning()
    {
        var vm = Page<ScanExportToolViewModel>();
        int full = vm.Presets.ToList().FindIndex(p => !p.Plan.PingOnly && p.Plan.ResolvePorts().Tcp.Count > 60000);
        Assert.True(full >= 0);
        vm.PresetIndex = full;
        ScanToolGate? asked = null;

        await vm.StartAsync(g => { asked = g; return false; });

        Assert.True(asked!.FullRange);
        Assert.Null(vm.LastReport);
    }

    [Fact]
    public void Marks_FollowTheAggressiveOptions()
    {
        var vm = Page<ChecksToolViewModel>();
        Assert.False(vm.WarnTiming);
        Assert.False(vm.WarnRange);
        Assert.False(vm.WarnIntrusive);
        vm.Timing = 4;
        vm.Scope = ScanToolViewModel.ScopeAll;
        vm.CheckIntrusive = true;
        Assert.True(vm.WarnTiming);
        Assert.True(vm.WarnRange);
        Assert.True(vm.WarnIntrusive);
    }

    [Fact]
    public void Defaults_AreCarefulEverywhere()
    {
        var config = new ScanConfig();
        Assert.Equal(3, config.PortScanTiming);
        Assert.Equal("", config.ScanPortRange);
        Assert.True(config.PortServiceDetection);
        Assert.False(config.ExportPorts);
        Assert.False(config.OsFingerprint);

        var pages = new ScanToolViewModel[]
        {
            Page<HostDiscoveryToolViewModel>(), Page<ServiceDetectionToolViewModel>(), Page<OsEstimateToolViewModel>(),
            Page<ChecksToolViewModel>(), Page<ScanExportToolViewModel>(),
        };
        foreach (var vm in pages)
        {
            Assert.Equal(3, vm.Timing);
            Assert.False(vm.SkipDiscovery);
            Assert.Equal(ScanToolViewModel.ScopeCommon, vm.Scope);
            Assert.True(vm.CheckSafe);
            Assert.False(vm.CheckIntrusive);
            Assert.False(vm.WarnTiming);
            Assert.False(vm.WarnRange);
            Assert.False(vm.WarnIntrusive);
        }

        var port = new PortCheckToolViewModel();
        Assert.False(ScanTypes.IsAggressive(port.SelectedScanType.Type));
        Assert.Equal(ScanType.Connect, port.SelectedScanType.Type);
        Assert.True(port.CheckSafe);
        Assert.False(port.CheckIntrusive);
    }

    [Fact]
    public void ScanToolRow_ToString_IsAddressAndFirstCell()
    {
        var row = new ScanToolRow("192.168.1.5", "up", "2 ms", "x");
        Assert.Equal("192.168.1.5 up", row.ToString());
        Assert.DoesNotContain("ScanToolRow {", row.ToString());
        Assert.Equal("192.168.1.6", new ScanToolRow("192.168.1.6", "", "", "").ToString());
    }
}
