using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core.Models;
using IpScanner.Core.Net;
using IpScanner.Core.Scanner.Discovery;
using IpScanner.Core.Scanner.Ipv6;
using Xunit;

namespace IpScanner.Tests.Core;

public class Ipv6DiscoveryTests
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

    private static FakeDiscoveryNet FakeTcp(Func<int, TcpOutcome> outcome)
        => new() { Tcp = (_, port) => outcome(port) };

    private static Ipv6Address A(string s, string? zone = null) => Ipv6Address.Parse(s) with { Zone = zone };

    private static OwnIpv6Address Own(string addr, int idx, int prefix = 64) => new("Ethernet", idx, A(addr), prefix);

    private static NeighborEntry Row(string addr, int idx, string? mac, bool router = false, NeighborState st = NeighborState.Reachable)
        => new(A(addr, idx.ToString()), mac, idx, st, router);

    // Neighbour table layout and parsing

    [Fact]
    public void Row_LayoutMatchesTheOffsetsParseUses()
    {
        Assert.Equal(WindowsNeighborTable.RowSize, Marshal.SizeOf<WindowsNeighborTable.Row>());
        var row = typeof(WindowsNeighborTable.Row);
        var sa = typeof(WindowsNeighborTable.SockAddrIn6);
        Assert.Equal(28, Marshal.SizeOf<WindowsNeighborTable.SockAddrIn6>());
        Assert.Equal(8, (int)Marshal.OffsetOf<WindowsNeighborTable.SockAddrIn6>("Addr"));
        Assert.Equal(24, (int)Marshal.OffsetOf<WindowsNeighborTable.SockAddrIn6>("ScopeId"));
        Assert.Equal(28, (int)Marshal.OffsetOf<WindowsNeighborTable.Row>("InterfaceIndex"));
        Assert.Equal(32, (int)Marshal.OffsetOf<WindowsNeighborTable.Row>("InterfaceLuid"));
        Assert.Equal(40, (int)Marshal.OffsetOf<WindowsNeighborTable.Row>("PhysicalAddress"));
        Assert.Equal(72, (int)Marshal.OffsetOf<WindowsNeighborTable.Row>("PhysicalAddressLength"));
        Assert.Equal(76, (int)Marshal.OffsetOf<WindowsNeighborTable.Row>("State"));
        Assert.Equal(80, (int)Marshal.OffsetOf<WindowsNeighborTable.Row>("Flags"));
        Assert.Equal(84, (int)Marshal.OffsetOf<WindowsNeighborTable.Row>("ReachabilityTime"));
        Assert.NotNull(row);
        Assert.NotNull(sa);
    }

    private static void PutRow(byte[] table, int index, string addr, uint scope, uint ifIndex, byte[]? mac, uint state, uint flags, ushort family = 23)
    {
        int at = 8 + index * WindowsNeighborTable.RowSize;
        BitConverter.TryWriteBytes(table.AsSpan(at, 2), family);
        Ipv6Address.Parse(addr).GetBytes().CopyTo(table, at + 8);
        BitConverter.TryWriteBytes(table.AsSpan(at + 24, 4), scope);
        BitConverter.TryWriteBytes(table.AsSpan(at + 28, 4), ifIndex);
        if (mac is not null) mac.CopyTo(table, at + 40);
        BitConverter.TryWriteBytes(table.AsSpan(at + 72, 4), (uint)(mac?.Length ?? 0));
        BitConverter.TryWriteBytes(table.AsSpan(at + 76, 4), state);
        BitConverter.TryWriteBytes(table.AsSpan(at + 80, 4), flags);
    }

    [Fact]
    public void Parse_KeepsResolvedHostsAndDropsNoise()
    {
        var t = new byte[8 + 6 * WindowsNeighborTable.RowSize];
        BitConverter.TryWriteBytes(t.AsSpan(0, 4), 6);
        PutRow(t, 0, "fe80::1", 12, 12, [0xAA, 0xBB, 0xCC, 0x00, 0x11, 0x22], 5, 1);          // router, reachable
        PutRow(t, 1, "fe80::2", 12, 12, null, 1, 0);                                            // incomplete
        PutRow(t, 2, "fe80::3", 12, 12, null, 0, 0);                                            // unreachable
        PutRow(t, 3, "ff02::1", 12, 12, [0x33, 0x33, 0, 0, 0, 1], 6, 0);                       // multicast
        PutRow(t, 4, "2001:db8::5", 0, 7, [1, 2, 3, 4, 5, 6], 4, 0);                           // stale global host
        PutRow(t, 5, "fe80::9", 12, 12, [1, 2, 3, 4, 5, 6], 5, 0, family: 2);                  // wrong family

        var list = WindowsNeighborTable.Parse(t);
        Assert.Equal(2, list.Count);
        Assert.Equal("fe80::1", list[0].Address.Value == Ipv6Address.Parse("fe80::1").Value ? "fe80::1" : "?");
        Assert.Equal("12", list[0].Address.Zone);
        Assert.Equal("AA:BB:CC:00:11:22", list[0].Mac);
        Assert.True(list[0].IsRouter);
        Assert.Equal(NeighborState.Reachable, list[0].State);
        Assert.Equal(12, list[0].InterfaceIndex);
        Assert.Null(list[1].Address.Zone);
        Assert.False(list[1].IsRouter);
        Assert.Equal(NeighborState.Stale, list[1].State);
        Assert.Equal(7, list[1].InterfaceIndex);
    }

    [Fact]
    public void Parse_ToleratesShortOrTruncatedBuffers()
    {
        Assert.Empty(WindowsNeighborTable.Parse(new byte[3]));
        var t = new byte[8 + 10];
        BitConverter.TryWriteBytes(t.AsSpan(0, 4), 5);
        Assert.Empty(WindowsNeighborTable.Parse(t));
    }

    [Fact]
    public void LiveTable_ReadDoesNotThrow()
    {
        var rows = new WindowsNeighborTable().Read();
        Assert.All(rows, r => Assert.False(r.Address.IsMulticast));
    }

    // ICMPv6 packet images

    [Fact]
    public void SockAddr_HasFamilyAddressAndScope()
    {
        var b = SystemIpv6Net.SockAddr(A("ff02::1", "12"));
        Assert.Equal(28, b.Length);
        Assert.Equal(23, BitConverter.ToUInt16(b, 0));
        Assert.Equal(0xFF, b[8]);
        Assert.Equal(0x02, b[9]);
        Assert.Equal(1, b[23]);
        Assert.Equal(12u, BitConverter.ToUInt32(b, 24));
    }

    [Fact]
    public void ParseReplies_DecodesSuccessfulRecordsOnly()
    {
        var buf = new byte[SystemIpv6Net.ReplySize * 3];
        void Rec(int i, string addr, uint scope, uint status, uint rtt)
        {
            int at = i * SystemIpv6Net.ReplySize;
            Ipv6Address.Parse(addr).GetBytes().CopyTo(buf, at + 6);
            BitConverter.TryWriteBytes(buf.AsSpan(at + 22, 4), scope);
            BitConverter.TryWriteBytes(buf.AsSpan(at + 28, 4), status);
            BitConverter.TryWriteBytes(buf.AsSpan(at + 32, 4), rtt);
        }
        Rec(0, "fe80::a", 12, 0, 3);
        Rec(1, "fe80::b", 12, 11010, 0);   // timed out
        Rec(2, "fe80::c", 0, 0, 0);

        var got = SystemIpv6Net.ParseReplies(buf, 3, 7.5);
        Assert.Equal(2, got.Count);
        Assert.Equal(Ipv6Address.Parse("fe80::a").Value, got[0].From.Value);
        Assert.Equal("12", got[0].From.Zone);
        Assert.Equal(3, got[0].RttMs);
        Assert.Null(got[1].From.Zone);
        Assert.Equal(7.5, got[1].RttMs);
    }

    [Fact]
    public async Task LiveEcho_ToLoopbackAnswers()
    {
        var got = await new SystemIpv6Net().EchoAsync(Ipv6Address.Parse("::1"), 1500, CancellationToken.None);
        // A host without IPv6 loopback stays silent; the call itself must never throw.
        Assert.All(got, r => Assert.Equal(Ipv6Address.Parse("::1").Value, r.From.Value));
    }

    // Link discovery

    [Fact]
    public async Task DiscoverLink_EchoesAllNodesPerLinkLocalInterface()
    {
        var net = new FakeNet();
        var d = new Ipv6Discovery(new FakeTable(), net);
        await d.DiscoverLinkAsync([Own("fe80::10", 12, 64), Own("fe80::20", 15, 64), Own("2001:db8::10", 12)], 50, 0);
        Assert.Equal(2, net.Sent.Count);
        Assert.All(net.Sent, s => Assert.Equal(Ipv6Address.AllNodes.Value, s.Value));
        Assert.Equal(new[] { "12", "15" }, net.Sent.Select(s => s.Zone).OrderBy(z => z, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task DiscoverLink_ReadsCacheExcludesOwnAndOtherInterfaces()
    {
        var table = new FakeTable(
            Row("fe80::1", 12, "AA:AA:AA:AA:AA:01", router: true),
            Row("fe80::77", 12, "AA:AA:AA:AA:AA:02"),
            Row("fe80::10", 12, "AA:AA:AA:AA:AA:10"),        // our own address
            Row("fe80::99", 40, "AA:AA:AA:AA:AA:99"));       // an interface we do not scan
        var d = new Ipv6Discovery(table, new FakeNet());
        var hosts = await d.DiscoverLinkAsync([Own("fe80::10", 12)], 50, 0);
        Assert.Equal(2, hosts.Count);
        Assert.True(hosts.Single(h => h.IsRouter).Mac == "AA:AA:AA:AA:AA:01");
        Assert.All(hosts, h => Assert.Equal("ndp", h.Method));
        Assert.All(hosts, h => Assert.Null(h.RttMs));
    }

    [Fact]
    public async Task DiscoverLink_MarksEchoRepliersAndAddsCachelessOnes()
    {
        var net = new FakeNet
        {
            Reply = _ => [new(A("fe80::1", "12"), 2.5), new(A("fe80::55", "12"), 4)],
        };
        var table = new FakeTable(Row("fe80::1", 12, "AA:AA:AA:AA:AA:01", router: true));
        var hosts = await new Ipv6Discovery(table, net).DiscoverLinkAsync([Own("fe80::10", 12)], 50, 0);
        Assert.Equal(2, hosts.Count);
        var router = hosts.Single(h => h.IsRouter);
        Assert.Equal("icmp6", router.Method);
        Assert.Equal(2.5, router.RttMs);
        var bare = hosts.Single(h => !h.IsRouter);
        Assert.Null(bare.Mac);
        Assert.Equal("icmp6", bare.Method);
        Assert.Equal(12, bare.InterfaceIndex);
    }

    [Fact]
    public async Task DiscoverLink_ResolvesNamesWhenAsked()
    {
        var table = new FakeTable(Row("fe80::1", 12, "AA:AA:AA:AA:AA:01"));
        var seen = new List<string>();
        var d = new Ipv6Discovery(table, new FakeNet(), nameOf: ip => { seen.Add(ip); return "router"; });
        var hosts = await d.DiscoverLinkAsync([Own("fe80::10", 12)], 50, 0);
        Assert.Equal("router", hosts.Single().Name);
        Assert.Equal(["fe80::1%12"], seen);
    }

    [Fact]
    public async Task DiscoverLink_WithoutInterfacesDoesNothing()
    {
        var net = new FakeNet();
        var hosts = await new Ipv6Discovery(new FakeTable(Row("fe80::1", 12, null)), net).DiscoverLinkAsync([], 50, 0);
        Assert.Empty(hosts);
        Assert.Empty(net.Sent);
    }

    // Explicit targets

    [Fact]
    public async Task ProbeTarget_EchoReplyWins()
    {
        var net = new FakeNet { Reply = t => [new(t, 1.5)] };
        var tcp = FakeTcp(_ => TcpOutcome.NoAnswer);
        var r = await new Ipv6Discovery(new FakeTable(), net, tcp).ProbeTargetAsync(A("2001:db8::5"), [443], 100);
        Assert.True(r.Found);
        Assert.Equal("icmp6", r.Method);
        Assert.Equal(1.5, r.RttMs);
        Assert.Empty(tcp.Log);
    }

    [Fact]
    public async Task ProbeTarget_IgnoresRepliesFromOtherHosts()
    {
        var net = new FakeNet { Reply = _ => [new(A("2001:db8::99"), 1)] };
        var r = await new Ipv6Discovery(new FakeTable(), net).ProbeTargetAsync(A("2001:db8::5"), [443], 100);
        Assert.False(r.Found);
    }

    [Theory]
    [InlineData(TcpOutcome.Open)]
    [InlineData(TcpOutcome.Refused)]
    public async Task ProbeTarget_TcpOpenOrRefusedMeansUp(TcpOutcome outcome)
    {
        var tcp = FakeTcp(p => p == 443 ? outcome : TcpOutcome.NoAnswer);
        var r = await new Ipv6Discovery(new FakeTable(), new FakeNet(), tcp)
            .ProbeTargetAsync(A("fe80::5", "12"), [80, 443, 22], 100);
        Assert.True(r.Found);
        Assert.Equal("tcp:443", r.Method);
        Assert.Equal(new[] { "tcp fe80::5%12:443", "tcp fe80::5%12:80" }, tcp.Log.OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task ProbeTarget_NothingAnswersIsNotFound()
    {
        var tcp = FakeTcp(_ => TcpOutcome.NoAnswer);
        var r = await new Ipv6Discovery(new FakeTable(), new FakeNet(), tcp).ProbeTargetAsync(A("2001:db8::5"), [80, 22], 100);
        Assert.False(r.Found);
        Assert.Equal(2, tcp.Calls);
    }

    // The sweep cap

    [Theory]
    [InlineData("2001:db8::/112", true)]
    [InlineData("2001:db8::/120", true)]
    [InlineData("2001:db8::1/128", true)]
    [InlineData("2001:db8::/111", false)]
    [InlineData("2001:db8::/64", false)]
    [InlineData("fe80::/10", false)]
    public void CanSweep_StopsAbove65536Addresses(string prefix, bool expected)
    {
        Assert.True(Ipv6Prefix.TryParse(prefix, out var p));
        Assert.Equal(expected, Ipv6Discovery.CanSweep(p));
    }
}
