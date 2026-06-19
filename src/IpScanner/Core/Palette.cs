using System.Text.RegularExpressions;

namespace IpScanner.Core;

/// <summary>Catppuccin Mocha hex colors used from C# (mirrors Resources/Styles.xaml).
/// Single source for code-side colors so they don't drift across view models.</summary>
public static class Palette
{
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
    public const string Text = "#CDD6F4";
    public const string MidGray = "#585B70";
    public const string Surface2 = "#45475A";
    public const string Green = "#A6E3A1";
    public const string Lime = "#C9E88A";
    public const string Yellow = "#F9E2AF";
    public const string Peach = "#FAB387";
    public const string Red = "#F38BA8";
    public const string Mauve = "#CBA6F7";
    public const string Blue = "#89B4FA";
    /// <summary>Ubiquiti brand blue (gateway color for UniFi hostnames).</summary>
    public const string UnifiBlue = "#0559C9";
    public const string Transparent = "#00000000";
    public const string DarkText = "#1E1E2E";

    /// <summary>Color-picker palette swatches (Catppuccin Mocha spectrum).</summary>
    public static readonly string[] Swatches =
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

    /// <summary>Latency heatmap: green (fast) -> red (slow). Mirrors Python thresholds.</summary>
    public static string Heat(double? ms) => ms switch
    {
        null => MidGray,
        <= 50 => Green,     // excellent
        <= 100 => Lime,     // good
        <= 200 => Yellow,   // okay
        <= 400 => Peach,    // bad
        _ => Red,           // very bad
    };
}
