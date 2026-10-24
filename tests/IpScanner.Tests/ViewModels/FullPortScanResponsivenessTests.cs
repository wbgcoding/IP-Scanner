using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using IpScanner.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace IpScanner.Tests.ViewModels;

/// <summary>A full 1-65535 sweep must leave the UI thread free: its input-priority queue keeps
/// draining while tens of thousands of probes report back.</summary>
[Collection("PortScannerGate")]
public class FullPortScanResponsivenessTests
{
    private readonly ITestOutputHelper _out;
    public FullPortScanResponsivenessTests(ITestOutputHelper output) => _out = output;

    /// <summary>Longest delay of an input-priority callback while <paramref name="work"/> runs.</summary>
    private static async Task<(double MaxStallMs, double TotalMs)> MeasureStall(Dispatcher d, Func<Task> work)
    {
        double max = 0;
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var t0 = Stopwatch.GetTimestamp();
                try { await d.InvokeAsync(() => { }, DispatcherPriority.Input); }
                catch { return; }
                var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                if (ms > max) max = ms;
                try { await Task.Delay(10, stop.Token); } catch (OperationCanceledException) { return; }
            }
        });
        var sw = Stopwatch.StartNew();
        await work();
        sw.Stop();
        stop.Cancel();
        await sampler;
        return (max, sw.Elapsed.TotalMilliseconds);
    }

    [Fact]
    public async Task FullSweepOfLoopback_KeepsTheUiThreadResponsive()
    {
        using var fast = new FastProbeTimeouts();
        var d = await WpfTestHost.RunAsync(() => Task.FromResult(Dispatcher.CurrentDispatcher));
        var vm = await WpfTestHost.RunAsync(() => Task.FromResult(new DeviceViewModel(new Device("127.0.0.1"))));

        var (maxStall, totalMs) = await MeasureStall(d, () => WpfTestHost.RunAsync(() => vm.StartFullPortScan()));

        _out.WriteLine($"sweep {totalMs:F0} ms, longest UI stall {maxStall:F0} ms, found {vm.Services.Count}");
        Assert.False(vm.IsFullPortScanRunning);
        Assert.True(maxStall < 500, $"UI thread stalled for {maxStall:F0} ms during the sweep");
    }

    /// <summary>A host that answers on every port (tarpit, SYN proxy) must not freeze the list.</summary>
    [Fact]
    public async Task FullSweepThatFindsEveryPort_KeepsTheUiThreadResponsive()
    {
        var d = await WpfTestHost.RunAsync(() => Task.FromResult(Dispatcher.CurrentDispatcher));
        var all = Enumerable.Range(1, PortScanner.MaxPort)
            .Select(p => new NetService("svc" + p, p, NetProtocol.Tcp)).ToList();
        DeviceViewModel? vm = null;
        await WpfTestHost.RunAsync(() =>
        {
            vm = new DeviceViewModel(new Device("192.0.2.9"), fullScan: async (ip, progress, ct, onFound) =>
            {
                await Task.Yield();
                for (int i = 0; i < all.Count; i++)
                {
                    onFound(all[i]);
                    progress.Report(i + 1);
                }
                return all;
            });
            return Task.CompletedTask;
        });

        var (maxStall, totalMs) = await MeasureStall(d, () => WpfTestHost.RunAsync(() => vm!.StartFullPortScan()));

        _out.WriteLine($"tarpit sweep {totalMs:F0} ms, longest UI stall {maxStall:F0} ms, listed {vm!.Services.Count}");
        Assert.Equal(PortScanner.MaxPort, vm.Services.Count);
        Assert.True(maxStall < 500, $"UI thread stalled for {maxStall:F0} ms during the sweep");
    }
}
