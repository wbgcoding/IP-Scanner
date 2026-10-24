using System.Net;

namespace IpScanner.Core.Scanner;

/// <summary>Which TCP flags each raw scan type probes with.</summary>
internal static class TcpPacketBuilder
{
    /// <summary>The flag combination each raw scan type sends (the probe, not the expected reply).</summary>
    public static TcpFlags FlagsFor(ScanType type) => type switch
    {
        ScanType.Syn => TcpFlags.Syn,
        ScanType.Ack or ScanType.Window => TcpFlags.Ack,
        ScanType.Fin => TcpFlags.Fin,
        ScanType.Null => TcpFlags.None,
        ScanType.Xmas => TcpFlags.Fin | TcpFlags.Psh | TcpFlags.Urg,
        ScanType.Maimon => TcpFlags.Fin | TcpFlags.Ack,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "not a raw TCP scan type"),
    };
}

internal static class RawPacketParser
{
    /// <summary>True when <paramref name="reply"/> (a TCP segment from the target, or an ICMP destination-unreachable
    /// quoting our probe) answers the probe sent from <paramref name="sourcePort"/> to <paramref name="target"/>:<paramref name="port"/>.</summary>
    public static bool Answers(ParsedPacket reply, IPAddress target, int port, int sourcePort)
        => reply switch
        {
            { Kind: PacketKind.Tcp } => target.Equals(reply.SrcIp) && reply.SrcPort == port && reply.DstPort == sourcePort,
            { Kind: PacketKind.Icmp, IcmpType: 3 } => reply.QuotedDst is { } q && q.Equals(target)
                                                       && reply.QuotedDstPort == port && reply.QuotedSrcPort == sourcePort,
            _ => false,
        };
}

/// <summary>Maps a raw TCP probe type plus the reply (or silence) to a port state and reason,
/// following the RFC 793 behaviour the classic scanners rely on.</summary>
internal static class RawScanClassifier
{
    public static (PortState State, string Reason) Classify(ScanType type, ParsedPacket? reply)
    {
        if (reply is { Kind: PacketKind.Icmp, IcmpType: 3 })
            return (PortState.Filtered, PortReason.IcmpUnreachable);

        bool synAck = reply is { Kind: PacketKind.Tcp } r1 && r1.Flags.HasFlag(TcpFlags.Syn) && r1.Flags.HasFlag(TcpFlags.Ack);
        bool rst = reply is { Kind: PacketKind.Tcp } r2 && r2.Flags.HasFlag(TcpFlags.Rst);

        switch (type)
        {
            case ScanType.Syn:
                if (synAck) return (PortState.Open, PortReason.SynAck);
                if (rst) return (PortState.Closed, PortReason.Rst);
                return (PortState.Filtered, PortReason.NoResponse);
            case ScanType.Ack:
                if (rst) return (PortState.Unfiltered, PortReason.Rst);
                return (PortState.Filtered, PortReason.NoResponse);
            case ScanType.Window:
                if (rst) return reply!.Window > 0
                    ? (PortState.Open, PortReason.RstWindow)
                    : (PortState.Closed, PortReason.Rst);
                return (PortState.Filtered, PortReason.NoResponse);
            case ScanType.Fin:
            case ScanType.Null:
            case ScanType.Xmas:
            case ScanType.Maimon:
                if (rst) return (PortState.Closed, PortReason.Rst);
                // A SYN/ACK to a stealth probe is non-standard: report it as open instead of guessing.
                if (synAck) return (PortState.Open, PortReason.SynAck);
                return (PortState.OpenFiltered, PortReason.NoResponse);
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, "not a raw TCP scan type");
        }
    }
}

/// <summary>Sends one prepared IPv4 packet and waits for the first packet that matches, the seam
/// to whatever packet driver backs the raw scans.</summary>
internal interface IRawTransport : IDisposable
{
    /// <summary>The local address probes are sent from (they must carry a valid source).</summary>
    IPAddress LocalAddress { get; }

    /// <returns>The raw IPv4 bytes of the first received packet for which <paramref name="isReply"/> holds, null on timeout.</returns>
    Task<byte[]?> ExchangeAsync(byte[] ipPacket, Func<byte[], bool> isReply, int timeoutMs, CancellationToken ct);
}

/// <summary>Where the raw scans get their transport. Stays null without a usable packet driver, so the
/// scan types that need it stay greyed out.</summary>
internal static class RawTransportProvider
{
    internal static Func<IPAddress, IRawTransport?>? Factory { get; set; }

    public static IRawTransport? Create(IPAddress target) => Factory?.Invoke(target);

    /// <summary>Startup hook: backs the raw scans with the capture driver when it is installed and usable.</summary>
    public static void RegisterDriverTransport()
    {
        if (Factory is null && PacketDriver.Available) Factory = target => WpcapRawTransport.Create(target);
    }
}
