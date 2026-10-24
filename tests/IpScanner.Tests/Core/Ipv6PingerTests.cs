using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core.Net;
using IpScanner.Core.Scanner;
using IpScanner.Core.Scanner.Discovery;
using IpScanner.Core.Scanner.Ipv6;
using Xunit;

namespace IpScanner.Tests.Core;

public class Ipv6PingerTests
{
    private sealed class FakeTable(params NeighborEntry[] rows) : INeighborTable
    {
        public IReadOnlyList<NeighborEntry> Read() => rows;
    }

    private sealed class FakeNet : IIpv6Net
    {
        public readonly List<Ipv6Address> Sent = [];
        public Func<Ipv6Address, IReadOnlyList<Ipv6EchoReply>> Reply = _ => [];
        public Task<IReadOnlyList<Ipv6EchoReply>> EchoAsync(Ipv6Address target, int timeoutMs, CancellationToken ct)
        {
            lock (Sent) Sent.Add(target);
            return Task.FromResult(Reply(target));
        }
    }

    private static Ipv6Address A(string s, string? zone = null) => Ipv6Address.Parse(s) with { Zone = zone };

    private static NeighborEntry Row(string addr, int idx, string? mac)
        => new(A(addr, idx.ToString()), mac, idx, NeighborState.Reachable, false);

    private static Ipv6Discovery Discovery(INeighborTable table, IIpv6Net net) =>
        new(table, net, new FakeDiscoveryNet { Tcp = (_, _) => TcpOutcome.NoAnswer });

    [Fact]
    public void Ping_EchoReplyIsUpWithItsLatencyAndMethod()
    {
        var table = new FakeTable();
        var net = new FakeNet { Reply = t => [new(t, 2.5)] };
        var r = Ipv6Pinger.Ping(Discovery(table, net), table, "2001:db8::10", 100);
        Assert.True(r.Success);
        Assert.Equal(2.5, r.LatencyMs);
        Assert.Equal("icmp6", r.Method);
    }

    [Fact]
    public void Ping_SilenceIsDown()
    {
        var table = new FakeTable();
        var r = Ipv6Pinger.Ping(Discovery(table, new FakeNet()), table, "2001:db8::10", 100);
        Assert.False(r.Success);
        Assert.Null(r.Method);
    }

    [Fact]
    public void Ping_GarbageIsDownWithoutSending()
    {
        var table = new FakeTable();
        var net = new FakeNet();
        Assert.False(Ipv6Pinger.Ping(Discovery(table, net), table, "not-an-address", 100).Success);
        Assert.Empty(net.Sent);
    }

    [Fact]
    public void Ping_LinkLocalWithoutZoneGetsTheNeighbourInterface()
    {
        var table = new FakeTable(Row("fe80::5", 12, "AA-BB-CC-DD-EE-FF"));
        var net = new FakeNet { Reply = t => [new(t, 1)] };
        Assert.True(Ipv6Pinger.Ping(Discovery(table, net), table, "fe80::5", 100).Success);
        Assert.Equal("12", Assert.Single(net.Sent).Zone);
    }

    [Fact]
    public void MacOf_ReadsTheNeighbourCacheAndIgnoresStrangers()
    {
        var table = new FakeTable(Row("fe80::5", 12, "AA-BB-CC-DD-EE-FF"), Row("fe80::6", 12, null));
        Assert.Equal("AA-BB-CC-DD-EE-FF", Ipv6Pinger.MacOf(table, "fe80::5"));
        Assert.Null(Ipv6Pinger.MacOf(table, "fe80::6"));
        Assert.Null(Ipv6Pinger.MacOf(table, "fe80::7"));
        Assert.Null(Ipv6Pinger.MacOf(table, "bogus"));
    }

    [Theory]
    [InlineData("::1", true)]
    [InlineData("fe80::1%12", true)]
    [InlineData("192.168.1.1", false)]
    [InlineData("host", false)]
    public void IsIpv6_SeparatesTheFamilies(string ip, bool expected) => Assert.Equal(expected, Ipv6Pinger.IsIpv6(ip));
}
