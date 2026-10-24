using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using IpScanner.Core.Scanner.Discovery;
using Xunit;

namespace IpScanner.Tests.Core;

/// <summary>Counts every packet-level call so tests can prove what was (not) sent.</summary>
internal sealed class FakeDiscoveryNet : IDiscoveryNet
{
    public int Calls;
    public readonly ConcurrentBag<string> Log = new();
    public Func<string, int, TcpOutcome> Tcp = (_, _) => TcpOutcome.NoAnswer;
    public Func<string, int, bool> Udp = (_, _) => false;
    public Func<string, byte, bool> Icmp = (_, _) => false;
    public Func<string, string?> Arp = _ => null;
    public Func<string, bool> OnLink = _ => true;
    public Func<string, string?, IRawLink?> Link = (_, _) => null;
    public RawRoute? Route = new("\\Device\\NPF_{TEST}", new byte[] { 2, 0, 0, 0, 0, 1 }, IPAddress.Parse("10.0.0.2"), new byte[] { 2, 0, 0, 0, 0, 9 });

    private void Hit(string what) { Interlocked.Increment(ref Calls); Log.Add(what); }

    public Task<TcpOutcome> TcpConnectAsync(string ip, int port, int timeoutMs, CancellationToken ct)
    { Hit($"tcp {ip}:{port}"); return Task.FromResult(Tcp(ip, port)); }

    public Task<bool> UdpExchangeAsync(string ip, int port, byte[] payload, int timeoutMs, CancellationToken ct)
    { Hit($"udp {ip}:{port}"); return Task.FromResult(Udp(ip, port)); }

    public Task<bool> IcmpRawAsync(string ip, byte type, int timeoutMs, CancellationToken ct)
    { Hit($"icmp{type} {ip}"); return Task.FromResult(Icmp(ip, type)); }

    public string? ArpResolve(string ip) { Hit($"arp {ip}"); return Arp(ip); }
    public bool IsOnLink(string ip) => OnLink(ip);
    public IRawLink? OpenLink(string deviceName, string? bpf) { Hit("link"); return Link(deviceName, bpf); }
    public RawRoute? RouteTo(IPAddress target) => Route;
}

/// <summary>Answers every TCP frame sent to it: SYN/ACK or RST, depending on the configured open ports.</summary>
internal sealed class FakeTcpLink : IRawLink
{
    private readonly RawRoute _route;
    private readonly Func<int, TcpFlags?> _reply;
    private readonly Queue<byte[]> _inbox = new();
    public int Sent;

    public FakeTcpLink(RawRoute route, Func<int, TcpFlags?> reply) { _route = route; _reply = reply; }

    public bool Send(byte[] frame)
    {
        Sent++;
        var p = RawPackets.Parse(frame);
        if (p is not { Kind: PacketKind.Tcp }) return true;
        var flags = _reply(p.DstPort);
        if (flags is null) return true;
        _inbox.Enqueue(RawPackets.BuildTcp(_route.NextHopMac, _route.SrcMac, p.DstIp!, p.SrcIp!, p.DstPort, p.SrcPort, 1000, p.Seq + 1,
            flags.Value, 8192, null, 128));
        return true;
    }

    public byte[]? Receive(int timeoutMs)
    {
        if (_inbox.Count > 0) return _inbox.Dequeue();
        Thread.Sleep(Math.Min(timeoutMs, 5));
        return null;
    }

    public void Dispose() { }
}

[Collection("PacketDriverState")]
public class DiscoveryTests
{
    private const string Host = "10.0.0.7";
    private static readonly PingResult NoPing = new(false, null, null);

    [Fact]
    public void Parse_DefaultsToIcmp_AndKeepsOrderAndDropsUnknown()
    {
        Assert.Equal(new[] { DiscoveryMode.IcmpEcho }, DiscoveryModes.Parse(null));
        Assert.Equal(new[] { DiscoveryMode.IcmpEcho }, DiscoveryModes.Parse("nonsense"));
        Assert.Equal(new[] { DiscoveryMode.Arp, DiscoveryMode.IcmpEcho, DiscoveryMode.TcpConnect }, DiscoveryModes.Parse("tcp, icmp,bogus,arp"));
        Assert.Equal("arp,icmp,tcp", DiscoveryModes.Format(DiscoveryModes.Parse("tcp,icmp,arp")));
    }

    [Fact]
    public void Parse_ListScanExcludesEveryOtherMode()
        => Assert.Equal(new[] { DiscoveryMode.ListScan }, DiscoveryModes.Parse("icmp,list,tcp"));

    [Fact]
    public void ParsePorts_ValidatesRangeDuplicatesAndLimit()
    {
        Assert.Equal(new[] { 80, 443 }, DiscoveryModes.ParsePorts("80, 443;80,0,70000,x", "1"));
        Assert.Equal(new[] { 22 }, DiscoveryModes.ParsePorts("", "22"));
        Assert.Equal(DiscoveryModes.MaxPorts, DiscoveryModes.ParsePorts(string.Join(",", Enumerable.Range(1, 40)), "1").Length);
    }

