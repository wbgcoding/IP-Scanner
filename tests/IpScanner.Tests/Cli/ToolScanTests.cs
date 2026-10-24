using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core.Cli;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using IpScanner.Core.Scanner.Checks;
using Xunit;

namespace IpScanner.Tests.Cli;

[Collection("ToolScan")]
public sealed class ToolScanTests
{
    private static readonly HashSet<string> Up = ["192.168.1.1", "192.168.1.2"];

    private static CliServices Fake(CliPortRunner? ports = null, CliCheckRunner? checks = null) => new()
    {
        Ping = (ip, _) => Up.Contains(ip) ? new PingResult(true, 1.0, 64) : new PingResult(false, null, null),
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

    private static ToolScanRequest Request(bool pingOnly = false, CheckSelection? checks = null) => new()
    {
        Targets = ["192.168.1.1", "192.168.1.2", "192.168.1.3"],
        TargetsText = "192.168.1.1-3",
        Command = "test",
        Plan = new ScanPlan(3, false, pingOnly, "", false, false),
        Ports = new PortSpec([80, 81], []),
        Options = new ScanOptions(ScanType.Connect, true, false),
        Checks = checks,
    };

    [Fact]
    public async Task Run_AnnouncesEachLiveHostOnceAndReportsPorts()
    {
        var hosts = new ConcurrentBag<string>();
        var ports = new ConcurrentBag<string>();
        var events = new ToolScanEvents { HostFound = d => hosts.Add(d.Ip), PortFound = (ip, r) => ports.Add(ip + ":" + r.Port) };

        var outcome = await ToolScan.RunAsync(Request(), Fake(), events, CancellationToken.None);

        Assert.Equal(new[] { "192.168.1.1", "192.168.1.2" }, hosts.OrderBy(h => h).ToArray());
        Assert.Equal(4, ports.Count);
        Assert.False(outcome.Cancelled);
        Assert.Equal(2, outcome.Report.Hosts.Count(h => h.Status == "ONLINE"));
    }

    [Fact]
    public async Task Run_PingOnlySkipsThePortScan()
    {
        int calls = 0;
        var services = Fake((_, _, _, _, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<IReadOnlyList<PortResult>>([]);
        });

        await ToolScan.RunAsync(Request(pingOnly: true), services, new ToolScanEvents(), CancellationToken.None);

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Run_CancelledScanReturnsAPartialReport()
    {
        using var cts = new CancellationTokenSource();
        var services = Fake((_, _, _, _, ct, _) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<PortResult>>([]);
        });

        var outcome = await ToolScan.RunAsync(Request(), services, new ToolScanEvents(), cts.Token);

        Assert.True(outcome.Cancelled);
        Assert.True(outcome.Report.Meta.Partial);
    }

    [Fact]
    public async Task Run_RunsChecksOnlyWhenSelectedAndFeedsOpenServices()
    {
        var seen = new ConcurrentBag<string>();
        var services = Fake(checks: (ip, list, _, _) =>
        {
            seen.Add(ip + ":" + string.Join(',', list.Select(v => v.Port)));
            return Task.FromResult<IReadOnlyList<CheckResult>>([]);
        });

        await ToolScan.RunAsync(Request(), services, new ToolScanEvents(), CancellationToken.None);
        Assert.Empty(seen);

        await ToolScan.RunAsync(Request(checks: CheckSelection.Default), services, new ToolScanEvents(), CancellationToken.None);
        Assert.Contains("192.168.1.1:80", seen);
    }

    [Fact]
    public async Task Run_PortFailureBecomesAWarningNotACrash()
    {
        var services = Fake((_, _, _, _, _, _) => throw new InvalidOperationException("boom"));
        var outcome = await ToolScan.RunAsync(Request(), services, new ToolScanEvents(), CancellationToken.None);
        Assert.Contains(outcome.Warnings, w => w.Contains("boom"));
    }

    [Fact]
    public void TryEnter_AllowsOneScanAtATime()
    {
        using var first = ToolScan.TryEnter();
        Assert.NotNull(first);
        Assert.Null(ToolScan.TryEnter());
        first!.Dispose();
        using var again = ToolScan.TryEnter();
        Assert.NotNull(again);
    }
}
