namespace IpScanner.Core.Net;

public static class Ipv4
{
    public const int FirstHost = 1;
    public const int LastHost = 254;

    public static bool IsValid(string text)
    {
        var parts = text.Split('.');
        return parts.Length == 4 &&
               parts.All(p => int.TryParse(p, out var n) && n is >= 0 and <= 255);
    }

    public static string PrefixToMask(int prefix)
    {
        uint bits = prefix == 0 ? 0 : 0xFFFFFFFF << (32 - prefix);
        return $"{(bits >> 24) & 0xFF}.{(bits >> 16) & 0xFF}.{(bits >> 8) & 0xFF}.{bits & 0xFF}";
    }

    public static string SubnetPrefix(string ip)
        => string.Join('.', ip.Split('.')[..3]);

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
}