    [Fact]
    public async Task Arp_FoundWhenOnLinkAndAnswered_NotWhenOffLink()
    {
        var net = new FakeDiscoveryNet { Arp = _ => "AA:BB:CC:DD:EE:FF" };
        var r = await new ArpProbe(net).ProbeAsync(Host, default);
        Assert.True(r.Found);
        Assert.Equal("arp", r.Method);

        net.OnLink = _ => false;
        int before = net.Calls;
        Assert.False((await new ArpProbe(net).ProbeAsync(Host, default)).Found);
        Assert.Equal(before, net.Calls);
    }

    [Fact]
    public async Task IcmpEcho_UsesTheInjectedPing()
    {
        var probe = new IcmpEchoProbe((_, _) => new PingResult(true, 3.5, 64), 500);
        var r = await probe.ProbeAsync(Host, default);
        Assert.True(r.Found);
        Assert.Equal("icmp", r.Method);
        Assert.Equal(3.5, r.RttMs);
        Assert.Equal(64, r.Ttl);
        Assert.False((await new IcmpEchoProbe((_, _) => NoPing, 500).ProbeAsync(Host, default)).Found);
    }

    [Theory]
    [InlineData(DiscoveryMode.IcmpTimestamp, 13, "icmp-ts")]
    [InlineData(DiscoveryMode.IcmpMask, 17, "icmp-mask")]
    public async Task IcmpRaw_SendsTheRightTypeAndReportsItsMethod(DiscoveryMode mode, int type, string method)
    {
        var net = new FakeDiscoveryNet { Icmp = (_, t) => t == type };
        var r = await new IcmpRawProbe(net, mode, 500).ProbeAsync(Host, default);
        Assert.True(r.Found);
        Assert.Equal(method, r.Method);

        var silent = new FakeDiscoveryNet();
        Assert.False((await new IcmpRawProbe(silent, mode, 500).ProbeAsync(Host, default)).Found);
    }

    [Fact]
    public async Task TcpConnect_OpenPortFindsHost_AndNamesThePort()
    {
        var net = new FakeDiscoveryNet { Tcp = (_, p) => p == 445 ? TcpOutcome.Open : TcpOutcome.NoAnswer };
        var r = await new TcpConnectProbe(net, new[] { 80, 443, 22, 445 }, 500).ProbeAsync(Host, default);
        Assert.True(r.Found);
        Assert.Equal("tcp:445", r.Method);
    }

    [Fact]
    public async Task TcpConnect_RefusedAlsoProvesTheHostIsUp()
    {
        var net = new FakeDiscoveryNet { Tcp = (_, p) => p == 22 ? TcpOutcome.Refused : TcpOutcome.NoAnswer };
        var r = await new TcpConnectProbe(net, new[] { 80, 22 }, 500).ProbeAsync(Host, default);
        Assert.True(r.Found);
        Assert.Equal("tcp:22", r.Method);
    }

    [Fact]
    public async Task TcpConnect_NoAnswerOnAnyPort_IsNotFound()
    {
        var net = new FakeDiscoveryNet();
        var r = await new TcpConnectProbe(net, new[] { 80, 443 }, 500).ProbeAsync(Host, default);
        Assert.False(r.Found);
        Assert.Equal(2, net.Calls);
    }

    [Fact]
    public async Task Udp_AnswerOnAConfiguredPortFindsTheHost()
    {
        var net = new FakeDiscoveryNet { Udp = (_, p) => p == 161 };
        var r = await new UdpPingProbe(net, new[] { 53, 161, 137 }, 500).ProbeAsync(Host, default);
        Assert.True(r.Found);
        Assert.Equal("udp:161", r.Method);
    }

    [Fact]
    public void UdpPayloads_AreProtocolSpecific()
    {
        Assert.NotEmpty(DiscoveryPayloads.For(53));
        Assert.NotEmpty(DiscoveryPayloads.For(161));
        Assert.NotEmpty(DiscoveryPayloads.For(137));
        Assert.Empty(DiscoveryPayloads.For(9999));
    }

    [Theory]
    [InlineData(DiscoveryMode.TcpSyn, TcpFlags.Syn | TcpFlags.Ack, "tcp-syn:443")]
    [InlineData(DiscoveryMode.TcpSyn, TcpFlags.Rst | TcpFlags.Ack, "tcp-syn:443")]
    [InlineData(DiscoveryMode.TcpAck, TcpFlags.Rst, "tcp-ack:443")]
    public async Task RawTcpPing_AnswerFromTheTargetFindsIt(DiscoveryMode mode, TcpFlags reply, string method)
    {
        var net = new FakeDiscoveryNet();
        FakeTcpLink? link = null;
        net.Link = (_, _) => link = new FakeTcpLink(net.Route!, port => port == 443 ? reply : null);
        var r = await new RawTcpPingProbe(net, mode, new[] { 80, 443 }, 800).ProbeAsync(Host, default);
        Assert.True(r.Found);
        Assert.Equal(method, r.Method);
        Assert.Equal(128, r.Ttl);
        Assert.Equal(2, link!.Sent);
    }

