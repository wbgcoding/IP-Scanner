using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace IpScanner.Tests;

/// <summary>The welcome card asks for update checks and internet measuring as an opt-in:
/// both boxes start unchecked.</summary>
public class FirstRunOptInTests
{
    private static string Xaml()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "IP-Scanner.slnx")))
                return File.ReadAllText(Path.Combine(d.FullName, "src", "IpScanner", "MainWindow.xaml"));
        throw new InvalidOperationException("Could not locate repo root.");
    }

    [Theory]
    [InlineData("FirstRunUpdatesBox")]
    [InlineData("FirstRunInternetBox")]
    public void WelcomeCardBox_StartsUnchecked(string name)
    {
        var tag = Regex.Match(Xaml(), "<CheckBox\\s[^>]*x:Name=\"" + name + "\"[^>]*>", RegexOptions.Singleline);
        Assert.True(tag.Success);
        Assert.Contains("IsChecked=\"False\"", tag.Value);
        Assert.DoesNotContain("IsChecked=\"True\"", tag.Value);
    }
}
