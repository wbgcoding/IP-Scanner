using System.Text.RegularExpressions;

namespace IpScanner.Core;

/// <summary>Catppuccin hex colors used from C# (mirrors Resources/Theme.Dark.xaml
/// and Theme.Light.xaml - same hex values, kept in sync by ThemeKeysMatchTests).
/// Single source for code-side colors so they don't drift across view models.
/// Semantic colors (Text/status/heat/etc.) switch with <see cref="IsLight"/> so a
/// value bound directly to a C# property (not a XAML DynamicResource) still
/// re-colors correctly when the theme changes - swatches and other colors that
/// are never used as theme-following text/background stay constant.</summary>
public static class Palette
{
    /// <summary>Set by ThemeManager whenever the active theme changes (both at
    /// startup and on a live switch) - the single source both XAML's
    /// DynamicResource lookups and these C#-computed colors read from.</summary>
    public static bool IsLight { get; set; }

    public const string Rosewater = "#F5E0DC";
    public const string Flamingo = "#F2CDCD";
    public const string Pink = "#F5C2E7";
    public const string Maroon = "#EBA0AC";
    public const string Teal = "#94E2D5";
    public const string Sky = "#89DCEB";
    public const string Sapphire = "#74C7EC";
    public const string Lavender = "#B4BEFE";
    public const string Subtext0 = "#A6ADC8";
    public const string Overlay0 = "#6C7086";
    public const string Surface0 = "#313244";
    public static string Text => IsLight ? "#4C4F69" : "#CDD6F4";
    /// <summary>Matches the "Subtext" brush key in Theme.Dark/Light.xaml -
    /// used to dim a muted device's row in code (not reachable via DynamicResource).</summary>
    public static string Subtext => IsLight ? "#8C8FA1" : "#6C7086";
    public static string MidGray => IsLight ? "#9CA0B0" : "#585B70";
    public static string Surface2 => IsLight ? "#BCC0CC" : "#45475A";
    public static string Green => IsLight ? "#40A02B" : "#A6E3A1";
    public static string Lime => IsLight ? "#8FA82B" : "#C9E88A";
    public static string Yellow => IsLight ? "#DF8E1D" : "#F9E2AF";
    public static string Peach => IsLight ? "#FE640B" : "#FAB387";
    public static string Red => IsLight ? "#D20F39" : "#F38BA8";
    public static string Mauve => IsLight ? "#8839EF" : "#CBA6F7";
    public static string Blue => IsLight ? "#1E66F5" : "#89B4FA";
    /// <summary>Ubiquiti brand blue (gateway color for UniFi hostnames).</summary>
    public const string UnifiBlue = "#0559C9";
    public const string Transparent = "#00000000";
    public const string DarkText = "#1E1E2E";

    /// <summary>Color-picker palette swatches (recomputed each access so the
    /// theme-dependent entries follow the active theme).</summary>
    public static string[] Swatches => new[]
    {
        Rosewater, Flamingo, Pink, Mauve, Red,
        Maroon, Peach, Yellow, Green, Teal,
        Sky, Sapphire, Blue, Lavender, Text,
        Subtext0, Overlay0, MidGray, Surface2, Surface0,
    };

    private static readonly Regex HexPattern = new("^#?[0-9A-Fa-f]{6}$", RegexOptions.Compiled);

    /// <summary>True for a 6-digit hex color, with or without a leading '#'.</summary>
    public static bool IsHexColor(string? s) => s is not null && HexPattern.IsMatch(s.Trim());

    /// <summary>Readable text color (dark or white) for the given background.</summary>
    public static string ContrastOn(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length != 6) return Text;
        int r = Convert.ToInt32(h[..2], 16);
        int g = Convert.ToInt32(h.Substring(2, 2), 16);
        int b = Convert.ToInt32(h.Substring(4, 2), 16);
        double luminance = 0.299 * r + 0.587 * g + 0.114 * b;
        return luminance < 140 ? "#FFFFFF" : DarkText;
    }

    /// <summary>Latency heatmap thresholds in ms, set once from config on load/save
    /// (same pattern as <see cref="IsLight"/>). Defaults match the
    /// built-in values so a fresh config behaves the same.</summary>
    public static double HeatMs1 { get; set; } = Models.ScanConfig.Bounds.HeatMs1Default;
    public static double HeatMs2 { get; set; } = Models.ScanConfig.Bounds.HeatMs2Default;
    public static double HeatMs3 { get; set; } = Models.ScanConfig.Bounds.HeatMs3Default;
    public static double HeatMs4 { get; set; } = Models.ScanConfig.Bounds.HeatMs4Default;

    /// <summary>Latency heatmap: green (fast) -> red (slow).</summary>
    public static string Heat(double? ms) => ms switch
    {
        null => MidGray,
        _ when ms <= HeatMs1 => Green,     // excellent
        _ when ms <= HeatMs2 => Lime,      // good
        _ when ms <= HeatMs3 => Yellow,    // okay
        _ when ms <= HeatMs4 => Peach,     // bad
        _ => Red,                         // very bad
    };
}
