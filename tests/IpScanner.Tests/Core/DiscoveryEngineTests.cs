using System.Linq;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core.Models;
using IpScanner.Core.Net;
using IpScanner.Core.Scanner;
using IpScanner.Core.Scanner.Discovery;
using Xunit;

namespace IpScanner.Tests.Core;

[Collection("PacketDriverState")]
public class DiscoveryEngineTests
{
    private static ScanConfig Cfg(string modes) => new()
    {
        PingCount = 1, InitPingCount = 1, PingIntervalMs = 0, ScanThreads = 8, DiscoveryModes = modes,
    };

    private static System.Collections.Generic.IEnumerable<string> Targets()
        => TargetSpec.FromPrefixes(new[] { "10.0.0" }, new[] { Ipv4.HostRange("10.0.0.0", 30)!.Value }).Expand();

    [Fact]
    public async Task IcmpBlockedHost_IsFoundByTcpPing_AndKeepsTheMethod()
    {
        var net = new FakeDiscoveryNet { Tcp = (ip, port) => ip == "10.0.0.2" && port == 445 ? TcpOutcome.Open : TcpOutcome.NoAnswer };
        var engine = new ScanEngine((_, _) => new PingResult(false, null, null)) { Net = net };

        await engine.ScanAsync(Targets(), Cfg("icmp,tcp"), CancellationToken.None);

        var up = Assert.Single(engine.Devices, d => d.IsOnline);
        Assert.Equal("10.0.0.2", up.Ip);
        Assert.Equal("tcp:445", up.DiscoveryMethod);
    }

    [Fact]
    public async Task ListScan_SendsNoPacket_AndNamesTheTargets()
    {
        var net = new FakeDiscoveryNet();
        int pings = 0;
        var engine = new ScanEngine((_, _) => { Interlocked.Increment(ref pings); return new PingResult(true, 1, 64); })
        {
            Net = net,
            NameResolver = (ip, _) => Task.FromResult<string?>("host-" + ip.Replace('.', '-')),
        };

        await engine.ScanAsync(Targets(), Cfg("list"), CancellationToken.None);

        Assert.Equal(0, net.Calls);
        Assert.Equal(0, pings);
        Assert.NotEmpty(engine.Devices);
        Assert.All(engine.Devices, d =>
        {
            Assert.Equal("list", d.DiscoveryMethod);
            Assert.Equal("host-" + d.Ip.Replace('.', '-'), d.Hostname);
            Assert.False(d.IsOnline);
            Assert.True(d.IsListed && d.Seen);
        });
    }

    [Fact]
    public async Task ListScan_ResolvesNamesWithoutBlockingWorkerThreads()
    {
        const int count = 48;
        int active = 0, peak = 0;
        var engine = new ScanEngine((_, _) => new PingResult(false, null, null))
        {
            Net = new FakeDiscoveryNet(),
            NameResolver = async (ip, ct) =>
            {
                int now = Interlocked.Increment(ref active);
                int seen;
                while ((seen = Volatile.Read(ref peak)) < now && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
                await Task.Delay(400, ct);
                Interlocked.Decrement(ref active);
                return "h-" + ip;
            },
        };
        var ips = Enumerable.Range(1, count).Select(i => "10.1.0." + i).ToList();
        var cfg = Cfg("list");
        cfg.ScanThreads = count;

        await engine.ScanAsync(ips, cfg, CancellationToken.None);

        Assert.Equal(count, peak);
        Assert.All(engine.Devices, d => Assert.StartsWith("h-", d.Hostname));
    }

    [Fact]
    public async Task SkipNextToLiveModes_CountsMissedHostsAsUpAtDiscoveryOnly()
    {
        var net = new FakeDiscoveryNet { Tcp = (ip, port) => ip == "10.0.0.2" && port == 445 ? TcpOutcome.Open : TcpOutcome.NoAnswer };
        var engine = new ScanEngine((_, _) => new PingResult(false, null, null)) { Net = net };

        await engine.ScanAsync(Targets(), Cfg("icmp,tcp,skip"), CancellationToken.None);

        Assert.NotEmpty(engine.Devices);
        Assert.All(engine.Devices, d => Assert.True(d.Seen, d.Ip));
        Assert.Equal("tcp:445", engine.Devices.Single(d => d.Ip == "10.0.0.2").DiscoveryMethod);
        Assert.All(engine.Devices.Where(d => d.Ip != "10.0.0.2"), d => Assert.Equal("skip", d.DiscoveryMethod));
    }

    [Fact]
    public async Task SkipDiscovery_TreatsEveryTargetAsUp_WithoutProbing()
    {
        var net = new FakeDiscoveryNet();
        var seen = new ConcurrentBag<string>();
        var engine = new ScanEngine((ip, _) => { seen.Add(ip); return new PingResult(false, null, null); }) { Net = net };
        var cfg = Cfg("skip");
        cfg.PingCount = 1;

        await engine.ScanAsync(Targets(), cfg, CancellationToken.None);

        Assert.Equal(0, net.Calls);
        Assert.NotEmpty(engine.Devices);
        Assert.All(engine.Devices, d => { Assert.True(d.Seen); Assert.Equal("skip", d.DiscoveryMethod); });
    }

    [Fact]
    public async Task DefaultModes_KeepThePlainIcmpPath()
    {
        var net = new FakeDiscoveryNet();
        var engine = new ScanEngine((ip, _) => ip == "10.0.0.1" ? new PingResult(true, 2, 128) : new PingResult(false, null, null)) { Net = net };

        await engine.ScanAsync(Targets(), Cfg("icmp"), CancellationToken.None);

        Assert.Equal(0, net.Calls);
        var up = Assert.Single(engine.Devices, d => d.IsOnline);
        Assert.Equal("icmp", up.DiscoveryMethod);
    }

    [Fact]
    public void Display_NamesTheMethodAndPort()
    {
        Assert.Equal("TCP 445", DiscoveryModes.Display("tcp:445"));
        Assert.Equal("ICMP", DiscoveryModes.Display("icmp"));
        Assert.Equal("SYN 22", DiscoveryModes.Display("tcp-syn:22"));
        Assert.Equal("", DiscoveryModes.Display(null));
    }

    [Fact]
    public void Exports_CarryTheDiscoveryMethod()
    {
        var up = new Device("10.0.0.5") { DiscoveryMethod = "tcp:445" };
        up.RecordPing(new PingResult(true, 1, 64, "tcp:445"));
        var listed = new Device("10.0.0.6");
        listed.MarkListed();
        var dir = System.IO.Directory.CreateTempSubdirectory().FullName;

        var csv = System.IO.File.ReadAllText(IpScanner.Core.Export.CsvExporter.Write(new[] { up, listed }, dir, "20260601_120000", "x"));
        var json = System.IO.File.ReadAllText(IpScanner.Core.Export.JsonExporter.Write(new[] { up, listed }, new IpScanner.Core.Models.NetworkInfo(), dir, "20260601_120000", "x"));

        Assert.Contains("discovery_method", csv);
        Assert.Contains("tcp:445", csv);
        Assert.Contains("LISTED", csv);
        Assert.Contains("\"discovery\": \"tcp:445\"", json);
        Assert.Contains("LISTED", json);
    }
}
