using System.Globalization;

namespace IpScanner.Core;

/// <summary>Compact number formatting: 1000 -> 1k, 1500 -> 1,5k, 1_000_000 -> 1M.</summary>
public static class NumberFormat
{
    public static string Short(long n)
    {
        var c = CultureInfo.CurrentCulture;
        if (Math.Abs(n) >= 1_000_000) return (n / 1_000_000.0).ToString("0.#", c) + "M";
        if (Math.Abs(n) >= 1_000) return (n / 1_000.0).ToString("0.#", c) + "k";
        return n.ToString(c);
    }
}
