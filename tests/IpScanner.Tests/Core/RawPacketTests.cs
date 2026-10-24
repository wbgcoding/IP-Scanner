using System.IO;
using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

// Touches the process-wide packet driver override and transport factory.
[Collection("PortScannerGate")]
public class RawPacketTests
{
    private static readonly IPAddress Local = IPAddress.Parse("192.168.1.10");
    private static readonly IPAddress Target = IPAddress.Parse("192.168.1.20");

    [Fact]
    public void Checksum_MatchesTheKnownIpv4HeaderVector()
    {
        // Classic worked example: header with the checksum field zeroed sums to 0xB861.
        byte[] header = { 0x45, 0x00, 0x00, 0x73, 0x00, 0x00, 0x40, 0x00, 0x40, 0x11, 0x00, 0x00,
                          0xC0, 0xA8, 0x00, 0x01, 0xC0, 0xA8, 0x00, 0xC7 };
        Assert.Equal(0xB861, RawPackets.Checksum(header));
        header[10] = 0xB8; header[11] = 0x61;
        Assert.Equal(0, RawPackets.Checksum(header));
    }

    [Fact]
    public void BuildTcp_IsTheBareSegmentInAnEthernetFrame()
    {
        byte[] mac = { 1, 2, 3, 4, 5, 6 };
        var ip = RawPackets.BuildTcpIp(Local, Target, 41000, 80, 5, 0, TcpFlags.Syn, 1024);
        var frame = RawPackets.BuildTcp(mac, mac, Local, Target, 41000, 80, 5, 0, TcpFlags.Syn, 1024);
        Assert.Equal(ip, frame[RawPackets.EthernetHeader..]);
        Assert.Equal(RawPackets.ParseIp(ip)!.Seq, RawPackets.Parse(frame)!.Seq);
    }

