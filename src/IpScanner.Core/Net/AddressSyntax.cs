namespace IpScanner.Core.Net;

/// <summary>Entry validation for the places that take an address or a subnet of either family: sidebar lists,
/// list import, tool inputs.</summary>
public static class AddressSyntax
{
    /// <summary>A single IPv4 or IPv6 address (an IPv6 link-local address may carry a zone).</summary>
    public static bool IsValidAddress(string? text)
        => !string.IsNullOrWhiteSpace(text) && (Ipv4.IsValid(text.Trim()) || Ipv6Address.TryParse(text.Trim(), out _));

    /// <summary>An address, or "address/prefix" with a prefix of 1..32 (IPv4) or 1..128 (IPv6).</summary>
    public static bool IsValidSubnetEntry(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (Ipv4.IsValidSubnetEntry(text)) return true;
        if (!Ipv6Prefix.TryParse(text, out var prefix)) return false;
        return prefix.Length >= 1 || !text.Contains('/');
    }

    /// <summary>The stored form of an entry: IPv6 addresses and prefixes in short RFC 5952 form, anything else unchanged.</summary>
    public static string Normalize(string text)
    {
        var t = text.Trim();
        if (!t.Contains(':')) return text;
        if (t.Contains('/')) return Ipv6Prefix.TryParse(t, out var p) ? p.ToString() : text;
        return Ipv6Address.TryParse(t, out var a) ? a.ToScopedString() : text;
    }
}
