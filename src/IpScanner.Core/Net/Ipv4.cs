namespace IpScanner.Core.Net;

public static class Ipv4
{
    public const int FirstHost = 1;
    public const int LastHost = 254;
    /// <summary>Usable host addresses in a /24 (FirstHost..LastHost).</summary>
    public const int HostsPerSubnet = LastHost - FirstHost + 1;

    public static bool IsValid(string text)
    {
        var parts = text.Split('.');
        return parts.Length == 4 &&
               parts.All(p => int.TryParse(p, out var n) && n is >= 0 and <= 255);
    }

    /// <summary>"a.b.c.d" or "a.b.c.d/1..32" - shared by the sidebar's manual-entry
    /// validation and the file-import parser.</summary>
    public static bool IsValidSubnetEntry(string entry)
    {
        var parts = entry.Split('/');
        if (parts.Length > 2 || !IsValid(parts[0].Trim())) return false;
        return parts.Length == 1 ||
               (int.TryParse(parts[1].Trim(), out var cidr) && cidr is >= 1 and <= 32);
    }

    public static string SubnetPrefix(string ip)
    {
        var parts = ip.Split('.');
        return parts.Length >= 3 ? string.Join('.', parts[..3]) : ip;
    }

    /// <summary>Numeric sort key for an IPv4 string (0 when unparseable).</summary>
    public static long SortKey(string ip)
    {
        var p = ip.Split('.');
        if (p.Length != 4) return 0;
        long key = 0;
        foreach (var part in p)
        {
            if (!int.TryParse(part, out var n)) return 0;
            key = key * 256 + n;
        }
        return key;
    }

    public static List<string> HostsInSubnet(string prefix)
    {
        var list = new List<string>(LastHost - FirstHost + 1);
        for (int i = FirstHost; i <= LastHost; i++)
            list.Add($"{prefix}.{i}");
        return list;
    }

    /// <summary>Hard cap on the number of /24 prefixes one scan may enumerate
    /// (~65,000 devices at 254 hosts/prefix) - keeps a fat-fingered mask or
    /// pinned-IP pile-up from allocating millions of ping tasks at once.</summary>
    public const int MaxPrefixes = 256;

    public static bool TryParseUInt(string ip, out uint value)
    {
        value = 0;
        var p = ip.Split('.');
        if (p.Length != 4) return false;
        uint v = 0;
        foreach (var part in p)
        {
            if (!byte.TryParse(part, out var b)) return false;
            v = (v << 8) | b;
        }
        value = v;
        return true;
    }

    internal static string FromUInt(uint v)
        => $"{v >> 24 & 0xFF}.{v >> 16 & 0xFF}.{v >> 8 & 0xFF}.{v & 0xFF}";

    /// <summary>Converts a dotted-decimal subnet mask ("255.255.252.0") to its
    /// CIDR prefix length, or null if it isn't a valid contiguous mask.</summary>
    public static int? MaskToCidr(string? mask)
    {
        if (!TryParseUInt(mask ?? "", out var m)) return null;
        int ones = System.Numerics.BitOperations.PopCount(m);
        // A valid mask is a run of 1s followed by 0s (e.g. never 255.0.255.0).
        uint expected = ones == 0 ? 0u : 0xFFFFFFFFu << (32 - ones);
        return m == expected ? ones : null;
    }

    /// <summary>The directed-broadcast address for a card's own IP + subnet
    /// mask (host bits all set to 1) - reaches devices on that segment even through
    /// a router that blocks the general 255.255.255.255 broadcast. Null when the ip
    /// or mask does not parse to a valid contiguous mask.</summary>
    public static string? DirectedBroadcast(string ip, string? mask)
    {
        if (!TryParseUInt(ip, out var addr)) return null;
        if (MaskToCidr(mask) is not { } cidr) return null;

        int hostBits = 32 - cidr;
        uint blockMask = hostBits >= 32 ? 0u : 0xFFFFFFFFu << hostBits;
        uint broadcast = hostBits >= 32 ? 0xFFFFFFFFu : addr | ~blockMask;
        return FromUInt(broadcast);
    }

    /// <summary>Inclusive first/last scannable address of "ip/cidr": a /32 is the
    /// single address, a /31 both addresses, anything wider network+1 to
    /// broadcast-1. Null when the input is not a usable IPv4 network.</summary>
    public static (uint First, uint Last)? HostRange(string ip, int cidr)
    {
        if (cidr is < 1 or > 32) return null;
        if (!TryParseUInt(ip, out var addr)) return null;
        int hostBits = 32 - cidr;
        uint blockMask = hostBits >= 32 ? 0u : 0xFFFFFFFFu << hostBits;
        uint network = addr & blockMask;
        uint broadcast = network | ~blockMask;
        if (cidr >= 31) return (network, broadcast);
        return (network + 1, broadcast - 1);
    }

    /// <summary>The addresses a scan pings for the given /24 prefixes. Without
    /// ranges that is .1 to .254 of every prefix; with ranges only addresses
    /// inside at least one range are kept.</summary>
    public static List<string> HostsInRanges(IEnumerable<string> prefixes,
                                             IReadOnlyList<(uint First, uint Last)>? ranges)
    {
        if (ranges is null) return prefixes.SelectMany(HostsInSubnet).ToList();
        var list = new List<string>();
        foreach (var prefix in prefixes)
        {
            if (!TryParseUInt(prefix + ".0", out var baseAddr)) continue;
            for (uint i = 0; i <= 255; i++)
            {
                uint a = baseAddr + i;
                foreach (var (first, last) in ranges)
                    if (a >= first && a <= last) { list.Add(FromUInt(a)); break; }
            }
        }
        return list;
    }

    /// <summary>Expands "ip/cidr" into the exact /24 prefixes the mask covers,
    /// aligned to the real network address (not the typed octets) - /20 yields
    /// 16 prefixes, /16 yields 256, /8 (or smaller) is rejected as too large.
    /// Returns null when the input isn't a usable IPv4 subnet.</summary>
    public static List<string>? PrefixesForCidr(string ip, int cidr)
    {
        if (cidr is < 1 or > 32) return null;
        if (!TryParseUInt(ip, out var addr)) return null;

        int hostBits = 32 - cidr;
        uint blockMask = hostBits >= 32 ? 0u : 0xFFFFFFFFu << hostBits;
        uint network = addr & blockMask;
        uint broadcast = hostBits >= 32 ? 0xFFFFFFFFu : network | ~blockMask;

        uint firstPrefix = network >> 8;
        uint lastPrefix = broadcast >> 8;
        long count = lastPrefix - firstPrefix + 1;
        if (count > MaxPrefixes) return null;

        var list = new List<string>((int)count);
        for (uint p = firstPrefix; p <= lastPrefix; p++)
        {
            var octets = FromUInt(p << 8).Split('.');
            list.Add($"{octets[0]}.{octets[1]}.{octets[2]}");
            if (p == uint.MaxValue) break;   // guard against wraparound at 255.255.255
        }
        return list;
    }
}
