using System.Windows;
using Microsoft.Win32;

namespace IpScanner.Views;

/// <summary>
/// Picks the color theme at startup and merges the matching resource dictionary
/// (Theme.Dark.xaml / Theme.Light.xaml) ahead of Styles.xaml. All brushes are
/// consumed via DynamicResource, so calling Switch() at runtime re-colors the
/// whole UI immediately - no restart needed.
/// </summary>
internal static class ThemeManager
{
    /// <summary>True when the light theme is active (window chrome mirrors this).</summary>
    public static bool IsLight { get; private set; }

    private static ResourceDictionary? _themeDict;

    /// <summary>Merge the theme + styles into the app resources. Call once at startup.</summary>
    public static void Apply(Application app, string theme)
    {
        IsLight = Resolve(theme);
        Core.Palette.IsLight = IsLight;
        _themeDict = LoadTheme(IsLight);
        app.Resources.MergedDictionaries.Add(_themeDict);
        Merge(app, "Resources/Styles.xaml");    // styles reference the colors above
        Merge(app, "Resources/Icons.xaml");     // Device-type icon geometries
    }

    /// <summary>Swap the active theme dictionary at runtime. Every DynamicResource
    /// consumer (Styles.xaml, MainWindow.xaml, ...) re-resolves automatically.</summary>
    public static void Switch(Application app, string theme)
    {
        IsLight = Resolve(theme);
        Core.Palette.IsLight = IsLight;
        var next = LoadTheme(IsLight);
        var dicts = app.Resources.MergedDictionaries;
        int index = _themeDict is not null ? dicts.IndexOf(_themeDict) : -1;
        if (index >= 0) dicts[index] = next; else dicts.Insert(0, next);
        _themeDict = next;
    }

    private static ResourceDictionary LoadTheme(bool light)
        => new() { Source = new Uri($"Resources/{(light ? "Theme.Light.xaml" : "Theme.Dark.xaml")}", UriKind.Relative) };

    private static void Merge(Application app, string relativePath)
        => app.Resources.MergedDictionaries.Add(
            new ResourceDictionary { Source = new Uri(relativePath, UriKind.Relative) });

    private static bool Resolve(string theme) => theme.ToLowerInvariant() switch
    {
        "light" => true,
        "dark" => false,
        _ => WindowsPrefersLight(),   // "auto"
    };

    /// <summary>Read the Windows apps light/dark preference (1 = light).</summary>
    private static bool WindowsPrefersLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch { return false; }   // default to dark on any failure
    }
}