    [Fact]
    public async Task RawTcpPing_SilenceMeansNotFound_AndNoLinkMeansNotFound()
    {
        var net = new FakeDiscoveryNet();
        net.Link = (_, _) => new FakeTcpLink(net.Route!, _ => null);
        Assert.False((await new RawTcpPingProbe(net, DiscoveryMode.TcpSyn, new[] { 80 }, 200).ProbeAsync(Host, default)).Found);

        var noLink = new FakeDiscoveryNet();
        Assert.False((await new RawTcpPingProbe(noLink, DiscoveryMode.TcpAck, new[] { 80 }, 200).ProbeAsync(Host, default)).Found);
    }

    [Fact]
    public async Task ListScan_SendsNoPacket()
    {
        var net = new FakeDiscoveryNet();
        int pings = 0;
        var runner = DiscoveryRunner.Create(DiscoveryModes.Parse("list"), "80", "53", net, (_, _) => { pings++; return NoPing; }, 500);
        Assert.True(runner.ListScan);
        Assert.Empty(runner.Live);
        var r = await runner.ProbeAsync(Host, null, default);
        Assert.False(r.Found);
        Assert.Equal(0, net.Calls);
        Assert.Equal(0, pings);
    }

    [Fact]
    public async Task SkipDiscovery_TreatsTheTargetAsUpWithoutSending()
    {
        var net = new FakeDiscoveryNet();
        var runner = DiscoveryRunner.Create(new[] { DiscoveryMode.SkipDiscovery }, "80", "53", net, (_, _) => NoPing, 500);
        var r = await runner.ProbeAsync(Host, null, default);
        Assert.True(r.Found);
        Assert.Equal("skip", r.Method);
        Assert.Equal(0, net.Calls);
    }

    [Fact]
    public void Runner_IcmpBlockedHostAnsweringOn445_IsFoundByTcpConnect()
    {
        var net = new FakeDiscoveryNet { Tcp = (_, p) => p == 445 ? TcpOutcome.Open : TcpOutcome.NoAnswer };
        var runner = DiscoveryRunner.Create(DiscoveryModes.Parse("icmp,tcp"), "80,443,22,445", "53", net, (_, _) => NoPing, 500);
        var r = runner.Probe(Host, null, default);
        Assert.True(r.Success);
        Assert.Equal("tcp:445", r.Method);
    }

    [Fact]
    public void Runner_PrefersTheModeThatFoundTheHostBefore()
    {
        var net = new FakeDiscoveryNet { Tcp = (_, p) => p == 445 ? TcpOutcome.Open : TcpOutcome.NoAnswer };
        int pings = 0;
        var runner = DiscoveryRunner.Create(DiscoveryModes.Parse("icmp,tcp"), "445", "53", net, (_, _) => { pings++; return NoPing; }, 500);
        var r = runner.Probe(Host, "tcp:445", default);
        Assert.True(r.Success);
        Assert.Equal(0, pings);
    }

    [Fact]
    public void Runner_NothingAnswering_IsNotFound()
    {
        var net = new FakeDiscoveryNet();
        var runner = DiscoveryRunner.Create(DiscoveryModes.Parse("icmp,tcp,udp"), "80", "53", net, (_, _) => NoPing, 300);
        var r = runner.Probe(Host, null, default);
        Assert.False(r.Success);
        Assert.Null(r.Method);
    }

    [Theory]
    [InlineData("tcp:445", DiscoveryMode.TcpConnect)]
    [InlineData("icmp", DiscoveryMode.IcmpEcho)]
    [InlineData("tcp-syn:80", DiscoveryMode.TcpSyn)]
    [InlineData("udp:53", DiscoveryMode.Udp)]
    public void ModeOfMethod_MapsCodesBack(string method, DiscoveryMode mode)
        => Assert.Equal(mode, DiscoveryRunner.ModeOfMethod(method));

    [Fact]
    public void Usable_DropsDriverModesWithoutDriver_AndNeverReturnsEmpty()
    {
        PacketDriver.OverrideForTests(new PacketDriverStatus(false, "missing"));
        try
        {
            var usable = DiscoveryModes.Usable(new[] { DiscoveryMode.TcpSyn, DiscoveryMode.TcpConnect });
            Assert.Equal(new[] { DiscoveryMode.TcpConnect }, usable);
            Assert.Equal(new[] { DiscoveryMode.IcmpEcho }, DiscoveryModes.Usable(new[] { DiscoveryMode.TcpAck }));
        }
        finally { PacketDriver.OverrideForTests(null); }
    }
}
