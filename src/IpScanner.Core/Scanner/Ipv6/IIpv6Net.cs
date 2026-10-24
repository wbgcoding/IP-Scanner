using IpScanner.Core.Net;

namespace IpScanner.Core.Scanner.Ipv6;

/// <summary>One answer to an ICMPv6 echo request.</summary>
public readonly record struct Ipv6EchoReply(Ipv6Address From, double RttMs);

/// <summary>The ICMPv6 packets discovery sends; a seam so tests can fake and count them.</summary>
public interface IIpv6Net
{
    /// <summary>Sends one echo request and returns every reply that arrived in time. A link-local or
    /// multicast target carries its interface index as the zone. Empty when nobody answered.</summary>
    Task<IReadOnlyList<Ipv6EchoReply>> EchoAsync(Ipv6Address target, int timeoutMs, CancellationToken ct);
}
