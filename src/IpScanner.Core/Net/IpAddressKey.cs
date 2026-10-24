namespace IpScanner.Core.Net;

public enum IpFamily
{
    V4 = 4,
    V6 = 6,
}

/// <summary>A host address of either family in one comparable value: the family, the numeric value and the
/// normalized text. Ordering puts every IPv4 address before every IPv6 address and sorts numerically inside a
/// family. A zone id never takes part in equality or order (the same host is reachable on one link only).</summary>
public readonly struct IpAddressKey : IEquatable<IpAddressKey>, IComparable<IpAddressKey>, IComparable
{
    public IpFamily Family { get; }
    public UInt128 Value { get; }
    /// <summary>Normalized text: dotted decimal, or RFC 5952 for IPv6 (without zone).</summary>
    public string Text { get; }
    public string? Zone { get; }

    private IpAddressKey(IpFamily family, UInt128 value, string text, string? zone)
    {
        Family = family;
        Value = value;
        Text = text;
        Zone = zone;
    }

    public bool IsValid => Text is not null;
    public bool IsV6 => Family == IpFamily.V6;

    public static IpAddressKey FromV4(uint value) => new(IpFamily.V4, value, Ipv4.FromUInt(value), null);

    public static IpAddressKey From(Ipv6Address address) => new(IpFamily.V6, address.Value, address.ToString(), address.Zone);

    /// <summary>Parses an IPv4 dotted-decimal or an IPv6 address (with an optional zone). IPv4 must be
    /// exactly four plain decimal parts; the key of "192.168.001.5" does not exist.</summary>
    public static bool TryParse(string? text, out IpAddressKey key)
    {
        key = default;
        if (string.IsNullOrEmpty(text)) return false;
        if (text.Contains(':'))
        {
            if (!Ipv6Address.TryParse(text, out var v6)) return false;
            key = From(v6);
            return true;
        }
        if (!IsPlainIpv4(text) || !Ipv4.TryParseUInt(text, out var v4)) return false;
        key = FromV4(v4);
        return true;
    }

    /// <summary>The normalized text of an address of either family, or the input unchanged when it parses as neither.</summary>
    public static string Normalize(string text) => TryParse(text, out var k) ? k.Text : text;

    /// <summary>String comparer for address columns that may hold either family: IPv4 first, numeric order inside
    /// a family; text that is no address sorts last, ordinally.</summary>
    public static IComparer<string?> TextComparer { get; } = Comparer<string?>.Create((a, b) =>
    {
        bool pa = TryParse(a, out var ka), pb = TryParse(b, out var kb);
        if (pa && pb) return ka.CompareTo(kb);
        if (pa != pb) return pa ? -1 : 1;
        return string.CompareOrdinal(a, b);
    });

    public int CompareTo(IpAddressKey other)
    {
        int c = Family.CompareTo(other.Family);
        return c != 0 ? c : Value.CompareTo(other.Value);
    }

    int IComparable.CompareTo(object? obj) => obj is IpAddressKey other ? CompareTo(other) : 1;

    public bool Equals(IpAddressKey other) => Family == other.Family && Value == other.Value;
    public override bool Equals(object? obj) => obj is IpAddressKey k && Equals(k);
    public override int GetHashCode() => HashCode.Combine((int)Family, Value);
    public override string ToString() => Text ?? "";

    public static bool operator ==(IpAddressKey a, IpAddressKey b) => a.Equals(b);
    public static bool operator !=(IpAddressKey a, IpAddressKey b) => !a.Equals(b);
    public static bool operator <(IpAddressKey a, IpAddressKey b) => a.CompareTo(b) < 0;
    public static bool operator >(IpAddressKey a, IpAddressKey b) => a.CompareTo(b) > 0;

    private static bool IsPlainIpv4(string text)
    {
        var parts = text.Split('.');
        if (parts.Length != 4) return false;
        foreach (var p in parts)
        {
            if (p.Length is 0 or > 3 || (p.Length > 1 && p[0] == '0')) return false;
            foreach (var c in p)
                if (!char.IsAsciiDigit(c)) return false;
            if (int.Parse(p) > 255) return false;
        }
        return true;
    }
}
