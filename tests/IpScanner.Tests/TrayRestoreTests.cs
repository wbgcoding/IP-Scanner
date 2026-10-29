using System.Windows;
using IpScanner.Core.Localization;
using Xunit;

namespace IpScanner.Tests;

[Collection("Localization")]
public sealed class TrayRestoreTests
{
    [Fact]
    public void RestoreTarget_StartsNormal()
        => Assert.Equal(WindowState.Normal, new TrayWindowState().RestoreTarget);

    [Fact]
    public void RestoreTarget_KeepsMaximizedAcrossAMinimize()
    {
        var s = new TrayWindowState();
        s.Observe(WindowState.Maximized);
        s.Observe(WindowState.Minimized);
        Assert.Equal(WindowState.Maximized, s.RestoreTarget);
    }

    [Fact]
    public void RestoreTarget_IsNormalAfterTheWindowWasRestoredToNormal()
    {
        var s = new TrayWindowState();
        s.Observe(WindowState.Maximized);
        s.Observe(WindowState.Normal);
        s.Observe(WindowState.Minimized);
        Assert.Equal(WindowState.Normal, s.RestoreTarget);
    }

    [Fact]
    public void RestoreTarget_IgnoresRepeatedMinimizedStates()
    {
        var s = new TrayWindowState();
        s.Observe(WindowState.Maximized);
        s.Observe(WindowState.Minimized);
        s.Observe(WindowState.Minimized);
        Assert.Equal(WindowState.Maximized, s.RestoreTarget);
    }

    [Theory]
    [InlineData("en", 1, "1 device online.")]
    [InlineData("en", 0, "0 devices online.")]
    [InlineData("en", 2, "2 devices online.")]
    [InlineData("de", 1, "1 Gerät online.")]
    [InlineData("de", 3, "3 Geräte online.")]
    public void TrayScanDoneText_UsesTheSingularForOne(string lang, int online, string expected)
    {
        Loc.SetLanguage(lang);
        try { Assert.Equal(expected, Loc.TrayScanDoneText(online)); }
        finally { Loc.SetLanguage("en"); }
    }
}
