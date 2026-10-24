using System.Globalization;
using System.Text;

namespace IpScanner.Core.Net;

/// <summary>What an IPv6 address is for, by its leading bits.</summary>
public enum Ipv6Kind
{
    Unspecified,
    Loopback,
    LinkLocal,
    UniqueLocal,
    Multicast,
    IPv4Mapped,
    Documentation,
    Global,
    Reserved,
}

/// <summary>One IPv6 address: the 128-bit value plus an optional zone (scope) id as written after '%'.
/// Equality includes the zone; use <see cref="Value"/> to compare addresses only.</summary>
public readonly record struct Ipv6Address(UInt128 Value, string? Zone = null)
{
    public static readonly Ipv6Address Unspecified = new(UInt128.Zero);
    public static readonly Ipv6Address AllNodes = new((UInt128)0xFF02 << 112 | 1);

    private const int MaxZoneLength = 64;

    /// <summary>Strict parser: hex groups of 1 to 4 digits, one optional "::", an optional trailing dotted
    /// IPv4 part, an optional "%zone". No brackets, no whitespace, no prefix length.</summary>
    public static bool TryParse(string? text, out Ipv6Address address)
    {
        address = default;
        if (string.IsNullOrEmpty(text)) return false;

        string? zone = null;
        var body = text;
        int pct = text.IndexOf('%');
        if (pct >= 0)
        {
            zone = text[(pct + 1)..];
            body = text[..pct];
            if (!IsValidZone(zone)) return false;
        }
        if (!TryParseBody(body, out var value)) return false;
        address = new Ipv6Address(value, zone);
        return true;
    }

    public static Ipv6Address Parse(string text)
        => TryParse(text, out var a) ? a : throw new FormatException("Not an IPv6 address.");

    public static Ipv6Address FromBytes(ReadOnlySpan<byte> bytes, string? zone = null)
    {
        if (bytes.Length != 16) throw new ArgumentException("An IPv6 address has 16 bytes.", nameof(bytes));
        UInt128 v = 0;
        foreach (var b in bytes) v = (v << 8) | b;
        return new Ipv6Address(v, zone);
    }

    public byte[] GetBytes()
    {
        var bytes = new byte[16];
        for (int i = 0; i < 16; i++) bytes[i] = (byte)(Value >> (120 - 8 * i));
        return bytes;
    }

    public Ipv6Address WithZone(string? zone) => new(Value, string.IsNullOrEmpty(zone) ? null : zone);

    /// <summary>The address as a 16-bit group (0 = leftmost).</summary>
    private ushort Group(int index) => (ushort)(Value >> (112 - 16 * index));

    public Ipv6Kind Kind
    {
        get
        {
            if (Value == 0) return Ipv6Kind.Unspecified;
            if (Value == 1) return Ipv6Kind.Loopback;
            if ((Value >> 120) == 0xFF) return Ipv6Kind.Multicast;
            if ((Value >> 118) == 0x3FA) return Ipv6Kind.LinkLocal;      // fe80::/10
            if ((Value >> 121) == 0x7E) return Ipv6Kind.UniqueLocal;     // fc00::/7
            if ((Value >> 32) == 0xFFFF) return Ipv6Kind.IPv4Mapped;     // ::ffff:0:0/96
            if ((Value >> 96) == 0x20010DB8) return Ipv6Kind.Documentation;
            if ((Value >> 125) == 1) return Ipv6Kind.Global;             // 2000::/3
            return Ipv6Kind.Reserved;
        }
    }

    public bool IsLinkLocal => Kind == Ipv6Kind.LinkLocal;
    public bool IsMulticast => Kind == Ipv6Kind.Multicast;

    /// <summary>The embedded IPv4 address of an IPv4-mapped address, else null.</summary>
    public string? MappedIpv4 => Kind == Ipv6Kind.IPv4Mapped ? Ipv4.FromUInt((uint)Value) : null;

    /// <summary>Normalized text (RFC 5952): lower case, no leading zeros, the longest run of two or more zero
    /// groups written "::" (the first on a tie), IPv4-mapped addresses as "::ffff:a.b.c.d". No zone.</summary>
    public override string ToString()
    {
        if (Kind == Ipv6Kind.IPv4Mapped) return "::ffff:" + MappedIpv4;

        int bestStart = -1, bestLen = 0;
        for (int i = 0; i < 8;)
        {
            if (Group(i) != 0) { i++; continue; }
            int j = i;
            while (j < 8 && Group(j) == 0) j++;
            if (j - i > bestLen) { bestStart = i; bestLen = j - i; }
            i = j;
        }
        if (bestLen < 2) bestStart = -1;

        var sb = new StringBuilder(39);
        for (int i = 0; i < 8; i++)
        {
            if (i == bestStart)
            {
                sb.Append("::");
                i += bestLen - 1;
                continue;
            }
            if (sb.Length > 0 && sb[^1] != ':') sb.Append(':');
            sb.Append(Group(i).ToString("x", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>Normalized text with the zone appended when there is one ("fe80::1%12").</summary>
    public string ToScopedString() => Zone is null ? ToString() : ToString() + "%" + Zone;

    /// <summary>The nibble-reversed ip6.arpa name used for reverse lookups.</summary>
    public string ReverseName()
    {
        var sb = new StringBuilder(72);
        for (int i = 0; i < 32; i++)
            sb.Append("0123456789abcdef"[(int)((Value >> (4 * i)) & 0xF)]).Append('.');
        return sb.Append("ip6.arpa").ToString();
    }

    /// <summary>The MAC a modified EUI-64 interface id (ff:fe in the middle, flipped universal/local bit) was built
    /// from, as "AA:BB:CC:DD:EE:FF"; null when the interface id is not EUI-64 shaped. The result is only a
    /// hint: privacy addresses and stable-opaque ids never match, and some hosts fake the pattern.</summary>
    public string? TryGetEui64Mac()
    {
        var b = GetBytes();
        if (b[11] != 0xFF || b[12] != 0xFE) return null;
        if (Kind is not (Ipv6Kind.LinkLocal or Ipv6Kind.Global or Ipv6Kind.UniqueLocal)) return null;
        return string.Join(':', new[] { (byte)(b[8] ^ 0x02), b[9], b[10], b[13], b[14], b[15] }
            .Select(x => x.ToString("X2", CultureInfo.InvariantCulture)));
    }

    /// <summary>The address with this prefix's upper 64 bits and the modified EUI-64 interface id of the MAC.</summary>
    public static Ipv6Address FromEui64(UInt128 upper64Value, ReadOnlySpan<byte> mac)
    {
        if (mac.Length != 6) throw new ArgumentException("A MAC address has 6 bytes.", nameof(mac));
        ulong iid = (ulong)(mac[0] ^ 0x02) << 56 | (ulong)mac[1] << 48 | (ulong)mac[2] << 40
                    | 0xFFUL << 32 | 0xFEUL << 24
                    | (ulong)mac[3] << 16 | (ulong)mac[4] << 8 | mac[5];
        return new Ipv6Address((upper64Value & ~(UInt128)ulong.MaxValue) | iid);
    }

    /// <summary>The solicited-node multicast address (ff02::1:ffXX:XXXX) of this address.</summary>
    public Ipv6Address SolicitedNode()
        => new(((UInt128)0xFF02 << 112) | ((UInt128)1 << 32) | ((UInt128)0xFF << 24) | (Value & 0xFFFFFF));

    // ---- parsing ----

    private static bool IsValidZone(string zone)
    {
        if (zone.Length is 0 or > MaxZoneLength) return false;
        foreach (var c in zone)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-')) return false;
        return true;
    }

    private static bool TryParseBody(string s, out UInt128 value)
    {
        value = 0;
        int dbl = s.IndexOf("::", StringComparison.Ordinal);
        if (dbl >= 0 && s.IndexOf("::", dbl + 1, StringComparison.Ordinal) >= 0) return false;

        var head = dbl < 0 ? s : s[..dbl];
        var tail = dbl < 0 ? "" : s[(dbl + 2)..];
        if (!TryGroups(head, dbl < 0, out var h)) return false;
        if (!TryGroups(tail, true, out var t)) return false;

        if (dbl < 0 ? h.Count != 8 : h.Count + t.Count > 7) return false;

        var all = new ushort[8];
        for (int i = 0; i < h.Count; i++) all[i] = h[i];
        for (int i = 0; i < t.Count; i++) all[8 - t.Count + i] = t[i];
        foreach (var g in all) value = (value << 16) | g;
        return true;
    }

    private static bool TryGroups(string part, bool allowIpv4Tail, out List<ushort> groups)
    {
        groups = new List<ushort>(8);
        if (part.Length == 0) return true;
        var pieces = part.Split(':');
        for (int i = 0; i < pieces.Length; i++)
        {
            var p = pieces[i];
            if (p.Length == 0) return false;
            if (p.Contains('.'))
            {
                if (!allowIpv4Tail || i != pieces.Length - 1 || !TryStrictIpv4(p, out var v4)) return false;
                groups.Add((ushort)(v4 >> 16));
                groups.Add((ushort)v4);
                continue;
            }
            if (p.Length > 4) return false;
            foreach (var c in p)
                if (!char.IsAsciiHexDigit(c)) return false;
            groups.Add(ushort.Parse(p, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
        }
        return true;
    }

    private static bool TryStrictIpv4(string text, out uint value)
    {
        value = 0;
        var parts = text.Split('.');
        if (parts.Length != 4) return false;
        foreach (var p in parts)
        {
            if (p.Length is 0 or > 3 || (p.Length > 1 && p[0] == '0')) return false;
            foreach (var c in p)
                if (!char.IsAsciiDigit(c)) return false;
            int n = int.Parse(p, CultureInfo.InvariantCulture);
            if (n > 255) return false;
            value = (value << 8) | (uint)n;
        }
        return true;
    }
}

/// <summary>An IPv6 network: the masked 128-bit network address and a prefix length 0 to 128.</summary>
public readonly record struct Ipv6Prefix
{
    public UInt128 Network { get; }
    public int Length { get; }

    public Ipv6Prefix(UInt128 address, int length)
    {
        if (length is < 0 or > 128) throw new ArgumentOutOfRangeException(nameof(length));
        Length = length;
        Network = address & MaskOf(length);
    }

    /// <summary>"address/length" (address in any valid form, host bits are cleared); a bare address is a /128.</summary>
    public static bool TryParse(string? text, out Ipv6Prefix prefix)
    {
        prefix = default;
        if (string.IsNullOrEmpty(text)) return false;
        var parts = text.Split('/');
        if (parts.Length > 2) return false;
        if (!Ipv6Address.TryParse(parts[0], out var addr) || addr.Zone is not null) return false;
        int len = 128;
        if (parts.Length == 2)
        {
            var l = parts[1];
            if (l.Length is 0 or > 3 || !l.All(char.IsAsciiDigit) || (l.Length > 1 && l[0] == '0')) return false;
            len = int.Parse(l, CultureInfo.InvariantCulture);
            if (len > 128) return false;
        }
        prefix = new Ipv6Prefix(addr.Value, len);
        return true;
    }

    public static UInt128 MaskOf(int length)
        => length <= 0 ? UInt128.Zero : length >= 128 ? UInt128.MaxValue : UInt128.MaxValue << (128 - length);

    public Ipv6Address First => new(Network);
    public Ipv6Address Last => new(Network | ~MaskOf(Length));

    /// <summary>log2 of the number of addresses in the prefix (128 minus the length).</summary>
    public int HostBits => 128 - Length;

    public bool Contains(Ipv6Address address) => (address.Value & MaskOf(Length)) == Network;

    public bool Contains(Ipv6Prefix other) => other.Length >= Length && (other.Network & MaskOf(Length)) == Network;

    /// <summary>True when the prefix holds more than <paramref name="maxAddresses"/> addresses.</summary>
    public bool ExceedsAddresses(ulong maxAddresses)
        => HostBits >= 64 || (UInt128.One << HostBits) > maxAddresses;

    public override string ToString() => new Ipv6Address(Network) + "/" + Length.ToString(CultureInfo.InvariantCulture);
}
