using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core.Cli;
using IpScanner.Core.Export;
using IpScanner.Core.Models;
using IpScanner.Core.Net;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Cli;

/// <summary>An IPv6 target gives the same structured result whether the engine is driven like the window drives
/// it or through the command line with -6.</summary>
[Collection("Localization")]
public sealed class Ipv6RouteEquivalenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ips-v6eq-" + Guid.NewGuid().ToString("N"));

    public Ipv6RouteEquivalenceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static readonly HashSet<string> Up = ["::1", "2001:db8::10", "fe80::5%12"];

    private static PingResult FakePing(string ip, int _) =>
        Up.Contains(ip) ? new PingResult(true, 1.0, 64) : new PingResult(false, null, null);

    private static PortResult Port(int port, PortState state, string reason, string? service = null) =>
        new(port, NetProtocol.Tcp, state, reason, service is null ? null : new NetService(service, port, NetProtocol.Tcp));

    private static Task<IReadOnlyList<PortResult>> FakePorts(string ip, PortSpec spec, ScanOptions o, IProgress<int>? _, CancellationToken ct,
        Action<PortResult>? onResult)
    {
        var list = spec.Tcp.Select(p => p switch
        {
            22 => Port(22, PortState.Open, PortReason.SynAck, "SSH"),
            80 => Port(80, PortState.Open, PortReason.SynAck, "HTTP"),
            _ => Port(p, PortState.Closed, PortReason.Rst),
        }).ToList();
        foreach (var r in list) onResult?.Invoke(r);
        return Task.FromResult<IReadOnlyList<PortResult>>(list);
    }

    [Theory]
    [InlineData("::1")]
    [InlineData("2001:db8:0:0:0:0:0:10,fe80::5%12")]
    public async Task WindowRoute_And_CommandLineRoute_AgreeOnTheReport(string targets)
    {
        // Command line route.
        var file = Path.Combine(_dir, "cli.json");
        var services = new CliServices
        {
            Ping = FakePing,
            PortRunner = FakePorts,
            CheckRunner = (_, _, _, _) => Task.FromResult<IReadOnlyList<IpScanner.Core.Scanner.Checks.CheckResult>>([]),
            Network = _ => new NetworkInfo { Ip = "192.168.1.5", SubnetMask = "255.255.255.0", Gateway = "192.168.1.1", Interface = "Test" },
            Cards = () => [],
            Now = () => new DateTime(2026, 1, 2, 3, 4, 5),
            Availability = _ => (true, null),
        };
        var err = new StringWriter();
        int code = CliApp.Run(["-6", "-p", "22,80", "-oJ", file, targets], new StringWriter(), err, default, services, "IP-Scanner");
        Assert.True(code == CliExit.Ok, err.ToString());
        var cli = ReportWriters.ParseJson(File.ReadAllText(file))!;

        // Window route: same expansion, the engine with the same ping, the same port runner, the shared report builder.
        var spec = TargetSpec.Parse(targets, new TargetSpecOptions { HostFamily = IpFamily.V6 });
        using var engine = new ScanEngine(FakePing);
        var cfg = new ScanConfig { PingCount = 1, InitPingCount = 1, OfflineRecheckSeconds = 0, KnownDevicesDb = false, ScanHistory = false, EnableInternetPing = false, PinnedIps = [] };
        await engine.ScanAsync(spec.Expand(false).ToList(), cfg, default);
        Assert.True(PortSpec.TryParse("22,80", out var ports));
        var found = new Dictionary<string, IReadOnlyList<PortResult>>();
        foreach (var d in engine.Devices.Where(d => d.IsOnline))
            found[d.Ip] = (await FakePorts(d.Ip, ports, new ScanOptions(ScanType.Connect, true, false), null, default, null))
                .OrderBy(r => r.Port).ToList();
        var gui = ReportBuilder.Build(engine.Devices.ToList(), new NetworkInfo(), "20260102_030405", null, null,
            new ReportExtras { PortResults = found });

        static string[] Shape(ScanReport r) => r.Hosts.Where(h => h.IsOnline).Select(h =>
            h.Ip + "|" + h.Family + "|" + string.Join(",", h.Ports.Select(p => p.Port + "/" + p.State + "/" + p.Service))).ToArray();
        Assert.NotEmpty(Shape(cli));
        Assert.Equal(Shape(gui), Shape(cli));
        Assert.All(cli.Hosts, h => Assert.Equal("ipv6", h.Family));
    }
}
