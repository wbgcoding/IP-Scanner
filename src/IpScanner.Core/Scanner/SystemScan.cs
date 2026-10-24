using IpScanner.Core.Models;
using IpScanner.Core.Scanner.Ipv6;

namespace IpScanner.Core.Scanner;

/// <summary>The real ping and the real MAC/name techniques, one set for the window and the command line.
/// IPv6 targets take the echo/TCP probe and the neighbour cache; the IPv4-only techniques skip them.</summary>
public static class SystemScan
{
    public static PingResult Ping(string ip, int timeoutMs, string? sourceIp)
        => Ipv6Pinger.IsIpv6(ip) ? Ipv6Pinger.Ping(ip, timeoutMs) : IcmpPinger.Ping(ip, timeoutMs, sourceIp);

    public static Func<string, CancellationToken, Task<(string? mac, string? host)>>[] Enrichers(int mdnsTimeoutMs)
        => new Func<string, CancellationToken, Task<(string? mac, string? host)>>[]
    {
        async (ip, ct) => Ipv6Pinger.IsIpv6(ip)
            ? (Ipv6Pinger.MacOf(ip), null)
            : (await ArpHelper.ResolveAsync(ip, ct).ConfigureAwait(false), null),
        async (ip, ct) => (null, await HostnameResolver.ResolveAsync(ip, ct).ConfigureAwait(false)),
        EnrichTechnique.FromSync(ip => Ipv6Pinger.IsIpv6(ip) ? (null, null) : (null, MdnsHelper.Resolve(ip, mdnsTimeoutMs))),
        async (ip, ct) =>
        {
            if (Ipv6Pinger.IsIpv6(ip)) return (null, null);
            var (name, mac) = await NetBiosHelper.LookupAsync(ip, ct).ConfigureAwait(false);
            return (mac, name);
        },
    };
}
