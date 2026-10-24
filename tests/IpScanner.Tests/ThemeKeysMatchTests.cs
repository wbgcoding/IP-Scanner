using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace IpScanner.Tests;

/// <summary>Theme.Dark.xaml and Theme.Light.xaml must define the exact same set
/// of SolidColorBrush keys - a DynamicResource lookup that only exists in one
/// theme silently falls back to nothing (no error, just a blank/default brush)
/// when the other theme is active.</summary>
public class ThemeKeysMatchTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "IP-Scanner.slnx"))) return d.FullName;
        throw new InvalidOperationException("Could not locate repo root from " + dir);
    }

    private static string[] BrushKeys(string fileName)
    {
        var path = Path.Combine(RepoRoot(), "src", "IpScanner", "Resources", fileName);
        var ns = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        var doc = XDocument.Load(path);
        return doc.Descendants()
            .Where(e => e.Name.LocalName == "SolidColorBrush")
            .Select(e => e.Attribute(ns + "Key")?.Value ?? "")
            .Where(k => k.Length > 0)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();
    }

    [Fact]
    public void DarkAndLightTheme_DefineTheSameKeys()
    {
        var dark = BrushKeys("Theme.Dark.xaml");
        var light = BrushKeys("Theme.Light.xaml");
        Assert.NotEmpty(dark);
        Assert.Equal(dark, light);
    }

    // A hardcoded hex color in Styles.xaml can't adapt per theme and
    // is how the DangerButton light-theme contrast failure (3.61:1, below AA)
    // went unnoticed - it never went through a theme file where a ratio could
    // be checked at all. Colors belong in Theme.Dark.xaml/Theme.Light.xaml.
    [Fact]
    public void Styles_HaveNoHardcodedHexColors()
    {
        var path = Path.Combine(RepoRoot(), "src", "IpScanner", "Resources", "Styles.xaml");
        var text = File.ReadAllText(path);
        var matches = System.Text.RegularExpressions.Regex.Matches(text, @"(?:Value|Stroke|Fill|Background|Foreground|BorderBrush)=""#[0-9A-Fa-f]{6,8}""");
        Assert.True(matches.Count == 0,
            "Styles.xaml has hardcoded hex colors that won't adapt per theme: " +
            string.Join(", ", matches.Select(m => m.Value)));
    }
}
