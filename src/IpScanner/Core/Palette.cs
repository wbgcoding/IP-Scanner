namespace IpScanner.Core;

/// <summary>Catppuccin Mocha hex colors used from C# (mirrors Resources/Styles.xaml).
/// Single source for code-side colors so they don't drift across view models.</summary>
public static class Palette
{
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
