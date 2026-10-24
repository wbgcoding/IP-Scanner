using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace IpScanner.Core.Scanner;

[Flags]
public enum TcpFlags : byte { None = 0, Fin = 1, Syn = 2, Rst = 4, Psh = 8, Ack = 16, Urg = 32, Ece = 64, Cwr = 128 }

public enum PacketKind { Other, Tcp, Icmp }

/// <summary>Fields of a captured Ethernet frame that discovery and OS analysis care about.</summary>
public sealed record ParsedPacket
{
    public PacketKind Kind { get; init; }
    public IPAddress? SrcIp { get; init; }
    public IPAddress? DstIp { get; init; }
    public byte[] SrcMac { get; init; } = Array.Empty<byte>();
    public int Ttl { get; init; }
    public bool DontFragment { get; init; }
    public int IpId { get; init; }
    public int SrcPort { get; init; }
    public int DstPort { get; init; }
    public TcpFlags Flags { get; init; }
    public int Window { get; init; }
    public uint Seq { get; init; }
    public uint Ack { get; init; }
    /// <summary>TCP option kinds in wire order, e.g. "MSS,NOP,WS,NOP,NOP,SACK,TS".</summary>
    public string TcpOptions { get; init; } = "";
    public int Mss { get; init; }
    public int WindowScale { get; init; } = -1;
    /// <summary>ICMP type.</summary>
    public int IcmpType { get; init; }
    public int IcmpCode { get; init; }
    public int IcmpId { get; init; }
    public int IcmpSeq { get; init; }
    /// <summary>For ICMP destination-unreachable: the destination and ports of the packet it quotes.</summary>
    public IPAddress? QuotedDst { get; init; }
    public int QuotedSrcPort { get; init; }
    public int QuotedDstPort { get; init; }
    public byte[] Payload { get; init; } = Array.Empty<byte>();
}

/// <summary>Builds and parses the few frame types the raw-packet features use. Pure functions, no I/O.</summary>
public static class RawPackets
{
    public const int EthernetHeader = 14;

    /// <summary>MSS 1460, NOP, window scale 8, NOP, NOP, SACK permitted: a common option block for a probe SYN.</summary>
    public static readonly byte[] ProbeTcpOptions = { 2, 4, 0x05, 0xB4, 1, 3, 3, 8, 1, 1, 4, 2 };

    /// <summary>MSS 1460 only: the option block of a plain scan SYN.</summary>
    public static readonly byte[] MssOption = { 2, 4, 0x05, 0xB4 };

    /// <summary>Fixed payload of the fingerprint ICMP echo.</summary>
    public static readonly byte[] FingerprintEchoPayload = Encoding.ASCII.GetBytes("abcdefghijklmnopqrstuvwxyz012345");

    public static ushort Checksum(ReadOnlySpan<byte> data, uint seed = 0)
    {
        uint sum = seed;
        int i = 0;
        for (; i + 1 < data.Length; i += 2) sum += (uint)((data[i] << 8) | data[i + 1]);
        if (i < data.Length) sum += (uint)(data[i] << 8);
        while ((sum >> 16) != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)~sum;
    }

    public static byte[] BuildTcp(byte[] srcMac, byte[] dstMac, IPAddress srcIp, IPAddress dstIp, int srcPort, int dstPort,
        uint seq, uint ack, TcpFlags flags, int window, byte[]? options = null, int ttl = 64, int ipId = 0, bool dontFragment = true)
        => Frame(BuildTcpIp(srcIp, dstIp, srcPort, dstPort, seq, ack, flags, window, options, ttl, ipId, dontFragment), srcMac, dstMac);

