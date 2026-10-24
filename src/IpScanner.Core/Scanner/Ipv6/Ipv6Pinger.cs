using System.Globalization;
using IpScanner.Core.Models;
using IpScanner.Core.Net;
using IpScanner.Core.Scanner.Discovery;

namespace IpScanner.Core.Scanner.Ipv6;

/// <summary>The ping function and the MAC lookup for IPv6 targets. A host is up when it answers an echo or
/// accepts or refuses a TCP connect on one of a few common ports; a link-local target without a zone is
/// completed from the neighbour cache.</summary>
public static class Ipv6Pinger
{
    /// <summary>Ports tried when a host ignores echo requests.</summary>
    public static readonly int[] FallbackPorts = { 443, 80, 22, 445, 3389 };

    private static readonly Lazy<(Ipv6Discovery Discovery, INeighborTable Table)> Real = new(() =>
    {
        var table = new WindowsNeighborTable();
        return (new Ipv6Discovery(table, new SystemIpv6Net(), SystemDiscoveryNet.Instance), table);
    });

    public static bool IsIpv6(string ip) => ip.Contains(':');

    public static PingResult Ping(string ip, int timeoutMs) => Ping(Real.Value.Discovery, Real.Value.Table, ip, timeoutMs);

    internal static PingResult Ping(Ipv6Discovery discovery, INeighborTable table, string ip, int timeoutMs)
    {
        if (!Ipv6Address.TryParse(ip, out var address)) return new PingResult(false, null, null);
        address = WithZone(address, table);
        try
        {
            var r = discovery.ProbeTargetAsync(address, FallbackPorts, timeoutMs).GetAwaiter().GetResult();
            return new PingResult(r.Found, r.RttMs, null, r.Found ? r.Method : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug(LogArea.Scan, "IPv6 probe failed: " + ex.GetType().Name);
            return new PingResult(false, null, null);
        }
    }

    /// <summary>The MAC of an IPv6 neighbour from the system cache, or null (routed and unknown hosts have none).</summary>
    public static string? MacOf(string ip) => MacOf(Real.Value.Table, ip);

    internal static string? MacOf(INeighborTable table, string ip)
    {
        if (!Ipv6Address.TryParse(ip, out var address)) return null;
        try
        {
            foreach (var n in table.Read())
                if (n.Address.Value == address.Value && n.Mac is not null) return n.Mac;
        }
        catch (Exception ex) { Log.Debug(LogArea.Scan, "Neighbour lookup failed: " + ex.GetType().Name); }
        return null;
    }

    private static Ipv6Address WithZone(Ipv6Address address, INeighborTable table)
    {
        if (!address.IsLinkLocal || address.Zone is not null) return address;
        try
        {
            foreach (var n in table.Read())
                if (n.Address.Value == address.Value)
                    return address.WithZone(n.InterfaceIndex.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex) { Log.Debug(LogArea.Scan, "Neighbour lookup failed: " + ex.GetType().Name); }
        return address;
    }
}
