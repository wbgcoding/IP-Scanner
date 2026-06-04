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

    /// <summary>Latency: "12,3 ms" (current culture), "—" when missing.</summary>
    public static string Ms(double? v) =>
        v is null ? "—" : v.Value.ToString("F1", CultureInfo.CurrentCulture) + " ms";

    /// <summary>File size: 980 B, 12,3 KB, 4,2 MB, 1,1 GB.</summary>
    public static string Bytes(long b)
    {
        var c = CultureInfo.CurrentCulture;
        return b switch
        {
            < 1024 => $"{b} B",
            < 1024 * 1024 => (b / 1024.0).ToString("0.#", c) + " KB",
            < 1024L * 1024 * 1024 => (b / (1024.0 * 1024)).ToString("0.#", c) + " MB",
            _ => (b / (1024.0 * 1024 * 1024)).ToString("0.#", c) + " GB",
        };
    }
}
