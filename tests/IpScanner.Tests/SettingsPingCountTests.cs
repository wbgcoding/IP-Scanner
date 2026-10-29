using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace IpScanner.Tests;

/// <summary>The ping count in the main header is the only ping-count setting: the settings page
/// has no second input for it, for the discovery pass or for the startup sweep.</summary>
public class SettingsPingCountTests
{
    private static string Xaml()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "IP-Scanner.slnx")))
                return File.ReadAllText(Path.Combine(d.FullName, "src", "IpScanner", "MainWindow.xaml"));
        throw new InvalidOperationException("Could not locate repo root.");
    }

    [Theory]
    [InlineData("InitPingCountBox")]
    [InlineData("StartupPingsBox")]
    [InlineData("StartupPingsDiscoveryBox")]
    [InlineData("DefaultPingsBox")]
    [InlineData("DefaultPingsEndlessBox")]
    public void SettingsPage_HasNoPingCountField(string name)
    {
        Assert.DoesNotContain("x:Name=\"" + name + "\"", Xaml());
    }

    [Fact]
    public void HeaderPingCount_IsStillThere()
    {
        Assert.Matches(new Regex("<ComboBox x:Name=\"PingCountBox\""), Xaml());
    }
}
