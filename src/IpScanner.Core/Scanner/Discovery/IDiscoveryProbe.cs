using System.Net;

namespace IpScanner.Core.Scanner.Discovery;

/// <param name="Found">The host answered.</param>
/// <param name="Method">Which probe found it: icmp, arp, icmp-ts, icmp-mask, tcp:PORT, tcp-syn:PORT, tcp-ack:PORT, udp:PORT, skip, list.</param>
public readonly record struct DiscoveryResult(bool Found, string Method, double? RttMs, int? Ttl = null)
{
    public static DiscoveryResult None(string method = "") => new(false, method, null);
}

public interface IDiscoveryProbe
{
    DiscoveryMode Mode { get; }
    Task<DiscoveryResult> ProbeAsync(string target, CancellationToken ct);
}

public enum TcpOutcome { NoAnswer, Open, Refused }

/// <summary>Every packet the discovery probes put on the wire goes through here, so tests can fake and count it.</summary>
public interface IDiscoveryNet
{
    Task<TcpOutcome> TcpConnectAsync(string ip, int port, int timeoutMs, CancellationToken ct);

    /// <summary>Sends one datagram. True when the target replied or answered with port unreachable (it is up either way).</summary>
    Task<bool> UdpExchangeAsync(string ip, int port, byte[] payload, int timeoutMs, CancellationToken ct);

    /// <summary>Raw ICMP request of the given type (13 timestamp, 17 address mask); true on the matching reply. Needs elevation.</summary>
    Task<bool> IcmpRawAsync(string ip, byte type, int timeoutMs, CancellationToken ct);

    /// <summary>Live ARP request through the OS; no table lookups.</summary>
    string? ArpResolve(string ip);

    /// <summary>True when the target sits in a subnet of a local adapter (ARP can reach it).</summary>
    bool IsOnLink(string ip);

    IRawLink? OpenLink(string deviceName, string? bpf);

    RawRoute? RouteTo(IPAddress target);
}
