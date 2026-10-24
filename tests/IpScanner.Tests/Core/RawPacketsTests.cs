using System;
using System.Net;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

[Collection("PacketDriverState")]
public class RawPacketsTests
{
    private static readonly byte[] MacA = { 0x02, 0, 0, 0, 0, 0x0A };
    private static readonly byte[] MacB = { 0x02, 0, 0, 0, 0, 0x0B };
    private static readonly IPAddress IpA = IPAddress.Parse("192.168.0.1");
    private static readonly IPAddress IpB = IPAddress.Parse("192.168.0.199");

    [Fact]
    public void Checksum_MatchesKnownIpv4HeaderExample()
    {
        byte[] header = { 0x45, 0x00, 0x00, 0x73, 0x00, 0x00, 0x40, 0x00, 0x40, 0x11, 0x00, 0x00, 0xC0, 0xA8, 0x00, 0x01, 0xC0, 0xA8, 0x00, 0xC7 };
        Assert.Equal(0xB861, RawPackets.Checksum(header));
    }

    [Fact]
    public void BuiltSyn_HasValidChecksumsAndRoundTrips()
    {
        var frame = RawPackets.BuildTcp(MacA, MacB, IpA, IpB, 40000, 445, 0x01020304, 0, TcpFlags.Syn, 1024, RawPackets.ProbeTcpOptions, ttl: 64, ipId: 7);
        Assert.Equal(14 + 20 + 20 + 12, frame.Length);
        Assert.Equal(0, RawPackets.Checksum(frame.AsSpan(14, 20)));
        // Pseudo header sum plus the segment must fold to zero.
        uint pseudo = 0xC0A8u + 0x0001u + 0xC0A8u + 0x00C7u + 6u + 32u;
        Assert.Equal(0, RawPackets.Checksum(frame.AsSpan(34, 32), pseudo));

        var p = RawPackets.Parse(frame)!;
        Assert.Equal(PacketKind.Tcp, p.Kind);
        Assert.Equal(IpA, p.SrcIp);
        Assert.Equal(IpB, p.DstIp);
        Assert.Equal(40000, p.SrcPort);
        Assert.Equal(445, p.DstPort);
        Assert.Equal(TcpFlags.Syn, p.Flags);
        Assert.Equal(1024, p.Window);
        Assert.Equal(0x01020304u, p.Seq);
        Assert.Equal(64, p.Ttl);
        Assert.True(p.DontFragment);
        Assert.Equal("MSS,NOP,WS,NOP,NOP,SACK", p.TcpOptions);
        Assert.Equal(1460, p.Mss);
        Assert.Equal(8, p.WindowScale);
    }

    [Fact]
    public void Parse_RecordedWindowsSynAck()
    {
        // SYN/ACK as sent by a Windows host: TTL 128, window 64240, MSS 1460, NOP, WS 8, NOP, NOP, SACK.
        var frame = RawPackets.BuildTcp(MacB, MacA, IpB, IpA, 445, 40000, 99, 0x01020305, TcpFlags.Syn | TcpFlags.Ack, 64240,
            RawPackets.ProbeTcpOptions, ttl: 128, ipId: 1234);
        var p = RawPackets.Parse(frame)!;
        Assert.Equal(TcpFlags.Syn | TcpFlags.Ack, p.Flags);
        Assert.Equal(128, p.Ttl);
        Assert.Equal(64240, p.Window);
        Assert.Equal(0x01020305u, p.Ack);
    }

    [Fact]
    public void BuiltEchoRequest_RoundTrips()
    {
        var frame = RawPackets.BuildIcmpEcho(MacA, MacB, IpA, IpB, 0x1234, 5);
        Assert.Equal(0, RawPackets.Checksum(frame.AsSpan(34)));
        var p = RawPackets.Parse(frame)!;
        Assert.Equal(PacketKind.Icmp, p.Kind);
        Assert.Equal(8, p.IcmpType);
        Assert.Equal(0x1234, p.IcmpId);
        Assert.Equal(5, p.IcmpSeq);
        Assert.Equal(RawPackets.FingerprintEchoPayload, p.Payload);
    }

    [Fact]
    public void Parse_RejectsTruncatedAndForeignFrames()
    {
        Assert.Null(RawPackets.Parse(new byte[5]));
        var frame = RawPackets.BuildTcp(MacA, MacB, IpA, IpB, 1, 2, 0, 0, TcpFlags.Syn, 100);
        Assert.Null(RawPackets.Parse(frame.AsSpan(0, 30)));
        frame[12] = 0x86; frame[13] = 0xDD;
        Assert.Null(RawPackets.Parse(frame));
        frame[12] = 0x08; frame[13] = 0x06;
        Assert.Null(RawPackets.Parse(frame));
    }

    [Fact]
    public void BuildTcp_RejectsUnpaddedOptions()
        => Assert.Throws<ArgumentException>(() => RawPackets.BuildTcp(MacA, MacB, IpA, IpB, 1, 2, 0, 0, TcpFlags.Syn, 100, new byte[] { 1, 1, 1 }));

    [Fact]
    public void Wpcap_WithoutDriver_OpensNothing()
    {
        PacketDriver.OverrideForTests(PacketDriverStatus.Missing);
        try { Assert.Null(Wpcap.Open(@"\Device\NPF_{X}")); }
        finally { PacketDriver.OverrideForTests(null); }
    }

    [Fact]
    public void ParseMacAndSubnet()
    {
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0, 1, 2 }, Wpcap.ParseMac("AA-BB-CC-00-01-02"));
        Assert.Null(Wpcap.ParseMac("nope"));
        Assert.True(Wpcap.SameSubnet(IpA, IpB, IPAddress.Parse("255.255.255.0")));
        Assert.False(Wpcap.SameSubnet(IpA, IPAddress.Parse("192.168.1.5"), IPAddress.Parse("255.255.255.0")));
    }
}
