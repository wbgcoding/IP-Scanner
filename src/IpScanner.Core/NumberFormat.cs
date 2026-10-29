using System.Globalization;
using IpScanner.Core.Localization;

namespace IpScanner.Core;

/// <summary>Compact number formatting: 1000 -> 1k, 1500 -> 1,5k, 1_000_000 -> 1M.</summary>
public static class NumberFormat
{
    // Captured once, on first touch - before any config-driven override runs -
    // so "auto" always means "whatever Windows itself is set to", not whatever
    // the override happened to leave behind.
    private static readonly CultureInfo SystemCulture = CultureInfo.CurrentCulture;

    /// <summary>Apply the configured date/number format app-wide.
    /// Setting both DefaultThreadCurrentCulture (future threads - scan workers)
    /// and CurrentCulture (this, the UI thread, live) means every existing
    /// CultureInfo.CurrentCulture-based format call (here and in DeviceViewModel,
    /// exporters, etc.) picks it up with no per-call-site change needed.</summary>
    public static void ApplyRegionOverride(string code)
    {
        CultureInfo culture;
        try { culture = code == "auto" ? SystemCulture : CultureInfo.GetCultureInfo(code); }
        catch { culture = SystemCulture; }
        CultureInfo.DefaultThreadCurrentCulture = ReferenceEquals(culture, SystemCulture) ? null : culture;
        CultureInfo.CurrentCulture = culture;
    }

    public static string Short(long n)
    {
        var c = CultureInfo.CurrentCulture;
        if (Math.Abs(n) >= 1_000_000) return (n / 1_000_000.0).ToString("0.#", c) + "M";
        if (Math.Abs(n) >= 1_000) return (n / 1_000.0).ToString("0.#", c) + "k";
        return n.ToString(c);
    }

    /// <summary>Latency: "12,3 ms" (current culture), "-" when missing.</summary>
    public static string Ms(double? v) =>
        v is null ? "\u2013" : MsValue(v.Value) + " ms";

    /// <summary>The number part of <see cref="Ms"/> ("12,3"), for texts that carry the unit themselves.</summary>
    public static string MsValue(double v) => v.ToString("F1", CultureInfo.CurrentCulture);

    /// <summary>Packet loss "12,3 %" (current culture), "-" when missing.</summary>
    public static string Percent(double? v) =>
        v is null ? "–" : v.Value.ToString("F1", CultureInfo.CurrentCulture) + " %";

    /// <summary>Rough remaining-time estimate for a long-running sweep -
    /// "~12 s", "~3 min" (localized unit). Deliberately coarse (one unit, rounded)
    /// since it's an estimate, not a countdown.</summary>
    public static string Eta(TimeSpan remaining)
    {
        var c = CultureInfo.CurrentCulture;
        if (remaining.TotalSeconds < 1) return "";
        if (remaining.TotalMinutes < 1) return string.Format(Loc.EtaSecondsFmt, Math.Ceiling(remaining.TotalSeconds).ToString("0", c));
        if (remaining.TotalHours < 1) return string.Format(Loc.EtaMinutesFmt, Math.Ceiling(remaining.TotalMinutes).ToString("0", c));
        return string.Format(Loc.EtaHoursFmt, Math.Ceiling(remaining.TotalHours).ToString("0", c));
    }

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