    /// <summary>The same TCP segment as a bare IPv4 packet, for transports that add the link layer themselves.</summary>
    public static byte[] BuildTcpIp(IPAddress srcIp, IPAddress dstIp, int srcPort, int dstPort,
        uint seq, uint ack, TcpFlags flags, int window, byte[]? options = null, int ttl = 64, int ipId = 0, bool dontFragment = true)
    {
        options ??= Array.Empty<byte>();
        if (options.Length % 4 != 0) throw new ArgumentException("options must be padded to 4 bytes", nameof(options));
        int tcpLen = 20 + options.Length;
        var packet = new byte[20 + tcpLen];
        WriteIpv4(packet, 6, packet.Length, srcIp, dstIp, ttl, ipId, dontFragment);

        var tcp = packet.AsSpan(20, tcpLen);
        WriteU16(tcp, 0, srcPort); WriteU16(tcp, 2, dstPort);
        WriteU32(tcp, 4, seq); WriteU32(tcp, 8, ack);
        tcp[12] = (byte)((tcpLen / 4) << 4);
        tcp[13] = (byte)flags;
        WriteU16(tcp, 14, window);
        options.CopyTo(tcp[20..]);
        WriteU16(tcp, 16, Checksum(tcp, PseudoHeaderSum(srcIp, dstIp, 6, tcpLen)));
        return packet;
    }

    public static byte[] BuildIcmpEcho(byte[] srcMac, byte[] dstMac, IPAddress srcIp, IPAddress dstIp, int id, int seq,
        byte[]? payload = null, int ttl = 64, int ipId = 0, bool dontFragment = false)
    {
        payload ??= FingerprintEchoPayload;
        int icmpLen = 8 + payload.Length;
        var frame = new byte[EthernetHeader + 20 + icmpLen];
        WriteEthernet(frame, srcMac, dstMac, 0x0800);
        WriteIpv4(frame.AsSpan(EthernetHeader), 1, 20 + icmpLen, srcIp, dstIp, ttl, ipId, dontFragment);
        var icmp = frame.AsSpan(EthernetHeader + 20, icmpLen);
        icmp[0] = 8; icmp[1] = 0;
        WriteU16(icmp, 4, id); WriteU16(icmp, 6, seq);
        payload.CopyTo(icmp[8..]);
        WriteU16(icmp, 2, Checksum(icmp));
        return frame;
    }

    /// <summary>Wraps a bare IPv4 packet in an Ethernet II header.</summary>
    public static byte[] Frame(byte[] ipPacket, byte[] srcMac, byte[] dstMac)
    {
        var frame = new byte[EthernetHeader + ipPacket.Length];
        WriteEthernet(frame, srcMac, dstMac, 0x0800);
        ipPacket.CopyTo(frame, EthernetHeader);
        return frame;
    }