    [Fact]
    public void Build_Syn_HasTheExpectedHeaderFieldsAndValidChecksums()
    {
        var p = RawPackets.BuildTcpIp(Local, Target, 41000, 80, 0x01020304, 0, TcpFlags.Syn, 1024, RawPackets.MssOption, ipId: 9, dontFragment: false);
        Assert.Equal(44, p.Length);
        Assert.Equal(0x45, p[0]);
        Assert.Equal(6, p[9]);
        Assert.Equal(Local.GetAddressBytes(), p[12..16]);
        Assert.Equal(Target.GetAddressBytes(), p[16..20]);
        Assert.Equal(0, RawPackets.Checksum(p.AsSpan(0, 20)));
        Assert.Equal(41000, (p[20] << 8) | p[21]);
        Assert.Equal(80, (p[22] << 8) | p[23]);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, p[24..28]);
        Assert.Equal(6 << 4, p[32]);                    // 24 byte TCP header
        Assert.Equal((byte)TcpFlags.Syn, p[33]);
        Assert.Equal(new byte[] { 2, 4, 5, 0xB4 }, p[40..44]);
        var tcp = p.AsSpan(20);
        var sum = RawPackets.Checksum(tcp, RawPackets.PseudoHeaderSum(Local, Target, 6, tcp.Length));
        Assert.Equal(0, sum);
    }

    [Theory]
    [InlineData(ScanType.Syn, TcpFlags.Syn)]
    [InlineData(ScanType.Ack, TcpFlags.Ack)]
    [InlineData(ScanType.Window, TcpFlags.Ack)]
    [InlineData(ScanType.Fin, TcpFlags.Fin)]
    [InlineData(ScanType.Null, TcpFlags.None)]
    [InlineData(ScanType.Xmas, TcpFlags.Fin | TcpFlags.Psh | TcpFlags.Urg)]
    [InlineData(ScanType.Maimon, TcpFlags.Fin | TcpFlags.Ack)]
    public void FlagsFor_EachScanType_SendsItsClassicFlags(ScanType type, TcpFlags expected)
    {
        Assert.Equal(expected, TcpPacketBuilder.FlagsFor(type));
    }

    [Fact]
    public void FlagsFor_Connect_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TcpPacketBuilder.FlagsFor(ScanType.Connect));
    }

    [Fact]
    public void Parse_TcpReply_ReadsPortsFlagsAndWindow()
    {
        var bytes = Seg(Target, Local, 80, 41000, TcpFlags.Syn | TcpFlags.Ack, 1, 2, window: 5840);
        var r = RawPackets.ParseIp(bytes)!;
        Assert.Equal(PacketKind.Tcp, r.Kind);
        Assert.Equal(Target, r.SrcIp);
        Assert.Equal(80, r.SrcPort);
        Assert.Equal(41000, r.DstPort);
        Assert.Equal(TcpFlags.Syn | TcpFlags.Ack, r.Flags);
        Assert.Equal(5840, r.Window);
        Assert.True(RawPacketParser.Answers(r, Target, 80, 41000));
        Assert.False(RawPacketParser.Answers(r, Target, 81, 41000));
        Assert.False(RawPacketParser.Answers(r, Target, 80, 41001));
        Assert.False(RawPacketParser.Answers(r, IPAddress.Parse("192.168.1.99"), 80, 41000));
    }

    private static byte[] IcmpUnreachable(byte code, byte[] probe)
    {
        // IPv4 header (20) from the target, ICMP type 3, then the probe's IP header + 8 bytes.
        var quoted = probe[..28];
        var packet = new byte[20 + 8 + quoted.Length];
        packet[0] = 0x45; packet[8] = 64; packet[9] = 1;
        Target.GetAddressBytes().CopyTo(packet, 12);
        Local.GetAddressBytes().CopyTo(packet, 16);
        packet[20] = 3; packet[21] = code;
        quoted.CopyTo(packet, 28);
        return packet;
    }

    [Fact]
    public void Parse_IcmpPortUnreachable_ReadsTheQuotedProbe()
    {
        var probe = Seg(Local, Target, 41000, 80, TcpFlags.Syn, 7);
        var r = RawPackets.ParseIp(IcmpUnreachable(3, probe))!;
        Assert.Equal(PacketKind.Icmp, r.Kind);
        Assert.Equal(3, r.IcmpType);
        Assert.Equal(3, r.IcmpCode);
        Assert.Equal(Target, r.QuotedDst);
        Assert.Equal(41000, r.QuotedSrcPort);
        Assert.Equal(80, r.QuotedDstPort);
        Assert.True(RawPacketParser.Answers(r, Target, 80, 41000));
        Assert.False(RawPacketParser.Answers(r, Target, 80, 41001));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x45, 0, 0, 20 })]
    [InlineData(new byte[] { 0x60, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    public void Parse_MalformedOrNonIpv4_ReturnsFalse(byte[] bytes)
    {
        Assert.Null(RawPackets.ParseIp(bytes));
    }

    private static ParsedPacket Tcp(TcpFlags flags, int window = 0)
        => new() { Kind = PacketKind.Tcp, SrcIp = Target, SrcPort = 80, DstPort = 41000, Flags = flags, Window = window };

    private static ParsedPacket Icmp()
        => new() { Kind = PacketKind.Icmp, SrcIp = Target, IcmpType = 3, IcmpCode = 13, QuotedDst = Target, QuotedSrcPort = 41000, QuotedDstPort = 80 };

    private static byte[] Seg(IPAddress src, IPAddress dst, int srcPort, int dstPort, TcpFlags flags, uint seq, uint ack = 0, int window = 1024)
        => RawPackets.BuildTcpIp(src, dst, srcPort, dstPort, seq, ack, flags, window, dontFragment: false);

    [Theory]
    [InlineData(ScanType.Syn, TcpFlags.Syn | TcpFlags.Ack, PortState.Open, PortReason.SynAck)]
    [InlineData(ScanType.Syn, TcpFlags.Rst | TcpFlags.Ack, PortState.Closed, PortReason.Rst)]
    [InlineData(ScanType.Ack, TcpFlags.Rst, PortState.Unfiltered, PortReason.Rst)]
    [InlineData(ScanType.Fin, TcpFlags.Rst, PortState.Closed, PortReason.Rst)]
    [InlineData(ScanType.Null, TcpFlags.Rst, PortState.Closed, PortReason.Rst)]
    [InlineData(ScanType.Xmas, TcpFlags.Rst, PortState.Closed, PortReason.Rst)]
    [InlineData(ScanType.Maimon, TcpFlags.Rst, PortState.Closed, PortReason.Rst)]
    public void Classify_TcpReply_MapsToTheClassicState(ScanType type, TcpFlags flags, PortState state, string reason)
    {
        Assert.Equal((state, reason), RawScanClassifier.Classify(type, Tcp(flags)));
    }

    [Fact]
    public void Classify_Window_SplitsOnTheWindowOfTheRst()
    {
        Assert.Equal((PortState.Open, PortReason.RstWindow), RawScanClassifier.Classify(ScanType.Window, Tcp(TcpFlags.Rst, 512)));
        Assert.Equal((PortState.Closed, PortReason.Rst), RawScanClassifier.Classify(ScanType.Window, Tcp(TcpFlags.Rst, 0)));
    }

    [Theory]
    [InlineData(ScanType.Syn, PortState.Filtered)]
    [InlineData(ScanType.Ack, PortState.Filtered)]
    [InlineData(ScanType.Window, PortState.Filtered)]
    [InlineData(ScanType.Fin, PortState.OpenFiltered)]
    [InlineData(ScanType.Null, PortState.OpenFiltered)]
    [InlineData(ScanType.Xmas, PortState.OpenFiltered)]
    [InlineData(ScanType.Maimon, PortState.OpenFiltered)]
    public void Classify_Silence_FollowsTheScanType(ScanType type, PortState state)
    {
        Assert.Equal((state, PortReason.NoResponse), RawScanClassifier.Classify(type, null));
    }

    [Fact]
    public void Classify_IcmpUnreachable_IsFilteredForEveryType()
    {
        foreach (var type in ScanTypes.All.Where(ScanTypes.RequiresDriver))
            Assert.Equal((PortState.Filtered, PortReason.IcmpUnreachable), RawScanClassifier.Classify(type, Icmp()));
    }

    // ---- Strategy and engine through a fake transport ----

    private sealed class FakeTransport : IRawTransport
    {
        private readonly Func<byte[], byte[]?> _responder;
        public int Sent;
        public FakeTransport(Func<byte[], byte[]?> responder) => _responder = responder;
        public IPAddress LocalAddress => Local;
        public Task<byte[]?> ExchangeAsync(byte[] ipPacket, Func<byte[], bool> isReply, int timeoutMs, CancellationToken ct)
        {
            Interlocked.Increment(ref Sent);
            var reply = _responder(ipPacket);
            return Task.FromResult(reply is not null && isReply(reply) ? reply : null);
        }
        public void Dispose() { }
    }

    /// <summary>Open on 80 (SYN/ACK), closed on 81 (RST), silent elsewhere.</summary>
    private static byte[]? Responder(byte[] probe)
    {
        var p = RawPackets.ParseIp(probe)!;
        return p.DstPort switch
        {
            80 => Seg(Target, Local, 80, p.SrcPort, TcpFlags.Syn | TcpFlags.Ack, 1, 1),
            81 => Seg(Target, Local, 81, p.SrcPort, TcpFlags.Rst | TcpFlags.Ack, 0, 1),
            _ => null,
        };
    }

    [Fact]
    public async Task RawTcpStrategy_Syn_ReportsOpenClosedAndFiltered()
    {
        using var transport = new FakeTransport(Responder);
        var strategy = new RawTcpStrategy(ScanType.Syn, transport, Target, () => 50);

        var open = await strategy.ProbeAsync(Target.ToString(), 80, "http", default);
        var closed = await strategy.ProbeAsync(Target.ToString(), 81, "x", default);
        var silent = await strategy.ProbeAsync(Target.ToString(), 82, "y", default);

        Assert.Equal((PortState.Open, PortReason.SynAck), (open!.State, open.Reason));
        Assert.Equal("http", open.Service!.Name);
        Assert.Equal((PortState.Closed, PortReason.Rst), (closed!.State, closed.Reason));
        Assert.Null(closed.Service);
        Assert.Equal((PortState.Filtered, PortReason.NoResponse), (silent!.State, silent.Reason));
    }

    [Fact]
    public async Task RawTcpStrategy_Fin_SilenceIsOpenFiltered()
    {
        using var transport = new FakeTransport(Responder);
        var strategy = new RawTcpStrategy(ScanType.Fin, transport, Target, () => 50);
        var r = await strategy.ProbeAsync(Target.ToString(), 82, "y", default);
        Assert.Equal(PortState.OpenFiltered, r!.State);
    }

    [Fact]
    public async Task RawTcpStrategy_AlreadyCancelled_ReturnsNullWithoutSending()
    {
        using var transport = new FakeTransport(Responder);
        var strategy = new RawTcpStrategy(ScanType.Syn, transport, Target, () => 50);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Null(await strategy.ProbeAsync(Target.ToString(), 80, "http", cts.Token));
        Assert.Equal(0, transport.Sent);
    }

    [Fact]
    public async Task RawTcpStrategy_IgnoresAReplyForAnotherPort()
    {
        // A transport that answers every probe with port 80's SYN/ACK must not make port 90 open.
        using var transport = new FakeTransport(probe =>
        {
            var p = RawPackets.ParseIp(probe)!;
            return Seg(Target, Local, 80, p.SrcPort, TcpFlags.Syn | TcpFlags.Ack, 1, 1);
        });
        var strategy = new RawTcpStrategy(ScanType.Syn, transport, Target, () => 50);
        var r = await strategy.ProbeAsync(Target.ToString(), 90, "x", default);
        Assert.Equal(PortState.Filtered, r!.State);
    }

    [Fact]
    public void Availability_FollowsDriverAndTransport()
    {
        var oldFactory = RawTransportProvider.Factory;
        try
        {
            RawTransportProvider.Factory = null;
            PacketDriver.OverrideForTests(PacketDriverStatus.Missing);
            Assert.Equal((true, (string?)null), ScanTypes.Availability(ScanType.Connect));
            Assert.Equal((false, (string?)"driver-missing"), ScanTypes.Availability(ScanType.Syn));

            PacketDriver.OverrideForTests(new PacketDriverStatus(true, null));
            Assert.Equal((false, (string?)"transport-missing"), ScanTypes.Availability(ScanType.Xmas));

            RawTransportProvider.Factory = _ => new FakeTransport(_ => null);
            Assert.Equal((true, (string?)null), ScanTypes.Availability(ScanType.Xmas));
        }
        finally
        {
            RawTransportProvider.Factory = oldFactory;
            PacketDriver.OverrideForTests(null);
        }
    }

    [Fact]
    public async Task Engine_RawSynScanThroughTheFactory_ReturnsStatesInPortOrder()
    {
        var oldFactory = RawTransportProvider.Factory;
        try
        {
            PacketDriver.OverrideForTests(new PacketDriverStatus(true, null));
            RawTransportProvider.Factory = _ => new FakeTransport(Responder);
            var spec = new PortSpec(new[] { 82, 80, 81 }, Array.Empty<int>());
            var results = await PortScanEngine.RunAsync(Target.ToString(), spec,
                new ScanOptions(ScanType.Syn, Tcp: true, Udp: false), null, default);
            Assert.Equal(new[] { 80, 81, 82 }, results.Select(r => r.Port));
            Assert.Equal(new[] { PortState.Open, PortState.Closed, PortState.Filtered }, results.Select(r => r.State));
        }
        finally
        {
            RawTransportProvider.Factory = oldFactory;
            PacketDriver.OverrideForTests(null);
        }
    }

    [Fact]
    public async Task Engine_RawScanWithoutDriver_ThrowsAndProbesNothing()
    {
        PacketDriver.OverrideForTests(PacketDriverStatus.Missing);
        try
        {
            var spec = new PortSpec(new[] { 80 }, Array.Empty<int>());
            await Assert.ThrowsAsync<InvalidOperationException>(() => PortScanEngine.RunAsync(
                "127.0.0.1", spec, new ScanOptions(ScanType.Syn, true, false), null, default));
        }
        finally { PacketDriver.OverrideForTests(null); }
    }

    private sealed class BrokenTransport : IRawTransport
    {
        public int Sent;
        public IPAddress LocalAddress => Local;
        public Task<byte[]?> ExchangeAsync(byte[] ipPacket, Func<byte[], bool> isReply, int timeoutMs, CancellationToken ct)
        {
            Interlocked.Increment(ref Sent);
            throw new IOException("adapter removed");
        }
        public void Dispose() { }
    }

    [Fact]
    public async Task RawTcpStrategy_SendFailure_IsADriverFaultNotAFilteredPort()
    {
        using var transport = new BrokenTransport();
        var strategy = new RawTcpStrategy(ScanType.Syn, transport, Target, () => 50);
        var ex = await Assert.ThrowsAsync<RawScanException>(() => strategy.ProbeAsync(Target.ToString(), 80, "http", default));
        Assert.Contains("adapter removed", ex.Message);
    }

    [Fact]
    public async Task Engine_DriverFault_EndsTheRunWithTheError()
    {
        var oldFactory = RawTransportProvider.Factory;
        var broken = new BrokenTransport();
        try
        {
            PacketDriver.OverrideForTests(new PacketDriverStatus(true, null));
            RawTransportProvider.Factory = _ => broken;
            var spec = new PortSpec(Enumerable.Range(1, 400).ToArray(), Array.Empty<int>());
            var ex = await Assert.ThrowsAsync<RawScanException>(() => PortScanEngine.RunAsync(Target.ToString(), spec,
                new ScanOptions(ScanType.Syn, Tcp: true, Udp: false), null, default));
            Assert.Contains("adapter removed", ex.Message);
            Assert.True(broken.Sent < 400, "the run must stop instead of probing every port");
        }
        finally
        {
            RawTransportProvider.Factory = oldFactory;
            PacketDriver.OverrideForTests(null);
        }
    }
}
