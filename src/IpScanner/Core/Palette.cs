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
    public const string Transparent = "#00000000";

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
