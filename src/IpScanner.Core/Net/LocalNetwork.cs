using IpScanner.Core.Models;

namespace IpScanner.Core.Net;

/// <summary>Decides whether a target address sits on a network the machine itself is attached to.</summary>
public static class LocalNetwork
{
    /// <summary>True when <paramref name="ip"/> lies in the detected IPv4 subnet or, for an IPv6 target, on one of
    /// the card's own IPv6 networks (see <see cref="IsOnLinkIpv6"/>). Unparseable text is never local.</summary>
    public static bool Contains(string ip, NetworkInfo net)
    {
        if (Ipv6Address.TryParse(StripBrackets(ip), out _)) return IsOnLinkIpv6(ip, net.Ipv6);
        if (Ipv4.MaskToCidr(net.SubnetMask) is not { } cidr || Ipv4.HostRange(net.Ip ?? "", cidr) is not { } range) return false;
        return Ipv4.TryParseUInt(ip, out var v) && v >= range.First && v <= range.Last;
    }

    /// <summary>An IPv6 target is on-link when it is link-local and its zone names one of the own cards (a bare
    /// link-local address counts when the machine has any link-local address), or when it lies inside the prefix
    /// of one of the own non-link-local addresses. Multicast, loopback and IPv4-mapped targets are never on-link.</summary>
    public static bool IsOnLinkIpv6(string ip, IEnumerable<OwnIpv6Address> own)
    {
        if (!Ipv6Address.TryParse(StripBrackets(ip), out var target)) return false;
        var list = own as IReadOnlyCollection<OwnIpv6Address> ?? own.ToList();
        switch (target.Kind)
        {
            case Ipv6Kind.LinkLocal:
                if (target.Zone is null) return list.Any(o => o.IsLinkLocal);
                return list.Any(o => o.Interface.Equals(target.Zone, StringComparison.OrdinalIgnoreCase)
                    || o.InterfaceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) == target.Zone);
            case Ipv6Kind.UniqueLocal or Ipv6Kind.Global or Ipv6Kind.Documentation or Ipv6Kind.Reserved:
                return list.Any(o => !o.IsLinkLocal && o.Prefix.Contains(target));
            default:
                return false;
        }
    }

    private static string StripBrackets(string ip)
        => ip.Length > 1 && ip[0] == '[' && ip[^1] == ']' ? ip[1..^1] : ip;
}
