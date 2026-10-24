using IpScanner.Core.Models;
using IpScanner.Core.Scanner;

namespace IpScanner.Core.Net;

/// <summary>Every IPv4 range that counts as the user's own: the full network of each up network card,
/// each configured subnet and each pinned IP. Used to decide whether a target lies outside all of them.
/// Ranges are checked by exact address, so wide networks (a /16 card, a /8 entry) need no prefix cap.</summary>
public sealed class OwnNetworks
{
    private readonly List<(uint First, uint Last)> _ranges = new();

    private OwnNetworks() { }

    /// <summary>Builds the set from the raw configured subnet entries, the raw pinned entries and the given
    /// cards (null reads the machine's own). IPv6 and hostname entries are skipped.</summary>
    public static OwnNetworks Build(IEnumerable<string> subnets, IEnumerable<string> pinned,
        IEnumerable<NetworkDetector.NetworkCard>? cards = null)
    {
        var own = new OwnNetworks();
        foreach (var card in cards ?? NetworkDetector.ListIpv4Cards())
            own.AddNetwork(card.Ip, Ipv4.MaskToCidr(card.SubnetMask) ?? 24);
        foreach (var raw in subnets)
        {
            var slash = NetEntry.Parse(raw).Target.Split('/');
            int cidr = slash.Length > 1 && int.TryParse(slash[1].Trim(), out var c) ? c : 24;
            own.AddNetwork(slash[0].Trim(), cidr);
        }
        foreach (var raw in pinned)
        {
            if (Ipv4.TryParseUInt(NetEntry.Parse(raw).Target.Trim(), out var v)) own._ranges.Add((v, v));
        }
        return own;
    }

    private void AddNetwork(string ip, int cidr)
    {
        if (cidr is < 0 or > 32 || !Ipv4.TryParseUInt(ip, out var addr)) return;
        int hostBits = 32 - cidr;
        uint blockMask = hostBits >= 32 ? 0u : 0xFFFFFFFFu << hostBits;
        uint network = addr & blockMask;
        _ranges.Add((network, network | ~blockMask));
    }

    /// <summary>True when <paramref name="ip"/> lies inside any own range. Unparseable text is not contained.</summary>
    public bool Contains(string ip)
    {
        if (!Ipv4.TryParseUInt(ip.Trim(), out var v)) return false;
        foreach (var (first, last) in _ranges)
            if (v >= first && v <= last) return true;
        return false;
    }
}