    /// <summary>The IPv4 packet inside an Ethernet frame, null for any other frame type.</summary>
    public static byte[]? StripEthernet(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < EthernetHeader + 20 || frame[12] != 0x08 || frame[13] != 0x00) return null;
        return frame[EthernetHeader..].ToArray();
    }

    public static ParsedPacket? Parse(ReadOnlySpan<byte> f)
    {
        if (f.Length < EthernetHeader) return null;
        int etherType = (f[12] << 8) | f[13];
        if (etherType != 0x0800) return null;
        return ParseIp(f[EthernetHeader..]) is { } p ? p with { SrcMac = f.Slice(6, 6).ToArray() } : null;
    }

    /// <summary>Parses a bare IPv4 packet (no link-layer header); null when it is malformed or not IPv4.</summary>
    public static ParsedPacket? ParseIp(ReadOnlySpan<byte> ip)
    {
        if (ip.Length < 20 || (ip[0] >> 4) != 4) return null;
        int ihl = (ip[0] & 0x0F) * 4;
        int total = (ip[2] << 8) | ip[3];
        if (ihl < 20 || ip.Length < ihl) return null;
        if (total > ip.Length || total < ihl) total = ip.Length;
        var body = ip.Slice(ihl, total - ihl);
        int proto = ip[9];
        var basePacket = new ParsedPacket
        {
            SrcIp = new IPAddress(ip.Slice(12, 4)), DstIp = new IPAddress(ip.Slice(16, 4)),
            Ttl = ip[8], IpId = (ip[4] << 8) | ip[5], DontFragment = (ip[6] & 0x40) != 0,
        };

        if (proto == 6 && body.Length >= 20)
        {
            int dataOff = (body[12] >> 4) * 4;
            if (dataOff < 20 || dataOff > body.Length) return null;
            var (kinds, mss, ws) = ParseTcpOptions(body.Slice(20, dataOff - 20));
            return basePacket with
            {
                Kind = PacketKind.Tcp, SrcPort = (body[0] << 8) | body[1], DstPort = (body[2] << 8) | body[3],
                Seq = ReadU32(body, 4), Ack = ReadU32(body, 8), Flags = (TcpFlags)body[13],
                Window = (body[14] << 8) | body[15], TcpOptions = kinds, Mss = mss, WindowScale = ws,
                Payload = body[dataOff..].ToArray(),
            };
        }
        if (proto == 1 && body.Length >= 8)
        {
            var icmp = basePacket with
            {
                Kind = PacketKind.Icmp, IcmpType = body[0], IcmpCode = body[1],
                IcmpId = (body[4] << 8) | body[5], IcmpSeq = (body[6] << 8) | body[7], Payload = body[8..].ToArray(),
            };
            // Destination unreachable quotes the original IP header plus 8 bytes: the probe it answers.
            var quoted = body[8..];
            if (body[0] == 3 && quoted.Length >= 20 && (quoted[0] >> 4) == 4)
            {
                int qIhl = (quoted[0] & 0x0F) * 4;
                if (qIhl >= 20 && quoted.Length >= qIhl + 4)
                    icmp = icmp with
                    {
                        QuotedDst = new IPAddress(quoted.Slice(16, 4)),
                        QuotedSrcPort = (quoted[qIhl] << 8) | quoted[qIhl + 1],
                        QuotedDstPort = (quoted[qIhl + 2] << 8) | quoted[qIhl + 3],
                    };
            }
            return icmp;
        }
        return basePacket with { Kind = PacketKind.Other };
    }

    private static (string Kinds, int Mss, int WindowScale) ParseTcpOptions(ReadOnlySpan<byte> o)
    {
        var names = new List<string>();
        int mss = 0, ws = -1;
        for (int i = 0; i < o.Length;)
        {
            byte kind = o[i];
            if (kind == 0) { names.Add("EOL"); break; }
            if (kind == 1) { names.Add("NOP"); i++; continue; }
            if (i + 1 >= o.Length) break;
            int len = o[i + 1];
            if (len < 2 || i + len > o.Length) break;
            switch (kind)
            {
                case 2: names.Add("MSS"); if (len == 4) mss = (o[i + 2] << 8) | o[i + 3]; break;
                case 3: names.Add("WS"); if (len == 3) ws = o[i + 2]; break;
                case 4: names.Add("SACK"); break;
                case 8: names.Add("TS"); break;
                default: names.Add("O" + kind); break;
            }
            i += len;
        }
        return (string.Join(",", names), mss, ws);
    }

    private static void WriteEthernet(byte[] frame, byte[] src, byte[] dst, int etherType)
    {
        dst.CopyTo(frame, 0); src.CopyTo(frame, 6);
        frame[12] = (byte)(etherType >> 8); frame[13] = (byte)etherType;
    }

    private static void WriteIpv4(Span<byte> ip, int proto, int totalLen, IPAddress src, IPAddress dst, int ttl, int id, bool df)
    {
        ip[0] = 0x45; ip[1] = 0;
        WriteU16(ip, 2, totalLen); WriteU16(ip, 4, id);
        WriteU16(ip, 6, df ? 0x4000 : 0);
        ip[8] = (byte)ttl; ip[9] = (byte)proto;
        src.GetAddressBytes().CopyTo(ip[12..]); dst.GetAddressBytes().CopyTo(ip[16..]);
        WriteU16(ip, 10, Checksum(ip[..20]));
    }

    internal static uint PseudoHeaderSum(IPAddress src, IPAddress dst, int proto, int len)
    {
        uint sum = 0;
        foreach (var b in new[] { src.GetAddressBytes(), dst.GetAddressBytes() })
            sum += (uint)((b[0] << 8) | b[1]) + (uint)((b[2] << 8) | b[3]);
        return sum + (uint)proto + (uint)len;
    }

    private static void WriteU16(Span<byte> s, int off, int v) { s[off] = (byte)(v >> 8); s[off + 1] = (byte)v; }

    private static void WriteU32(Span<byte> s, int off, uint v)
    { s[off] = (byte)(v >> 24); s[off + 1] = (byte)(v >> 16); s[off + 2] = (byte)(v >> 8); s[off + 3] = (byte)v; }

    private static uint ReadU32(ReadOnlySpan<byte> s, int off)
        => ((uint)s[off] << 24) | ((uint)s[off + 1] << 16) | ((uint)s[off + 2] << 8) | s[off + 3];
}
